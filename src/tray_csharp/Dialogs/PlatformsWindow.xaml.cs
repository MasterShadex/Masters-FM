// Stage 7.7 Surface 06 code-behind.

using System.Windows;

namespace MastersFM.Tray.Dialogs;

public partial class PlatformsWindow : Window
{
    public PlatformsWindow()
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
