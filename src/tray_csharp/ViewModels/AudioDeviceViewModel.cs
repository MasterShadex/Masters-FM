// Stage 7.7: AudioDeviceViewModel. Backs AudioDeviceWindow (Surface 05).
// Uses Windows.Devices.Enumeration WinRT API for device enumeration
// (NO new NuGet per ABSOLUTE RULE 4 - NAudio not allowed).
//
// Stage 7.12 Batch A rev9:
//   - SelectedWasapiDevice + SelectedMmeDevice replace the shared SelectedDevice
//     so each tab holds its own independent selection.
//   - Auto-persist: clicking any device in either tab immediately saves it to
//     config via ApplyDevice().
//   - Reset (Cancel): both tabs revert to their respective IsDefault device;
//     the WASAPI default is written to config as the active source.

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

    // Per-tab independent selections. Each tab binds to its own property so
    // selecting a WASAPI device doesn't deselect the MME tab and vice-versa.
    [ObservableProperty]
    private AudioDeviceInfo? selectedWasapiDevice;

    [ObservableProperty]
    private AudioDeviceInfo? selectedMmeDevice;

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

    // Suppresses ApplyDevice() during initial RefreshAsync restore and Reset
    // so we don't write to config or trigger the toast for programmatic changes.
    private bool _suppressApply;

    public AudioDeviceViewModel(ILogger logger, IConfigService config)
    {
        _logger = logger;
        _config = config;
    }

    // Auto-persist on user selection (WASAPI tab).
    partial void OnSelectedWasapiDeviceChanged(AudioDeviceInfo? oldValue, AudioDeviceInfo? newValue)
    {
        if (!_suppressApply && newValue != null)
            ApplyDevice(newValue);
    }

    // Auto-persist on user selection (MME tab).
    partial void OnSelectedMmeDeviceChanged(AudioDeviceInfo? oldValue, AudioDeviceInfo? newValue)
    {
        if (!_suppressApply && newValue != null)
            ApplyDevice(newValue);
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

            // Restore previously-selected device from config.
            // Each backend gets its own highlight: if the saved device is WASAPI it
            // shows on the WASAPI tab; if MME it shows on the MME tab. The other tab
            // shows its default device highlighted so the user can see the fallback.
            try
            {
                var savedId = _config.GetValue<string>("audio.outputDeviceId");
                _suppressApply = true;

                // Always highlight the default on each tab first.
                SelectedWasapiDevice = OutputDevices.FirstOrDefault(d => d.IsDefault);
                SelectedMmeDevice    = MmeDevices.FirstOrDefault(d => d.IsDefault);

                // Then override with the saved selection on its own tab.
                if (!string.IsNullOrEmpty(savedId))
                {
                    foreach (var d in OutputDevices)
                    {
                        if (string.Equals(d.DeviceId, savedId, StringComparison.OrdinalIgnoreCase))
                        {
                            SelectedWasapiDevice = d;
                            break;
                        }
                    }
                    foreach (var d in MmeDevices)
                    {
                        if (string.Equals(d.DeviceId, savedId, StringComparison.OrdinalIgnoreCase))
                        {
                            SelectedMmeDevice = d;
                            break;
                        }
                    }
                }
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

    // Reset: revert both tabs to their respective default (IsDefault) device.
    // The WASAPI default is saved to config as the active source.
    [RelayCommand]
    private void Cancel()
    {
        _suppressApply = true;
        SelectedWasapiDevice = OutputDevices.FirstOrDefault(d => d.IsDefault);
        SelectedMmeDevice    = MmeDevices.FirstOrDefault(d => d.IsDefault);
        _suppressApply = false;

        // Persist: WASAPI default wins; fall back to MME default if no WASAPI default.
        var active = SelectedWasapiDevice ?? SelectedMmeDevice;
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
