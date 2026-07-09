using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ValheimSync.Core.Models;
using ValheimSync.Core.Storage;
using ValheimSync.Core.Util;

namespace ValheimSync.Core.Sync;

/// <summary>
/// Coordinates the whole sync lifecycle for the selected worlds:
///
///   * FileSystemWatcher (debounced) → upload after Valheim finishes a save
///   * Periodic poll (default 15 min) → download remote changes / fallback upload check
///   * MD5 comparison against Drive's md5Checksum → no duplicate transfers, ever
///   * .fwl is uploaded last and acts as the "commit marker" for the pair
///   * Downloads go to a temp file, are hash-verified, then moved atomically
///   * Nothing is downloaded while Valheim is running
/// </summary>
public sealed class SyncEngine : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly ICloudStorageProvider _cloud;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _syncGate = new(1, 1); // one sync at a time
    private DebouncedWorldWatcher? _watcher;
    private PeriodicTimer? _timer;
    private PeriodicTimer? _inGameTimer;
    private CancellationTokenSource? _cts;

    public event Action<string, SyncStatus>? WorldStatusChanged;
    public event Action<string>? Log;

    public SyncEngine(AppSettings settings, ICloudStorageProvider cloud, ILogger? log = null)
    {
        _settings = settings;
        _cloud = cloud;
        _log = log ?? NullLogger.Instance;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _cloud.InitializeAsync(ct);
        Info($"Connected to {_cloud.ProviderName}.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (Directory.Exists(_settings.WorldsPath))
        {
            _watcher = new DebouncedWorldWatcher(
                _settings.WorldsPath, TimeSpan.FromSeconds(_settings.DebounceSeconds));
            _watcher.WorldChanged += world =>
                _ = SafeSyncWorldAsync(world, _cts.Token);
        }

        // Fallback / download poll.
        _timer = new PeriodicTimer(TimeSpan.FromMinutes(_settings.PollIntervalMinutes));
        _ = Task.Run(async () =>
        {
            // Initial pass on startup, then on every tick.
            await SafeSyncAllAsync(_cts.Token);
            while (await _timer.WaitForNextTickAsync(_cts.Token))
                await SafeSyncAllAsync(_cts.Token);
        }, _cts.Token);

        // In-game auto-save uploader: while Valheim is open, push the in-progress save
        // on this interval — but only if it changed and has settled (never mid-write).
        _inGameTimer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, _settings.InGameUploadMinutes)));
        _ = Task.Run(async () =>
        {
            while (await _inGameTimer.WaitForNextTickAsync(_cts.Token))
                if (ValheimProcess.IsRunning())
                    await SafeSyncAllAsync(_cts.Token, allowUploadWhileRunning: true);
        }, _cts.Token);
    }

    public Task SyncNowAsync() => SafeSyncAllAsync(_cts?.Token ?? default);

    // ---------------------------------------------------------------------

    private async Task SafeSyncAllAsync(CancellationToken ct, bool allowUploadWhileRunning = false)
    {
        foreach (var world in _settings.SelectedWorlds.ToArray())
            await SafeSyncWorldAsync(world, ct, allowUploadWhileRunning);
    }

    private async Task SafeSyncWorldAsync(string worldName, CancellationToken ct,
        bool allowUploadWhileRunning = false)
    {
        if (!_settings.SelectedWorlds.Contains(worldName)) return;

        await _syncGate.WaitAsync(ct);
        try
        {
            await SyncWorldAsync(worldName, ct, allowUploadWhileRunning);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sync failed for {World}", worldName);
            Info($"[{worldName}] Sync failed: {ex.Message}");
            WorldStatusChanged?.Invoke(worldName, SyncStatus.Error);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task SyncWorldAsync(string worldName, CancellationToken ct,
        bool allowUploadWhileRunning = false)
    {
        var local = WorldScanner.Scan(_settings.WorldsPath)
            .FirstOrDefault(w => string.Equals(w.Name, worldName, StringComparison.OrdinalIgnoreCase));

        var remote = await _cloud.ListFilesAsync(ct);
        var remoteDb = remote.FirstOrDefault(f => f.Name.Equals($"{worldName}.db", StringComparison.OrdinalIgnoreCase));
        var remoteFwl = remote.FirstOrDefault(f => f.Name.Equals($"{worldName}.fwl", StringComparison.OrdinalIgnoreCase));

        var whoHasLock = await _cloud.GetLockAsync(worldName, ct);
        bool iHoldLock = whoHasLock is not null &&
            whoHasLock.PlayerName.Equals(_settings.PlayerName, StringComparison.OrdinalIgnoreCase);
        bool otherHoldsLock = whoHasLock is not null && !whoHasLock.IsStale && !iHoldLock;

        // ---- Case 1: nothing local, remote exists → first-time download ----
        if (local is null && remoteDb is not null && remoteFwl is not null)
        {
            await DownloadWorldAsync(worldName, ct);
            return;
        }

        if (local is null)
        {
            WorldStatusChanged?.Invoke(worldName, SyncStatus.Unknown);
            return;
        }

        // ---- Compare hashes ----
        var localDbHash = await Hashing.Md5Async(local.DbPath, ct);
        bool dbMatches = remoteDb?.Md5Checksum == localDbHash;

        if (dbMatches)
        {
            WorldStatusChanged?.Invoke(worldName,
                otherHoldsLock ? SyncStatus.LockedByOther : SyncStatus.InSync);
            return;
        }

        // ---- Divergence: decide direction ----
        // If I hold the lock (or no one does and my file is newer), upload.
        // If someone else holds the lock, or the remote is newer, download —
        // but never while Valheim is running locally.
        bool remoteIsNewer = remoteDb is not null &&
            remoteDb.ModifiedTime.UtcDateTime > local.LastWriteUtc;

        if (iHoldLock || (remoteDb is null) || (!otherHoldsLock && !remoteIsNewer))
        {
            if (ValheimProcess.IsRunning())
            {
                // Only push mid-session when the in-game timer asked us to AND the save
                // has been quiet long enough that Valheim isn't part-way through writing it.
                var settled = (DateTime.UtcNow - local.LastWriteUtc)
                    >= TimeSpan.FromSeconds(_settings.DebounceSeconds);
                if (!allowUploadWhileRunning || !settled)
                {
                    Info($"[{worldName}] Valheim is running — will upload after the next completed save.");
                    return;
                }
                Info($"[{worldName}] Valheim running — pushing in-progress save.");
            }
            await UploadWorldAsync(local, ct);
        }
        else
        {
            if (ValheimProcess.IsRunning())
            {
                Info($"[{worldName}] Remote is newer but Valheim is running — skipping download.");
                WorldStatusChanged?.Invoke(worldName, SyncStatus.RemoteNewer);
                return;
            }
            await DownloadWorldAsync(worldName, ct);
        }
    }

    private async Task UploadWorldAsync(WorldSave world, CancellationToken ct)
    {
        WorldStatusChanged?.Invoke(world.Name, SyncStatus.Syncing);
        Info($"[{world.Name}] Uploading ({world.SizeBytes / (1024.0 * 1024):F1} MB)...");

        // .db first, .fwl last — the .fwl acts as the commit marker, so a
        // half-finished upload never looks like a complete world remotely.
        await _cloud.UploadAsync(world.DbPath, world.DbFileName, null, ct);
        await _cloud.UploadAsync(world.FwlPath, world.FwlFileName, null, ct);

        Info($"[{world.Name}] Upload complete.");
        WorldStatusChanged?.Invoke(world.Name, SyncStatus.InSync);
    }

    private async Task DownloadWorldAsync(string worldName, CancellationToken ct)
    {
        WorldStatusChanged?.Invoke(worldName, SyncStatus.Syncing);
        Info($"[{worldName}] Downloading...");

        Directory.CreateDirectory(_settings.WorldsPath);

        var tmpDb = Path.Combine(Path.GetTempPath(), $"{worldName}-{Guid.NewGuid():N}.db");
        var tmpFwl = Path.Combine(Path.GetTempPath(), $"{worldName}-{Guid.NewGuid():N}.fwl");
        try
        {
            await _cloud.DownloadAsync($"{worldName}.db", tmpDb, null, ct);
            await _cloud.DownloadAsync($"{worldName}.fwl", tmpFwl, null, ct);

            // Verify the .db against the remote hash before touching the live folder.
            var remote = await _cloud.ListFilesAsync(ct);
            var remoteDb = remote.First(f => f.Name.Equals($"{worldName}.db", StringComparison.OrdinalIgnoreCase));
            if (remoteDb.Md5Checksum is not null &&
                remoteDb.Md5Checksum != await Hashing.Md5Async(tmpDb, ct))
                throw new IOException("Downloaded .db failed hash verification — aborting.");

            var dbDest = Path.Combine(_settings.WorldsPath, $"{worldName}.db");
            var fwlDest = Path.Combine(_settings.WorldsPath, $"{worldName}.fwl");

            // Keep one local safety copy of what we are about to replace.
            if (File.Exists(dbDest)) File.Copy(dbDest, dbDest + ".synbak", overwrite: true);
            if (File.Exists(fwlDest)) File.Copy(fwlDest, fwlDest + ".synbak", overwrite: true);

            File.Move(tmpDb, dbDest, overwrite: true);
            File.Move(tmpFwl, fwlDest, overwrite: true);

            // Also drop it into Valheim's LocalLow folder so it actually shows up in the
            // in-game world list (see MirrorToLocalLow). Never fatal to the download.
            MirrorToLocalLow(worldName, dbDest, fwlDest);

            Info($"[{worldName}] Download complete.");
            WorldStatusChanged?.Invoke(worldName, SyncStatus.InSync);
        }
        finally
        {
            if (File.Exists(tmpDb)) File.Delete(tmpDb);
            if (File.Exists(tmpFwl)) File.Delete(tmpFwl);
        }
    }

    /// <summary>
    /// Copies a freshly downloaded world into Valheim's LocalLow "local storage" folder.
    /// Valheim only adds a world to the in-game list once it imports it from there and
    /// registers it with Steam Cloud — a world written solely into the Steam userdata\…\
    /// remote folder never appears. This automates the manual "copy into LocalLow, then
    /// open Valheim" step. It is best-effort: any failure is logged, never thrown, so a
    /// download is still considered complete even if this copy can't happen.
    /// </summary>
    private void MirrorToLocalLow(string worldName, string dbSrc, string fwlSrc)
    {
        try
        {
            var folder = ValheimSaveLocations.ResolveLocalLowWorldsFolder();
            if (folder is null) return;

            // If the app already syncs straight out of the LocalLow folder, the files are
            // already there — nothing to mirror.
            if (string.Equals(
                    Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(_settings.WorldsPath).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                return;

            Directory.CreateDirectory(folder);
            var dbDest = Path.Combine(folder, $"{worldName}.db");
            var fwlDest = Path.Combine(folder, $"{worldName}.fwl");

            // Keep one safety copy of anything we replace, same as the main download does.
            if (File.Exists(dbDest)) File.Copy(dbDest, dbDest + ".synbak", overwrite: true);
            if (File.Exists(fwlDest)) File.Copy(fwlDest, fwlDest + ".synbak", overwrite: true);

            File.Copy(dbSrc, dbDest, overwrite: true);
            File.Copy(fwlSrc, fwlDest, overwrite: true);

            Info($"[{worldName}] Copied into Valheim's local folder — it will appear in game.");
        }
        catch (Exception ex)
        {
            Info($"[{worldName}] Couldn't copy into Valheim's local folder: {ex.Message}");
        }
    }

    private void Info(string message)
    {
        _log.LogInformation("{Message}", message);
        Log?.Invoke($"{DateTime.Now:HH:mm:ss}  {message}");
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        _inGameTimer?.Dispose();
        _watcher?.Dispose();
        await Task.CompletedTask;
    }
}
