// Stage 7.7: IDialogService abstraction. Decouples dialog show calls
// from the rest of the app. The 7.6 tray menu calls
// _dialogService.ShowAudioDeviceAsync() without knowing the XAML
// class name.
//
// All Show*Async methods marshal to the WPF Dispatcher thread
// internally; callers can invoke from any thread.

namespace MastersFM.Tray.Dialogs;

public interface IDialogService
{
    /// <summary>Show Welcome dialog (Surface 04). When showAboutTab=true,
    /// the About panel (Surface 10 embedded) is selected by default.</summary>
    Task ShowWelcomeAsync(bool showAboutTab = false);

    /// <summary>Show Audio device dialog (Surface 05). Returns the
    /// selected device on Apply, null on Cancel.</summary>
    Task<AudioDeviceResult?> ShowAudioDeviceAsync();

    /// <summary>Show Platforms dialog (Surface 06).</summary>
    Task ShowPlatformsAsync();

    /// <summary>Show Setup wizard (Surface 09). Returns true if
    /// completed (welcome_seen written), false if cancelled.</summary>
    Task<bool> ShowSetupWizardAsync();

    /// <summary>Show Error dialog (Surface 11).</summary>
    Task ShowErrorAsync(string title, string message, Exception? ex = null);
}

/// <summary>Result of Audio device dialog. Null indicates Cancel.</summary>
public sealed record AudioDeviceResult
{
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public bool IsDefault { get; init; }
}
