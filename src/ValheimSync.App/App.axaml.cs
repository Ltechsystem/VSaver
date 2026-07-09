using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ValheimSync.App.ViewModels;
using ValheimSync.App.Views;

namespace ValheimSync.App;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the window hides it to the tray instead of exiting, so we must
            // shut down explicitly (from the tray "Quit"), not when the last window closes.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;

            SetupTrayIcon(desktop, window);

            // A second launch of the exe signals us instead of opening a duplicate — bring
            // the window back to the foreground (it may have been hidden in the tray).
            Program.ShowRequested = () => Dispatcher.UIThread.Post(() => ShowWindow(window));

            // The idle watchdog (Valheim gone 15 min + running only in background) asks
            // the app to finalize and exit; route that through the normal shutdown path.
            vm.ExitRequested += () =>
            {
                window.AllowRealClose();
                desktop.Shutdown();
            };

            // Shutdown cleanup (release held locks, upload final save) is async and does
            // network I/O. The framework does NOT await event handlers, so we cancel the
            // first shutdown, run cleanup to completion, then let the real shutdown through.
            desktop.ShutdownRequested += async (_, e) =>
            {
                if (_shutdownCleanupDone) return; // second pass — allow the real exit
                e.Cancel = true;
                try { await vm.ShutdownAsync(); }
                finally
                {
                    _shutdownCleanupDone = true;
                    window.AllowRealClose();
                    desktop.Shutdown();
                }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private bool _shutdownCleanupDone;

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        var trayIcon = new TrayIcon
        {
            ToolTipText = "VSaver — syncing in the background",
            // Reuse the window's already-loaded icon so this doesn't depend on the
            // assembly name (avares:// URIs are keyed by assembly, which is now "VSaver").
            Icon = window.Icon,
            IsVisible = true,
        };

        var open = new NativeMenuItem("Open VSaver");
        open.Click += (_, _) => ShowWindow(window);

        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) =>
        {
            window.AllowRealClose(); // let the pending close actually close
            desktop.Shutdown();      // OnExplicitShutdown → fires ShutdownRequested → disposes engine
        };

        var menu = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);
        trayIcon.Menu = menu;

        // Left-click the tray icon also reopens the window.
        trayIcon.Clicked += (_, _) => ShowWindow(window);

        // Register with the app so Avalonia keeps it alive and disposes it on exit.
        TrayIcon.SetIcons(this, new TrayIcons { trayIcon });
    }

    private static void ShowWindow(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
