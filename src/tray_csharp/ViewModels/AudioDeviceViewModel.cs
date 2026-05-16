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
using System.Net.Http;
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
    private readonly HttpClient _http;
    private readonly AudioBackendBridge _backend;

    [ObservableProperty]
    private ObservableCollection<AudioDeviceInfo> outputDevices = new();

    [ObservableProperty]
    private ObservableCollection<AudioDeviceInfo> inputDevices = new();

    // Stage 7.12 Batch B Phase K (DIAG 04): KS = same capture endpoints as
    // InputDevices but tagged with Backend="KS" so the audio_spectrum opens
    // them in exclusive (WDM-KS) mode.
    [ObservableProperty]
    private ObservableCollection<AudioDeviceInfo> ksDevices = new();

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

    // Stage 7.12 Batch B Phase K (DIAG 04): KS and ASIO list-box bindings.
    public AudioDeviceInfo? SelectedKsDevice =>
        _selectedDevice?.Backend == "KS" ? _selectedDevice : null;

    public AudioDeviceInfo? SelectedAsioDevice =>
        _selectedDevice?.Backend == "ASIO" ? _selectedDevice : null;

    [ObservableProperty]
    private bool stereoMixEnabled;

    [ObservableProperty]
    private bool stereoMixDevicePresent;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private bool hasAsio;

    // Phase K (DIAG 04): real KS enumeration. HasKs is true when at least one
    // WinRT capture endpoint exists (every WASAPI input can be opened in
    // WDM-KS exclusive mode by audio_spectrum).
    [ObservableProperty]
    private bool hasKs;

    // INTERRUPT #3 STEP 7 (Issue 2): MME output device collection.
    [ObservableProperty]
    private ObservableCollection<AudioDeviceInfo> mmeDevices = new();

    [ObservableProperty]
    private bool hasMme;

    [ObservableProperty]
    private string statusText = "Idle.";

    public Dialogs.AudioDeviceResult? PendingResult { get; private set; }

    public AudioDeviceViewModel(ILogger logger, IConfigService config, HttpClient http, AudioBackendBridge backend)
    {
        _logger  = logger;
        _config  = config;
        _http    = http;
        _backend = backend;
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
        // Update each device's IsActive flag so the data-driven DataTrigger
        // in the ListBoxItem template can show the highlight on the single
        // active item across ALL four tabs.
        foreach (var d in OutputDevices) d.IsActive = ReferenceEquals(d, _selectedDevice);
        foreach (var d in MmeDevices)    d.IsActive = ReferenceEquals(d, _selectedDevice);
        foreach (var d in KsDevices)     d.IsActive = ReferenceEquals(d, _selectedDevice);
        foreach (var d in AsioDevices)   d.IsActive = ReferenceEquals(d, _selectedDevice);

        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(SelectedWasapiDevice));
        OnPropertyChanged(nameof(SelectedMmeDevice));
        OnPropertyChanged(nameof(SelectedKsDevice));
        OnPropertyChanged(nameof(SelectedAsioDevice));
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
            KsDevices.Clear();
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

            // Input devices via WinRT — used internally; not currently surfaced
            // in the dialog UI but populated for parity with output devices.
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
                // Stage 7.12 Batch B Phase K (DIAG 04): KS list mirrors the
                // same WinRT capture endpoints but is tagged Backend="KS" so
                // ApplyDevice persists selectedBackend="KS" and the
                // audio_spectrum opens this endpoint in WDM-KS exclusive mode
                // (case "wdm_ks" / "ks" in audio_spectrum.cs).
                KsDevices.Add(new AudioDeviceInfo
                {
                    DeviceId = d.Id,
                    Name = d.Name,
                    IsDefault = d.IsDefault,
                    IsEnabled = d.IsEnabled,
                    IsStereoMix = false,
                    Backend = "KS"
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
            // Stage 7.12 Batch B Phase K rev2 (DIAG 04): ASIO entries from the
            // running audio_spectrum process.  It probes each registered
            // driver via NAudio's AsioOut for the real input-channel count
            // and emits one entry per stereo pair (e.g. "VB-Matrix VASIO-32
            // — Ch 5-6" with compound id "VB-Matrix VASIO-32|4").  Bypasses
            // the channel-count guessing we'd otherwise have to do in the
            // tray.  Falls back to the registry-only single-entry-per-driver
            // path if the spectrum isn't reachable (rare race during startup;
            // the user can hit Refresh to retry).
            IReadOnlyList<AudioApi.AsioDriver> asioList = await AudioApi.FetchAsioFromSpectrumAsync(_http);
            if (asioList.Count == 0)
            {
                asioList = AudioApi.EnumerateAsioDrivers();
                if (asioList.Count > 0)
                    _logger.Log("ASIO: spectrum unreachable, using registry-only fallback (no channel pairs)", Component);
            }
            // Phase N #2: sort ASIO entries with Windows-native natural
            // ordering on the display name so e.g. "VASIO-32  -  Ch 1-2"
            // sorts before "VASIO-128  -  Ch 1-2" (and Ch 3-4, Ch 5-6 stay
            // grouped by driver in numeric channel order — same comparer
            // Explorer uses for filenames containing numbers).
            var sortedAsio = asioList
                .OrderBy(d => string.IsNullOrWhiteSpace(d.Description) ? d.Name : d.Description,
                              AudioApi.NaturalStringComparer.OrdinalIgnoreCase);
            foreach (var drv in sortedAsio)
            {
                AsioDevices.Add(new AudioDeviceInfo
                {
                    DeviceId = drv.Name,                       // compound id "driver|offset" or bare driver name
                    Name     = string.IsNullOrWhiteSpace(drv.Description) ? drv.Name : drv.Description,
                    IsDefault   = false,
                    IsEnabled   = true,                        // spectrum already filtered unusable drivers
                    IsStereoMix = false,
                    Backend     = "ASIO"
                });
            }

            HasMme  = MmeDevices.Count  > 0;
            HasKs   = KsDevices.Count   > 0;
            HasAsio = AsioDevices.Count > 0;

            // Restore previously-selected device from config.
            // Priority: saved device (any backend) → WASAPI default → MME default.
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
                    toSelect ??= KsDevices.FirstOrDefault(d =>
                        string.Equals(d.DeviceId, savedId, StringComparison.OrdinalIgnoreCase));
                    toSelect ??= AsioDevices.FirstOrDefault(d =>
                        string.Equals(d.DeviceId, savedId, StringComparison.OrdinalIgnoreCase));
                }
                toSelect ??= OutputDevices.FirstOrDefault(d => d.IsDefault);

                SetSelectedDeviceSilent(toSelect);
            }
            catch (Exception ex)
            {
                _logger.LogWarn("config read for audio selection: " + ex.Message, Component);
            }

            StatusText = $"{OutputDevices.Count} WASAPI, {MmeDevices.Count} MME, " +
                         $"{KsDevices.Count} KS, {AsioDevices.Count} ASIO.";
            _logger.Log(
                $"enumerated {OutputDevices.Count} WASAPI output, {InputDevices.Count} WASAPI input, " +
                $"{MmeDevices.Count} MME, {KsDevices.Count} KS, {AsioDevices.Count} ASIO",
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

        // Stage 7.12 Batch B Phase O: actually apply the selection to the
        // running audio_spectrum process AND persist to the keys its bootstrap
        // reader consults.  Fire-and-forget — the bridge handles its own
        // logging and never throws; spectrum unreachable just means the
        // selection takes effect on the next spectrum start via BootstrapFromConfig.
        _ = _backend.PushAsync(device);

        PendingResult = new Dialogs.AudioDeviceResult
        {
            DeviceId    = device.DeviceId,
            DisplayName = device.Name,
            IsDefault   = device.IsDefault
        };
    }

    // Reset: select the system default (WASAPI preferred; falls back through
    // MME / KS / ASIO if no WASAPI default is known).
    [RelayCommand]
    private void Cancel()
    {
        var active = OutputDevices.FirstOrDefault(d => d.IsDefault)
                     ?? (AudioDeviceInfo?)MmeDevices.FirstOrDefault(d => d.IsDefault)
                     ?? KsDevices.FirstOrDefault(d => d.IsDefault)
                     ?? AsioDevices.FirstOrDefault();

        SetSelectedDeviceSilent(active);

        if (active != null)
            ApplyDevice(active);

        PendingResult = null;
    }
}

public sealed class AudioDeviceInfo : System.ComponentModel.INotifyPropertyChanged
{
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public bool IsDefault { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsStereoMix { get; init; }
    public string Backend { get; init; } = "WASAPI";

    public string Detail => Backend + (IsDefault ? " (default)" : "") +
                             (IsEnabled ? "" : " (disabled)");

    // Data-driven selection visual. The ViewModel sets IsActive=true on the
    // currently selected device (and false on all others) whenever selection
    // changes. The ListBoxItem template binds its highlight to this property,
    // so the visual is completely independent of WPF's Selector machinery.
    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
