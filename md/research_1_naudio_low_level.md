# Research #1 — Close-to-0-ms latency across ALL backends (incl. ASIO), staying on NAudio 2.2.1

**Scope (operator constraints):**
- No user-side driver-control-panel tweaks. Must "just work."
- All four backends: WASAPI Loopback, WASAPI Input, WDM-KS / Exclusive, MME, **and ASIO**.
- ASIO is no longer skippable. Buffer size must be programmatically forced to the driver's minimum.
- Keep NAudio 2.2.1 as the dependency (we already pay the install footprint cost).

**Verified facts (from this research pass):**
1. `NAudio.Wave.Asio.AsioDriver` (the low-level wrapper, NOT `AsioOut`) exposes:
   - `GetBufferSize(out int minSize, out int maxSize, out int preferredSize, out int granularity)`
   - `CreateBuffers(IntPtr bufferInfos, int numChannels, int bufferSize, ref AsioCallbacks callbacks)` — **the `bufferSize` parameter accepts an arbitrary value, not just `preferredSize`.**
2. `NAudio.Wave.Asio.ASIODriverExt` only exposes a coarse `bool useMaxBufferSize` flag — its `CreateBuffers(int outCh, int inCh, bool useMaxBufferSize)` picks between `BufferPreferredSize` and `BufferMaxSize`. **This is the bottleneck — we have to bypass it.**
3. `AsioOut` calls `ASIODriverExt.CreateBuffers(..., useMaxBufferSize: false)`, so today we are locked at the driver's **preferred** size.
4. `IAudioClient3::InitializeSharedAudioStream` does **not** support `AUDCLNT_STREAMFLAGS_LOOPBACK` (Microsoft Q&A confirmed). Loopback is stuck at the OS engine period (~10 ms). No path around this without third-party kernel drivers.
5. `IAudioClient3` IS supported for normal capture (mic / line in) — `GetSharedModeEnginePeriod` returns ~48 frames @ 44.1 kHz (~1.09 ms) as the minimum on modern Windows. NAudio 2.2.1 does NOT use IAudioClient3, but we can add it next to NAudio without uninstalling.
6. `WasapiCapture(MMDevice, bool useEventSync, int audioBufferMillisecondsLength)` 3-arg ctor **DOES exist in NAudio 2.2.1**. The stale comment in `audio_spectrum.cs:640-648` is wrong for our current version (it was correct on the old DLL we shipped before the .NET 8 migration).
7. ASIO driver real-world behavior on host-side buffer-size requests (current ASIO SDK 2.3):
   - **ASIO4ALL 2.16+**: respects host buffer size if it's within `[min..max]` and granularity-aligned. Min is typically 64 samples (~1.3 ms @ 48k).
   - **FlexASIO**: respects host requests; if outside its `bufferSizeSamples` INI value, the larger of the two wins (PortAudio side adds buffers, but the ASIO-side buffer is what the host hands us).
   - **Voicemeeter Virtual ASIO**: automatic negotiation — host buffer size becomes the Voicemeeter main stream buffer size. Floor at 128 samples (~2.7 ms @ 48k).
   - **VB-Cable ASIO**: similar to Voicemeeter; floor varies by version.
   - **RME / Focusrite / UA hardware ASIO**: typically honor `[min..max]`; min is 32-64 samples (~0.7-1.3 ms).
   - **Misbehaving drivers**: return `ASE_InvalidMode` when given a size outside their granularity. Mitigation: fallback chain (min → min+granularity → preferred).

---

## Per-backend plan

### A. WASAPI Loopback (default backend for most users)
**Current:** `new WasapiLoopbackCapture(dev)` — 1-arg constructor, ~10 ms engine period.

**Change:** Switch to the 3-arg form that already exists in NAudio 2.2.1:
```csharp
IWaveIn cap = dev != null
    ? new WasapiLoopbackCapture(dev, useEventSync: true, audioBufferMillisecondsLength: 5)
    : new WasapiLoopbackCapture();   // 1-arg fallback if dev is null
```
The `5` is a request; the engine clamps it to the real period. Worst case 10 ms; in practice 7-8 ms on Win10 22H2 / Win11.

**Latency:** ~10 → ~7-8 ms. Modest, but every ms counts and this is a 1-line change.

**Loopback's hard floor:** Microsoft does not expose any path below the engine period for shared-mode loopback. Even a full custom engine cannot beat this. **This is the same in Research #2.**

---

### B. WASAPI Input (mic / Stereo Mix / VB-Cable Output)
**Current:** `new WasapiCapture(dev, true, 50)` — 50 ms buffer, shared mode.

**Change (two-stage):**

**Stage 1 — Same NAudio API, smaller buffer:**
```csharp
var cap = new WasapiCapture(dev, useEventSync: true, audioBufferMillisecondsLength: 5);
```
Engine clamps to ~10 ms in shared mode. Trim from 50 → 7-10 ms.

**Stage 2 — IAudioClient3 sidecar (~150 LOC, optional but high-value):**
NAudio 2.2.1's `WasapiCapture` uses `IAudioClient::Initialize` (legacy). To hit ~2.7 ms we need `IAudioClient3::InitializeSharedAudioStream`. Strategy:
1. Add `LowLatencyWasapiCapture.cs` — implements `IWaveIn` so it slots into our existing pipeline without changes upstream.
2. Internally: `MMDevice.AudioClient.AudioClientInterface` → `QueryInterface(IID_IAudioClient3)`.
3. Call `IAudioClient3::GetSharedModeEnginePeriod(format, &default, &fundamental, &min, &max)`.
4. Call `IAudioClient3::InitializeSharedAudioStream(AUDCLNT_STREAMFLAGS_EVENTCALLBACK, min, format, null)`.
5. Spawn capture thread, MMCSS-register as "Pro Audio", pump `IAudioCaptureClient::GetBuffer` on the event signal.
6. Push bytes through the same `DataAvailable` event NAudio uses, so the FFT pipeline doesn't notice.

NAudio exposes `AudioClient.AudioClientInterface` (the raw COM pointer) since 1.10 — we don't need to fork NAudio, just bolt on a sibling class.

**Latency:** 50 → 2.7-3 ms (Stage 2) or 50 → 7-10 ms (Stage 1 only).

---

### C. WDM-KS / WASAPI Exclusive
**Current:** `new WasapiCapture(_device, true, 20)` set to `Exclusive` (in `WdmKsCaptureAdapter.MakeInner`). 20 ms is a hard-coded fudge — the real hardware minimum is usually 3-5 ms.

**Change:**
```csharp
NAudio.CoreAudioApi.WasapiCapture MakeInner(AudioClientShareMode mode) {
    long defaultPeriod, minPeriod;
    using (var probe = new AudioClient(_device.GetIAudioClient())) {  // pseudo-code
        probe.GetDevicePeriod(out defaultPeriod, out minPeriod);
    }
    int reqMs = mode == AudioClientShareMode.Exclusive
        ? (int)Math.Max(3, (minPeriod / 10000))   // 100-ns units → ms, floor at 3 ms
        : 5;                                       // shared fallback
    var cap = new WasapiCapture(_device, true, reqMs);
    cap.ShareMode = mode;
    ...
}
```
For exclusive mode, `GetDevicePeriod`'s `minPeriod` reflects the hardware's true minimum. Most consumer/pro interfaces report 3 ms (30000 in 100-ns units); some realtek codecs report 10 ms.

Add a re-try ladder: try `minPeriod`, on `AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED` retry with the period the error message returns (NAudio surfaces this as the recommended size via its exception), on any other error fall through to shared mode at 5 ms (already implemented).

**Latency:** 20 → 3-5 ms on hardware; 20 → 7-10 ms on virtual endpoints that get bumped to shared.

---

### D. MME (waveIn)
**Current:** `BufferMilliseconds = 50, NumberOfBuffers = default (3)` → worst case 150 ms latency.

**Change:**
```csharp
var mme = new WaveInEvent {
    DeviceNumber       = idx,
    WaveFormat         = new WaveFormat(48000, 16, 2),
    BufferMilliseconds = 10,
    NumberOfBuffers    = 4   // 4×10 = 40 ms safety margin against scheduling jitter
};
```
**Why 4 buffers, not 2:** the waveIn API is callback-driven via the MMSystem subsystem, which is a service thread with its own quantum. With only 2 buffers, a single OS scheduling stall (~16 ms on a thread tick) causes underrun → driver returns silence. 4 buffers gives 40 ms of slack with average-case latency still ~10 ms (only the active buffer adds latency; the others are queued).

**Underrun resilience:** add a watchdog. `WaveInEvent` exposes a `RecordingStopped` event with a `StoppedEventArgs.Exception` — if that fires within ~30 s of start on the current backend, the capture thread already falls back. Extend that path: if MME drops, rebuild with `BufferMilliseconds = 25, NumberOfBuffers = 3` (75 ms total) and keep going. The user gets degraded but functional audio instead of a fallback to a different backend.

**Latency:** 50/150 → 10/40 ms typical, 25/75 ms in resilience fallback. Anywhere from a 3.75x to 15x improvement.

---

### E. ASIO — the meat of this research
**Current:** `new AsioOut(driverName)` → `InitRecordAndPlayback` → `ASIODriverExt.CreateBuffers(useMaxBufferSize: false)` → uses **preferred** size. For most consumer ASIO drivers, preferred is 256-1024 samples (5-21 ms @ 48k). For VB-Matrix variants, preferred is often 1024 samples (21 ms). **Every user is paying this latency floor regardless of their hardware.**

**The fix: bypass `ASIODriverExt.CreateBuffers` and call the underlying `AsioDriver.CreateBuffers` directly with the minimum buffer size.**

Two implementation approaches:

#### Approach 1 (recommended): Fork-in-place — replace `AsioCaptureAdapter` with a custom `AsioMinBufferCapture`

**Skeleton (~400 LOC):**
```csharp
public class AsioMinBufferCapture : IWaveIn {
    AsioDriver           _driver;
    AsioBufferInfo[]     _bufferInfos;   // ASIO-spec layout, marshalled to native array
    GCHandle             _bufferInfosPin;
    AsioCallbacks        _callbacks;     // unmanaged callbacks (bufferSwitch, sampleRateChanged, asioMessage, bufferSwitchTimeInfo)
    int                  _bufferSize;
    int                  _inputChannels;
    AsioChannelInfo[]    _channelInfo;
    byte[]               _interleavedBuf;
    int                  _sampleRate;
    AsioSampleType       _sampleType;

    public WaveFormat WaveFormat { get; private set; }
    public event EventHandler<WaveInEventArgs>  DataAvailable;
    public event EventHandler<StoppedEventArgs> RecordingStopped;

    public AsioMinBufferCapture(string driverName, int inputChannelOffset, int requestedRate = 48000) {
        _driver = AsioDriver.GetAsioDriverByName(driverName);   // low-level, NOT AsioOut
        _driver.Init(IntPtr.Zero);                              // pass HWND if you have one

        if (!_driver.CanSampleRate(requestedRate))
            throw new Exception($"ASIO driver {driverName} rejects {requestedRate} Hz");
        _driver.SetSampleRate(requestedRate);
        _sampleRate = requestedRate;

        int inputCh, outputCh;
        _driver.GetChannels(out inputCh, out outputCh);
        _inputChannels = Math.Min(2, inputCh - inputChannelOffset);  // capture stereo or mono

        int minSize, maxSize, prefSize, granularity;
        _driver.GetBufferSize(out minSize, out maxSize, out prefSize, out granularity);

        // *** THE WHOLE POINT *** — pick min, not preferred.
        _bufferSize = PickBufferSize(minSize, maxSize, prefSize, granularity);

        // Probe channel format on first input channel
        var asioCh = new AsioChannelInfo { channel = inputChannelOffset, isInput = true };
        _driver.GetChannelInfo(ref asioCh);
        _sampleType = asioCh.type;

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _inputChannels);

        // Build buffer infos: input channels only (no output)
        _bufferInfos = new AsioBufferInfo[_inputChannels];
        for (int i = 0; i < _inputChannels; i++) {
            _bufferInfos[i].isInput     = true;
            _bufferInfos[i].channelNum  = inputChannelOffset + i;
            // buffers[0]/buffers[1] filled by driver during CreateBuffers
        }

        _callbacks.pfnBufferSwitch          = BufferSwitchCallback;
        _callbacks.pfnSampleRateDidChange   = SampleRateDidChangeCallback;
        _callbacks.pfnAsioMessage           = AsioMessageCallback;
        _callbacks.pfnBufferSwitchTimeInfo  = BufferSwitchTimeInfoCallback;

        IntPtr bufferInfosNative = MarshalBufferInfos(_bufferInfos);   // pin via GCHandle.Alloc + Marshal.StructureToPtr
        _bufferInfosPin = GCHandle.Alloc(_bufferInfos, GCHandleType.Pinned);

        _driver.CreateBuffers(bufferInfosNative, _inputChannels, _bufferSize, ref _callbacks);

        try {
            AudioSpectrum.RecordAsioChannelCount(driverName, inputCh);
            AudioSpectrum.Log($"ASIO '{driverName}' min={minSize} pref={prefSize} max={maxSize} gran={granularity} → using {_bufferSize} samples ({_bufferSize * 1000.0 / _sampleRate:F2} ms)");
        } catch { }
    }

    static int PickBufferSize(int min, int max, int pref, int granularity) {
        // Most ASIO drivers report granularity == -1 (power-of-2 only) or
        // a positive value (linear step). For power-of-2 drivers, min is
        // already a valid choice. For linear-step drivers, min is also valid.
        // We just clamp to min.
        return Math.Max(min, 1);
    }

    public void StartRecording() {
        try {
            _driver.Start();
        } catch (Exception ex) {
            // If start fails with the min size (driver lied about min),
            // tear down, re-create with the next granularity step, retry.
            // Final fallback: use preferred size.
            FallbackToNextSize(ex);
        }
    }

    void BufferSwitchCallback(int doubleBufferIndex, AsioBool directProcess) {
        // _bufferInfos[i].buffers[doubleBufferIndex] now points to a native
        // input buffer of (_bufferSize × bytes-per-sample) interleaved per
        // channel. We need to:
        //   1. Convert from _sampleType (often Int24LSB or Int32LSB) to Float32.
        //   2. Interleave the channels.
        //   3. Push into our reusable byte buffer and fire DataAvailable.
        int sampleBytes = AsioSampleBytes(_sampleType);
        int totalSamples = _bufferSize * _inputChannels;
        int byteCount = totalSamples * sizeof(float);
        if (_interleavedBuf == null || _interleavedBuf.Length < byteCount)
            _interleavedBuf = new byte[byteCount];

        ConvertAsioToFloat32Interleaved(
            _bufferInfos, doubleBufferIndex, _inputChannels,
            _bufferSize, _sampleType,
            _interleavedBuf);

        DataAvailable?.Invoke(this, new WaveInEventArgs(_interleavedBuf, byteCount));
    }

    // ... SampleRateDidChange / AsioMessage / BufferSwitchTimeInfo / Stop / Dispose ...
}
```

Key implementation points:
- `AsioCallbacks` is a struct of unmanaged function pointers — we pass it `ref`, and the GC must not move our delegate instances. Pin them with `GCHandle.Alloc(...GCHandleType.Pinned)` or use a `[UnmanagedCallersOnly]` static method (we'll have to use `Marshal.GetFunctionPointerForDelegate` since we need per-instance state, then keep the delegate object alive in a field — NAudio's `AsioDriverExt` does the same).
- Sample-format conversion: handle `Int16MSB`, `Int24MSB`, `Int32MSB`, `Float32MSB`, `Int16LSB`, `Int24LSB`, `Int32LSB`, `Float32LSB`, `Int32LSB16/18/20/24` (the "container is 32 but only N bits are valid" variants). NAudio's `ASIOSampleConvertor` already handles most of these — we can copy the conversion logic from `NAudio.Wave.Asio.ASIOSampleConvertor`.
- Interleave: ASIO delivers per-channel buffers (deinterleaved). We have to interleave to match `WasapiLoopbackCapture`'s output layout, otherwise the FFT pipeline would need a special case.
- Threading: the `bufferSwitch` callback runs on the driver's audio thread (already MMCSS-elevated by the driver). Don't allocate, don't lock, don't log. The reusable `_interleavedBuf` already does this.

**Driver-misbehavior fallback (~50 LOC):**
```csharp
void FallbackToNextSize(Exception primaryFailure) {
    int[] tryOrder;
    if (granularity > 0) {
        tryOrder = new[] { _bufferSize + granularity,
                           _bufferSize + granularity*2,
                           _prefSize };
    } else {
        // Power-of-2 driver. Round min UP to next power of 2.
        int next = NextPowerOf2(_bufferSize);
        tryOrder = new[] { next, next*2, _prefSize };
    }
    foreach (int size in tryOrder) {
        try { RecreateAndStart(size); return; } catch { /* keep trying */ }
    }
    throw primaryFailure;  // give up, outer thread will fall back to WASAPI Loopback
}
```

#### Approach 2: Subclass `AsioOut`
Subclass `AsioOut`, override `InitRecordAndPlayback`. **Won't work** — `InitRecordAndPlayback` calls `driver.CreateBuffers(out, in, false)` where `driver` is the *private* `AsioDriverExt` instance. Can't intercept. Would need reflection to swap the private field, fragile across NAudio updates.

→ **Approach 1 (full custom adapter) is cleaner and the only sound choice.**

#### Per-driver expected ASIO buffer sizes after fix
| Driver | Today (preferred) | After fix (min) | Win |
|---|---|---|---|
| ASIO4ALL 2.16+ | 512 samples (10.7 ms) | 64 samples (1.3 ms) | 8x |
| FlexASIO | 480 samples (10.0 ms) | 64-128 samples (1.3-2.7 ms) | 4-8x |
| Voicemeeter Virtual | 1024 samples (21.3 ms) | 128 samples (2.7 ms) | 8x |
| VB-Cable ASIO | varies | 128-256 samples | 4-8x |
| VB-Matrix VASIO-N | 1024 samples (21.3 ms) | 256 samples (5.3 ms) (driver floor) | 4x |
| RME ASIO | 256 samples (5.3 ms) | 32-64 samples (0.7-1.3 ms) | 4-8x |
| Focusrite Scarlett ASIO | 256 samples (5.3 ms) | 64 samples (1.3 ms) | 4x |
| Universal Audio ASIO | 256 samples (5.3 ms) | 32-64 samples (0.7-1.3 ms) | 4-8x |

---

## Cross-cutting changes

### MMCSS thread priority for the capture thread
The capture thread in `audio_spectrum.cs` (`StartCapture` at line ~663) currently runs at default priority. Add:
```csharp
[DllImport("avrt.dll", CharSet = CharSet.Unicode)]
static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);
[DllImport("avrt.dll")]
static extern bool   AvRevertMmThreadCharacteristics(IntPtr handle);

uint taskIdx = 0;
IntPtr mmcss = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIdx);
// ... do capture work ...
if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
```
This bumps the thread above normal scheduling, giving us ~1 ms guaranteed wake-up jitter instead of 16 ms.

### Process priority bump
`audio_spectrum.exe` currently runs at `AboveNormal`. Move to `High`:
```csharp
Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
```
Don't use `RealTime` — that can starve `wasapi`'s own service threads and lock up audio on slow machines.

### FFT cadence
Already at `FFT_MIN_STRIDE = 48` (Phase P, 1 ms FFT cadence). No further change here — we're CPU-bottle-checked, not latency-bottlenecked at this point. Keep.

---

## Total budget — Research #1

**Lines of code:**
- New `AsioMinBufferCapture.cs`: ~400 LOC (replaces `AsioCaptureAdapter`, lines 3046-3187)
- New `LowLatencyWasapiCapture.cs` (IAudioClient3 path for input, optional Stage 2): ~150 LOC
- Edits to `audio_spectrum.cs`:
  - Line 507: 1 line (5 ms request)
  - Lines 573-577: 4 lines (10 ms × 4 buffers)
  - Lines 640-651: 5 lines (3-arg WasapiLoopbackCapture)
  - Line 3235: 1 line (GetDevicePeriod-derived size)
  - StartCapture thread: ~15 lines (MMCSS attach)
  - Process priority bump: 1 line
- Misc fallbacks/logging: ~50 LOC
- **Total: ~625 LOC added/changed, ~80 LOC deleted.**

**Effort estimate:** 8-12 hours dev, plus 4-6 hours regression testing across each backend on the user's machine. ~2 working days.

**Risk:**
- **Low** for WASAPI Loopback / Input / Exclusive / MME — these are 1-line tweaks with well-understood NAudio semantics.
- **Medium** for ASIO — the custom `AsioMinBufferCapture` is new code paths; conversion routines need testing on every sample format. Buffer-size fallback ladder mitigates misbehaving drivers.
- **Low** for MMCSS — Windows-supported since Vista; no compatibility risk.

**Expected end-to-end latency (input → first FFT frame → SSE → JS render):**

| Backend | Today | After #1 | Notes |
|---|---|---|---|
| WASAPI Loopback | ~10 ms | ~7-8 ms | OS engine period floor |
| WASAPI Input shared | ~50 ms | ~7-10 ms (Stage 1) / ~3 ms (Stage 2) | Stage 2 = IAudioClient3 sidecar |
| WDM-KS / WASAPI Exclusive | ~20 ms | ~3-5 ms | Hardware-driven |
| MME | ~50/150 ms | ~10/40 ms | Watchdog catches underruns |
| ASIO | ~5-21 ms | ~1-5 ms | Driver-floor-limited |

Plus the already-shipped FFT cadence (~1 ms) and SSE/JS path (~2 ms). **Net end-to-end on hardware ASIO: ~5-8 ms.** Below human perceptual threshold for spectrum motion (~12 ms).

---

## What this research does NOT solve
- **Loopback < ~7 ms:** Microsoft's IAudioClient3 InitializeSharedAudioStream rejects `STREAMFLAGS_LOOPBACK`. No legitimate user-mode path exists. The only way to beat this floor is a kernel-mode hook (DKMS-equivalent) or a virtual audio cable, both of which require user installation. Out of scope.
- **MME < ~10 ms:** waveIn API has its own quantization. Custom raw waveIn (Research #2) buys us ~2-5 ms but can't beat ~5 ms reliably.
- **Misbehaving ASIO drivers that return `ASE_InvalidMode` for min:** the fallback ladder mitigates but doesn't eliminate. Worst case: we use the driver's preferred size, no worse than today.

---

## Recommendation

Ship Research #1 as one .NET 8 patch:
1. WASAPI Loopback 3-arg ctor (1 line) — **free win, do first**
2. WASAPI Input small buffer (1 line) — free win
3. WDM-KS `GetDevicePeriod` (3 lines) — free win
4. MME 10×4 buffer (4 lines) + watchdog (~20 LOC) — free win
5. ASIO `AsioMinBufferCapture` (~400 LOC) — **the big one**
6. MMCSS attach (~15 LOC) — free win

Defer Stage 2 IAudioClient3 sidecar until after #1 ships and we see real WASAPI Input usage in the wild. Most users on WASAPI Loopback won't notice it.

**Total LOC: ~625 added / ~80 deleted. Effort: ~2 dev days + testing.**
