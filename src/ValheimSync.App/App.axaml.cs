using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
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

            desktop.ShutdownRequested += async (_, _) => await vm.ShutdownAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        var trayIcon = new TrayIcon
        {
            ToolTipText = "ValheimSync — syncing in the background",
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ValheimSync.App/app.ico"))),
            IsVisible = true,
        };

        var open = new NativeMenuItem("Open ValheimSync");
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
