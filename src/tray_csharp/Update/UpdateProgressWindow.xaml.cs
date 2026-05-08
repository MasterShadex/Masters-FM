// Stage 7.2: UpdateProgressWindow code-behind. Pragmatic MVVM: the
// ViewModel does the work; this code-behind only wires the DataContext
// + handles the Closing event guard.

using System.ComponentModel;
using System.Windows;

namespace MastersFM.Tray.Update;

public partial class UpdateProgressWindow : Window
{
    private readonly UpdateCheckViewModel _viewModel;

    public UpdateProgressWindow(UpdateCheckViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

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
}
