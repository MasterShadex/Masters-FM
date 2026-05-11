// Stage 7.7B STEP 4: AudioDeviceWindow code-behind.
// Stage 7.12 Batch A rev16:
//   Selection state is managed ENTIRELY from code-behind — no SelectedItem
//   bindings exist on either ListBox.  This bypasses every WPF binding edge
//   case (TwoWay push-back, OneWay reconnect timing, IsSynchronizedWith-
//   CurrentItem auto-select, ContentPresenter detach behaviour).
//
//   Synchronization triggers, each of which sets BOTH ListBoxes' SelectedItem
//   directly from the ViewModel's single SelectedDevice (filtered by Backend):
//     - DataContext attaches (initial load)
//     - PropertyChanged("SelectedDevice") fires (Reset, RefreshAsync restore)
//     - A ListBox raises Loaded (tab re-enters the visual tree)
//     - User clicks an item (OnDeviceSelectionChanged forwards to vm and
//       explicitly clears the *other* ListBox)

using System.ComponentModel;
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
    // True while we set SelectedItem on a ListBox programmatically; the
    // resulting SelectionChanged event must be ignored so it isn't treated
    // as a user click and routed back into the ViewModel.
    private bool _syncing;

    public AudioDeviceWindow()
    {
        SetValue(ForegroundProperty, SystemColors.WindowTextBrush);

        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
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
    // ViewModel subscription
    // -------------------------------------------------------------------------

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is AudioDeviceViewModel oldVm)
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        if (e.NewValue is AudioDeviceViewModel newVm)
        {
            newVm.PropertyChanged += OnVmPropertyChanged;
            SyncListBoxes(newVm);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AudioDeviceViewModel.SelectedDevice)) return;
        if (sender is AudioDeviceViewModel vm)
            SyncListBoxes(vm);
    }

    // Set both ListBoxes' SelectedItem from the single ViewModel selection.
    // The ListBox whose collection contains the active device gets it; the
    // other gets null (an explicit null deselects everything cleanly).
    private void SyncListBoxes(AudioDeviceViewModel vm)
    {
        var device = vm.SelectedDevice;
        _syncing = true;
        try
        {
            if (WasapiListBox != null)
                WasapiListBox.SelectedItem = device?.Backend == "WASAPI" ? device : null;
            if (MmeListBox != null)
                MmeListBox.SelectedItem = device?.Backend == "MME" ? device : null;
        }
        finally
        {
            _syncing = false;
        }
    }

    // -------------------------------------------------------------------------
    // Loaded handlers: fire each time a tab re-enters the visual tree.
    // -------------------------------------------------------------------------

    private void OnWasapiListBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AudioDeviceViewModel vm) SyncListBoxes(vm);
        // Force container rebuild so DataTriggers re-evaluate against the
        // current IsActive flags. WPF caches the trigger's last visual state
        // on detached containers and does not re-evaluate on re-attach.
        if (sender is ListBox lb) lb.Items.Refresh();
    }

    private void OnMmeListBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AudioDeviceViewModel vm) SyncListBoxes(vm);
        if (sender is ListBox lb) lb.Items.Refresh();
    }

    // -------------------------------------------------------------------------
    // User clicks an item
    // -------------------------------------------------------------------------

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;                       // programmatic update, not a click
        if (e.AddedItems.Count == 0) return;        // deselection
        if (e.AddedItems[0] is not AudioDeviceInfo device) return;
        var vm = DataContext as AudioDeviceViewModel;
        if (vm == null || vm.IsLoading) return;
        if (device == vm.SelectedDevice) return;    // already the active device

        vm.SelectDevice(device);
        // SyncListBoxes (via PropertyChanged) clears the other tab.
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
