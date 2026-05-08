// Stage 7.6 STEP 11: MainWindow code-behind. Adds Mica backdrop gate (Win11 22H2+)
// via ContextMenuExtensions.ApplyMica and WindowBackdrop.IsSupported.
//
// Responsibilities:
//   - Bind tray IconSource via pack URI (XAML).
//   - Set ContextMenu.DataContext = TrayMenuViewModel (popup not in visual tree).
//   - Branch A (Win11 22H2+): apply Mica backdrop; set Background=Transparent.
//   - Branch B (Win10 / older): keep TrayMenuBackgroundBrush from XAML.
//   - Block accidental window-close.

using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MastersFM.Tray.Services;
using MastersFM.Tray.ViewModels;
using Wpf.Ui.Controls;

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

        // Stage 7.6 STEP 11: Q3=C backdrop gate.
        // ContextMenuExtensions.ApplyMica is internal in WPF-UI 4.3.0 and cannot be
        // called directly. Equivalent via the public API: WindowBackdrop.ApplyBackdrop
        // (IntPtr hwnd, WindowBackdropType) + PresentationSource.FromVisual to get the
        // popup's hwnd. WindowBackdropType.Acrylic = DWMSBT_TRANSIENTWINDOW (correct for
        // popups; Mica = DWMSBT_MAINWINDOW and is semantically wrong for a ContextMenu).
        //
        // Branch A (Win11 22H2+, build ≥ 22621): apply Acrylic via public DWM API;
        //   override Background to Transparent so the system backdrop shows through.
        // Branch B (Win10 / older Win11): TrayMenuBackgroundBrush from App.xaml stays.
        var acrylicSupported = WindowBackdrop.IsSupported(WindowBackdropType.Acrylic);
        _logger.Log($"Acrylic supported={acrylicSupported}", "Tray");
        if (acrylicSupported && NotifyIcon.ContextMenu != null)
        {
            var cm = NotifyIcon.ContextMenu;
            cm.Opened += (_, _) =>
            {
                try
                {
                    if (PresentationSource.FromVisual(cm) is HwndSource src)
                    {
                        WindowBackdrop.ApplyBackdrop(src.Handle, WindowBackdropType.Acrylic);
                        cm.Background = Brushes.Transparent;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogErr("ContextMenu ApplyBackdrop(Acrylic)", ex, "Tray");
                }
            };
        }

        _logger.Log("MainWindow.Loaded: TaskbarIcon initialized; tray visible; ContextMenu DataContext wired", "Tray");
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
