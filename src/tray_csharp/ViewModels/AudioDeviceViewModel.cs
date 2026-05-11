// Stage 7.7: AudioDeviceViewModel. Backs AudioDeviceWindow (Surface 05).
// Uses Windows.Devices.Enumeration WinRT API for device enumeration
// (NO new NuGet per ABSOLUTE RULE 4 - NAudio not allowed).
//
// Stage 7.12 Batch A rev13:
//   - Single SelectedDevice [ObservableProperty]; both WASAPI and MME
//     ListBoxes bind to it with Mode=OneWay. User clicks are forwarded
//     via SelectionChanged event handlers (code-behind) → SelectDevice().
//     OneWay means the ListBox NEVER pushes back, eliminating all binding
//     reconnection / cross-clearing timing issues.
//   - WPF ListBox shows nothing when SelectedDevice is not in its
//     ItemsSource, so only the matching tab highlights naturally.
//   - Auto-persist: SelectDevice() → OnSelectedDeviceChanged → ApplyDevice().
//   - Reset (Cancel): sets SelectedDevice to IsDefault device, explicit
//     ApplyDevice() call since _suppressApply suppresses the partial method.

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

    // Single selection across all tabs.  Both WASAPI and MME ListBoxes bind
    // to this with Mode=OneWay; each ListBox shows a highlight only when
    // SelectedDevice is an item in its own collection.
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

    // Suppresses ApplyDevice() during RefreshAsync restore and Reset.
    private bool _suppressApply;

    public AudioDeviceViewModel(ILogger logger, IConfigService config)
    {
        _logger = logger;
        _config = config;
    }

    // Auto-persist when the user selects a device (not during programmatic changes).
    partial void OnSelectedDeviceChanged(AudioDeviceInfo? oldValue, AudioDeviceInfo? newValue)
    {
        if (!_suppressApply && newValue != null)
            ApplyDevice(newValue);
    }

    // Called by code-behind SelectionChanged handlers (user clicks only).
    public void SelectDevice(AudioDeviceInfo device) => SelectedDevice = device;

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

                SelectedDevice = toSelect;
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
        SelectedDevice = active;
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
