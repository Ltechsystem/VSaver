namespace ValheimSync.Core.Models;

/// <summary>
/// Contents of a "WorldName.lock" file in the cloud.
/// Whoever holds the lock is allowed to play (and upload) that world.
/// </summary>
public sealed record WorldLock(string PlayerName, DateTimeOffset AcquiredUtc)
{
    /// <summary>Locks older than this are considered stale (a crashed client).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(12);

    public bool IsStale => DateTimeOffset.UtcNow - AcquiredUtc > StaleAfter;
}
