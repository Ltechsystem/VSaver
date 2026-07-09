using System.Diagnostics;
using Avalonia;
using ValheimSync.Core.Update;

namespace ValheimSync.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Silent auto-update before the UI starts. If a newer release is published on
        // GitHub it is downloaded, swapped in, and relaunched here — in which case we
        // exit and let the fresh copy take over. Never let update trouble block launch.
        try
        {
            if (Updater.TryUpdateAsync(line => Trace.WriteLine(line)).GetAwaiter().GetResult())
                return;
        }
        catch { /* offline / read-only folder / etc. — just start normally */ }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
