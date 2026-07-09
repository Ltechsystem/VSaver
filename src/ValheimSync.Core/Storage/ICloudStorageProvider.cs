using ValheimSync.Core.Models;

namespace ValheimSync.Core.Storage;

/// <summary>
/// Abstraction over the cloud backend. Google Drive is the first implementation;
/// Dropbox/OneDrive can be added later without touching the sync engine.
/// </summary>
public interface ICloudStorageProvider
{
    string ProviderName { get; }

    /// <summary>Authenticate (interactive on first run) and verify the shared folder.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>List every file in the shared sync folder.</summary>
    Task<IReadOnlyList<RemoteFile>> ListFilesAsync(CancellationToken ct = default);

    /// <summary>Upload (create or overwrite) a file into the shared folder.</summary>
    Task UploadAsync(string localPath, string remoteName,
        IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>Download a remote file to a local path (path is written atomically by the caller).</summary>
    Task DownloadAsync(string remoteName, string localPath,
        IProgress<double>? progress = null, CancellationToken ct = default);

    Task DeleteAsync(string remoteName, CancellationToken ct = default);

    // ---- World locking -------------------------------------------------
    // A lock is a small JSON file named "<world>.lock" living in the same
    // folder. It is advisory but the app enforces it in the UI.

    Task<WorldLock?> GetLockAsync(string worldName, CancellationToken ct = default);

    /// <summary>Returns false if someone else already holds a non-stale lock.</summary>
    Task<bool> TryAcquireLockAsync(string worldName, string playerName, CancellationToken ct = default);

    /// <summary>Releases the lock only if it is held by <paramref name="playerName"/>.</summary>
    Task ReleaseLockAsync(string worldName, string playerName, CancellationToken ct = default);
}
