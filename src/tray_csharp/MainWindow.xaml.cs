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
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

        // Stage 7.12 Batch A STEP 5 (rev2): wire left-click -> ShowMenu delegate.
        // Cursor.Position returns DEVICE pixels; ContextMenu.HorizontalOffset/
        // VerticalOffset with AbsolutePoint expect WPF LOGICAL pixels. At any
        // DPI > 100% (the common case), feeding device pixels straight in places
        // the menu off-screen / misaligned. Query the cursor's monitor for its
        // effective DPI and divide so the menu opens exactly at the cursor —
        // matching how right-click already behaves (handled internally by
        // H.NotifyIcon).
        _trayMenuViewModel.OpenContextMenu = () =>
        {
            if (NotifyIcon.ContextMenu is { } cm)
            {
                var pos = System.Windows.Forms.Cursor.Position;
                var (scaleX, scaleY) = GetDpiScaleAtPoint(pos.X, pos.Y);
                cm.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
                cm.HorizontalOffset = pos.X / scaleX;
                cm.VerticalOffset   = pos.Y / scaleY;
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

    // -------------------------------------------------------------------------
    // Now-playing header marquee (Stage 7.12 Batch A)
    // -------------------------------------------------------------------------
    // When the track title is wider than the menu, scroll it horizontally
    // (text shifts leftward; when the end has passed the left edge the
    // animation loops, snapping back to the start at the LEFT — operator's
    // explicit preference: "go from left to right and reset on the left
    // again, not right to left backwards").

    private void OnNowPlayingMarqueeLoaded(object sender, RoutedEventArgs e)
        => UpdateNowPlayingMarquee();

    private void OnNowPlayingMarqueeSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateNowPlayingMarquee();

    private void UpdateNowPlayingMarquee()
    {
        // Defer to after the next layout pass so ActualWidth values are valid.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (NowPlayingMarquee == null
                    || NowPlayingMarqueeViewport == null
                    || NowPlayingMarqueeTransform == null) return;

                // Always clear any running animation before re-evaluating.
                NowPlayingMarqueeTransform.BeginAnimation(TranslateTransform.XProperty, null);
                NowPlayingMarqueeTransform.X = 0;

                var textWidth     = NowPlayingMarquee.ActualWidth;
                var viewportWidth = NowPlayingMarqueeViewport.ActualWidth;
                if (textWidth <= viewportWidth || viewportWidth <= 0) return;

                const double gap      = 40;   // pause-equivalent off the right edge
                const double pxPerSec = 40;   // scrolling speed
                var distance = textWidth - viewportWidth + gap;
                var duration = TimeSpan.FromSeconds(distance / pxPerSec);

                var anim = new DoubleAnimation
                {
                    From           = 0,
                    To             = -distance,
                    Duration       = duration,
                    RepeatBehavior = RepeatBehavior.Forever
                };
                NowPlayingMarqueeTransform.BeginAnimation(TranslateTransform.XProperty, anim);
            }
            catch (Exception ex)
            {
                _logger.LogErr("now-playing marquee setup", ex, "Tray");
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // -------------------------------------------------------------------------
    // Per-monitor DPI helper (Stage 7.12 Batch A STEP 5 rev2)
    // -------------------------------------------------------------------------
    // Returns the effective DPI scale factor (1.0 == 96 DPI / 100% scaling) of
    // the monitor that contains the given device-pixel point. Used to convert
    // Cursor.Position into WPF logical pixels for PlacementMode.AbsolutePoint.

    private static (double scaleX, double scaleY) GetDpiScaleAtPoint(int deviceX, int deviceY)
    {
        try
        {
            var pt = new POINT { X = deviceX, Y = deviceY };
            var hmon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            if (hmon == IntPtr.Zero) return (1.0, 1.0);
            if (GetDpiForMonitor(hmon, MonitorDpiType.Effective, out var dx, out var dy) != 0)
                return (1.0, 1.0);
            return (dx / 96.0, dy / 96.0);
        }
        catch
        {
            return (1.0, 1.0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private enum MonitorDpiType { Effective = 0, Angular = 1, Raw = 2 }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);
}
