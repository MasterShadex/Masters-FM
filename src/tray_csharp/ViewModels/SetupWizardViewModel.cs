// Stage 7.7: SetupWizardViewModel. Backs SetupWizardWindow (Surface 09).
// 3-step internal navigation: Welcome -> Audio -> Platforms. On Done,
// writes welcome_seen + welcome_seen_version to config and returns true.
// Cancel/Skip returns false (tray still initializes; defaults stand).

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MastersFM.Tray.Services;

namespace MastersFM.Tray.ViewModels;

public enum WizardStep
{
    Welcome = 1,
    Audio = 2,
    Platforms = 3
}

public sealed partial class SetupWizardViewModel : ObservableObject
{
    private const string Component = "SetupWizard";

    private readonly ILogger _logger;
    private readonly IConfigService _config;

    [ObservableProperty]
    private WizardStep currentStep = WizardStep.Welcome;

    [ObservableProperty]
    private string stepLabel = "Step 1 / 3";

    [ObservableProperty]
    private bool canGoBack;

    [ObservableProperty]
    private bool canGoForward = true;

    [ObservableProperty]
    private string forwardButtonLabel = "Continue";

    [ObservableProperty]
    private bool completed;

    public WelcomeViewModel WelcomeVm { get; }
    public AudioDeviceViewModel AudioVm { get; }
    public PlatformsViewModel PlatformsVm { get; }

    public Action? RequestClose { get; set; }

    public SetupWizardViewModel(ILogger logger, IConfigService config,
        WelcomeViewModel welcomeVm, AudioDeviceViewModel audioVm, PlatformsViewModel platformsVm)
    {
        _logger = logger;
        _config = config;
        WelcomeVm = welcomeVm;
        AudioVm = audioVm;
        PlatformsVm = platformsVm;

        // First-run platform default enforcement happens in App.xaml.cs
        // BEFORE this VM is constructed (per brief absolute rule 15).
        Reset();
    }

    public void Reset()
    {
        CurrentStep = WizardStep.Welcome;
        Completed = false;
        UpdateStepUi();
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        switch (CurrentStep)
        {
            case WizardStep.Welcome:
                CurrentStep = WizardStep.Audio;
                UpdateStepUi();
                // Trigger audio enumeration when arriving at the audio step
                _ = AudioVm.RefreshAsync();
                // v14.2.0: kick off the auto-detect poller so the banner
                // populates while the user reads the page. If they're
                // already playing music it finds it in 1-3 s and they just
                // click "Use this device" — no manual picking needed.
                _ = AudioVm.StartAutoDetectAsync();
                await Task.CompletedTask;
                break;

            case WizardStep.Audio:
                CurrentStep = WizardStep.Platforms;
                UpdateStepUi();
                PlatformsVm.RefreshFromConfig();
                await Task.CompletedTask;
                break;

            case WizardStep.Platforms:
                // Done -- persist welcome_seen flag and exit.
                // SetWelcomeSeen writes welcome_seen_version via GetCurrentAppVersion()
                // (strips + build suffix) so subsequent GetWelcomeSeen() comparisons
                // succeed. Hotfix 7.10: removed redundant SetValue("welcome_seen_version", ...)
                // that was overwriting the correctly formatted value with the raw
                // InformationalVersion including + build metadata.
                try
                {
                    _config.SetWelcomeSeen(true);
                    _logger.Log("setup wizard completed; welcome_seen=true", Component);
                }
                catch (Exception ex)
                {
                    _logger.LogErr("persist welcome_seen on done", ex, Component);
                }
                Completed = true;
                RequestClose?.Invoke();
                break;
        }
    }

    [RelayCommand]
    private void Back()
    {
        switch (CurrentStep)
        {
            case WizardStep.Audio:
                CurrentStep = WizardStep.Welcome;
                UpdateStepUi();
                break;
            case WizardStep.Platforms:
                CurrentStep = WizardStep.Audio;
                UpdateStepUi();
                break;
        }
    }

    [RelayCommand]
    private void Skip()
    {
        // Hotfix 7.10: Skip now persists the gate flag so the wizard does not
        // re-show on subsequent launches (completed=false semantics -- step-state
        // left at defaults; user can revisit Audio/Platforms via tray menu).
        // Documented as ID-28 candidate per interrupt brief S1.3.
        try { _config.SetWelcomeSeen(true); } catch { /* best-effort; tray still works */ }
        Completed = false;
        RequestClose?.Invoke();
    }

    private void UpdateStepUi()
    {
        StepLabel = "Step " + (int)CurrentStep + " / 3";
        CanGoBack = CurrentStep != WizardStep.Welcome;
        ForwardButtonLabel = (CurrentStep == WizardStep.Platforms) ? "Done" : "Continue";
        CanGoForward = true;
    }
}
