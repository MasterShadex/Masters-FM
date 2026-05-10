// Stage 7.7B-FIX STEP 4: ErrorDialogWindow code-behind (visual rebuild).
// AppDialogStyle provides custom chrome (3px accent bar, title bar, close button).
// OnApplyTemplate wires the PART_TitleBar drag region and PART_CloseButton close action.
// DataContext is injected by DialogService (ErrorDialogViewModel singleton).

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MastersFM.Tray.Dialogs;

public partial class ErrorDialogWindow : Window
{
    public ErrorDialogWindow()
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

        // PART_TitleBar: enable drag on the WindowStyle=None chrome.
        if (GetTemplateChild("PART_TitleBar") is FrameworkElement titleBar)
        {
            titleBar.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                    DragMove();
            };
        }

        // PART_CloseButton: close the dialog.
        if (GetTemplateChild("PART_CloseButton") is Button closeBtn)
        {
            closeBtn.Click += (_, _) => Close();
        }

        // PART_MinimizeButton stays Collapsed (default in AppDialogStyle).
    }

    // -------------------------------------------------------------------------
    // Action button handlers
    // -------------------------------------------------------------------------

    // "OK" button click -- closes the dialog.
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
