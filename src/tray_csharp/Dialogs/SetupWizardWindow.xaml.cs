// Stage 7.7 Surface 09 code-behind. Wires VM RequestClose to Window.Close.

using System.Windows;
using MastersFM.Tray.ViewModels;

namespace MastersFM.Tray.Dialogs;

public partial class SetupWizardWindow : Window
{
    public SetupWizardWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is SetupWizardViewModel vm)
        {
            vm.RequestClose = () => Close();
        }
    }
}
