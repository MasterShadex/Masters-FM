// Stage 7.7: AudioDeviceViewModel. Backs AudioDeviceWindow (Surface 05).
// Uses Windows.Devices.Enumeration WinRT API for device enumeration
// (NO new NuGet per ABSOLUTE RULE 4 - NAudio not allowed).
//
// Stage 7.12 Batch A rev14:
//   - Single _selectedDevice backing field.
//   - SelectedWasapiDevice / SelectedMmeDevice are computed read-only
//     properties that return null when the device belongs to the other
//     backend. Both ListBoxes bind Mode=OneWay to their respective property,
//     so WPF always receives an explicit null (not a foreign-collection
//     object) when clearing the other tab — this guarantees a proper
//     deselect instead of WPF leaving the previous item highlighted.
//   - SelectedDevice (raw backing field, no filter) is exposed for the
//     code-behind SelectionChanged guard.
//   - User clicks forwarded via SelectDevice(); programmatic changes via
//     SetSelectedDeviceSilent().

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

    // -----------------------------------------------------------------------
    // Selection: single backing field, two computed read-only properties.
    // Each ListBox binds Mode=OneWay to its own property so it always gets
    // null (not a foreign object) when the other tab is active.
    // -----------------------------------------------------------------------

    private AudioDeviceInfo? _selectedDevice;

    // Raw accessor used by the code-behind SelectionChanged guard.
    public AudioDeviceInfo? SelectedDevice => _selectedDevice;

    // WASAPI ListBox binds to this. Returns null when an MME device is active.
    public AudioDeviceInfo? SelectedWasapiDevice =>
        _selectedDevice?.Backend == "WASAPI" ? _selectedDevice : null;

    // MME ListBox binds to this. Returns null when a WASAPI device is active.
    public AudioDeviceInfo? SelectedMmeDevice =>
        _selectedDevice?.Backend == "MME" ? _selectedDevice : null;

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

    public AudioDeviceViewModel(ILogger logger, IConfigService config)
    {
        _logger = logger;
        _config = config;
    }

    // -----------------------------------------------------------------------
    // Selection mutators
    // -----------------------------------------------------------------------

    // Called by code-behind when the user clicks a device row.
    // Notifies all three selection properties and persists to config.
    public void SelectDevice(AudioDeviceInfo device)
    {
        if (device == _selectedDevice) return;
        _selectedDevice = device;
        NotifySelectionChanged();
        ApplyDevice(device);
    }

    // Programmatic change: updates visuals but does NOT persist to config.
    // Used by RefreshAsync (restore on open) and Cancel (Reset).
    private void SetSelectedDeviceSilent(AudioDeviceInfo? device)
    {
        _selectedDevice = device;
        NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(SelectedWasapiDevice));
        OnPropertyChanged(nameof(SelectedMmeDevice));
    }

    // -----------------------------------------------------------------------

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

            // Restore previously-selected device from config.
            // Priority: saved device → WASAPI default → MME default.
            try
            {
                var savedId = _config.GetValue<string>("audio.outputDeviceId");

                AudioDeviceInfo? toSelect = null;
                if (!string.IsNullOrEmpty(savedId))
                {
                    toSelect = OutputDevices.FirstOrDefault(d =>
                        string.Equals(d.DeviceId, savedId, StringComparison.OrdinalIgnoreCase));
                    toSelect ??= MmeDevices.FirstOrDefault(d =>
                        string.Equals(d.DeviceId, savedId, StringComparison.OrdinalIgnoreCase));
                }
                toSelect ??= OutputDevices.FirstOrDefault(d => d.IsDefault);

                SetSelectedDeviceSilent(toSelect);
            }
            catch (Exception ex)
            {
                _logger.LogWarn("config read for audio selection: " + ex.Message, Component);
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
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogErr("open Windows Sound settings", ex, Component);
        }
    }

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

        SetSelectedDeviceSilent(active);

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
