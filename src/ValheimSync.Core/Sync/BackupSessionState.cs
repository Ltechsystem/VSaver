using System.Text.Json;

namespace ValheimSync.Core.Sync;

/// <summary>
/// Persists which worlds have already had their pre-session remote backup taken, plus
/// the last observed "Valheim running" flag. On disk (next to the exe by default) so an
/// app restart in the middle of a play session doesn't re-trigger the backup and
/// overwrite the pre-session snapshot with mid-session state.
///
/// Best-effort: a missing/corrupt file degrades to an empty state, which merely means
/// the next upload takes one extra backup — the same behavior the app had when this
/// state was memory-only.
/// </summary>
public sealed class BackupSessionState
{
    public HashSet<string> BackedUpWorlds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ValheimWasRunning { get; set; }

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "sessionstate.json");

    public static BackupSessionState Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<BackupSessionState>(File.ReadAllText(path));
                if (s is not null)
                {
                    // Deserialization replaces the set and drops the comparer — rewrap so
                    // world-name lookups stay case-insensitive (same fix as AppSettings).
                    s.BackedUpWorlds = new HashSet<string>(s.BackedUpWorlds, StringComparer.OrdinalIgnoreCase);
                    return s;
                }
            }
        }
        catch { /* unreadable state — start fresh */ }
        return new BackupSessionState();
    }

    public void Save(string path)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(this)); }
        catch { /* best-effort — never let bookkeeping break a sync */ }
    }
}
