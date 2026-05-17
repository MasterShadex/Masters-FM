# Research #2 — Close-to-0-ms latency via full rebuild of the audio capture engine

**Scope (operator constraints):**
- No user-side driver-control-panel tweaks. Must "just work."
- All four backends: WASAPI Loopback, WASAPI Input, WDM-KS / Exclusive, MME, **and ASIO**.
- **Full code rebuild explicitly authorized.** Drop NAudio entirely if it buys us latency.
- Output remains compatible with the existing FFT pipeline (interleaved float32 PCM at 48 kHz / 44.1 kHz, sample-by-sample feed into `s_fftStride`-triggered FFT).

**Thesis:** NAudio is a thin wrapper over Win32/COM audio APIs. The wrappers are convenient but add three classes of overhead we can eliminate by going direct:
1. **Indirection layers.** NAudio's `WasapiCapture` → `MMDevice.AudioClient` → `IAudioClient::Initialize` → managed callback into `DataAvailable`. Each hop is a ~1-3 µs cost and forces a heap-allocated `WaveInEventArgs` per buffer.
2. **API floor.** NAudio targets the COMMON denominator across Win7-Win11. It uses `IAudioClient`, not `IAudioClient3`. It uses `AsioDriverExt`'s preferred-only buffer sizing, not raw `IASIO::createBuffers`.
3. **Conservative defaults.** NAudio's `WaveInEvent` defaults to 3 buffers of 100 ms each. Even with our 50 ms / default settings, we're in the safety zone — never close to the kernel's actual capability.

A custom engine wins by addressing all three. The catch: we now own everything COM, P/Invoke, and ASIO-SDK-level. Maintenance cost is high.

---

## Verified facts (same research pass as #1)
- `IAudioClient3::InitializeSharedAudioStream` does **NOT** accept `AUDCLNT_STREAMFLAGS_LOOPBACK`. **Loopback's ~7-10 ms floor is OS-imposed and unbeatable in user mode.**
- `IAudioClient3` IS supported for normal capture; minimum period typically ~3 ms on modern Windows (some drivers go down to 48 frames @ 44.1k ≈ 1.09 ms).
- ASIO host-side buffer-size requests are respected by most modern drivers if within `[min..max]` and granularity-aligned.
- Direct waveIn API (`winmm.dll`) supports buffer sizes down to ~5-10 ms reliably; smaller works on most systems but underruns on slow ones.
- WDM-KS direct (bypassing WASAPI) opens kernel-streaming pins via `IRP_MJ_DEVICE_CONTROL` + `KSPROPERTY_PIN_DATAFORMAT`. **In practice it does NOT outperform WASAPI Exclusive** on Win10+ because Windows reserialized the WDM stack on top of the audio engine. Worth knowing but NOT worth pursuing.

---

## Per-backend rebuild plan

### A. WASAPI Loopback — `CustomWasapiLoopback.cs`
**Approach:** Raw `IAudioClient` (NOT `IAudioClient3` — loopback not allowed) + `IAudioCaptureClient` via direct COM interop.

**Skeleton (~350 LOC):**
```csharp
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator {
    int  EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection ppDevices);
    int  GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
    int  GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
    // ... endpoint notification callbacks
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice {
    int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    int OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
    int GetState(out uint pdwState);
}

[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient {
    int Initialize(AudioClientShareMode shareMode, uint streamFlags, long bufferDuration, long periodicity, [In] ref WAVEFORMATEX pFormat, [In] ref Guid AudioSessionGuid);
    int GetBufferSize(out uint pNumBufferFrames);
    int GetStreamLatency(out long phnsLatency);
    int GetCurrentPadding(out uint pNumPaddingFrames);
    int IsFormatSupported(AudioClientShareMode shareMode, [In] ref WAVEFORMATEX pFormat, IntPtr ppClosestMatch);
    int GetMixFormat(out IntPtr ppDeviceFormat);
    int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
    int Start();
    int Stop();
    int Reset();
    int SetEventHandle(IntPtr eventHandle);
    int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}

[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient {
    int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out long pu64DevicePosition, out long pu64QPCPosition);
    int ReleaseBuffer(uint NumFramesRead);
    int GetNextPacketSize(out uint pNumFramesInNextPacket);
}

public sealed class CustomWasapiLoopback : ICaptureSource {
    const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    const uint AUDCLNT_STREAMFLAGS_LOOPBACK      = 0x00020000;
    static readonly Guid IID_IAudioClient        = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    IAudioClient        _client;
    IAudioCaptureClient _capture;
    SafeWaitHandle      _eventHandle;
    Thread              _thread;
    volatile bool       _running;
    public WaveFormat   WaveFormat { get; private set; }
    public event Action<byte[], int> DataAvailable;
    public event Action<Exception>   CaptureStopped;

    public CustomWasapiLoopback(string endpointId) {
        IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        IMMDevice device;
        if (endpointId == null) enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device);
        else                    enumerator.GetDevice(endpointId, out device);

        object clientObj;
        var iid = IID_IAudioClient;
        device.Activate(ref iid, /* CLSCTX_ALL */ 0x17, IntPtr.Zero, out clientObj);
        _client = (IAudioClient)clientObj;

        IntPtr fmtPtr;
        _client.GetMixFormat(out fmtPtr);
        var fmt = Marshal.PtrToStructure<WAVEFORMATEX>(fmtPtr);
        Marshal.FreeCoTaskMem(fmtPtr);
        WaveFormat = WaveFormatFromWAVEFORMATEX(fmt);

        // Smallest meaningful request. Engine clamps to its period floor.
        long requestedDuration = 30000; // 3 ms in 100-ns units; engine bumps to ~10ms for loopback
        Guid sessionGuid = Guid.Empty;
        int hr = _client.Initialize(
            AudioClientShareMode.Shared,
            AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_LOOPBACK,
            requestedDuration, 0, ref fmt, ref sessionGuid);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

        _eventHandle = new SafeWaitHandle(CreateEvent(IntPtr.Zero, false, false, null), true);
        _client.SetEventHandle(_eventHandle.DangerousGetHandle());

        object captureObj;
        var captureIid = IID_IAudioCaptureClient;
        _client.GetService(ref captureIid, out captureObj);
        _capture = (IAudioCaptureClient)captureObj;
    }

    public void Start() {
        _running = true;
        _thread = new Thread(CaptureLoop) {
            IsBackground = true,
            Priority     = ThreadPriority.Highest,
            Name         = "MFM-WASAPI-Loopback"
        };
        _thread.Start();
    }

    void CaptureLoop() {
        uint taskIdx = 0;
        IntPtr mmcss = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIdx);
        try {
            _client.Start();
            using (var ev = new EventWaitHandle(false, EventResetMode.AutoReset)) {
                ev.SafeWaitHandle = _eventHandle;
                while (_running) {
                    if (!ev.WaitOne(2000)) continue;   // 2 s safety
                    uint packetFrames;
                    _capture.GetNextPacketSize(out packetFrames);
                    while (packetFrames > 0) {
                        IntPtr dataPtr;
                        uint   framesAvailable;
                        uint   flags;
                        long   devicePos, qpcPos;
                        int hr = _capture.GetBuffer(out dataPtr, out framesAvailable, out flags, out devicePos, out qpcPos);
                        if (hr >= 0) {
                            int byteCount = (int)framesAvailable * WaveFormat.BlockAlign;
                            byte[] buf    = RentBuffer(byteCount);
                            if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                                Array.Clear(buf, 0, byteCount);
                            else
                                Marshal.Copy(dataPtr, buf, 0, byteCount);
                            DataAvailable?.Invoke(buf, byteCount);
                            _capture.ReleaseBuffer(framesAvailable);
                        }
                        _capture.GetNextPacketSize(out packetFrames);
                    }
                }
            }
            _client.Stop();
            CaptureStopped?.Invoke(null);
        } catch (Exception ex) {
            CaptureStopped?.Invoke(ex);
        } finally {
            if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
        }
    }
    // ... Dispose, RentBuffer pool, etc.
}
```

**Latency floor:** Same as NAudio: 7-10 ms (OS engine period for loopback). **Zero advantage over Research #1 for this backend.**

**Why bother:** Removes the `WaveInEventArgs` heap allocation per buffer (NAudio allocates one per callback). At 100 Hz callback rate that's 360k allocs/hour. Doesn't lower latency, but does lower GC pressure → reduces tail-latency outliers when a Gen 0 collection happens to land mid-buffer.

---

### B. WASAPI Input — `CustomWasapi3Capture.cs` (IAudioClient3)
**Approach:** Same as A but use `IAudioClient3::InitializeSharedAudioStream`. This is the ONLY backend where the custom engine genuinely outperforms NAudio.

**Skeleton (~250 LOC delta vs A):**
```csharp
[ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient3 : IAudioClient2 {
    // ... IAudioClient + IAudioClient2 methods ...
    int GetSharedModeEnginePeriod([In] ref WAVEFORMATEX pFormat, out uint pDefaultPeriodInFrames, out uint pFundamentalPeriodInFrames, out uint pMinPeriodInFrames, out uint pMaxPeriodInFrames);
    int GetCurrentSharedModeEnginePeriod(out IntPtr ppFormat, out uint pCurrentPeriodInFrames);
    int InitializeSharedAudioStream(uint streamFlags, uint periodInFrames, [In] ref WAVEFORMATEX pFormat, [In] ref Guid AudioSessionGuid);
}
```

Init sequence:
```csharp
uint defaultPeriod, fundamentalPeriod, minPeriod, maxPeriod;
client3.GetSharedModeEnginePeriod(ref fmt, out defaultPeriod, out fundamentalPeriod, out minPeriod, out maxPeriod);
// minPeriod typically 48 frames @ 44.1k = 1.09 ms, or 144 frames @ 48k = 3 ms.
Guid sessionGuid = Guid.Empty;
int hr = client3.InitializeSharedAudioStream(
    AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
    minPeriod,
    ref fmt,
    ref sessionGuid);
```

**Latency:** **~1-3 ms** vs NAudio's ~10 ms.

**Caveat:** `IAudioClient3` was added in Windows 10 1607 (Anniversary Update, Aug 2016). We're already targeting modern Windows so no compat issue.

---

### C. WDM-KS / WASAPI Exclusive — `CustomWasapiExclusive.cs`
Same `IAudioClient` pattern as A, but `Initialize` with `AudioClientShareMode.Exclusive` and period = `GetDevicePeriod`'s min.

```csharp
long defaultPeriod, minPeriod;
_client.GetDevicePeriod(out defaultPeriod, out minPeriod);
// minPeriod ~30000 (3ms) on most modern interfaces, 100000 (10ms) on realtek codecs.
int hr = _client.Initialize(
    AudioClientShareMode.Exclusive,
    AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
    minPeriod, minPeriod, ref fmt, ref sessionGuid);
if (hr == AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED) {
    uint frames;
    _client.GetBufferSize(out frames);
    long aligned = (long)(10000000.0 * frames / fmt.nSamplesPerSec) + 1;
    // Release+re-Activate the client, retry.
    RetryWithAligned(aligned);
}
```

**Latency:** 3-5 ms on hardware; falls back to shared (~10 ms) on virtual endpoints. **Same as Research #1's NAudio version.**

**Why bother:** Same as A — no latency win, only GC pressure reduction.

**Pure WDM-KS direct (bypassing WASAPI entirely):** ~1500-3000 LOC of `IKsControl` / `KSPROPERTY_PIN_DATAFORMAT` / topology traversal. In Win10+ this rarely beats WASAPI Exclusive because Microsoft's stack reserializes. **Not worth pursuing — high complexity, marginal gain (if any).**

---

### D. MME — `CustomWaveIn.cs` (raw waveIn API)
**Approach:** Direct `winmm.dll` P/Invoke. Skip NAudio's `WaveInEvent` (which uses a hidden window + WM_WIM_DATA pump).

**Skeleton (~250 LOC):**
```csharp
[DllImport("winmm.dll")]
static extern uint waveInOpen(out IntPtr phwi, int uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
[DllImport("winmm.dll")]
static extern uint waveInPrepareHeader(IntPtr hwi, IntPtr lpwh, uint cbwh);
[DllImport("winmm.dll")]
static extern uint waveInAddBuffer(IntPtr hwi, IntPtr lpwh, uint cbwh);
[DllImport("winmm.dll")]
static extern uint waveInStart(IntPtr hwi);
[DllImport("winmm.dll")]
static extern uint waveInStop(IntPtr hwi);
[DllImport("winmm.dll")]
static extern uint waveInReset(IntPtr hwi);
[DllImport("winmm.dll")]
static extern uint waveInClose(IntPtr hwi);
[DllImport("winmm.dll")]
static extern uint waveInUnprepareHeader(IntPtr hwi, IntPtr lpwh, uint cbwh);

const uint CALLBACK_FUNCTION = 0x00030000;
const uint WIM_DATA          = 0x3C0;

[StructLayout(LayoutKind.Sequential)]
struct WAVEHDR {
    public IntPtr lpData;
    public uint   dwBufferLength;
    public uint   dwBytesRecorded;
    public IntPtr dwUser;
    public uint   dwFlags;
    public uint   dwLoops;
    public IntPtr lpNext;
    public IntPtr reserved;
}

public sealed class CustomWaveIn : ICaptureSource {
    IntPtr      _hwi;
    WAVEHDR[]   _headers;
    IntPtr[]    _bufferPtrs;
    IntPtr[]    _headerPtrs;
    GCHandle[]  _headerHandles;
    WaveInCallback _cb;        // keep delegate alive

    public CustomWaveIn(int deviceIndex, int sampleRate, int channels, int bufferMs, int bufferCount) {
        var fmt = new WAVEFORMATEX {
            wFormatTag      = 1,    // PCM
            nChannels       = (ushort)channels,
            nSamplesPerSec  = (uint)sampleRate,
            wBitsPerSample  = 16,
            nBlockAlign     = (ushort)(channels * 2),
            nAvgBytesPerSec = (uint)(sampleRate * channels * 2),
            cbSize          = 0
        };
        _cb = WaveInProc;
        uint err = waveInOpen(out _hwi, deviceIndex, ref fmt,
            Marshal.GetFunctionPointerForDelegate(_cb), IntPtr.Zero, CALLBACK_FUNCTION);
        if (err != 0) throw new Win32Exception((int)err);

        int bufferBytes = (sampleRate * channels * 2 * bufferMs) / 1000;
        _headers       = new WAVEHDR[bufferCount];
        _bufferPtrs    = new IntPtr[bufferCount];
        _headerPtrs    = new IntPtr[bufferCount];
        _headerHandles = new GCHandle[bufferCount];

        for (int i = 0; i < bufferCount; i++) {
            _bufferPtrs[i] = Marshal.AllocHGlobal(bufferBytes);
            _headers[i] = new WAVEHDR {
                lpData         = _bufferPtrs[i],
                dwBufferLength = (uint)bufferBytes
            };
            _headerHandles[i] = GCHandle.Alloc(_headers[i], GCHandleType.Pinned);
            _headerPtrs[i]    = _headerHandles[i].AddrOfPinnedObject();
            waveInPrepareHeader(_hwi, _headerPtrs[i], (uint)Marshal.SizeOf<WAVEHDR>());
            waveInAddBuffer(_hwi, _headerPtrs[i], (uint)Marshal.SizeOf<WAVEHDR>());
        }
    }

    delegate void WaveInCallback(IntPtr hwi, uint msg, IntPtr instance, IntPtr param1, IntPtr param2);

    void WaveInProc(IntPtr hwi, uint msg, IntPtr instance, IntPtr param1, IntPtr param2) {
        if (msg != WIM_DATA) return;
        // param1 is a pointer to the completed WAVEHDR.
        WAVEHDR hdr = Marshal.PtrToStructure<WAVEHDR>(param1);
        int byteCount = (int)hdr.dwBytesRecorded;
        if (byteCount > 0) {
            byte[] buf = RentBuffer(byteCount);
            Marshal.Copy(hdr.lpData, buf, 0, byteCount);
            DataAvailable?.Invoke(buf, byteCount);
        }
        // Re-queue this header for the next buffer.
        waveInAddBuffer(hwi, param1, (uint)Marshal.SizeOf<WAVEHDR>());
    }
    // ... Start/Stop/Dispose ...
}
```

**Latency:** With 5×8 (8 buffers of 5 ms each = 40 ms safety, 5 ms typical), we hit ~5 ms reliably. NAudio's `WaveInEvent` has hidden window-message dispatch overhead — this custom version uses a direct CALLBACK_FUNCTION which skips the WM_WIM_DATA pump.

**Win vs Research #1:** ~10 ms → ~5 ms. A real ~5 ms saved. Not huge, but real.

---

### E. ASIO — `CustomAsioHost.cs` (raw IASIO COM consumer)
**This is the centerpiece of Research #2.**

**Why bother going past Research #1's NAudio-low-level approach:**
- We don't have to ship NAudio.Asio (saves ~120 KB)
- We can implement the IASIO interface vtable manually, which means we can interpose on calls (e.g., log every `asioMessage` for diagnostics)
- We control sample-format conversion entirely (we can SIMD-optimize Int24LSB→Float32 if we want)
- We can implement multi-driver enumeration ourselves and pick the best driver heuristically

**Skeleton (~700 LOC):**
```csharp
// ASIO is COM-like but NOT proper COM — it doesn't use IUnknown.
// The vtable is hand-rolled. Driver CLSID is registered at
// HKLM\SOFTWARE\ASIO\<DriverName>\CLSID = "{guid}".

[ComImport, Guid("00000000-0000-0000-0000-000000000000"),  // per-driver CLSID
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IASIO {
    // The official IASIO has 21 methods. Critical ones:
    AsioBool init(IntPtr sysHandle);
    void     getDriverName([Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder name);
    int      getDriverVersion();
    void     getErrorMessage([Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder error);
    AsioError start();
    AsioError stop();
    AsioError getChannels(out int numInputs, out int numOutputs);
    AsioError getLatencies(out int inputLatency, out int outputLatency);
    AsioError getBufferSize(out int minSize, out int maxSize, out int preferredSize, out int granularity);
    AsioError canSampleRate(double sampleRate);
    AsioError getSampleRate(out double sampleRate);
    AsioError setSampleRate(double sampleRate);
    AsioError getClockSources([Out] AsioClockSource[] clocks, ref int numSources);
    AsioError setClockSource(int reference);
    AsioError getSamplePosition(out long samplePos, ref AsioTimeStamp timeStamp);
    AsioError getChannelInfo(ref AsioChannelInfo info);
    AsioError createBuffers(IntPtr bufferInfos, int numChannels, int bufferSize, ref AsioCallbacks callbacks);
    AsioError disposeBuffers();
    AsioError controlPanel();
    AsioError future(int selector, IntPtr opt);
    AsioError outputReady();
}

public sealed class CustomAsioHost : ICaptureSource {
    IASIO            _asio;
    Guid             _driverClsid;
    AsioCallbacks    _callbacks;   // unmanaged, pinned via GCHandle
    GCHandle         _callbacksHandle;
    AsioBufferInfo[] _bufferInfos;
    GCHandle         _bufferInfosHandle;
    IntPtr           _bufferInfosNative;
    int              _bufferSize;
    int              _inputChannels;
    int              _inputChannelOffset;
    AsioSampleType[] _sampleTypes;
    double           _sampleRate;
    byte[]           _interleaved;
    public event Action<byte[], int> DataAvailable;

    static readonly Dictionary<string, Guid> s_driverCache = new();

    public static string[] GetDriverNames() {
        // Enumerate HKLM\SOFTWARE\ASIO\ for installed drivers.
        var names = new List<string>();
        using (var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ASIO")) {
            if (root == null) return Array.Empty<string>();
            foreach (var sub in root.GetSubKeyNames()) {
                using (var driverKey = root.OpenSubKey(sub)) {
                    var clsid = driverKey?.GetValue("CLSID") as string;
                    if (Guid.TryParse(clsid, out var g)) {
                        names.Add(sub);
                        s_driverCache[sub] = g;
                    }
                }
            }
        }
        return names.ToArray();
    }

    public CustomAsioHost(string driverName, int inputChannelOffset, int requestedRate = 48000) {
        if (!s_driverCache.TryGetValue(driverName, out _driverClsid)) {
            GetDriverNames();  // populate cache
            if (!s_driverCache.TryGetValue(driverName, out _driverClsid))
                throw new Exception($"ASIO driver '{driverName}' not registered.");
        }
        var asioType = Type.GetTypeFromCLSID(_driverClsid);
        var asioInstance = Activator.CreateInstance(asioType);
        _asio = (IASIO)asioInstance;   // QI to IASIO

        if (_asio.init(IntPtr.Zero) != AsioBool.True)
            throw new Exception($"ASIO driver '{driverName}' init failed.");

        if (_asio.canSampleRate(requestedRate) != AsioError.OK) {
            // Multi-rate fallback (mirror current AsioCaptureAdapter logic).
            int[] rates = { 48000, 44100, 96000, 88200, 192000, 32000 };
            foreach (var r in rates) {
                if (_asio.canSampleRate(r) == AsioError.OK) {
                    requestedRate = r; break;
                }
            }
        }
        _asio.setSampleRate(requestedRate);
        _sampleRate = requestedRate;
        _inputChannelOffset = inputChannelOffset;

        int totalIn, totalOut;
        _asio.getChannels(out totalIn, out totalOut);
        _inputChannels = Math.Min(2, totalIn - inputChannelOffset);

        int minSize, maxSize, prefSize, granularity;
        _asio.getBufferSize(out minSize, out maxSize, out prefSize, out granularity);
        _bufferSize = minSize;   // *** the whole point ***

        // Probe channel format
        _sampleTypes = new AsioSampleType[_inputChannels];
        for (int i = 0; i < _inputChannels; i++) {
            var ci = new AsioChannelInfo { channel = inputChannelOffset + i, isInput = true };
            _asio.getChannelInfo(ref ci);
            _sampleTypes[i] = ci.type;
        }

        // Buffer infos (input only)
        _bufferInfos = new AsioBufferInfo[_inputChannels];
        for (int i = 0; i < _inputChannels; i++) {
            _bufferInfos[i] = new AsioBufferInfo {
                isInput    = AsioBool.True,
                channelNum = inputChannelOffset + i
            };
        }
        _bufferInfosHandle = GCHandle.Alloc(_bufferInfos, GCHandleType.Pinned);
        _bufferInfosNative = _bufferInfosHandle.AddrOfPinnedObject();

        // Callbacks: assemble unmanaged function pointers
        _callbacks = new AsioCallbacks {
            pfnBufferSwitch         = Marshal.GetFunctionPointerForDelegate<BufferSwitchDelegate>(BufferSwitchCallback),
            pfnSampleRateDidChange  = Marshal.GetFunctionPointerForDelegate<SampleRateChangedDelegate>(SampleRateChangedCallback),
            pfnAsioMessage          = Marshal.GetFunctionPointerForDelegate<AsioMessageDelegate>(AsioMessageCallback),
            pfnBufferSwitchTimeInfo = Marshal.GetFunctionPointerForDelegate<BufferSwitchTimeInfoDelegate>(BufferSwitchTimeInfoCallback)
        };
        _callbacksHandle = GCHandle.Alloc(_callbacks, GCHandleType.Pinned);

        var err = _asio.createBuffers(_bufferInfosNative, _inputChannels, _bufferSize, ref _callbacks);
        if (err != AsioError.OK) {
            // Fallback ladder (next granularity step, then preferred).
            FallbackBufferSize(minSize, maxSize, prefSize, granularity);
        }
    }

    void BufferSwitchCallback(int doubleBufferIndex, AsioBool directProcess) {
        // The per-channel native input buffer is at _bufferInfos[i].buffers[doubleBufferIndex].
        // Read AsioBufferInfo back from pinned memory (the driver wrote into it during createBuffers).
        int totalSamples = _bufferSize * _inputChannels;
        int byteCount = totalSamples * sizeof(float);
        if (_interleaved == null || _interleaved.Length < byteCount)
            _interleaved = new byte[byteCount];

        // Convert + interleave directly into _interleaved.
        ConvertAndInterleave(doubleBufferIndex);

        DataAvailable?.Invoke(_interleaved, byteCount);
    }

    void ConvertAndInterleave(int dblIdx) {
        // Re-read pinned _bufferInfos to get the current native buffer pointers.
        // For each frame f in [0, _bufferSize):
        //   For each channel c in [0, _inputChannels):
        //     float sample = ConvertSample(_sampleTypes[c], ptr + f * sampleBytes);
        //     _interleavedAsFloats[f * _inputChannels + c] = sample;
        // Hot loop, ~3 µs per call at 64-sample buffers. SIMD-able for Int24LSB→Float32.
    }
    // ... Start/Stop/Dispose ...
}
```

**The hard parts:**
1. **IASIO is not real COM.** It has a custom vtable layout and doesn't implement IUnknown. `Type.GetTypeFromCLSID` + `Activator.CreateInstance` followed by `(IASIO)` cast *does* work on most drivers because they DO derive from IUnknown internally (just not in the vtable). NAudio's `AsioDriver.GetAsioDriverByName` does exactly this dance — we'd be replicating it.
2. **Calling convention.** IASIO methods are `__thiscall` on x86, `__cdecl` on x64. Modern .NET handles this if we use `[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]`. NAudio also handles it — we'd inherit the same constraints.
3. **Sample conversion.** ASIO has 19 sample formats. We need conversion routines for the common 8 (Int16LSB/MSB, Int24LSB/MSB, Int32LSB/MSB, Float32LSB/MSB) + the "valid bits in container" variants (Int32LSB16/18/20/24). NAudio's `ASIOSampleConvertor` is ~200 LOC; we can copy-port it.
4. **Driver-specific quirks.** VB-Matrix sometimes returns `ASE_HWMalfunction` on the first `start()` and works on retry. ASIO4ALL has a race condition where `init()` fails if called within ~200 ms of process startup. Empirically: Research #1's existing retry logic in `AsioCaptureAdapter.StartRecording` is already good enough for these.

**Latency:** Same as Research #1 — `min` buffer size, ~1-5 ms depending on driver. **No latency win over #1.** Win is purely architectural (no NAudio.Asio dependency).

---

## Cross-cutting infrastructure

### Replacement of `MMDevice` / endpoint enumeration
NAudio's `MMDeviceEnumerator` is ~600 LOC of COM wrappers. We'd port the parts we use (~150 LOC of relevant subset): default-endpoint lookup, device-state filtering, property-store reads for `FriendlyName` and `DeviceState`.

### Thread management
- One dedicated capture thread per backend, `ThreadPriority.Highest`, MMCSS "Pro Audio".
- `SetThreadAffinityMask` to lock the thread to physical core 0 (or any specific core) — reduces tail-latency from migration.
- Watchdog thread (separate, normal priority) that monitors `_lastBufferTime`; if no buffer for 200 ms, signal a fault and let `audio_spectrum.cs`'s outer loop fall back.

### Byte buffer pool
Reuse `byte[]` buffers instead of allocating per callback. ~50 LOC. Eliminates GC pressure across all four backends.

### Sample-rate fallback
Mirror current `AsioCaptureAdapter`'s multi-rate retry logic. ~30 LOC.

---

## What Research #2 does NOT gain over Research #1

| Backend | Research #1 latency | Research #2 latency | Δ |
|---|---|---|---|
| WASAPI Loopback | 7-10 ms | 7-10 ms | **0 ms** (OS-bound) |
| WASAPI Input | 3-10 ms (Stage 2 IAudioClient3) | 2-3 ms | ~3-5 ms |
| WDM-KS / Exclusive | 3-5 ms | 3-5 ms | **0 ms** (HW-bound) |
| MME | 10 ms | 5 ms | ~5 ms |
| ASIO | 1-5 ms | 1-5 ms | **0 ms** (driver-bound) |

**Honest total saving over Research #1: ~5-10 ms in the worst case (input + MME), zero everywhere else.**

If Research #1 already implements the IAudioClient3 Stage 2 sidecar for WASAPI Input, the entire Research #2 saving collapses to ~5 ms (MME only). At that point Research #2 buys us nothing over Research #1 except code ownership.

---

## Cost analysis

| Item | Research #1 | Research #2 |
|---|---|---|
| New LOC | ~625 | ~3000-4000 |
| Deleted LOC | ~80 | ~200 (existing NAudio adapters) |
| Effort | ~2 dev days + testing | ~3-6 weeks full-time |
| Dependencies removed | None | NAudio (~150-300 KB binary saving) |
| Maintenance burden | Low (NAudio still ships fixes) | High (we own all COM/P/Invoke) |
| Win10/11 compat risk | Low | Medium (we're more sensitive to OS API quirks) |
| Per-driver test surface | 5 ASIO drivers × 4 backends | Same, but every regression is now ours |
| GC pressure | Same as today | Lower (~360k fewer allocs/hr) |

**Hidden cost of #2 we should not gloss over:** every Windows feature update can break a hand-rolled COM wrapper. NAudio's maintainers absorb that cost for us. By going custom, we volunteer to fix `INVALID_INTERFACE` regressions on Win11 25H2, etc.

---

## Recommendation

**Don't ship Research #2 unless / until Research #1 is in production and the operator confirms WASAPI Input or MME latency is the limiting factor in real-world feel.**

Reasoning:
1. The ASIO win is identical in both research paths. #2 doesn't help where the operator was most frustrated.
2. Loopback floor (~7-10 ms) is identical in both paths — Windows imposes it.
3. The only real wins #2 offers are 3-5 ms on WASAPI Input and ~5 ms on MME. Both are below human perceptual threshold for spectrum motion.
4. Cost ratio is roughly 15-20x for ~5 ms gain in two backends.

**Hybrid recommendation:** Ship Research #1 in full now. If post-deploy telemetry shows users on WASAPI Input or MME are still complaining, *selectively* port THOSE two backends from #2 (custom IAudioClient3 + custom waveIn) — that's ~500 LOC, not the whole engine.

The full #2 rebuild is a tool to keep in reserve, not a current spend.
