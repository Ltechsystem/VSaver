using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using ValheimSync.Core.Models;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace ValheimSync.Core.Storage;

/// <summary>
/// Google Drive implementation of <see cref="ICloudStorageProvider"/>.
///
/// Requires a credentials.json (OAuth "Desktop app" client) next to the exe —
/// see README for the 10-minute Google Cloud Console walkthrough. The OAuth
/// token is cached in %APPDATA%\ValheimSync\token so the browser consent
/// screen only appears once per machine.
/// </summary>
public sealed class GoogleDriveStorageProvider : ICloudStorageProvider
{
    private readonly string _folderId;
    private readonly string? _credentialsPathOverride;
    private DriveService? _drive;

    public string ProviderName => "Google Drive";

    /// <summary>Folder where the cached OAuth token is stored between runs.</summary>
    public static string TokenStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ValheimSync", "token");

    /// <summary>
    /// Deletes the cached OAuth token so the next connect starts a fresh
    /// sign-in — used by "Log out" to let the user pick a different account.
    /// </summary>
    public static void ClearCachedToken()
    {
        if (Directory.Exists(TokenStorePath))
            Directory.Delete(TokenStorePath, recursive: true);
    }

    public GoogleDriveStorageProvider(string folderId, string? credentialsPath = null)
    {
        _folderId = folderId;
        _credentialsPathOverride = credentialsPath;
    }

    /// <summary>
    /// Folders to look for credentials.json in, most-authoritative first. In a single-file
    /// build the exe's own folder is <see cref="Environment.ProcessPath"/>'s directory;
    /// <see cref="AppContext.BaseDirectory"/> matches it in normal runs, and the process
    /// working directory is included as a last resort (e.g. launched from a shortcut whose
    /// "Start in" points at the folder). Deduplicated, preserving order.
    /// </summary>
    private static IEnumerable<string> CredentialSearchDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[]
                 {
                     Path.GetDirectoryName(Environment.ProcessPath),
                     AppContext.BaseDirectory,
                     Environment.CurrentDirectory,
                 })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var full = Path.GetFullPath(dir);
            if (seen.Add(full)) yield return full;
        }
    }

    /// <summary>
    /// Locates credentials.json (explicit override wins), or throws a
    /// <see cref="FileNotFoundException"/> that lists every path checked so the user can
    /// see exactly where the app looked versus where they put the file.
    /// </summary>
    private string ResolveCredentialsPath()
    {
        if (!string.IsNullOrWhiteSpace(_credentialsPathOverride))
        {
            if (File.Exists(_credentialsPathOverride)) return _credentialsPathOverride;
            throw new FileNotFoundException(
                $"credentials.json not found at the configured path:\n  {_credentialsPathOverride}",
                _credentialsPathOverride);
        }

        var candidates = CredentialSearchDirs()
            .Select(d => Path.Combine(d, "credentials.json"))
            .ToList();
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null) return found;

        throw new FileNotFoundException(
            "credentials.json was not found next to the app.\n\n" +
            "Looked in:\n" + string.Join("\n", candidates.Select(p => "  • " + p)) + "\n\n" +
            "Checklist:\n" +
            "  • The file must sit in the SAME folder as the app's .exe.\n" +
            "  • It must be named exactly \"credentials.json\" — Windows often hides the real\n" +
            "    extension, so \"credentials.json.txt\" or \"credentials.json.json\" can look right.\n" +
            "    Turn on File Explorer → View → File name extensions to check.\n" +
            "  • It's the JSON you download from Google Cloud Console (README Part 1).");
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var credentialsPath = ResolveCredentialsPath();

        var credsText = await File.ReadAllTextAsync(credentialsPath, ct);
        if (credsText.Contains("YOUR_CLIENT_ID") || credsText.Contains("YOUR_CLIENT_SECRET"))
            throw new InvalidOperationException(
                $"credentials.json ({credentialsPath}) still contains the placeholder values from " +
                "the example file. Replace it with the real JSON you download from Google Cloud " +
                "Console (README Part 1).");

        await using var stream = File.OpenRead(credentialsPath);

        var tokenStore = new FileDataStore(TokenStorePath, fullPath: true);

        // Full "drive" scope so the app can access a shared folder that was
        // created in the Google Drive web UI (the drive.file scope only sees
        // files the app itself created, so it 404s on a pre-made folder).
        // The consent screen will warn about full Drive access — expected for a
        // private, unverified friend-group app.
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            (await GoogleClientSecrets.FromStreamAsync(stream, ct)).Secrets,
            new[] { DriveService.Scope.Drive },
            "user",
            ct,
            tokenStore);

        _drive = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "ValheimSync"
        });

        // Fail fast if the folder ID is wrong or not shared with this account.
        var probe = _drive.Files.Get(_folderId);
        probe.Fields = "id, name";
        probe.SupportsAllDrives = true;
        await probe.ExecuteAsync(ct);
    }

    private DriveService Drive => _drive
        ?? throw new InvalidOperationException("Call InitializeAsync first.");

    public async Task<string?> GetAccountEmailAsync(CancellationToken ct = default)
    {
        try
        {
            var request = Drive.About.Get();
            request.Fields = "user(emailAddress)";
            var about = await request.ExecuteAsync(ct);
            return about.User?.EmailAddress;
        }
        catch
        {
            return null; // non-fatal — the app just falls back to a manual name
        }
    }

    public async Task<IReadOnlyList<RemoteFile>> ListFilesAsync(CancellationToken ct = default)
    {
        var results = new List<RemoteFile>();
        string? pageToken = null;

        do
        {
            var request = Drive.Files.List();
            request.Q = $"'{_folderId}' in parents and trashed = false";
            request.Fields = "nextPageToken, files(id, name, md5Checksum, size, modifiedTime)";
            request.PageSize = 100;
            request.PageToken = pageToken;
            request.SupportsAllDrives = true;
            request.IncludeItemsFromAllDrives = true;

            var page = await request.ExecuteAsync(ct);
            results.AddRange(page.Files.Select(f => new RemoteFile(
                f.Id,
                f.Name,
                f.Md5Checksum,
                f.Size ?? 0,
                f.ModifiedTimeDateTimeOffset ?? DateTimeOffset.MinValue)));

            pageToken = page.NextPageToken;
        } while (pageToken is not null);

        return results;
    }

    public async Task UploadAsync(string localPath, string remoteName,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var existing = await FindByNameAsync(remoteName, ct);
        await using var stream = File.OpenRead(localPath);
        long total = stream.Length;

        if (existing is null)
        {
            var meta = new DriveFile { Name = remoteName, Parents = new[] { _folderId } };
            var create = Drive.Files.Create(meta, stream, "application/octet-stream");
            create.Fields = "id";
            create.SupportsAllDrives = true;
            create.ProgressChanged += p => Report(progress, p, total);
            var result = await create.UploadAsync(ct);
            ThrowIfFailed(result, remoteName);
        }
        else
        {
            var update = Drive.Files.Update(new DriveFile(), existing.Id, stream, "application/octet-stream");
            update.SupportsAllDrives = true;
            update.ProgressChanged += p => Report(progress, p, total);
            var result = await update.UploadAsync(ct);
            ThrowIfFailed(result, remoteName);
        }
    }

    public async Task DownloadAsync(string remoteName, string localPath,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var remote = await FindByNameAsync(remoteName, ct)
            ?? throw new FileNotFoundException($"'{remoteName}' not found in Drive folder.");

        var request = Drive.Files.Get(remote.Id);
        request.SupportsAllDrives = true;

        await using var output = File.Create(localPath);
        long total = remote.SizeBytes;
        request.MediaDownloader.ProgressChanged += p =>
        {
            if (total > 0) progress?.Report((double)p.BytesDownloaded / total);
        };

        var result = await request.DownloadAsync(output, ct);
        if (result.Status != Google.Apis.Download.DownloadStatus.Completed)
            throw new IOException($"Download of '{remoteName}' failed.", result.Exception);
    }

    public async Task DeleteAsync(string remoteName, CancellationToken ct = default)
    {
        var remote = await FindByNameAsync(remoteName, ct);
        if (remote is null) return;

        var request = Drive.Files.Delete(remote.Id);
        request.SupportsAllDrives = true;
        await request.ExecuteAsync(ct);
    }

    // ---- Locking --------------------------------------------------------

    private static string LockName(string worldName) => $"{worldName}.lock";

    public async Task<WorldLock?> GetLockAsync(string worldName, CancellationToken ct = default)
    {
        var remote = await FindByNameAsync(LockName(worldName), ct);
        if (remote is null) return null;

        var request = Drive.Files.Get(remote.Id);
        request.SupportsAllDrives = true;
        using var ms = new MemoryStream();
        await request.DownloadAsync(ms, ct);

        try
        {
            return JsonSerializer.Deserialize<WorldLock>(Encoding.UTF8.GetString(ms.ToArray()));
        }
        catch
        {
            return null; // unreadable lock — treat as absent
        }
    }

    public async Task<bool> TryAcquireLockAsync(string worldName, string playerName, CancellationToken ct = default)
    {
        var current = await GetLockAsync(worldName, ct);

        if (current is not null && !current.IsStale &&
            !string.Equals(current.PlayerName, playerName, StringComparison.OrdinalIgnoreCase))
            return false; // someone else is playing

        var payload = JsonSerializer.Serialize(new WorldLock(playerName, DateTimeOffset.UtcNow));
        var tmp = Path.Combine(Path.GetTempPath(), $"vs-{Guid.NewGuid():N}.lock");
        await File.WriteAllTextAsync(tmp, payload, ct);
        try
        {
            await UploadAsync(tmp, LockName(worldName), null, ct);

            // NOTE: Drive has no atomic compare-and-swap, so two people clicking
            // "Play" within the same second could race. We re-read after writing
            // to make the window as small as practical for a friend group.
            var check = await GetLockAsync(worldName, ct);
            return check is not null &&
                   string.Equals(check.PlayerName, playerName, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    public async Task ReleaseLockAsync(string worldName, string playerName, CancellationToken ct = default)
    {
        var current = await GetLockAsync(worldName, ct);
        if (current is not null &&
            string.Equals(current.PlayerName, playerName, StringComparison.OrdinalIgnoreCase))
        {
            await DeleteAsync(LockName(worldName), ct);
        }
    }

    // ---- Helpers ----------------------------------------------------------

    private async Task<RemoteFile?> FindByNameAsync(string name, CancellationToken ct)
    {
        var all = await ListFilesAsync(ct);
        return all.FirstOrDefault(f =>
            string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static void Report(IProgress<double>? progress, IUploadProgress p, long total)
    {
        if (total > 0) progress?.Report((double)p.BytesSent / total);
    }

    private static void ThrowIfFailed(IUploadProgress result, string name)
    {
        if (result.Status != UploadStatus.Completed)
            throw new IOException($"Upload of '{name}' failed.", result.Exception);
    }
}
