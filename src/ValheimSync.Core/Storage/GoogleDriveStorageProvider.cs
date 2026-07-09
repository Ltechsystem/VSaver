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
    private readonly string _credentialsPath;
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
        _credentialsPath = credentialsPath
            ?? Path.Combine(AppContext.BaseDirectory, "credentials.json");
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_credentialsPath))
            throw new FileNotFoundException(
                "credentials.json not found next to the app. You must download it yourself: " +
                "create a free OAuth 'Desktop app' client in Google Cloud Console, download the JSON, " +
                "rename it to 'credentials.json', and put it beside the app. See README Part 1.",
                _credentialsPath);

        var credsText = await File.ReadAllTextAsync(_credentialsPath, ct);
        if (credsText.Contains("YOUR_CLIENT_ID") || credsText.Contains("YOUR_CLIENT_SECRET"))
            throw new InvalidOperationException(
                "credentials.json still contains the placeholder values from the example file. " +
                "Replace it with the real JSON you download from Google Cloud Console (README Part 1).");

        await using var stream = File.OpenRead(_credentialsPath);

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
