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
}
