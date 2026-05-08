// Stage 7.3: MainWindow code-behind. ILogger now constructor-injected via DI.
// Pragmatic MVVM (no viewmodel for an empty skeleton; per
// V14_S7_REPLAN_WPF_LOCK.md section 5).
//
// Responsibilities:
//   - Bind tray IconSource via pack URI (XAML).
//   - Handle the Quit menu item click.
//   - Block accidental window-close.

using System.ComponentModel;
using System.Windows;
using MastersFM.Tray.Services;

namespace MastersFM.Tray;

public partial class MainWindow : Window
{
    private readonly ILogger _logger;
    private bool _allowClose;

    public MainWindow(ILogger logger)
    {
        _logger = logger;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // IconSource is set in MainWindow.xaml via pack URI; runtime fallback
        // not required. The brand icon (assets\MastersFM.ico) is embedded as
        // a Resource via csproj.
        _logger.Log("MainWindow.Loaded: TaskbarIcon initialized; tray visible", "Tray");
    }

    private void OnQuitClicked(object sender, RoutedEventArgs e)
    {
        _logger.Log("Quit clicked; calling Application.Current.Shutdown()", "Tray");
        _allowClose = true;
        // ShutdownMode=OnExplicitShutdown means Application.Shutdown() exits
        // the app WITHOUT firing Closing on each window. To dispose the
        // TaskbarIcon cleanly (hide it, free its handle), we close the
        // hidden host window explicitly first; that fires OnClosing with
        // _allowClose=true and runs the disposal path. Then Shutdown.
        Close();
        Application.Current.Shutdown(0);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            _logger.Log("MainWindow.OnClosing: suppressed (no Quit source); tray stays alive", "Tray");
            return;
        }

        try
        {
            NotifyIcon?.Dispose();
            _logger.Log("MainWindow.OnClosing: TaskbarIcon disposed", "Tray");
        }
        catch (Exception ex)
        {
            _logger.LogErr("TaskbarIcon disposal", ex, "Tray");
        }

        base.OnClosing(e);
    }
}
