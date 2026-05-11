// Stage 7.7B STEP 4: AudioDeviceWindow code-behind.
// Visual rebuild only; device enumeration logic unchanged (S4.5).
// OnApplyTemplate wires AppDialogStyle PART_ elements.
// OnResetClick reverts SelectedDevice to original-on-open value.
// ShowToast() displays the auto-persist "Audio source updated." banner
// for 3 seconds then fades it out.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        // Subscribe to SelectedDevice changes so we can show the toast.
        DataContextChanged += OnDataContextChanged;
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
    // DataContext wiring: subscribe to SelectedDevice property for toast trigger
    // -------------------------------------------------------------------------

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is AudioDeviceViewModel oldVm)
            oldVm.PropertyChanged -= OnVmPropertyChanged;

        if (e.NewValue is AudioDeviceViewModel newVm)
            newVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioDeviceViewModel.SelectedDevice))
        {
            // Suppress toast during initial device enumeration (IsLoading = true)
            // so the window doesn't announce "Audio source updated." on open.
            var vm = sender as AudioDeviceViewModel;
            if (vm?.IsLoading != true)
                ShowToast();
        }
    }

    // -------------------------------------------------------------------------
    // Reset: reverts selection to original-on-open value via CancelCommand
    // -------------------------------------------------------------------------

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is AudioDeviceViewModel vm)
            vm.CancelCommand.Execute(null);
    }

    // -------------------------------------------------------------------------
    // Auto-persist toast: fades in the ToastBanner, auto-dismisses after 3 s
    // -------------------------------------------------------------------------

    private void ShowToast()
    {
        if (ToastBanner == null) return;

        // Cancel any running dismiss timer.
        _toastTimer?.Stop();

        // Fade in (150 ms).
        var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150)));
        ToastBanner.BeginAnimation(OpacityProperty, fadeIn);

        // Dismiss after 3 s.
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
