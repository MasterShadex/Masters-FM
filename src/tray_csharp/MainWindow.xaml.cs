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
using System.Windows.Input;
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
        // INTERRUPT #3 STEP 2: expose ViewModel as Window DataContext so
        // TaskbarIcon.LeftClickCommand="{Binding ShowMenuCommand}" resolves.
        // ContextMenu.DataContext is still explicitly set in OnLoaded because
        // ContextMenu popups do not inherit Window.DataContext from the visual tree.
        DataContext = trayMenuViewModel;
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

        // Stage 7.12 Batch A STEP 5: wire left-click -> ShowMenu delegate.
        // PlacementMode.Mouse is unreliable for a hidden background window —
        // WPF may calculate the position relative to the wrong monitor.
        // AbsolutePoint + Win32 cursor position opens the menu on whichever
        // monitor the cursor (and tray icon) is on.
        _trayMenuViewModel.OpenContextMenu = () =>
        {
            if (NotifyIcon.ContextMenu is { } cm)
            {
                var pos = System.Windows.Forms.Cursor.Position;
                cm.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
                cm.HorizontalOffset = pos.X;
                cm.VerticalOffset   = pos.Y;
                cm.IsOpen = true;
            }
        };

        // Stage 7.8C: wire OBS toggle balloon-tip delegate.
        _trayMenuViewModel.ShowToast = (title, message) =>
            NotifyIcon.ShowNotification(title, message, H.NotifyIcon.Core.NotificationIcon.Info);

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

    // -------------------------------------------------------------------------
    // Keyboard: Escape -- no-op for hidden tray host (OnClosing guard blocks close)
    // -------------------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
        base.OnKeyDown(e);
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
