// Stage 7.7 Surface 05 code-behind. Apply / Cancel close the window;
// VM Apply command persists to config first.

using System.Windows;
using MastersFM.Tray.ViewModels;

namespace MastersFM.Tray.Dialogs;

public partial class AudioDeviceWindow : Window
{
    public AudioDeviceWindow()
    {
        InitializeComponent();
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is AudioDeviceViewModel vm)
        {
            vm.ApplyCommand.Execute(null);
        }
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is AudioDeviceViewModel vm)
        {
            vm.CancelCommand.Execute(null);
        }
        Close();
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is AudioDeviceViewModel vm)
        {
            vm.CancelCommand.Execute(null);
        }
        Close();
    }

    // INTERRUPT #3 STEP 3 (Issue 5): enable drag on WindowStyle=None window.
    private void OnTitleBarDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }
}
