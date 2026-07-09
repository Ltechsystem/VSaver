namespace ValheimSync.Core.Models;

public enum SyncStatus
{
    Unknown,
    InSync,
    LocalNewer,     // needs upload
    RemoteNewer,    // needs download
    LocalOnly,      // never uploaded
    RemoteOnly,     // exists only in the cloud
    Syncing,
    LockedByOther,
    Error
}
