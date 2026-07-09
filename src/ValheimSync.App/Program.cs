using System.Diagnostics;
using System.Threading;
using Avalonia;
using ValheimSync.Core.Update;

namespace ValheimSync.App;

internal static class Program
{
    // Single-instance coordination. The mutex marks "an instance is running"; the event
    // lets a second launch tell the running one to surface its window.
    private const string MutexName = "VSaver-single-instance";
    private const string ShowEventName = "VSaver-show-window";

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;

    /// <summary>
    /// Set by <see cref="App"/> once the window exists. The single-instance listener calls
    /// this when a second launch asks the running app to come to the foreground.
    /// </summary>
    public static Action? ShowRequested;

    [STAThread]
    public static void Main(string[] args)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool isPrimary);
        if (!isPrimary)
        {
            // Another copy is already running (possibly hidden in the tray). Ask it to show
            // its window, then exit so we never open a duplicate.
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var ev))
            {
                try { ev!.Set(); } catch { /* ignore */ }
                ev?.Dispose();
            }
            return;
        }

        // We're the primary instance. Publish the "show window" signal and start listening
        // for it before anything slow (like the update check) so second launches are heard.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        StartShowListener();

        // Silent auto-update before the UI starts. If a newer release is applied we relaunch
        // and exit — releasing the single-instance lock first so the fresh copy can take it.
        try
        {
            if (Updater.TryUpdateAsync(line => Trace.WriteLine(line),
                    beforeRelaunch: ReleaseSingleInstance).GetAwaiter().GetResult())
                return;
        }
        catch { /* offline / read-only folder / etc. — just start normally */ }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        ReleaseSingleInstance();
    }

    private static void StartShowListener()
    {
        var thread = new Thread(() =>
        {
            try
            {
                while (_showEvent is not null && _showEvent.WaitOne())
                    ShowRequested?.Invoke();
            }
            catch { /* event disposed during shutdown/relaunch — stop listening */ }
        })
        {
            IsBackground = true,
            Name = "single-instance-listener"
        };
        thread.Start();
    }

    private static void ReleaseSingleInstance()
    {
        try { _showEvent?.Dispose(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        _showEvent = null;
        _mutex = null;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
