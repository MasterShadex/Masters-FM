// Stage 7.7: DialogService. Concrete impl of IDialogService. Resolves
// dialog windows from DI as transient (each show creates a fresh
// window instance). ViewModels are singletons (per App.xaml.cs DI
// wiring), so dialog state can persist across show/hide cycles where
// that's desired (e.g., AudioDeviceViewModel keeps its enumerated
// device list cached).
//
// All Show*Async marshal to UI thread via Application.Current.Dispatcher.
// Logs show + close at INFO level.

using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using MastersFM.Tray.Services;
using MastersFM.Tray.Update;
using MastersFM.Tray.ViewModels;

namespace MastersFM.Tray.Dialogs;

public sealed class DialogService : IDialogService
{
    private const string Component = "Dialog";

    private readonly ILogger _logger;
    private readonly IServiceProvider _services;
    // INTERRUPT #3 STEP 6: non-modal update window singleton guard.
    // Transient windows created by GetRequiredService<UpdateProgressWindow>() would
    // otherwise open multiple overlapping windows if the user clicks the menu item
    // repeatedly. We track the live instance and bring it to front on repeat clicks.
    private UpdateProgressWindow? _updateWindow;

    public DialogService(ILogger logger, IServiceProvider services)
    {
        _logger = logger;
        _services = services;
    }

    public Task ShowWelcomeAsync(bool showAboutTab = false)
    {
        return ShowOnDispatcherAsync(() =>
        {
            _logger.Log("showing Welcome (showAboutTab=" + showAboutTab + ")", Component);
            var window = _services.GetRequiredService<WelcomeWindow>();
            var vm = _services.GetRequiredService<WelcomeViewModel>();
            vm.ShowAboutTab = showAboutTab;
            window.DataContext = vm;
            window.Owner = Application.Current.MainWindow;
            PositionDialogOnPrimaryMonitor(window);
            window.ShowDialog();
            _logger.Log("Welcome closed", Component);
        });
    }

    public Task<AudioDeviceResult?> ShowAudioDeviceAsync()
    {
        return ShowOnDispatcherAsync<AudioDeviceResult?>(() =>
        {
            _logger.Log("showing AudioDevice", Component);
            var window = _services.GetRequiredService<AudioDeviceWindow>();
            var vm = _services.GetRequiredService<AudioDeviceViewModel>();
            window.DataContext = vm;
            window.Owner = Application.Current.MainWindow;
            // Trigger initial enumeration if not already loaded
            _ = vm.RefreshAsync();
            PositionDialogOnPrimaryMonitor(window);
            window.ShowDialog();
            var result = vm.PendingResult;
            _logger.Log("AudioDevice closed result=" + (result?.DisplayName ?? "(cancelled)"), Component);
            return result;
        });
    }

    public Task ShowPlatformsAsync()
    {
        return ShowOnDispatcherAsync(() =>
        {
            _logger.Log("showing Platforms", Component);
            var window = _services.GetRequiredService<PlatformsWindow>();
            var vm = _services.GetRequiredService<PlatformsViewModel>();
            vm.RefreshFromConfig();
            window.DataContext = vm;
            window.Owner = Application.Current.MainWindow;
            PositionDialogOnPrimaryMonitor(window);
            window.ShowDialog();
            _logger.Log("Platforms closed", Component);
        });
    }

    public Task<bool> ShowSetupWizardAsync()
    {
        return ShowOnDispatcherAsync<bool>(() =>
        {
            _logger.Log("showing SetupWizard", Component);
            var window = _services.GetRequiredService<SetupWizardWindow>();
            var vm = _services.GetRequiredService<SetupWizardViewModel>();
            vm.Reset();
            window.DataContext = vm;
            window.Owner = Application.Current.MainWindow;
            PositionDialogOnPrimaryMonitor(window);
            window.ShowDialog();
            var completed = vm.Completed;
            _logger.Log("SetupWizard closed completed=" + completed, Component);
            return completed;
        });
    }

    public Task ShowErrorAsync(string title, string message, Exception? ex = null)
    {
        return ShowOnDispatcherAsync(() =>
        {
            _logger.Log("showing Error title=" + title, Component);
            var window = _services.GetRequiredService<ErrorDialogWindow>();
            var vm = _services.GetRequiredService<ErrorDialogViewModel>();
            vm.Populate(title, message, ex);
            window.DataContext = vm;
            window.Owner = Application.Current.MainWindow;
            // Stage 7.8B STEP 9: SizeToContent="Height" window — position after layout via ContentRendered
            // so ActualHeight is available for vertical centring.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.ContentRendered += (_, _) => PositionDialogOnPrimaryMonitor(window);
            window.ShowDialog();
            _logger.Log("Error closed", Component);
        });
    }

    // INTERRUPT #3 STEP 6 (Issue 3): show update-progress window.
    // Non-modal (Show, not ShowDialog) so the async download/install state
    // machine can progress while the window is open.
    public Task ShowUpdateProgressAsync()
    {
        return ShowOnDispatcherAsync(() =>
        {
            if (_updateWindow != null && _updateWindow.IsVisible)
            {
                _logger.Log("UpdateProgress already visible; activating", Component);
                _updateWindow.Activate();
                return;
            }
            _logger.Log("showing UpdateProgress", Component);
            _updateWindow = _services.GetRequiredService<UpdateProgressWindow>();
            _updateWindow.Owner = Application.Current.MainWindow;
            _updateWindow.Closed += (_, _) =>
            {
                _logger.Log("UpdateProgress closed", Component);
                _updateWindow = null;
            };
            PositionDialogOnPrimaryMonitor(_updateWindow);
            _updateWindow.Show();
        });
    }

    // Stage 7.12 Batch A (post STEP 6): centre every modal dialog on the
    // PRIMARY (main) monitor regardless of where the cursor is.  Operator
    // preference: Customize Overlay already opens on the main monitor centred,
    // and all in-process dialogs (Welcome / Patch notes, Audio Source, Platform
    // Detection, Setup Wizard, Error, Update Progress) should match that.
    //
    // Screen.WorkingArea is in device pixels; WPF Window.Left/Top behave like
    // device pixels in PerMonitorV2 awareness (which the app declares in csproj).
    // For dialog Width/Height in WPF logical units, scale by the primary
    // monitor's DPI so the centring math stays in the same unit system.
    //
    // Call BEFORE ShowDialog for windows with explicit Width/Height set in XAML.
    // For SizeToContent windows, subscribe ContentRendered so ActualHeight is known.
    private static void PositionDialogOnPrimaryMonitor(Window dialog)
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return;
        var wa = screen.WorkingArea;
        // Use actual dimensions if layout has run (ContentRendered path); fall back
        // to XAML Width/Height when called before ShowDialog.
        var w = dialog.ActualWidth  > 0 ? dialog.ActualWidth  : dialog.Width;
        var h = dialog.ActualHeight > 0 ? dialog.ActualHeight : dialog.Height;
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = wa.Left + (wa.Width  - w) / 2;
        dialog.Top  = wa.Top  + (wa.Height - h) / 2;
    }

    // -- Dispatcher marshalling helpers --
    private Task ShowOnDispatcherAsync(Action action)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null) { action(); return Task.CompletedTask; }
        if (disp.CheckAccess()) { action(); return Task.CompletedTask; }
        return disp.InvokeAsync(action).Task;
    }

    private Task<T> ShowOnDispatcherAsync<T>(Func<T> func)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null) { return Task.FromResult(func()); }
        if (disp.CheckAccess()) { return Task.FromResult(func()); }
        return disp.InvokeAsync(func).Task;
    }
}
