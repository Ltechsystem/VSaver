using Avalonia;
using Avalonia.Controls;
using ValheimSync.App.ViewModels;

namespace ValheimSync.App.Views;

public partial class MainWindow : Window
{
    // When false, clicking the window's X hides the window instead of closing it,
    // so the app keeps syncing in the background from the system tray. The tray's
    // "Quit" flips this true (via AllowRealClose) before shutting the app down.
    private bool _allowRealClose;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;

        // Tell the view model whenever we're hidden (background/tray) vs visible, so its
        // idle watchdog knows when the app is only running in the background.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && DataContext is MainWindowViewModel vm)
                vm.IsInBackground = !(bool)e.NewValue!;
        };
    }

    /// <summary>Called by the tray "Quit" action so the next close actually closes.</summary>
    public void AllowRealClose() => _allowRealClose = true;

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowRealClose) return; // real shutdown in progress — let it close
        e.Cancel = true;            // otherwise: hide to tray, keep running
        Hide();
    }
}
