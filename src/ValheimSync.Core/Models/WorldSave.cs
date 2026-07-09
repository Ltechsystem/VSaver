namespace ValheimSync.Core.Models;

/// <summary>
/// A Valheim world on the local disk. Always a pair: WorldName.db + WorldName.fwl.
/// The pair must be treated atomically — never sync one half without the other.
/// </summary>
public sealed record WorldSave(string Name, string DbPath, string FwlPath)
{
    public DateTime LastWriteUtc =>
        File.Exists(DbPath) ? File.GetLastWriteTimeUtc(DbPath) : DateTime.MinValue;

    public long SizeBytes =>
        (File.Exists(DbPath) ? new FileInfo(DbPath).Length : 0) +
        (File.Exists(FwlPath) ? new FileInfo(FwlPath).Length : 0);

    public string DbFileName => Path.GetFileName(DbPath);
    public string FwlFileName => Path.GetFileName(FwlPath);
}
