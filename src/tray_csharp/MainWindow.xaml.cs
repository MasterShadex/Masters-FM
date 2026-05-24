// Stage 7.6 origin / Stage 7.23 REVISED: MainWindow code-behind. Tray context
// menu uses the solid TrayMenuBackgroundBrush (#1A1A1A) from Theme/Colors.xaml
// unconditionally. Stage 7.22's acrylic-on-Win11 gate was removed in Stage 7.23
// after operator feedback flagged "weird edges" caused by acrylic haze. The
// Wpf.Ui WindowBackdrop API stays available via the NuGet package but is no
// longer applied to the tray menu locally.
//
// Responsibilities:
//   - Bind tray IconSource via pack URI (XAML).
//   - Set ContextMenu.DataContext = TrayMenuViewModel (popup not in visual tree).
//   - Wire ShowMenu and ShowToast delegates.
//   - Block accidental window-close.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

        // Stage 7.23 REMOVED: previously this method applied an Acrylic backdrop
        // to the tray ContextMenu on Win11 22H2+ via Wpf.Ui's WindowBackdrop API.
        // Operator feedback after Stage 7.22 flagged the acrylic haze as "weird
        // edges" against busy desktop backgrounds; Stage 7.23 switches to a solid
        // TrayMenuBackgroundBrush (#1A1A1A) from Theme/Colors.xaml unconditionally.
        // The ContextMenu now reads the solid brush from AppContextMenuStyle and
        // no runtime override is performed. If a future stage wants acrylic back
        // the original wiring is in commit a7d53dd (Stage 7.22 SE5 FIX HEAD) and
        // earlier; restore the Acrylic gate here + revert ContextMenu.xaml's
        // AppContextMenuStyle Background to a Transparent/Surface1 fallback.

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

                // Stage 7.12 Batch B Phase G (marquee timing fix):
                // Operator: "It just keeps going and going and it's unreadable
                // going so fast."  Previous animation was a single DoubleAnimation
                // with RepeatBehavior.Forever — slide, snap, slide, snap, no
                // pauses.  Replaced with a 4-stop key-framed animation:
                //   t=0       hold @ start  (text visible at left)
                //   t=2s      hold @ start  (end of 2-second read pause)
                //   t=2+slide reach end     (linear slide left)
                //   t=2+slide+2  hold @ end (2-second read pause at end)
                // RepeatBehavior.Forever wraps t=end → t=0 instantly (snap-back
                // to the start without animating), then the next cycle's
                // initial hold gives the second 2-second start-pause.
                const double pauseSeconds = 2.0;
                const double pxPerSec     = 40;   // slide speed
                var distance      = textWidth - viewportWidth;
                var slideDuration = TimeSpan.FromSeconds(distance / pxPerSec);
                var pause         = TimeSpan.FromSeconds(pauseSeconds);

                var anim = new DoubleAnimationUsingKeyFrames
                {
                    RepeatBehavior = RepeatBehavior.Forever
                };
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(0,
                    KeyTime.FromTimeSpan(TimeSpan.Zero)));
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(0,
                    KeyTime.FromTimeSpan(pause)));
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(-distance,
                    KeyTime.FromTimeSpan(pause + slideDuration)));
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(-distance,
                    KeyTime.FromTimeSpan(pause + slideDuration + pause)));

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
