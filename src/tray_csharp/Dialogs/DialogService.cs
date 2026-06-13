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

    public Task ShowJustUpdatedAsync(string version)
    {
        return ShowOnDispatcherAsync(() =>
        {
            _logger.Log("showing JustUpdated v" + version, Component);
            var window = _services.GetRequiredService<WelcomeWindow>();
            var vm = _services.GetRequiredService<WelcomeViewModel>();
            vm.JustUpdated = true;
            vm.UpdatedToVersion = "v" + version;
            window.DataContext = vm;
            window.Owner = Application.Current.MainWindow;
            PositionDialogOnPrimaryMonitor(window);
            var ok = window.ShowDialog();
            _logger.Log("JustUpdated closed openCustomize=" + (ok == true), Component);
            if (ok == true)
            {
                _services.GetRequiredService<MastersFM.Tray.Services.ICustomizerLauncher>().Launch();
            }
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
            // SizeToContent window: ApplyPrimaryAndTopmost re-centres at ContentRendered
            // once the final height is known (it hooks that event internally).
            PositionDialogOnPrimaryMonitor(window);
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

    // Centre every window on the PRIMARY monitor + mark it always-on-top. Multi-monitor
    // / mixed-DPI safe: WindowPlacement positions via Win32 in physical pixels AFTER the
    // HWND + final size exist, so a window never opens off-screen on a 4-5 display rig
    // (the old pre-show SystemParameters.WorkArea + Left/Top DIP math could). Safe to call
    // before Show()/ShowDialog() for both fixed-size and SizeToContent windows.
    private static void PositionDialogOnPrimaryMonitor(Window dialog)
        => WindowPlacement.ApplyPrimaryAndTopmost(dialog);

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
