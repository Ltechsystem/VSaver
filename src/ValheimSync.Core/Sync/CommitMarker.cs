namespace ValheimSync.Core.Sync;

/// <summary>
/// The "&lt;world&gt;.commit" marker file: uploaded LAST after a world's .db + .fwl, it
/// certifies that the pair currently in the cloud folder was uploaded together. Its
/// content is canonical (byte-for-byte reproducible from the pair's MD5s), so its own
/// MD5 is predictable — which lets any client verify pair consistency straight from the
/// Drive folder listing, without downloading anything.
///
/// A remote whose .db/.fwl MD5s don't hash to the marker's MD5 is "torn" (an upload was
/// interrupted between files) and must not be downloaded. A remote with no marker at all
/// is legacy (pre-marker upload) and is trusted as before.
/// </summary>
public static class CommitMarker
{
    public static string Name(string worldName) => worldName + ".commit";

    /// <summary>
    /// Canonical marker content for a .db/.fwl pair. Never reformat this string —
    /// consistency checks depend on its bytes being reproducible.
    /// </summary>
    public static string Content(string dbMd5, string fwlMd5) =>
        $"{{\"DbMd5\":\"{dbMd5}\",\"FwlMd5\":\"{fwlMd5}\"}}";
}
