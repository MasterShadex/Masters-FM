// Stage 7.7: AudioDeviceViewModel. Backs AudioDeviceWindow (Surface 05).
// Uses Windows.Devices.Enumeration WinRT API for device enumeration
// (NO new NuGet per ABSOLUTE RULE 4 - NAudio not allowed).
//
// Stage 7.12 Batch A rev11:
//   - Single _selectedDevice backing field; SelectedWasapiDevice and
//     SelectedMmeDevice are computed getters that filter by backend.
//     Each ListBox naturally shows nothing when the active device belongs
//     to the other backend — no cross-clearing, no timing dependency.
//   - Auto-persist: SetSelectedDevice() calls ApplyDevice() on user clicks.
//   - Reset: sets _selectedDevice to WASAPI default (MME default as fallback).

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

    // Single backing store for the active selection.
    // Getters filter by backend so WPF ListBoxes on each tab naturally
    // show a selection only when a device from THEIR backend is active.
    // Setting either property calls SetSelectedDevice() which notifies both,
    // so switching from WASAPI→MME clears the WASAPI tab and vice-versa.
    private AudioDeviceInfo? _selectedDevice;

    public AudioDeviceInfo? SelectedWasapiDevice
    {
        get => _selectedDevice is { Backend: "WASAPI" } ? _selectedDevice : null;
        set => SetSelectedDevice(value);
    }

    public AudioDeviceInfo? SelectedMmeDevice
    {
        get => _selectedDevice is { Backend: "MME" } ? _selectedDevice : null;
        set => SetSelectedDevice(value);
    }

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

    // Suppresses ApplyDevice() during RefreshAsync restore and Reset.
    private bool _suppressApply;

    public AudioDeviceViewModel(ILogger logger, IConfigService config)
    {
        _logger = logger;
        _config = config;
    }

    // Core selection mutator. Notifies BOTH tab properties so WPF updates
    // the correct tab and clears the other automatically (via the getter filter).
    private void SetSelectedDevice(AudioDeviceInfo? value)
    {
        if (_suppressApply)
        {
            if (_selectedDevice == value) return;
            _selectedDevice = value;
            OnPropertyChanged(nameof(SelectedWasapiDevice));
            OnPropertyChanged(nameof(SelectedMmeDevice));
            return;
        }
        // Ignore null push-backs from WPF ListBox and no-op re-selections.
        if (value == null || value == _selectedDevice) return;
        _selectedDevice = value;
        OnPropertyChanged(nameof(SelectedWasapiDevice));
        OnPropertyChanged(nameof(SelectedMmeDevice));
        ApplyDevice(value);
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

            // Restore previously-selected device from config (one selection, all tabs).
            // Priority: saved device → WASAPI default → MME default.
            try
            {
                var savedId = _config.GetValue<string>("audio.outputDeviceId");
                _suppressApply = true;

                AudioDeviceInfo? toSelect = null;
                if (!string.IsNullOrEmpty(savedId))
                {
                    toSelect = OutputDevices.FirstOrDefault(d =>
                        string.Equals(d.DeviceId, savedId, StringComparison.OrdinalIgnoreCase));
                    toSelect ??= MmeDevices.FirstOrDefault(d =>
                        string.Equals(d.DeviceId, savedId, StringComparison.OrdinalIgnoreCase));
                }
                toSelect ??= OutputDevices.FirstOrDefault(d => d.IsDefault);

                _selectedDevice = toSelect;
                OnPropertyChanged(nameof(SelectedWasapiDevice));
                OnPropertyChanged(nameof(SelectedMmeDevice));
            }
            catch (Exception ex)
            {
                _logger.LogWarn("config read for audio selection: " + ex.Message, Component);
            }
            finally
            {
                _suppressApply = false;
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

    // Persist a device selection to config and update PendingResult.
    private void ApplyDevice(AudioDeviceInfo device)
    {
        try
        {
            _config.SetValue("audio.outputDeviceId", device.DeviceId);
            _config.SetValue("audio.outputDeviceName", device.Name);
            _config.SetValue("audio.selectedBackend", device.Backend);
            _logger.Log("audio device persisted: " + device.Name + " (" + device.Backend + ")", Component);
        }
        catch (Exception ex)
        {
            _logger.LogErr("persist audio selection", ex, Component);
        }
        PendingResult = new Dialogs.AudioDeviceResult
        {
            DeviceId    = device.DeviceId,
            DisplayName = device.Name,
            IsDefault   = device.IsDefault
        };
    }

    // Reset: select the system default (WASAPI preferred; MME fallback).
    [RelayCommand]
    private void Cancel()
    {
        var active = OutputDevices.FirstOrDefault(d => d.IsDefault)
                     ?? (AudioDeviceInfo?)MmeDevices.FirstOrDefault(d => d.IsDefault);

        _suppressApply = true;
        _selectedDevice = active;
        OnPropertyChanged(nameof(SelectedWasapiDevice));
        OnPropertyChanged(nameof(SelectedMmeDevice));
        _suppressApply = false;

        if (active != null)
            ApplyDevice(active);

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
