// Stage 7.7B STEP 4: AudioDeviceWindow code-behind.
// Stage 7.12 Batch A rev13:
//   - Both WASAPI and MME ListBoxes use SelectedItem Mode=OneWay so WPF
//     never pushes selection state back to the ViewModel.
//   - User clicks are forwarded via the shared OnDeviceSelectionChanged
//     handler; programmatic binding updates (from SelectedDevice changing)
//     are filtered by the device==vm.SelectedDevice guard.
//   - Toast fires from OnDeviceSelectionChanged (user clicks) and OnResetClick.

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
        // Guard against '{DependencyProperty.UnsetValue}' for Foreground during
        // AppDialogStyle application -- see WelcomeWindow.xaml.cs for explanation.
        SetValue(ForegroundProperty, SystemColors.WindowTextBrush);

        InitializeComponent();
    }

    // -------------------------------------------------------------------------
    // Template parts (AppDialogStyle PART_ wiring)
    // -------------------------------------------------------------------------

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (GetTemplateChild("PART_TitleBar") is FrameworkElement titleBar)
            titleBar.MouseLeftButtonDown += OnTitleBarDrag;

        if (GetTemplateChild("PART_CloseButton") is Button closeBtn)
            closeBtn.Click += (_, _) => Close();
    }

    // -------------------------------------------------------------------------
    // Shared SelectionChanged handler for WASAPI and MME ListBoxes.
    // Mode=OneWay means WPF only pushes SelectedDevice → ListBox, never back.
    // This handler fires for BOTH binding-driven changes and user clicks;
    // the guard "device == vm.SelectedDevice" discards binding-driven ones
    // so we only call SelectDevice() on genuine user clicks.
    // -------------------------------------------------------------------------

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is not AudioDeviceInfo device) return;
        var vm = DataContext as AudioDeviceViewModel;
        if (vm == null || vm.IsLoading) return;

        // If device already matches the ViewModel's selection, this change was
        // triggered by the OneWay binding updating the ListBox — not a user click.
        if (device == vm.SelectedDevice) return;

        vm.SelectDevice(device);
        ShowToast();
    }

    // -------------------------------------------------------------------------
    // Reset: reverts to system default via CancelCommand.
    // Always shows the toast so the user sees confirmation.
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
    // Auto-persist toast: fades in the ToastBanner, auto-dismisses after 3 s
    // -------------------------------------------------------------------------

    private void ShowToast()
    {
        if (ToastBanner == null) return;

        _toastTimer?.Stop();

        var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150)));
        ToastBanner.BeginAnimation(OpacityProperty, fadeIn);

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(300)));
            ToastBanner.BeginAnimation(OpacityProperty, fadeOut);
        };
        _toastTimer.Start();
    }

    // -------------------------------------------------------------------------
    // Legacy handlers preserved for compatibility
    // -------------------------------------------------------------------------

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    // -------------------------------------------------------------------------
    // Keyboard: Escape closes
    // -------------------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
        base.OnKeyDown(e);
    }
}
