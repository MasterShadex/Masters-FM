// Stage 7.7B STEP 6: SetupWizardWindow code-behind.
// Visual rebuild. AppDialogStyle PART_ template parts wired in OnApplyTemplate.
// DataContextChanged wires VM RequestClose callback (unchanged from S7.7).

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MastersFM.Tray.ViewModels;

namespace MastersFM.Tray.Dialogs;

public partial class SetupWizardWindow : Window
{
    public SetupWizardWindow()
    {
        // Guard against '{DependencyProperty.UnsetValue}' for Foreground during
        // AppDialogStyle application -- see WelcomeWindow.xaml.cs for explanation.
        SetValue(ForegroundProperty, SystemColors.WindowTextBrush);
        InitializeComponent();
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
    // VM wiring: RequestClose callback
    // -------------------------------------------------------------------------

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is SetupWizardViewModel vm)
            vm.RequestClose = () => Close();
    }

    // -------------------------------------------------------------------------
    // Drag
    // -------------------------------------------------------------------------

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
