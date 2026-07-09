using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ValheimSync.Core;
using ValheimSync.Core.Models;
using ValheimSync.Core.Storage;
using ValheimSync.Core.Sync;

namespace ValheimSync.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private SyncEngine? _engine;
    private ICloudStorageProvider? _cloud;
    private readonly HashSet<string> _watchedWorlds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The main list: worlds ("servers") that exist in the shared cloud folder.</summary>
    public ObservableCollection<WorldItemViewModel> Worlds { get; } = new();

    /// <summary>Local worlds not yet published to the cloud — choices for "Add server".</summary>
    public ObservableCollection<string> AddableWorlds { get; } = new();

    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty]
    private string _playerName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddServerCommand))]
    private string? _selectedAddableWorld;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(LogoutCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddServerCommand))]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlayButtons))]
    private bool _userHoldsAnyLock;

    /// <summary>Play is only shown while the user holds no lock on any world.</summary>
    public bool ShowPlayButtons => !UserHoldsAnyLock;

    [ObservableProperty]
    private string _statusText = "Not connected. Enter your name and click Connect.";

    public MainWindowViewModel()
    {
        _settings = AppSettings.Load();
        _playerName = _settings.PlayerName;
        RefreshAddableWorlds();
        _ = AutoConnectAfterDelayAsync();
    }

    /// <summary>
    /// Auto-connect to Google Drive a few seconds after launch so the user
    /// doesn't have to click Connect every time. A browser may open on first
    /// run for the one-time Google sign-in.
    /// </summary>
    private async Task AutoConnectAfterDelayAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (ConnectCommand.CanExecute(null))
                await ConnectCommand.ExecuteAsync(null);
        });
    }

    /// <summary>
    /// Load the main server list straight from the cloud folder and kick a sync
    /// so any cloud-only saves get downloaded locally.
    /// </summary>
    private async Task RefreshServersAsync()
    {
        if (_cloud is null) return;

        var files = await _cloud.ListFilesAsync();
        var serverNames = files
            .Where(f => f.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileNameWithoutExtension(f.Name))
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Worlds.Clear();
        foreach (var name in serverNames)
        {
            var size = files.FirstOrDefault(f =>
                f.Name.Equals($"{name}.db", StringComparison.OrdinalIgnoreCase))?.SizeBytes ?? 0;

            var item = new WorldItemViewModel(name, size) { IsSelected = true };
            item.SelectionChanged += OnWorldSelectionChanged;

            var heldLock = await _cloud.GetLockAsync(name);
            var active = heldLock is not null && !heldLock.IsStale;
            item.LockHolder = active ? heldLock!.PlayerName : null;
            item.IsLockedByMe = active &&
                string.Equals(heldLock!.PlayerName, _settings.PlayerName, StringComparison.OrdinalIgnoreCase);

            Worlds.Add(item);
            _settings.SelectedWorlds.Add(name); // servers are synced by default
        }
        _settings.Save();

        // Recompute the global "do I hold a lock?" flag from the loaded rows.
        UserHoldsAnyLock = Worlds.Any(w => w.IsLockedByMe);

        RefreshAddableWorlds();

        // Pull down any server whose save we don't have locally yet.
        if (_engine is not null) await _engine.SyncNowAsync();

        StatusText = Worlds.Count == 0
            ? "No servers in the cloud yet. Pick a local world and click 'Add server' to publish one."
            : $"{Worlds.Count} server(s) synced from the cloud.";
    }

    /// <summary>Local worlds that aren't already published as servers — the "Add server" choices.</summary>
    private void RefreshAddableWorlds()
    {
        var alreadyServers = Worlds.Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        AddableWorlds.Clear();
        foreach (var world in WorldScanner.Scan(_settings.WorldsPath))
            if (!alreadyServers.Contains(world.Name))
                AddableWorlds.Add(world.Name);
    }

    private void OnWorldSelectionChanged(WorldItemViewModel item)
    {
        if (item.IsSelected) _settings.SelectedWorlds.Add(item.Name);
        else _settings.SelectedWorlds.Remove(item.Name);
        _settings.Save();
    }

    /// <summary>
    /// Set whether the current user holds a world's lock, then recompute the
    /// global flag from the rows. Deriving <see cref="UserHoldsAnyLock"/> purely
    /// from the per-row flags guarantees Play (hidden globally) and Done (shown
    /// per row) can never both be hidden on a world you actually hold.
    /// </summary>
    private void SetLockedByMe(WorldItemViewModel item, bool mine)
    {
        item.IsLockedByMe = mine;
        UserHoldsAnyLock = Worlds.Any(w => w.IsLockedByMe);
    }

    private bool CanConnect() => !IsConnected;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        IsBusy = true;
        StatusText = "Connecting to Google Drive (a browser window may open)...";
        try
        {
            _settings.PlayerName = PlayerName.Trim();
            _settings.Save();

            _cloud = new GoogleDriveStorageProvider(AppSettings.DriveFolderId);
            _engine = new SyncEngine(_settings, _cloud);
            _engine.Log += line => Dispatcher.UIThread.Post(() =>
            {
                LogLines.Insert(0, line);
                while (LogLines.Count > 200) LogLines.RemoveAt(LogLines.Count - 1);
            });
            _engine.WorldStatusChanged += (world, status) => Dispatcher.UIThread.Post(() =>
            {
                var item = Worlds.FirstOrDefault(w =>
                    string.Equals(w.Name, world, StringComparison.OrdinalIgnoreCase));
                if (item is not null) item.Status = status;
            });

            await _engine.StartAsync();
            IsConnected = true;
            await RefreshServersAsync(); // load the cloud server list + download saves
        }
        catch (Exception ex)
        {
            StatusText = $"Connection failed: {ex.Message}";
            _engine = null;
            _cloud = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanUseEngine() => IsConnected;

    /// <summary>Disconnect and forget the cached Google token so a different account can sign in.</summary>
    [RelayCommand(CanExecute = nameof(CanUseEngine))]
    private async Task LogoutAsync()
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync();
            _engine = null;
        }
        _cloud = null;

        GoogleDriveStorageProvider.ClearCachedToken();

        Worlds.Clear();          // the server list came from the cloud we just left
        UserHoldsAnyLock = false;
        RefreshAddableWorlds();  // local worlds are all "addable" again once reconnected

        IsConnected = false;
        StatusText = "Signed out. Click Connect to sign in with a different Google account.";
    }

    [RelayCommand(CanExecute = nameof(CanUseEngine))]
    private async Task SyncNowAsync()
    {
        if (_engine is null) return;
        IsBusy = true;
        try { await _engine.SyncNowAsync(); }
        finally { IsBusy = false; }
    }

    /// <summary>Reload the server list from the cloud and re-scan local worlds.</summary>
    [RelayCommand]
    private async Task RescanWorldsAsync()
    {
        if (IsConnected) await RefreshServersAsync();
        else RefreshAddableWorlds();
    }

    private bool CanAddServer() => IsConnected && !string.IsNullOrWhiteSpace(SelectedAddableWorld);

    /// <summary>Publish the chosen local world to the cloud so it becomes a shared server.</summary>
    [RelayCommand(CanExecute = nameof(CanAddServer))]
    private async Task AddServerAsync()
    {
        var name = SelectedAddableWorld;
        if (name is null || _cloud is null) return;

        var local = WorldScanner.Scan(_settings.WorldsPath)
            .FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        if (local is null)
        {
            StatusText = $"Local world '{name}' not found.";
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = $"Publishing '{name}' to the cloud...";
            // .db first, .fwl last — the .fwl is the commit marker (same rule as the engine).
            await _cloud.UploadAsync(local.DbPath, local.DbFileName);
            await _cloud.UploadAsync(local.FwlPath, local.FwlFileName);

            _settings.SelectedWorlds.Add(name);
            _settings.Save();

            await RefreshServersAsync();
            StatusText = $"'{name}' is now a server.";
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't add '{name}': {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Launch the game via Steam. Independent of the sync connection.</summary>
    [RelayCommand]
    private void LaunchValheim()
    {
        if (ValheimProcess.IsRunning())
        {
            StatusText = "Valheim is already running.";
            return;
        }
        try
        {
            ValheimProcess.Launch();
            StatusText = "Launching Valheim via Steam...";
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't launch Valheim (is Steam installed?): {ex.Message}";
        }
    }

    /// <summary>
    /// "I want to play this world now" — take the cloud lock, make sure the
    /// world is ticked for syncing, and launch Valheim.
    /// </summary>
    [RelayCommand]
    private async Task AcquireLockAsync(WorldItemViewModel? item)
    {
        if (item is null || _cloud is null) return;
        var got = await _cloud.TryAcquireLockAsync(item.Name, _settings.PlayerName);
        if (!got)
        {
            var holder = await _cloud.GetLockAsync(item.Name);
            item.LockHolder = holder?.PlayerName;
            SetLockedByMe(item, false);
            StatusText = $"'{item.Name}' is currently locked by {holder?.PlayerName ?? "someone else"}.";
            return;
        }

        item.LockHolder = _settings.PlayerName;
        SetLockedByMe(item, true);       // hides Play everywhere, shows Done on this world
        item.IsSelected = true;          // tick the sync checkbox so this world is kept in sync

        try
        {
            if (ValheimProcess.IsRunning())
            {
                StatusText = $"Locked '{item.Name}' — Valheim is already running. Syncing automatically.";
            }
            else
            {
                ValheimProcess.Launch();
                StatusText = $"Locked '{item.Name}', launching Valheim, and syncing automatically.";
            }
            StartValheimExitWatcher(item);
        }
        catch (Exception ex)
        {
            StatusText = $"Locked '{item.Name}' (syncing), but couldn't launch Valheim " +
                         $"(is Steam installed?): {ex.Message}";
        }
    }

    /// <summary>
    /// After Play, watch for Valheim to close and then auto-release the lock —
    /// uploading the final save first, exactly like clicking Done.
    /// </summary>
    private void StartValheimExitWatcher(WorldItemViewModel item)
    {
        lock (_watchedWorlds)
        {
            if (!_watchedWorlds.Add(item.Name)) return; // already watching this world
        }

        _ = WatchAsync();

        async Task WatchAsync()
        {
            try
            {
                var played = await ValheimProcess.WaitForExitAsync(TimeSpan.FromMinutes(5));
                if (!played) return; // game never started — leave the lock for a manual Done
                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await ReleaseWorldAsync(item,
                        $"Valheim closed — uploaded final save and released '{item.Name}'."));
            }
            catch { /* app closing / cancellation — ignore */ }
            finally
            {
                lock (_watchedWorlds) { _watchedWorlds.Remove(item.Name); }
            }
        }
    }

    /// <summary>"I'm done playing" — sync up, then release the lock.</summary>
    [RelayCommand]
    private async Task ReleaseLockAsync(WorldItemViewModel? item)
    {
        if (item is null) return;
        await ReleaseWorldAsync(item, $"Lock for '{item.Name}' released.");
    }

    /// <summary>Push the final save, release the lock, and clear the UI lock badge.</summary>
    private async Task ReleaseWorldAsync(WorldItemViewModel item, string doneStatus)
    {
        if (_cloud is null || _engine is null) return;
        await _engine.SyncNowAsync(); // push the final save before handing over
        await _cloud.ReleaseLockAsync(item.Name, _settings.PlayerName);
        item.LockHolder = null;
        SetLockedByMe(item, false);   // hides Done, brings Play back once no lock is held
        StatusText = doneStatus;
    }

    public async Task ShutdownAsync()
    {
        if (_engine is not null) await _engine.DisposeAsync();
    }
}
