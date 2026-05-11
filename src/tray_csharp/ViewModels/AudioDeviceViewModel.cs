// Stage 7.7: AudioDeviceViewModel. Backs AudioDeviceWindow (Surface 05).
// Uses Windows.Devices.Enumeration WinRT API for device enumeration
// (NO new NuGet per ABSOLUTE RULE 4 - NAudio not allowed).
//
// ASIO devices are NOT enumerable via WinRT (proprietary backend).
// Per Q-MOCK-05a default the ASIO tab is hidden when device count is 0,
// which it always is via WinRT. Future Brief can add ASIO enumeration
// via dedicated COM interop if needed.

using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MastersFM.Tray.Services;
using Windows.Devices.Enumeration;

namespace MastersFM.Tray.ViewModels;

public sealed partial class AudioDeviceViewModel : ObservableObject
{
    private const string Component = "AudioDevice";

    private readonly ILogger _logger;
    private readonly IConfigService _config;

    [ObservableProperty]
    private ObservableCollection<AudioDeviceInfo> outputDevices = new();

    [ObservableProperty]
    private ObservableCollection<AudioDeviceInfo> inputDevices = new();

    [ObservableProperty]
    private ObservableCollection<AudioDeviceInfo> asioDevices = new();

    [ObservableProperty]
    private AudioDeviceInfo? selectedDevice;

    [ObservableProperty]
    private bool stereoMixEnabled;

    [ObservableProperty]
    private bool stereoMixDevicePresent;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private bool hasAsio;

    // INTERRUPT #3 STEP 7 (Issue 2): MME output device collection.
    [ObservableProperty]
    private ObservableCollection<AudioDeviceInfo> mmeDevices = new();

    [ObservableProperty]
    private bool hasMme;

    [ObservableProperty]
    private string statusText = "Idle.";

    public Dialogs.AudioDeviceResult? PendingResult { get; private set; }

    // Original-on-open device for Reset (Cancel) support; suppress flag
    // prevents Apply() firing during the initial RefreshAsync restore.
    private bool _suppressApply;
    private AudioDeviceInfo? _originalDevice;

    public AudioDeviceViewModel(ILogger logger, IConfigService config)
    {
        _logger = logger;
        _config = config;
    }

    // Auto-persist whenever the user picks a device.
    // Guarded by _suppressApply so the initial restore in RefreshAsync
    // does not write to config or trigger a spurious toast.
    partial void OnSelectedDeviceChanged(AudioDeviceInfo? oldValue, AudioDeviceInfo? newValue)
    {
        if (!_suppressApply && newValue != null)
            Apply();
    }

    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusText = "Enumerating audio devices...";
        try
        {
            OutputDevices.Clear();
            InputDevices.Clear();
            AsioDevices.Clear();
            MmeDevices.Clear();
            StereoMixDevicePresent = false;
            StereoMixEnabled = false;

            // Output devices via WinRT
            var renderClass = await DeviceInformation.FindAllAsync(DeviceClass.AudioRender).AsTask().ConfigureAwait(true);
            foreach (var d in renderClass)
            {
                bool isStereoMix = d.Name.IndexOf("Stereo Mix", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isStereoMix)
                {
                    StereoMixDevicePresent = true;
                    StereoMixEnabled = d.IsEnabled;
                }
                OutputDevices.Add(new AudioDeviceInfo
                {
                    DeviceId = d.Id,
                    Name = d.Name,
                    IsDefault = d.IsDefault,
                    IsEnabled = d.IsEnabled,
                    IsStereoMix = isStereoMix,
                    Backend = "WASAPI"
                });
            }

            // Input devices via WinRT
            var captureClass = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture).AsTask().ConfigureAwait(true);
            foreach (var d in captureClass)
            {
                InputDevices.Add(new AudioDeviceInfo
                {
                    DeviceId = d.Id,
                    Name = d.Name,
                    IsDefault = d.IsDefault,
                    IsEnabled = d.IsEnabled,
                    IsStereoMix = false,
                    Backend = "WASAPI"
                });
            }

            // INTERRUPT #3 STEP 7 (Issue 2): MME output device enumeration via winmm.dll.
            var mmeList = AudioApi.EnumerateMmeOutputDevices();
            foreach (var d in mmeList)
            {
                MmeDevices.Add(new AudioDeviceInfo
                {
                    DeviceId = $"mme-out-{d.Index}",
                    Name = d.Name,
                    IsDefault = d.Index == 0,
                    IsEnabled = true,
                    IsStereoMix = d.Name.IndexOf("Stereo Mix", StringComparison.OrdinalIgnoreCase) >= 0,
                    Backend = "MME"
                });
            }
            HasMme = MmeDevices.Count > 0;

            HasAsio = AsioDevices.Count > 0;

            // Restore previously-selected device from config if present
            try
            {
                var savedId = _config.GetValue<string>("audio.outputDeviceId");
                if (!string.IsNullOrEmpty(savedId))
                {
                    _suppressApply = true;
                    foreach (var d in OutputDevices)
                    {
                        if (string.Equals(d.DeviceId, savedId, StringComparison.OrdinalIgnoreCase))
                        {
                            SelectedDevice = d;
                            break;
                        }
                    }
                    // If not found in WASAPI output, check MME devices
                    if (SelectedDevice == null)
                    {
                        foreach (var d in MmeDevices)
                        {
                            if (string.Equals(d.DeviceId, savedId, StringComparison.OrdinalIgnoreCase))
                            {
                                SelectedDevice = d;
                                break;
                            }
                        }
                    }
                    _suppressApply = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarn("config read for audio selection: " + ex.Message, Component);
            }
            finally
            {
                // Snapshot the restored (or null) device so Reset can revert to it.
                _originalDevice = SelectedDevice;
            }

            StatusText = $"{OutputDevices.Count} WASAPI output, {InputDevices.Count} input, {MmeDevices.Count} MME device(s).";
            _logger.Log("enumerated " + OutputDevices.Count + " WASAPI output, " + InputDevices.Count + " input, " + MmeDevices.Count + " MME devices",
                Component);
        }
        catch (Exception ex)
        {
            _logger.LogErr("audio enumeration", ex, Component);
            StatusText = "Couldn't enumerate audio devices: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync_Cmd() => await RefreshAsync();

    [RelayCommand]
    private void OpenWindowsSound()
    {
        try
        {
            // Deep link to Sound settings; ms-settings:sound on Win10/11
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogErr("open Windows Sound settings", ex, Component);
        }
    }

    [RelayCommand]
    private void Apply()
    {
        if (SelectedDevice == null) return;
        try
        {
            _config.SetValue("audio.outputDeviceId", SelectedDevice.DeviceId);
            _config.SetValue("audio.outputDeviceName", SelectedDevice.Name);
            _config.SetValue("audio.selectedBackend", SelectedDevice.Backend);
            _logger.Log("audio device persisted: " + SelectedDevice.Name + " (" + SelectedDevice.Backend + ")", Component);
        }
        catch (Exception ex)
        {
            _logger.LogErr("persist audio selection", ex, Component);
        }
        PendingResult = new Dialogs.AudioDeviceResult
        {
            DeviceId = SelectedDevice.DeviceId,
            DisplayName = SelectedDevice.Name,
            IsDefault = SelectedDevice.IsDefault
        };
    }

    [RelayCommand]
    private void Cancel()
    {
        // Revert to original-on-open device. Suppress Apply() so we don't
        // re-persist during the revert; the toast will still fire via
        // OnVmPropertyChanged in the code-behind.
        _suppressApply = true;
        SelectedDevice = _originalDevice;
        _suppressApply = false;
        // Re-persist the original selection (puts config back to what it was).
        if (_originalDevice != null)
            Apply();
        PendingResult = null;
    }
}

public sealed class AudioDeviceInfo
{
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public bool IsDefault { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsStereoMix { get; init; }
    public string Backend { get; init; } = "WASAPI";

    public string Detail => Backend + (IsDefault ? " (default)" : "") +
                             (IsEnabled ? "" : " (disabled)");
}
