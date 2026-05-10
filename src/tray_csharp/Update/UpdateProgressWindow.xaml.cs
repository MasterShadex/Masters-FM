// Stage 7.7B-FIX STEP 4: UpdateProgressWindow code-behind (visual rebuild).
// AppDialogStyle provides custom chrome (3px accent bar, title bar, close button).
// OnApplyTemplate wires the PART_TitleBar drag region and PART_CloseButton close action.
// DataContext is set in constructor (UpdateCheckViewModel injected by DI).
// OnClosing guard prevents accidental close during Downloading / Installing states.

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MastersFM.Tray.Update;

public partial class UpdateProgressWindow : Window
{
    private readonly UpdateCheckViewModel _viewModel;

    public UpdateProgressWindow(UpdateCheckViewModel viewModel)
    {
        _viewModel = viewModel;
        // Guard against '{DependencyProperty.UnsetValue}' for Foreground during
        // AppDialogStyle application -- see WelcomeWindow.xaml.cs for explanation.
        SetValue(ForegroundProperty, SystemColors.WindowTextBrush);
        InitializeComponent();
        DataContext = viewModel;
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

        // PART_CloseButton: close the window (respects OnClosing guard).
        if (GetTemplateChild("PART_CloseButton") is Button closeBtn)
        {
            closeBtn.Click += (_, _) => Close();
        }

        // PART_MinimizeButton stays Collapsed (default in AppDialogStyle).
    }

    // -------------------------------------------------------------------------
    // Closing guard: block close during active Downloading / Installing
    // -------------------------------------------------------------------------

    protected override void OnClosing(CancelEventArgs e)
    {
        // If we're mid-download/install, prevent close to avoid leaving the
        // operation in a half-state. The user can press Cancel first.
        var st = _viewModel.CurrentState;
        if (st == UpdateState.Downloading || st == UpdateState.Installing)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    // -------------------------------------------------------------------------
    // Action button handlers
    // -------------------------------------------------------------------------

    // "Close" button click -- closes the window (respects OnClosing guard).
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // -------------------------------------------------------------------------
    // Keyboard: Escape closes (respects OnClosing guard during Downloading/Installing)
    // -------------------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
        base.OnKeyDown(e);
    }
}
