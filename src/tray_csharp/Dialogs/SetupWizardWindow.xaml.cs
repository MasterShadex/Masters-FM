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

    // INTERRUPT #3 STEP 3 (Issue 5): enable drag on WindowStyle=None window.
    private void OnTitleBarDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }
}
