using System.Diagnostics;

namespace ValheimSync.Core.Sync;

public static class ValheimProcess
{
    /// <summary>Valheim's Steam app id (store.steampowered.com/app/892970).</summary>
    public const string SteamAppId = "892970";

    /// <summary>
    /// True while the game is running. We never download over the live save
    /// folder, and we wait for the debounce window before uploading, because
    /// Valheim writes the .db in place.
    /// </summary>
    public static bool IsRunning() =>
        Process.GetProcessesByName("valheim").Length > 0;

    /// <summary>
    /// Launches Valheim through Steam via the steam://run/&lt;appid&gt; URI.
    /// This works regardless of where the game is installed, as long as Steam
    /// is present. Throws if the shell can't start the URI (e.g. Steam missing).
    /// </summary>
    public static void Launch() =>
        Process.Start(new ProcessStartInfo($"steam://run/{SteamAppId}")
        {
            UseShellExecute = true
        });

    /// <summary>
    /// Polls until the game process appears (Steam can take a while to launch it),
    /// returning true once it's up. Returns false if it never came up within
    /// <paramref name="startupTimeout"/>. We poll by process name because the game is
    /// launched indirectly through Steam, so there is no child <see cref="Process"/>
    /// handle to await.
    /// </summary>
    public static async Task<bool> WaitForStartAsync(
        TimeSpan startupTimeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + startupTimeout;
        while (!IsRunning())
        {
            if (DateTime.UtcNow > deadline) return false; // never started
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        return true;
    }

    /// <summary>Waits until the (already-running) game process exits.</summary>
    public static async Task WaitUntilExitAsync(CancellationToken ct = default)
    {
        while (IsRunning())
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
    }
}
