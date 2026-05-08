// Stage 7.6 STEP 7: MainWindow code-behind. TrayMenuViewModel now constructor-
// injected; OnLoaded wires the ContextMenu DataContext. The old OnQuitClicked
// click-handler is replaced by TrayMenuViewModel.QuitAppCommand (RelayCommand).
//
// Responsibilities:
//   - Bind tray IconSource via pack URI (XAML).
//   - Set ContextMenu.DataContext = TrayMenuViewModel (popup not in visual tree).
//   - Allow Mica backdrop via ContextMenuExtensions.ApplyMica in STEP 11.
//   - Block accidental window-close.

using System.ComponentModel;
using System.Windows;
using MastersFM.Tray.Services;
using MastersFM.Tray.ViewModels;

namespace MastersFM.Tray;

public partial class MainWindow : Window
{
    private readonly ILogger _logger;
    private readonly TrayMenuViewModel _trayMenuViewModel;
    private bool _allowClose;

    public MainWindow(ILogger logger, TrayMenuViewModel trayMenuViewModel)
    {
        _logger = logger;
        _trayMenuViewModel = trayMenuViewModel;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Wire ContextMenu DataContext — popup is not in the visual tree so it
        // cannot inherit DataContext from MainWindow automatically.
        if (NotifyIcon.ContextMenu != null)
            NotifyIcon.ContextMenu.DataContext = _trayMenuViewModel;

        // Provide clean-shutdown delegate so Quit/Restart commands close the
        // host window (disposing TaskbarIcon) before Application.Shutdown.
        _trayMenuViewModel.CleanShutdown = () =>
        {
            _allowClose = true;
            Close();
            Application.Current.Shutdown(0);
        };

        _logger.Log("MainWindow.Loaded: TaskbarIcon initialized; tray visible; ContextMenu DataContext wired", "Tray");
        // STEP 11: Mica backdrop applied here after TrayMenu* brush resources are added.
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
