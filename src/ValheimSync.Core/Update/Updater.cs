using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace ValheimSync.Core.Update;

/// <summary>
/// Self-updater for the single-file exe. On startup it asks GitHub for the latest
/// published release; if that release is newer than the running build it downloads
/// the new exe, swaps it in (Windows lets you rename a running exe), relaunches, and
/// tells the caller to exit. Everything is best-effort — if the machine is offline,
/// the repo is unreachable, or the folder is read-only, it silently does nothing and
/// the app starts normally.
///
/// Publishing a new version = build the exe, create a GitHub Release whose tag is the
/// new version (e.g. "v1.0.1") and attach the exe as an asset named "ValheimSync.exe".
/// </summary>
public static class Updater
{
    /// <summary>
    /// The PUBLIC GitHub repo that hosts the releases, as "owner/name".
    /// ▶ Set this to your repo before you build & distribute.
    /// </summary>
    public const string Repo = "Ltechsystem/VSaver";

    /// <summary>The release asset that contains the app exe (must match your build output name).</summary>
    private const string AssetName = "VSaver.exe";

    public static Version CurrentVersion => Normalize(
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>
    /// Clean up leftovers from a previous update, check for a newer release, and if
    /// found download + swap it in and relaunch.
    /// </summary>
    /// <returns>
    /// <c>true</c> if an update is being applied and the caller should exit immediately;
    /// <c>false</c> to continue starting the app normally.
    /// </returns>
    public static async Task<bool> TryUpdateAsync(Action<string>? log = null,
        Action? beforeRelaunch = null, CancellationToken ct = default)
    {
        // Mirror every message to both the caller's log and the on-disk update log, so a
        // failed/absent update is always diagnosable after the fact (see LogPath).
        void Log(string m) { log?.Invoke(m); LogDiagnostic(m); }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return false;

        // Don't self-update a `dotnet run` dev session — that exe is the shared dotnet host,
        // not our packaged app. Only the distributed single-file exe should update itself.
        if (string.Equals(Path.GetFileNameWithoutExtension(exePath), "dotnet",
                StringComparison.OrdinalIgnoreCase))
            return false;

        CleanupLeftovers(exePath);
        Log($"Update check: running v{CurrentVersion}, exe: {exePath}");

        try
        {
            var (version, url) = await GetLatestAsync(ct);
            if (version is null || url is null)
            {
                Log($"No usable release found (repo {Repo}, asset '{AssetName}'). Nothing to update to.");
                return false;
            }
            if (version <= CurrentVersion)
            {
                Log($"Up to date (latest release v{version}, running v{CurrentVersion}).");
                return false;
            }

            Log($"Update found: v{CurrentVersion} → v{version}. Downloading {url}");

            var newPath = exePath + ".new";
            var oldPath = exePath + ".old";
            await DownloadAsync(url, newPath, ct);

            // Guard against a truncated download or an error page saved as the exe: the real
            // asset is ~90 MB and every Windows exe begins with the "MZ" signature. Swapping a
            // bad file in would brick the install, so verify before touching the live exe.
            var size = new FileInfo(newPath).Length;
            if (size < 1_000_000 || !StartsWithMzHeader(newPath))
            {
                TryDelete(newPath);
                throw new IOException(
                    $"Downloaded update is not a valid exe ({size} bytes) — aborting, keeping current version.");
            }
            Log($"Downloaded {size:N0} bytes, verified. Swapping in...");

            // Swap: move the running exe aside, drop the new one in its place, relaunch.
            TryDelete(oldPath);
            File.Move(exePath, oldPath);
            try
            {
                File.Move(newPath, exePath);
            }
            catch
            {
                // Put the original back if the final swap failed, so we never leave no exe.
                if (!File.Exists(exePath) && File.Exists(oldPath)) File.Move(oldPath, exePath);
                throw;
            }

            Log($"Updated to v{version}. Restarting...");
            beforeRelaunch?.Invoke(); // e.g. release the single-instance lock so the new copy can take it
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            // Full exception (not just Message) so the log shows why: permissions, network, AV, etc.
            Log($"Update skipped: {ex}");
            return false;
        }
    }

    /// <summary>Where the persistent update log lives (always writable, unlike a read-only exe folder).</summary>
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ValheimSync", "update.log");

    /// <summary>
    /// Append a timestamped line to the update log. Best-effort and self-capping — it must
    /// never throw during startup. Public so the launcher can record why it skipped the check
    /// (e.g. another instance already running in the tray).
    /// </summary>
    public static void LogDiagnostic(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 256 * 1024)
                File.Delete(LogPath); // simple rotation so it can't grow forever
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { /* logging must never break startup */ }
    }

    private static bool StartsWithMzHeader(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
        }
        catch { return false; }
    }

    private static async Task<(Version? version, string? assetUrl)> GetLatestAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ValheimSync", CurrentVersion.ToString()));
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var json = await http.GetStringAsync(
            $"https://api.github.com/repos/{Repo}/releases/latest", ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var version = ParseVersion(root.TryGetProperty("tag_name", out var t) ? t.GetString() : null);

        string? url = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                {
                    url = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                    break;
                }
            }
        }
        return (version, url);
    }

    private static async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ValheimSync", CurrentVersion.ToString()));

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await src.CopyToAsync(dst, ct);
    }

    /// <summary>Parse a release tag like "v1.0.1" or "1.0.1" into a comparable version.</summary>
    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(tag, out var v) ? Normalize(v) : null;
    }

    /// <summary>Pad missing components to 0 so "1.0.1" and "1.0.1.0" compare equal.</summary>
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static void CleanupLeftovers(string exePath)
    {
        TryDelete(exePath + ".old");
        TryDelete(exePath + ".new");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* still locked / in use — will be retried next launch */ }
    }
}
