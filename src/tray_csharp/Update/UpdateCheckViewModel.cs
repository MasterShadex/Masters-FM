// Stage 7.2: UpdateCheckViewModel. CommunityToolkit.Mvvm
// [ObservableProperty] + [RelayCommand] source generators. The first of
// the three viewmodels reserved at 7.1B (NowPlaying / UpdateCheck /
// DetectionStatus); UpdateCheck lands here in 7.2.
//
// Subscribes to IUpdateCheckService.StateChanged event; marshals to UI
// thread via Dispatcher.BeginInvoke; drives the Surface 07 progress
// window XAML bindings via direct primitive properties (no custom
// converters needed -- IsXxxEnabled bools and Visibility values are
// computed here).

using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MastersFM.Tray.Services;

namespace MastersFM.Tray.Update;

public sealed partial class UpdateCheckViewModel : ObservableObject
{
    private readonly IUpdateCheckService _service;
    private readonly ILogger _logger;

    [ObservableProperty]
    private UpdateState _currentState;

    [ObservableProperty]
    private string? _availableVersion;

    [ObservableProperty]
    private string _statusText = "Idle";

    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private bool _isAuthenticodeVerified;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _lastCheckedDisplay = "(never)";

    // Computed XAML-binding helpers (avoid the need for custom converters)

    [ObservableProperty]
    private bool _isCheckNowEnabled = true;

    [ObservableProperty]
    private bool _isDownloadEnabled;

    [ObservableProperty]
    private bool _isInstallEnabled;

    [ObservableProperty]
    private bool _isCancelEnabled;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private Visibility _authenticodeBadgeVisibility = Visibility.Collapsed;

    public UpdateCheckViewModel(IUpdateCheckService service, ILogger logger)
    {
        _service = service;
        _logger = logger;

        CurrentState = service.CurrentState;
        AvailableVersion = service.AvailableVersion;
        UpdateLastCheckedDisplay();
        RefreshComputedProperties();

        service.StateChanged += OnStateChanged;
    }

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        _logger.Log("CheckNowCommand invoked", "UpdateVM");
        await _service.CheckNowAsync();
        UpdateLastCheckedDisplay();
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        _logger.Log("DownloadCommand invoked", "UpdateVM");
        await _service.DownloadAsync();
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        _logger.Log("InstallCommand invoked", "UpdateVM");
        await _service.InstallAsync();
    }

    [RelayCommand]
    private void Cancel()
    {
        _logger.Log("CancelCommand invoked", "UpdateVM");
        _service.Cancel();
    }

    private void OnStateChanged(object? sender, UpdateStateChangedEventArgs e)
    {
        // Marshal to UI thread; XAML data binding requires it.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => OnStateChanged(sender, e)));
            return;
        }

        CurrentState = e.NewState;
        AvailableVersion = _service.AvailableVersion;
        if (e.ProgressPercent.HasValue) ProgressPercent = e.ProgressPercent.Value;
        ErrorMessage = (e.NewState == UpdateState.Error) ? _service.LastErrorMessage : null;
        RefreshComputedProperties();
    }

    private void RefreshComputedProperties()
    {
        IsCheckNowEnabled = CurrentState is UpdateState.Idle or UpdateState.Error;
        IsDownloadEnabled = CurrentState == UpdateState.Available;
        IsInstallEnabled = CurrentState == UpdateState.Ready;
        IsCancelEnabled = CurrentState is UpdateState.Checking or UpdateState.Downloading;
        IsProgressIndeterminate = CurrentState is UpdateState.Checking or UpdateState.Installing;
        IsAuthenticodeVerified = (CurrentState == UpdateState.Ready);
        AuthenticodeBadgeVisibility = (CurrentState == UpdateState.Ready) ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        StatusText = CurrentState switch
        {
            UpdateState.Idle => string.IsNullOrEmpty(AvailableVersion)
                                  ? "No updates available"
                                  : $"Update v{AvailableVersion} pending",
            UpdateState.Checking => "Checking for updates...",
            UpdateState.Available => $"Update v{AvailableVersion} available",
            UpdateState.Downloading => $"Downloading v{AvailableVersion}... {ProgressPercent}%",
            UpdateState.Ready => $"Update v{AvailableVersion} ready to install",
            UpdateState.Installing => $"Installing v{AvailableVersion}...",
            UpdateState.Error => $"Error: {ErrorMessage ?? "(unknown)"}",
            _ => CurrentState.ToString()
        };
    }

    private void UpdateLastCheckedDisplay()
    {
        var last = _service.LastCheckedUtc;
        if (!last.HasValue)
        {
            LastCheckedDisplay = "(never)";
            return;
        }
        var ago = DateTime.UtcNow - last.Value;
        if (ago.TotalMinutes < 2) LastCheckedDisplay = "just now";
        else if (ago.TotalHours < 1) LastCheckedDisplay = $"{(int)ago.TotalMinutes} min ago";
        else if (ago.TotalDays < 1) LastCheckedDisplay = $"{(int)ago.TotalHours} h ago";
        else LastCheckedDisplay = $"{(int)ago.TotalDays} days ago";
    }
}
