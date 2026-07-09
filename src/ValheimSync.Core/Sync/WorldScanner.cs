using ValheimSync.Core.Models;

namespace ValheimSync.Core.Sync;

/// <summary>Finds Valheim worlds (.db + .fwl pairs) in the local save folder.</summary>
public static class WorldScanner
{
    public static IReadOnlyList<WorldSave> Scan(string worldsPath)
    {
        if (!Directory.Exists(worldsPath))
            return Array.Empty<WorldSave>();

        // A world only counts if the .fwl (metadata) exists. Ignore Valheim's
        // own .old files (different extension) and its auto/manual backups, which
        // are named "<World>_backup_auto-<timestamp>.fwl" / "<World>_backup_...".
        return Directory.EnumerateFiles(worldsPath, "*.fwl")
            .Where(fwl => !Path.GetFileNameWithoutExtension(fwl)
                .Contains("_backup_", StringComparison.OrdinalIgnoreCase))
            .Select(fwl => new
            {
                Name = Path.GetFileNameWithoutExtension(fwl),
                Fwl = fwl,
                Db = Path.ChangeExtension(fwl, ".db")
            })
            .Select(w => new WorldSave(w.Name, w.Db, w.Fwl))
            .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
