using System.Security.Cryptography;
using System.Text;

namespace ValheimSync.Core.Util;

public static class Hashing
{
    /// <summary>MD5 of a string's UTF-8 bytes, lower-case hex — used to predict the
    /// MD5 Drive will report for a commit marker without downloading it.</summary>
    public static string Md5Text(string text) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

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
