using System.Security.Cryptography;

namespace ValheimSync.Core.Util;

public static class Hashing
{
    /// <summary>
    /// MD5 hex string, lower-case — matches the md5Checksum field that the
    /// Google Drive API reports for every file, so local vs remote comparison
    /// is a straight string equality check. (MD5 is fine here: it is used for
    /// change detection, not security.)
    /// </summary>
    public static async Task<string> Md5Async(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        using var md5 = MD5.Create();
        var hash = await md5.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
