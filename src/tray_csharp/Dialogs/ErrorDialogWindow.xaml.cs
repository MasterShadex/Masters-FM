// Stage 7.7 Surface 11 code-behind.

using System.Windows;

namespace MastersFM.Tray.Dialogs;

public partial class ErrorDialogWindow : Window
{
    public ErrorDialogWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // INTERRUPT #3 STEP 3 (Issue 5): enable drag on WindowStyle=None window.
    private void OnTitleBarDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }
}
