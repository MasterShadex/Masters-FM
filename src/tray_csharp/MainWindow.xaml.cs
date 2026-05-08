// Stage 7.1B: MainWindow code-behind. Pragmatic MVVM (no viewmodel for an
// empty skeleton; per V14_S7_REPLAN_WPF_LOCK.md section 5).
//
// Responsibilities:
//   - Set TaskbarIcon.IconSource at runtime by extracting the Win32
//     ApplicationIcon embedded in the exe (matches Stage 7.1 pattern via
//     Icon.ExtractAssociatedIcon, adapted for WPF's ImageSource).
//   - Handle the Quit menu item click.
//   - Block accidental window-close (the hidden window must NOT shut down
//     the app on a stray close; only Application.Current.Shutdown() does).

using System.ComponentModel;
using System.Windows;

namespace MastersFM.Tray;

public partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // IconSource is set in MainWindow.xaml via pack URI; runtime fallback
        // not required. The brand icon (assets\MastersFM.ico) is embedded as
        // a Resource via csproj.
        Logger.Log("MainWindow.Loaded: TaskbarIcon initialized; tray visible");
    }

    private void OnQuitClicked(object sender, RoutedEventArgs e)
    {
        Logger.Log("Quit clicked; closing MainWindow then Application.Shutdown");
        _allowClose = true;
        // ShutdownMode=OnExplicitShutdown means Application.Shutdown() exits
        // the app WITHOUT firing Closing on each window. To dispose the
        // TaskbarIcon cleanly (hide it, free its handle), we close the
        // hidden host window explicitly first; that fires OnClosing with
        // _allowClose=true and runs the disposal path. Then Shutdown.
        Close();
        Application.Current.Shutdown(0);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // ShutdownMode=OnExplicitShutdown means Application.Shutdown() exits
        // the app regardless of window state, so we don't need to "force"
        // the close path. We only need to dispose the TaskbarIcon cleanly.
        // _allowClose tracks whether OUR Quit handler initiated the close;
        // if some stray Close happens (rare for a Visibility=Hidden chromeless
        // window), suppress it so the tray stays alive until explicit Quit.
        if (!_allowClose)
        {
            e.Cancel = true;
            Logger.Log("MainWindow.OnClosing: suppressed (no Quit source); tray stays alive");
            return;
        }

        try
        {
            NotifyIcon?.Dispose();
            Logger.Log("MainWindow.OnClosing: TaskbarIcon disposed");
        }
        catch (Exception ex)
        {
            Logger.LogErr("TaskbarIcon disposal", ex);
        }

        base.OnClosing(e);
    }
}
