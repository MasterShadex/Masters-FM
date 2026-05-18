// Stage 7.7B STEP 4: AudioDeviceWindow code-behind.
// Stage 7.12 Batch A rev19:
//   Canonical WPF MVVM cross-tab selection.  Selection visual is driven by
//   ListBoxItem.IsSelected, which is TwoWay bound (in DeviceListItemStyle)
//   to AudioDeviceInfo.IsActive.  The framework's Selector handles all the
//   reconnect / regeneration plumbing.  Code-behind only needs to:
//     - Notify the ViewModel of user clicks (so config is persisted).
//     - Show the toast on user selection / Reset.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MastersFM.Tray.ViewModels;

namespace MastersFM.Tray.Dialogs;

public partial class AudioDeviceWindow : Window
{
    private DispatcherTimer? _toastTimer;

    public AudioDeviceWindow()
    {
        SetValue(ForegroundProperty, SystemColors.WindowTextBrush);
        InitializeComponent();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (GetTemplateChild("PART_TitleBar") is FrameworkElement titleBar)
            titleBar.MouseLeftButtonDown += OnTitleBarDrag;

        if (GetTemplateChild("PART_CloseButton") is Button closeBtn)
            closeBtn.Click += (_, _) => Close();
    }

    // -------------------------------------------------------------------------
    // User click → ViewModel.SelectDevice (which performs the cross-tab sweep
    // of IsActive). The two-way IsSelected binding handles all visual updates.
    // The guard discards the SelectionChanged events that originate from the
    // ViewModel's IsActive sweep itself.
    // -------------------------------------------------------------------------

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is not AudioDeviceInfo device) return;
        var vm = DataContext as AudioDeviceViewModel;
        if (vm == null || vm.IsLoading) return;
        if (device == vm.SelectedDevice) return;   // programmatic, not a user click

        vm.SelectDevice(device);
        ShowToast();
    }

    // -------------------------------------------------------------------------
    // Reset
    // -------------------------------------------------------------------------

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is AudioDeviceViewModel vm)
        {
            vm.CancelCommand.Execute(null);
            ShowToast();
        }
    }

    // -------------------------------------------------------------------------
    // Toast
    // -------------------------------------------------------------------------

    private void ShowToast()
    {
        if (ToastBanner == null) return;

        _toastTimer?.Stop();

        // Stage 7.12 Batch C (Issue 3 defect B): make the banner participate
        // in layout (Visibility=Visible) BEFORE animating opacity in, so the
        // user sees it. Default state is Collapsed so the 45px row doesn't
        // reserve space while the toast is idle.
        ToastBanner.Visibility = Visibility.Visible;

        var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150)));
        ToastBanner.BeginAnimation(OpacityProperty, fadeIn);

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(300)));
            // After the fade-out completes, collapse the banner so the row
            // it occupies vacates layout (footer reclaims the 45 px). The
            // Completed handler fires after the animation reaches 0 opacity.
            fadeOut.Completed += (_, _) =>
            {
                ToastBanner.Visibility = Visibility.Collapsed;
            };
            ToastBanner.BeginAnimation(OpacityProperty, fadeOut);
        };
        _toastTimer.Start();
    }

    // -------------------------------------------------------------------------
    // Chrome
    // -------------------------------------------------------------------------

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
        base.OnKeyDown(e);
    }
}
