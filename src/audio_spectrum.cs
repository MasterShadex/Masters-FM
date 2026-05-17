// audio_spectrum.cs - Master's FM multi-backend spectrum server (v5.3.0)
// Compiles to audio_spectrum.exe.  Captures audio via the user's choice
// of WASAPI Loopback / WASAPI Input / MME / ASIO, runs a 2048-point FFT,
// groups bins into 120 log-spaced bands, and serves them as Server-Sent
// Events on http://127.0.0.1:4243/spectrum.
//
// Backends (picked via the tray Audio Source dialog):
//
//   WASAPI Loopback   (default) - captures the output mix of any render
//                                 endpoint. Works out of the box for
//                                 Spotify, SoundCloud, YouTube, etc.
//   WASAPI Input      (capture) - captures an input endpoint (Stereo Mix,
//                                 VB-Cable output, microphone).
//   WDM-KS / Exclusive        - WASAPI exclusive-mode capture, which
//                                 goes through the same WDM-KS kernel
//                                 path professional audio apps use for
//                                 low-latency exclusive access.
//   MME (legacy WaveIn)       - the old Windows Multimedia Extensions
//                                 path via waveIn. Best for legacy
//                                 "Stereo Mix" inputs on Realtek cards
//                                 and any device that only exposes MME.
//   ASIO (Steinberg)          - pro audio drivers. VB-Audio Matrix /
//                                 Voicemeeter / FL Studio / Reaper users
//                                 can capture their ASIO driver's input
//                                 channels directly, bypassing Windows
//                                 audio entirely (the only way to see
//                                 VB-Matrix's internal buses).
//
// The overlay opens an EventSource to /spectrum; each frame arrives as
// JSON {f,b} where b is a 120-byte array of band magnitudes (0..255) -
// same shape `analyser.getByteFrequencyData()` produces.
//
// Why a separate exe: pkg (Node bundler) can't bundle native WASAPI/ASIO
// code, and PowerShell in the tray doesn't have easy access to these
// APIs. A small managed C# exe is the cleanest boundary - launcher.cs
// puts it in the Job Object so it dies when MastersFM.exe exits.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

[assembly: AssemblyTitle("Master's FM Audio Spectrum")]
[assembly: AssemblyDescription("Master's FM multi-backend spectrum provider (WASAPI/MME/KS/ASIO)")]
[assembly: AssemblyProduct("Master's FM")]
[assembly: AssemblyCompany("MasterShadex")]
[assembly: AssemblyVersion("5.3.0.0")]
[assembly: AssemblyFileVersion("5.3.0.0")]

class AudioSpectrum
{
    // Log path lives alongside every other Master's FM log in %LOCALAPPDATA%.
    static readonly string s_logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MastersFM");
    static readonly string s_logPath = Path.Combine(s_logDir, "audio_spectrum.log");

    // Output bands — 480 log-spaced bins (v6.3.3 bump from 240). 48 bars per
    // octave across the full 20 Hz – 20 kHz audible range - roughly 4x the
    // resolution of a pro-audio parametric EQ. Users who want individual
    // frequency spikes (a kick at 60 Hz, a snare crack at 2 kHz, cymbal
    // shimmer at 12 kHz) to show as SHARP single-bar spikes rather than
    // smearing across 3-4 neighbors now have the bar-grain to do so, and
    // the FFT_SIZE=8192 below gives the underlying frequency resolution to
    // back those bars with genuinely distinct data.
    const int BAND_COUNT = 480;
    // FFT size - 8192 at 48 kHz gives ~5.86 Hz bin width (half of the v6.0.5
    // 4096's ~11.7 Hz). With BAND_COUNT=480, the low-end bars from 20 Hz
    // upwards each map to a narrower FFT slice so adjacent bands actually
    // represent distinct frequencies (not just interpolated sub-bin values).
    // Window latency: 8192 / 48000 = 170 ms per FFT window — the time from
    // an audio transient hitting the mic to the bar moving. That's 2x the
    // previous 85 ms but still inside the perceptual "instant" threshold
    // for music visualizers, and the overlay's rise-snap + heartbeat
    // decay keep the visible response feeling fast. Idle cost 0 % (silence
    // gate + active-client counter gate the FFT entirely); active cost
    // ~1 % CPU on modern hardware.
    // v6.7.2: FFT_SIZE back to 2048 — that was the value at v6.6.7,
    // the most recent "this is great" moment per the user. Pairs
    // with HOP_SIZE=8 (default) for a 42 ms integration window and
    // 0.17 ms cadence. v6.7.1's FFT=8192 was too big a jump back.
    const int FFT_SIZE = 2048;
    // v8.2.8: FFT_SIZE - 1, used as a bitmask for circular-buffer wraparound.
    // Was a const local in DoFftAndPublish; promoted to class scope so the
    // OnData per-sample loop can also use it (`writePos = (writePos + 1) & FFT_MASK`
    // is several times faster than `% FFT_SIZE` on the critical hot path
    // that runs at ~96 000 calls/sec at 48 kHz × 2 ch).
    const int FFT_MASK = FFT_SIZE - 1;
    // v6.8.3: SSE interval 4 ms -> 16 ms (60 Hz, matches typical
    // monitor refresh). User report: audio_spectrum.exe still at
    // 6.3 % CPU after v6.8.2, and the dominant cost was the SSE
    // encoding loop running 250x per second. The overlay's rAF
    // can only render at the monitor's refresh rate (60-240 Hz),
    // so 250 Hz of SSE writes was 4x more Base64+HTTP work than
    // any display could possibly show. At 60 Hz, the overlay
    // still gets fresh data on every monitor frame (1:1 mapping
    // on the dominant 60 Hz hardware), but Base64 encoding cost
    // drops ~75 %. Should bring audio_spectrum.exe back to 1-2 %.
    // v7.0.6: dropped 16 → 8 ms (120 Hz publishes). The overlay now sees fresh
    // data twice as often, so transient peaks land in the rendered bars within
    // ~8 ms instead of ~16 ms after the FFT. SSE traffic is tiny (~480 bytes
    // per frame at BAND_COUNT=480) so doubling the rate adds negligible IO
    // cost; CPU stays low because the silence-skip below means there's no
    // band processing on quiet frames anyway.
    const int SSE_INTERVAL_MS = 8;
    // Silence gate — when the mono-mixed sample RMS stays below this
    // threshold, skip the FFT entirely (emit zeros, let the envelope
    // decay). Covers "truly silent" (no audio playing) and "background
    // apps that idle the audio graph" cases so we don't burn CPU on noise.
    const float SILENCE_RMS = 1.0e-4f;

    // Latest computed bands, atomically swapped each capture frame. The HTTP
    // SSE loop reads this on its own cadence so the capture and serve
    // threads don't need to coordinate.
    static volatile byte[] s_latest = new byte[BAND_COUNT];
    // Monotonic frame counter so the SSE client can tell if the spectrum
    // is stuck (e.g. capture thread hung). Published as `f=<n>` in the JSON.
    static long s_frame = 0;
    // v7.0.8: event-driven SSE signaling — actually wired up this time.
    // The v6.5.4 attempt was reverted because it added stopwatch-based
    // rate-limiting + skip-via-continue paths that produced jittery send
    // cadences the overlay then picked up on. The clean approach (this
    // one) is JUST a WaitOne on a per-client AutoResetEvent with
    // SSE_INTERVAL_MS as keep-alive timeout — no rate-limiting, no
    // continue paths. DoFftAndPublish Sets every client's event after
    // writing s_latest; each client wakes immediately on every new
    // frame regardless of how many other clients are connected (Set on
    // a single shared AutoResetEvent only wakes ONE waiter, hence the
    // per-client list). Latency from FFT-publish to SSE-write drops
    // from ~4 ms average (8 ms poll period / 2) to ~sub-ms.
    static AutoResetEvent s_newFrameEvent = new AutoResetEvent(false);   // legacy, kept so old code paths compile
    static bool s_lastSilenceWasZero = false;   // v7.0.9: silence-publish-skip flag
    static readonly System.Collections.Generic.List<AutoResetEvent> s_sseClientEvents = new System.Collections.Generic.List<AutoResetEvent>();
    static readonly object s_sseClientEventsLock = new object();
    static void SignalAllSseClients() {
        // v7.0.10: lock-free fast path. Reading Count without the lock is
        // racy but harmless — if we skip while there's actually 1 client
        // mid-add, that client misses ONE update but catches it on the
        // very next FFT (0.5 ms at HOP=24, 10.7 ms at HOP=512), or on the
        // SSE_INTERVAL_MS keep-alive timeout, whichever comes first. With
        // the producer running at 2000 Hz on the user's typical settings,
        // the per-cycle save is small but compounds over an entire stream.
        if (s_sseClientEvents.Count == 0) return;
        lock (s_sseClientEventsLock) {
            for (int i = 0; i < s_sseClientEvents.Count; i++) {
                try { s_sseClientEvents[i].Set(); } catch { }
            }
        }
    }
    // Active SSE client counter — incremented when /spectrum accepts a
    // connection, decremented on disconnect. When zero, the capture
    // callback skips the expensive FFT path entirely (overlay toggled the
    // visualizer off, no one's listening). True idle CPU while off.
    static int s_activeClients = 0;
    // v8.3.6: shared SSE payload cache. The SSE serve loop checks if the
    // bytes reference matches s_sseCacheBytes (means s_latest hasn't
    // swapped since the last send = duplicate frame from allow-duplicates
    // mode), and if so reuses the pre-encoded `data: BASE64\n\n` byte
    // array. Eliminates Convert.ToBase64String + Encoding.ASCII.GetBytes
    // for duplicate sends. Volatile so per-client SSE threads see updates
    // without locking; race during concurrent writes is harmless (worst
    // case both threads compute the same payload, loser is GC'd).
    static volatile byte[] s_sseCacheBytes = null;
    static volatile byte[] s_sseCacheRaw   = null;
    // v8.3.8: scratch buffers for OnData's per-sample byte-decode hot path.
    // Buffer.BlockCopy reinterprets the byte[] from NAudio as the typed array
    // (float / short / int) in one bulk memmove instead of per-sample
    // BitConverter calls (each of which does bounds checking + endian
    // dispatch). Per-sample reads from these scratches then have zero
    // overhead vs the BitConverter version. All four backends benefit:
    //   • WASAPI loopback / ASIO       → IEEE float (s_isFloatScratch)
    //   • MME (typical) / WDM-KS int16 → 16-bit signed (s_int16Scratch)
    //   • WDM-KS / WASAPI int32 mode   → 32-bit signed (s_int32Scratch)
    // Sized lazily — resize up if a larger NAudio buffer arrives than we
    // currently have room for.
    static float[] s_isFloatScratch = new float[8192];
    static short[] s_int16Scratch   = new short[16384];
    static int[]   s_int32Scratch   = new int[8192];
    // v8.3.7: incremental sum-of-squares for the silence-gate RMS. The
    // previous code scanned all 2048 samples once per FFT trigger
    // (~4 M fp ops/sec at HOP=24). Now we maintain `s_rmsSumSq` as a
    // rolling sum: when OnData writes a new sample, it adds (new² - old²)
    // before overwriting the oldest sample in the circular buffer. The
    // pre-FFT path then just reads s_rmsSumSq instead of re-scanning. A
    // periodic full rescan (every N seconds) corrects floating-point
    // drift accumulated by the incremental updates.
    static double s_rmsSumSq = 0.0;
    static int    s_rmsRescanCounter = 0;
    const  int    RMS_RESCAN_EVERY = 60000;   // ~60 s of FFTs at HOP=24 → drift correction

    static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(s_logDir);
            File.AppendAllText(s_logPath,
                string.Format("[{0:HH:mm:ss.fff}] {1}{2}",
                    DateTime.Now, msg, Environment.NewLine));
        }
        catch { }
    }

    // Reset log on each boot — same rotation pattern as host.log / server.log.
    static void InitLog()
    {
        try
        {
            Directory.CreateDirectory(s_logDir);
            File.WriteAllText(s_logPath,
                string.Format("=== audio_spectrum boot {0:yyyy-MM-dd HH:mm:ss} ==={1}",
                    DateTime.Now, Environment.NewLine));
        }
        catch { }
    }

    // ── Capture thread ────────────────────────────────────────────────────
    // Uses NAudio's WasapiLoopbackCapture which wraps IMMDeviceEnumerator +
    // IAudioClient (AUDCLNT_STREAMFLAGS_LOOPBACK) + IAudioCaptureClient for
    // us. Samples arrive as 32-bit float stereo at the endpoint's mix format
    // (usually 48000 or 44100 Hz). v6.3.3: sliding CIRCULAR buffer of
    // FFT_SIZE samples. `s_writePos` points at the slot the next sample
    // overwrites (oldest). Every HOP_SIZE samples we run FFT over the full
    // circular buffer - overlap-add pattern that decouples FFT window
    // length (resolution) from FFT rate (latency).
    static float[] s_fftBuf   = new float[FFT_SIZE];
    static int     s_writePos          = 0;
    static int     s_samplesSinceFft   = 0;
    // v6.8.2: HOP_SIZE back to 512. User report: 'CPU was 1-2 % at
    // the old setting, 6 % now'. HOP=128 was 4x more FFTs/sec which
    // showed up as the CPU spike. Visually 10.7 ms vs 2.7 ms cadence
    // is invisible (monitor refresh is the floor anyway). 10.7 ms
    // FFT cadence, ~30 ms total perceived delay.
    static volatile int s_hopSize = 512;
    // v9.9.3: effective stride used in the WASAPI hot loop.
    // = max(s_hopSize, FFT_MIN_STRIDE) so very small hop values
    // (e.g. HOP=1 from 0.01 ms slider) don't spin 48 000 FFTs/sec.
    //
    // Stage 7.12 Batch B Phase P (2026-05-17): floor lowered 384 → 48
    // (8 ms → 1 ms). The original 384 was set in v9.9.3 on the
    // assumption that the SSE-publish cadence was the visible bottleneck
    // — true at 60-fps OBS browser sources where 8 ms refreshes are
    // invisible. At 120+ fps customize preview and especially with
    // high-refresh monitors (240 Hz), 8 ms is two-to-three rendered
    // frames of FFT-data staleness — operator-visible on transients
    // like bass hits.
    //
    // At 48 samples / 1 ms cadence, the FFT publishes 1000 SSE frames/s
    // (vs 125/s before). Each FFT is ~0.05 ms (mean) per PERF-ROLLUP, so
    // total CPU ≈ 1000 × 0.05 = 50 ms/s = ~5 % of one core. Up from
    // <1 % currently. Acceptable on any modern CPU.
    //
    // Going lower than 48 (e.g. 24 = 0.5 ms) doubles CPU for a
    // sub-millisecond freshness improvement that's well below any
    // visible monitor refresh cycle — diminishing returns.
    const int           FFT_MIN_STRIDE = 48;
    static volatile int s_fftStride    = 512; // updated in HandleSetHop
    // Legacy alias — code below reads HOP_SIZE in a few places.
    // Old-style property getter for compatibility with the csc.exe
    // version the build pipeline uses (expression-bodied members
    // aren't supported in all csc versions we might run against).
    static int HOP_SIZE { get { return s_hopSize; } }
    static readonly object s_fftLock = new object();

    // v6.9.3: live "Sensitivity" multiplier — applied to the magnitude
    // before the compressor knee in DoFftAndPublish. 1.0x = unchanged
    // (the empirically tuned default). Higher values amplify quiet audio
    // so friends running music at 1-5 % system volume still see full
    // bars. The compressor + clamp downstream prevents loud audio from
    // blowing past 100 %, so the slider can be cranked safely.
    static float s_sensitivity = 1.0f;

    // Device-selection state. When the user picks a different audio device
    // via the tray dialog, the tray POSTs /set-device with the endpoint ID
    // AND backend name to switch to. HandleSetDevice stores them here and
    // signals the capture thread to tear down + reopen on the new endpoint.
    //
    // Backend is one of: wasapi_loopback, wasapi_input, wasapi_exclusive,
    // mme, asio. Default when nothing is configured is wasapi_loopback with
    // the system default render endpoint.
    static string s_currentDeviceId = null;     // native ID (WASAPI endpoint ID / MME index / ASIO driver name)
    static string s_currentBackend  = "wasapi_loopback";
    // Input-gain compensation for non-loopback backends. WASAPI Loopback
    // sees the post-mixer system output at ~0 dBFS peaks. MME WaveIn,
    // WASAPI Input, WDM-KS, and ASIO see physical/virtual input signals
    // that typically peak 20-40 dB lower (-20 to -60 dBFS) because they
    // don't go through the Windows volume mixer's gain.
    //
    // Clamped to [-1, +1] after the multiply so an unusually-loud input
    // can't produce NaN-ish FFT values.
    static volatile float s_inputGain = 1.0f;

    // Per-driver input-channel-count cache. ASIO drivers report their
    // channel count via AsioOut.DriverInputChannelCount - but creating
    // AsioOut is expensive (100-500ms per driver) so we probe lazily on
    // the first /devices call and cache for the process lifetime. If a
    // probe fails (driver already in use, not installed properly, etc.)
    // we store 2 as a conservative default (one stereo pair).
    static readonly Dictionary<string, int> s_asioChannelCache = new Dictionary<string, int>();
    static readonly object s_asioProbeLock = new object();

    // Called by AsioCaptureAdapter when a driver is successfully opened so
    // we can seed the channel-count cache with the real number reported by
    // the driver. Avoids the "1 pair" display for the currently-active
    // driver that happens otherwise (GetAsioInputChannelCount can't probe
    // the active one without disrupting capture).
    public static void RecordAsioChannelCount(string driverName, int count)
    {
        if (string.IsNullOrEmpty(driverName) || count < 2) return;
        lock (s_asioProbeLock) { s_asioChannelCache[driverName] = count; }
    }

    static int GetAsioInputChannelCount(string driverName)
    {
        lock (s_asioProbeLock)
        {
            int cached;
            if (s_asioChannelCache.TryGetValue(driverName, out cached)) return cached;
        }

        // Skip probing the driver we're currently capturing with - creating
        // a second AsioOut for the same driver would either fail or disrupt
        // the live capture. Fall back to the default (2 channels) for that
        // one entry; everything else probes normally.
        string activeDrv = null;
        if (s_currentBackend == "asio" && !string.IsNullOrEmpty(s_currentDeviceId))
        {
            activeDrv = s_currentDeviceId;
            int barIdx = activeDrv.IndexOf('|');
            if (barIdx >= 0) activeDrv = activeDrv.Substring(0, barIdx);
        }
        if (activeDrv == driverName) return 2;

        int count = 2;
        try
        {
            // STAThread required - ASIO is STA-COM. We're called from the
            // HTTP handler thread which is MTA by default, so we spin up
            // a short-lived STA thread for the probe.
            var sta = new Thread(() =>
            {
                try
                {
                    var probe = new NAudio.Wave.AsioOut(driverName);
                    try { count = probe.DriverInputChannelCount; }
                    finally { try { probe.Dispose(); } catch { } }
                }
                catch (Exception exInner) { Log("asio probe '" + driverName + "' inner: " + exInner.Message); }
            });
            sta.SetApartmentState(ApartmentState.STA);
            sta.IsBackground = true;
            sta.Start();
            sta.Join(2000);   // cap probe at 2 s per driver
        }
        catch (Exception ex)
        {
            Log("asio probe '" + driverName + "' outer: " + ex.Message);
        }

        if (count < 2) count = 2;
        lock (s_asioProbeLock) { s_asioChannelCache[driverName] = count; }
        return count;
    }
    static readonly ManualResetEventSlim s_deviceChangeEvent = new ManualResetEventSlim(false);

    // Static enumerator kept for the whole process lifetime — NAudio's
    // MMDevice instances don't re-activate cleanly after their parent
    // enumerator goes out of scope (silent samples came back for users on
    // VB-Audio virtual sinks after the enumerator was disposed).  Cheap:
    // one COM object for the life of the process.
    static MMDeviceEnumerator s_enumerator = new MMDeviceEnumerator();

    // Diagnostic: peak sample tracker. `s_peakSampleMax` is the LIFETIME
    // peak since the current capture session started (reset on new capture).
    // `s_peakRollingMax` is the rolling max over the last N FFT windows,
    // logged every 3 s so the log shows LIVE audio levels — you can watch
    // a tail -f while playing music and see peaks come in.
    static float s_peakSampleMax     = 0f;
    static float s_peakRollingMax    = 0f;
    // s_peakWindowCount removed in v8.3.8 (was unused since v8.2.7 switched
    // the peak-log gate to wall-clock via s_peakRollupAt below).
    // v8.2.7: time-based gate for the peak-log line. The `s_peakWindowCount >= 141`
    // tick-based threshold below was calibrated for HOP_SIZE=1024 (~21 ms × 141 ≈ 3 s)
    // and silently broke at low HOP settings: at HOP_SIZE=24 (responseMs slider at
    // minimum) it fires every 70 ms = 14 file-I/O calls per second from inside the
    // audio capture thread, causing visible spectrum stutter. Same fix shape as
    // PERF-ROLLUP: gate on wall-clock instead.
    static System.DateTime s_peakRollupAt = System.DateTime.MinValue;

    // v8.2.4 PERF instrumentation — measures per-tick cost of the FFT-or-decay
    // branch in OnData (the dominant per-buffer work). Logs per-tick when >5ms,
    // plus a 60-second rollup so trends are visible without log spam. Removed
    // by reverting if no perf issue is identified, kept otherwise per the
    // STEP 6 guidance (instrumentation pays dividends in future debugging).
    static int      s_perfTickCount   = 0;
    static double   s_perfTotalMs     = 0;
    static double   s_perfMaxMs       = 0;
    static int      s_perfCountOver5  = 0;
    static int      s_perfCountOver20 = 0;
    static int      s_perfSilentCount = 0;
    static int      s_perfFftCount    = 0;
    static System.DateTime s_perfRollupAt = System.DateTime.MinValue;
    // Once-per-capture hex dump of the first 32 bytes, so we can tell
    // whether the device is literally delivering 0x00 00 00 ... (true
    // silence) or some other bit-pattern we're mis-decoding.
    static bool  s_firstBufferDumped = false;

    // Resolve which endpoint to open for WASAPI backends. Priority:
    //   1. s_currentDeviceId (set at runtime via /set-device)
    //   2. audioSpectrumDevice field in Roaming config.json (persisted)
    //   3. default render endpoint (null return = caller uses default)
    //
    // For NON-WASAPI backends (MME, ASIO) this function is not called
    // the target ID is the numeric MME index / ASIO driver name string.
    static MMDevice ResolveTargetDevice(DataFlow flow)
    {
        // Regex-parse the config - avoids pulling in System.Web.Extensions
        // for a single-string read.  If the file doesn't exist yet, or the
        // key is missing / empty / "default", we return null which tells
        // the caller to use the default render endpoint.
        string configId = null;
        try
        {
            string cfgPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MastersFM", "config.json");
            if (File.Exists(cfgPath))
            {
                string text = File.ReadAllText(cfgPath);
                var m = System.Text.RegularExpressions.Regex.Match(
                    text, "\"audioSpectrumDevice\"\\s*:\\s*\"([^\"]*)\"");
                if (m.Success) configId = m.Groups[1].Value;
            }
        }
        catch (Exception ex) { Log("ResolveTargetDevice config-read failed: " + ex.Message); }

        string targetId = !string.IsNullOrEmpty(s_currentDeviceId)
                              ? s_currentDeviceId
                              : configId;
        if (string.IsNullOrEmpty(targetId) || targetId == "default") return null;

        try
        {
            // Use the process-lifetime enumerator - DO NOT wrap in using().
            // Disposing the enumerator invalidates the MMDevice handle it
            // returned on some systems (silent-sample bug on VB-Audio
            // virtual sinks).
            MMDevice d = s_enumerator.GetDevice(targetId);
            if (d != null && d.State == DeviceState.Active && d.DataFlow == flow) return d;
            // Caller asked for Render but saved ID is a Capture endpoint
            // (or vice-versa) - ignore and fall back to default. This also
            // catches the case where the user switches backend from WASAPI
            // Input to WASAPI Loopback without picking a new specific device.
        }
        catch (Exception ex)
        {
            Log("ResolveTargetDevice: " + ex.Message + " (falling back to default)");
        }
        return null;
    }

    // ------- Backend dispatch -----------------------------------------
    // Each backend produces raw audio samples via an IWaveIn (NAudio's
    // common capture interface). WASAPI Loopback / WASAPI Capture / MME
    // all natively implement IWaveIn. ASIO does not - we wrap AsioOut in
    // AsioCaptureAdapter further down which synthesizes an IWaveIn.
    //
    // OpenCaptureForBackend returns the capture object plus a pretty
    // display-name describing the active choice. The caller wires up
    // DataAvailable/RecordingStopped and uses the existing OnData path
    // unchanged - every backend ultimately delivers byte[] + WaveFormat
    // which the FFT code already knows how to consume.
    struct CaptureOpen
    {
        public IWaveIn  Capture;
        public string   Display;   // human-readable
        public DataFlow Flow;      // render = loopback, capture = input
        public string   BackendTag;// mirrored into s_currentBackend
        public string   ResolvedId;// mirrored into s_currentDeviceId
    }

    static CaptureOpen OpenCaptureForBackend(string backend)
    {
        backend = (backend ?? "wasapi_loopback").ToLowerInvariant();
        switch (backend)
        {
            case "wasapi_input":
            case "wasapi_capture":
            {
                // Capture-side WASAPI - reads from an input endpoint
                // (microphone, Stereo Mix, VB-Cable Output, Voicemeeter
                // Out B1/B2/B3, etc.). Useful when the user's setup
                // surfaces audio on capture endpoints instead of render.
                MMDevice dev = ResolveTargetDevice(DataFlow.Capture);
                if (dev == null)
                {
                    try { dev = s_enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); }
                    catch (Exception exDef) { Log("wasapi_input: default-endpoint lookup failed: " + exDef.Message); }
                }
                if (dev == null) throw new Exception("No active input endpoint available for WASAPI capture.");
                var cap = new WasapiCapture(dev, true, 50);  // shared mode, 50ms buffer
                return new CaptureOpen {
                    Capture    = cap,
                    Display    = dev.FriendlyName + " (WASAPI Input)",
                    Flow       = DataFlow.Capture,
                    BackendTag = "wasapi_input",
                    ResolvedId = dev.ID
                };
            }

            case "wasapi_exclusive":
            case "wdm_ks":
            case "wdmks":
            case "ks":
            {
                // WASAPI exclusive-mode capture. This is the user-space
                // path that goes straight through the WDM-KS kernel audio
                // driver stack (same as pro audio apps' "WDM-KS" mode in
                // FL Studio / Reaper / ASIO4ALL passthrough). Lower
                // latency, no mixing with other apps, requires exclusive
                // ownership of the endpoint while we're running.
                //
                // Virtual endpoints (VB-Matrix Media, Voicemeeter out B*,
                // VB-Cable Output, etc.) rarely support exclusive mode
                // because they're not physical hardware. The wrapper
                // adapter below tries exclusive first, falls back to
                // shared on the SAME device if exclusive is unsupported.
                // That way the user's device choice still takes effect
                // (just in shared mode) instead of the capture thread
                // giving up and dropping them back to WASAPI Loopback.
                MMDevice dev = ResolveTargetDevice(DataFlow.Capture);
                if (dev == null)
                {
                    try { dev = s_enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); }
                    catch (Exception exDef) { Log("wasapi_exclusive: default-endpoint lookup failed: " + exDef.Message); }
                }
                if (dev == null) throw new Exception("No active input endpoint for WDM-KS / WASAPI exclusive.");
                var cap = new WdmKsCaptureAdapter(dev);
                return new CaptureOpen {
                    Capture    = cap,
                    Display    = dev.FriendlyName + " (WDM-KS / Exclusive)",
                    Flow       = DataFlow.Capture,
                    BackendTag = "wasapi_exclusive",
                    ResolvedId = dev.ID
                };
            }

            case "mme":
            case "wavein":
            {
                // Classic Windows Multimedia Extensions (waveIn). Each
                // device is identified by a zero-based integer index into
                // WaveInEvent.DeviceCount. Index -1 means "Wave Mapper"
                // (default system input).
                int idx = -1;
                if (!string.IsNullOrEmpty(s_currentDeviceId) && s_currentDeviceId != "default")
                {
                    // Accept either a bare integer or the legacy backend-
                    // prefixed form. No crash on malformed values - fall
                    // back to Wave Mapper (-1).
                    int.TryParse(s_currentDeviceId, out idx);
                }
                // Prefer 48 kHz 16-bit stereo - widely supported and
                // matches the native format most virtual cables emit.
                // 50 ms buffer is small enough to feel responsive without
                // shredding CPU with tiny wave headers.
                var mme = new WaveInEvent {
                    DeviceNumber      = idx,
                    WaveFormat        = new WaveFormat(48000, 16, 2),
                    BufferMilliseconds = 50
                };
                string devName;
                try { devName = idx >= 0 ? WaveInEvent.GetCapabilities(idx).ProductName : "Wave Mapper (default MME input)"; }
                catch { devName = "MME device #" + idx; }
                return new CaptureOpen {
                    Capture    = mme,
                    Display    = devName + " (MME)",
                    Flow       = DataFlow.Capture,
                    BackendTag = "mme",
                    ResolvedId = idx.ToString()
                };
            }

            case "asio":
            {
                // Pro audio path. Requires a vendor ASIO driver (ASIO4ALL,
                // VB-Audio Matrix, Focusrite, UA, etc.). ID format is
                // "driverName|channelOffset" (e.g. "VB-Matrix VASIO-32|4"
                // = driver "VB-Matrix VASIO-32", offset 4 = channels 5-6).
                // Bare driver name (no pipe) defaults to offset 0 = Ch 1-2
                // for backward compatibility with saved v5.3.0 configs.
                string rawId      = s_currentDeviceId;
                string driverName = null;
                int    channelOffset = 0;
                if (!string.IsNullOrEmpty(rawId) && rawId != "default")
                {
                    int barIdx = rawId.IndexOf('|');
                    if (barIdx > 0)
                    {
                        driverName = rawId.Substring(0, barIdx);
                        int.TryParse(rawId.Substring(barIdx + 1), out channelOffset);
                    }
                    else
                    {
                        driverName = rawId;
                    }
                }
                if (string.IsNullOrEmpty(driverName))
                {
                    var names = AsioOut.GetDriverNames();
                    if (names == null || names.Length == 0)
                        throw new Exception("No ASIO drivers installed on this machine.");
                    driverName = names[0];
                }
                var asio = new AsioCaptureAdapter(driverName, channelOffset);
                string displaySuffix = channelOffset > 0
                    ? string.Format(" (ASIO Ch {0}-{1})", channelOffset + 1, channelOffset + 2)
                    : " (ASIO)";
                return new CaptureOpen {
                    Capture    = asio,
                    Display    = driverName + displaySuffix,
                    Flow       = DataFlow.Capture,
                    BackendTag = "asio",
                    ResolvedId = rawId   // preserve compound form so the dialog match key works
                };
            }

            case "wasapi_loopback":
            default:
            {
                // Default path - captures the output mix of any render
                // endpoint via AUDCLNT_STREAMFLAGS_LOOPBACK.
                MMDevice dev = ResolveTargetDevice(DataFlow.Render);
                // v6.6.4: reverted to 1-arg WasapiLoopbackCapture
                // constructor. The (dev, true, N) overload doesn't
                // exist in our bundled NAudio.Wasapi.dll — the build
                // silently failed since v6.5.7, so every latency win
                // we shipped between then and now was a LIE (old
                // audio_spectrum.exe kept running). WasapiLoopback in
                // shared mode has a fixed ~10 ms engine period on
                // Windows anyway; the buffer arg was only going to
                // shave 0-5 ms at best.
                IWaveIn cap = dev != null
                    ? (IWaveIn)new WasapiLoopbackCapture(dev)
                    : new WasapiLoopbackCapture();
                return new CaptureOpen {
                    Capture    = cap,
                    Display    = (dev != null ? dev.FriendlyName : "System Default") + " (WASAPI Loopback)",
                    Flow       = DataFlow.Render,
                    BackendTag = "wasapi_loopback",
                    ResolvedId = dev != null ? dev.ID : null
                };
            }
        }
    }

    static void StartCapture()
    {
        // How many consecutive capture failures we tolerate on the
        // current non-WASAPI backend before we give up and fall back.
        // ASIO drivers that don't support any of our sample rates
        // throw on every re-init attempt - without a counter the
        // capture thread spins at ~2 Hz forever, burning CPU and
        // flooding the log. Three strikes is enough to confirm it's
        // a persistent failure and not a transient init glitch.
        const int FAIL_THRESHOLD = 3;
        Thread t = new Thread(() =>
        {
            string lastBackend = "";
            int failCountOnBackend = 0;
            while (true)
            {
                try
                {
                    s_deviceChangeEvent.Reset();
                    // Arm the peak-sample diagnostic - log the loudest sample
                    // seen over the first ~2 s of this capture session so we
                    // can tell whether audio is actually being delivered
                    // from the selected endpoint or just silence buffers.
                    s_peakSampleMax     = 0f;
                    s_peakRollingMax    = 0f;
                    // v8.3.8: s_peakWindowCount removed (unused since v8.2.7);
                    // s_peakRollupAt resets so the next peak-log fires ~3 s
                    // after the new capture starts, not immediately on
                    // residual state from the prior session.
                    s_peakRollupAt      = System.DateTime.MinValue;
                    s_firstBufferDumped = false;

                    // Reset the failure counter when the user picks a
                    // different backend - don't punish them with a
                    // forced fallback just because the previous one
                    // failed.
                    if (s_currentBackend != lastBackend)
                    {
                        lastBackend = s_currentBackend;
                        failCountOnBackend = 0;
                    }

                    CaptureOpen open;
                    try
                    {
                        open = OpenCaptureForBackend(s_currentBackend);
                    }
                    catch (Exception ex)
                    {
                        // Backend failed to open (ASIO driver missing,
                        // Stereo Mix disabled, etc.) - fall back to
                        // WASAPI Loopback rather than leaving the user
                        // with no visualizer at all. User can switch
                        // backends again once they fix their setup.
                        Log(string.Format("backend '{0}' failed to open: {1} - falling back to WASAPI Loopback",
                            s_currentBackend, ex.Message));
                        // BUG FIX (found via instrumentation): the old s_currentDeviceId
                        // came from the failed backend (e.g. an ASIO driver name like
                        // "VB-Matrix VASIO-128"), and WASAPI's ResolveTargetDevice then
                        // tried to look that string up as a WASAPI endpoint GUID, failing
                        // with "Value does not fall within the expected range". Reset
                        // device ID here so the fallback uses the system-default render
                        // endpoint cleanly.
                        s_currentBackend = "wasapi_loopback";
                        s_currentDeviceId = null;
                        lastBackend = s_currentBackend;
                        failCountOnBackend = 0;
                        open = OpenCaptureForBackend("wasapi_loopback");
                    }

                    s_currentBackend  = open.BackendTag;
                    s_currentDeviceId = open.ResolvedId;
                    // Set per-backend input gain. WASAPI Loopback sees the
                    // post-mixer at near-0 dBFS so no boost. Everything else
                    // captures from hardware/virtual inputs that typically
                    // peak 20-40 dB lower - apply a 20x (~26 dB) multiplier
                    // so the FFT input magnitude matches loopback.
                    switch (s_currentBackend)
                    {
                        case "wasapi_loopback": s_inputGain = 1.0f;  break;
                        case "wasapi_input":    s_inputGain = 20.0f; break;
                        case "wasapi_exclusive":s_inputGain = 20.0f; break;
                        case "mme":             s_inputGain = 20.0f; break;
                        case "asio":            s_inputGain = 40.0f; break;   // ASIO virtual buses often 40 dB below loopback
                        default:                s_inputGain = 1.0f;  break;
                    }
                    Log(string.Format("capture: inputGain = {0:F1}x for backend '{1}'", s_inputGain, s_currentBackend));
                    Log(string.Format("capture: backend={0} target={1}", s_currentBackend, open.Display));

                    // WASAPI backends expose an MMDevice we can introspect
                    // for session info. MME / ASIO don't, so we skip this
                    // diagnostic for them.
                    if (open.Flow == DataFlow.Render || open.Flow == DataFlow.Capture)
                    {
                        if (s_currentBackend.StartsWith("wasapi") && !string.IsNullOrEmpty(open.ResolvedId))
                        {
                            try
                            {
                                var device = s_enumerator.GetDevice(open.ResolvedId);
                                if (device != null)
                                {
                                    Log(string.Format("device: ID={0} Flow={1} State={2} MixFormat={3}",
                                        device.ID, device.DataFlow, device.State, device.AudioClient.MixFormat));
                                    try
                                    {
                                        var mgr = device.AudioSessionManager;
                                        if (mgr != null && mgr.Sessions != null)
                                        {
                                            int n = mgr.Sessions.Count;
                                            Log(string.Format("sessions on this endpoint ({0}):", n));
                                            for (int i = 0; i < n; i++)
                                            {
                                                var sess = mgr.Sessions[i];
                                                string pname = "?";
                                                try
                                                {
                                                    var p = System.Diagnostics.Process.GetProcessById((int)sess.GetProcessID);
                                                    pname = p.ProcessName;
                                                } catch { }
                                                Log(string.Format("  [{0}] proc={1}(pid={2}) display='{3}' state={4}",
                                                    i, pname, sess.GetProcessID, sess.DisplayName, sess.State));
                                            }
                                            if (n == 0)
                                                Log("  (no active audio sessions on this endpoint right now)");
                                        }
                                    }
                                    catch (Exception ex) { Log("session enum failed: " + ex.Message); }
                                }
                            }
                            catch (Exception ex) { Log("device info log: " + ex.Message); }
                        }
                    }

                    IWaveIn capture = open.Capture;
                    using (capture)
                    using (var stoppedEvent = new ManualResetEventSlim(false))
                    {
                        // v8.2.3: WdmKsCaptureAdapter now populates WaveFormat in
                        // its constructor (matches the contract of the other
                        // backends), so this no longer needs the defensive null
                        // check that v8.2.0 added. All adapters' WaveFormat is
                        // valid by the time we get here.
                        var wf = capture.WaveFormat;
                        Log(string.Format(
                            "capture: starting, waveFormat={0} (Encoding={1} SampleRate={2} Bits={3} Chan={4})",
                            wf, wf.Encoding, wf.SampleRate, wf.BitsPerSample, wf.Channels));
                        // Track whether StartRecording threw, so we can
                        // bump the fail-counter. RecordingStopped with
                        // a non-null exception counts as a failure too.
                        bool startFailed = false;
                        Exception stopEx = null;
                        capture.DataAvailable += (s, a) => OnData(a.Buffer, a.BytesRecorded, capture.WaveFormat);
                        capture.RecordingStopped += (s, a) =>
                        {
                            if (a.Exception != null) stopEx = a.Exception;
                            Log("capture: stopped (exception=" + (a.Exception != null ? a.Exception.Message : "none") + ")");
                            stoppedEvent.Set();
                        };
                        try
                        {
                            capture.StartRecording();
                        }
                        catch (Exception ex)
                        {
                            startFailed = true;
                            stopEx = ex;
                            Log("capture: StartRecording threw: " + ex.Message);
                        }
                        if (!startFailed)
                        {
                            WaitHandle.WaitAny(new WaitHandle[] {
                                stoppedEvent.WaitHandle, s_deviceChangeEvent.WaitHandle
                            });
                            try { capture.StopRecording(); } catch { }
                        }
                        // If the capture died on its own (not because the
                        // user asked for a device change) tally a strike
                        // against the current backend.
                        if (stopEx != null && !s_deviceChangeEvent.IsSet)
                        {
                            failCountOnBackend++;
                            if (s_currentBackend != "wasapi_loopback" && failCountOnBackend >= FAIL_THRESHOLD)
                            {
                                Log(string.Format(
                                    "capture: backend '{0}' failed {1} times in a row (last='{2}') - forcing fallback to WASAPI Loopback",
                                    s_currentBackend, failCountOnBackend, stopEx.Message));
                                // Same reset-device-ID fix as the open-path fallback:
                                // the ID in s_currentDeviceId belongs to the failed
                                // backend and can't be used to resolve a WASAPI endpoint.
                                s_currentBackend = "wasapi_loopback";
                                s_currentDeviceId = null;
                                failCountOnBackend = 0;
                            }
                        }
                        else if (stopEx == null)
                        {
                            // Clean stop (user changed device) - reset counter.
                            failCountOnBackend = 0;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("capture: exception, will retry in 2 s: " + ex.Message);
                    failCountOnBackend++;
                    if (s_currentBackend != "wasapi_loopback" && failCountOnBackend >= FAIL_THRESHOLD)
                    {
                        Log("capture: outer exception threshold hit - forcing fallback to WASAPI Loopback");
                        s_currentBackend = "wasapi_loopback";
                        failCountOnBackend = 0;
                    }
                }
                // NOTE: no `device.Dispose()` here - we now keep a single
                // process-lifetime MMDeviceEnumerator (see s_enumerator) and
                // let GC reclaim the MMDevice. Disposing it explicitly while
                // capture still held a reference was breaking VB-Audio
                // virtual-sink capture (silent samples coming back forever).
                // Short retry cushion. If the user picked a non-WASAPI
                // backend that's about to fail on re-init (ASIO driver
                // busy, endpoint vanished, etc.), the failure counter
                // above will flip us to WASAPI Loopback after 3 strikes
                // so this sleep doesn't become an infinite-retry trap.
                if (!s_deviceChangeEvent.IsSet) Thread.Sleep(500);
            }
        });
        t.IsBackground = true;
        // ASIO drivers require the calling thread to be in a single-
        // threaded COM apartment (the Steinberg ASIO COM interfaces are
        // STA-only). Without this, AsioOut construction fails with
        // "Unable to instantiate ASIO. Check if STAThread is set".
        // STA is fine for WASAPI / MME too - they work from any
        // apartment - so we set STA unconditionally here.
        t.SetApartmentState(ApartmentState.STA);
        // v9.1.0: boost capture thread priority. The audio capture loop is
        // latency-sensitive — when other processes peg the CPU (game
        // launches, browser warmup, etc.), the OS can preempt this thread
        // and the spectrum stutters. AboveNormal priority asks the Windows
        // scheduler to favor it, reducing variance under load. Not
        // Highest — that would risk starving normal-priority work on
        // single-core scenarios. AboveNormal is the safe, well-known
        // answer for "audio thread that must keep up with sample timing".
        try {
            t.Priority = ThreadPriority.AboveNormal;
            Log("capture thread: priority=AboveNormal (v9.1.0)");
        } catch (Exception exPri) {
            Log("capture thread: failed to set priority: " + exPri.Message);
        }
        t.Start();
    }

    // Each DataAvailable payload is a block of 32-bit float PCM, interleaved
    // across channels.  We mix all channels into a mono stream, then FFT.
    static void OnData(byte[] buffer, int bytesRecorded, WaveFormat fmt)
    {
        try
        {
            int bytesPerSample = fmt.BitsPerSample / 8;
            int channelCount   = fmt.Channels;
            int frameBytes     = bytesPerSample * channelCount;
            int frameCount     = bytesRecorded / frameBytes;
            bool isFloat       = (fmt.Encoding == WaveFormatEncoding.IeeeFloat);

            // One-shot first-buffer hex dump — tells us whether the device
            // is literally pushing 0x00 00 00 ... silence or whether bytes
            // look like real audio data we're mis-interpreting (e.g. a
            // format mismatch where bits-per-sample is wrong).
            if (!s_firstBufferDumped && bytesRecorded > 0)
            {
                s_firstBufferDumped = true;
                int take = Math.Min(32, bytesRecorded);
                var sb = new StringBuilder(take * 3);
                for (int i = 0; i < take; i++) sb.AppendFormat("{0:X2} ", buffer[i]);
                Log(string.Format(
                    "first buffer ({0} bytes total, isFloat={1}, bps={2}, ch={3}), first {4} hex: {5}",
                    bytesRecorded, isFloat, fmt.BitsPerSample, channelCount, take, sb.ToString().Trim()));
            }

            // ── v8.2.8 OPTIMIZED HOT PATH ─────────────────────────────────────
            // Changes vs prior version (no behaviour change, just less overhead):
            //   1. Lock once per BUFFER, not per SAMPLE. NAudio delivers ~480-2400
            //      frames per OnData callback; the old code took/released the
            //      managed-lock that many times. New code takes it once at the top
            //      of the buffer, releases at the bottom.
            //   2. Hoist `s_inputGain` check + decoder-format dispatch outside the
            //      sample loop via local-bool/branch-fan-out, so the JIT can keep
            //      the inner loop tight.
            //   3. Cache hot fields (s_writePos, s_samplesSinceFft, s_peakSampleMax,
            //      s_peakRollingMax) into locals so JIT keeps them in registers
            //      and only writes back ONCE per buffer (or before each FFT call
            //      that reads s_writePos).
            //   4. Replace `% FFT_SIZE` with `& FFT_MASK` — a single AND vs a
            //      modulo (slow on x64), one of the most common micro-ops in
            //      this whole hot loop.
            // Net effect at HOP_SIZE=24 / 48 kHz / 2 ch: ~96 000 sample iterations/sec
            // each saving ~ 1 lock op + 1 modulo + 1-2 BitConverter dispatches +
            // 1 branch on gain = several hundred CPU cycles per sample. Translates
            // to a meaningfully smoother SSE delivery cadence at the user's
            // 0.5 ms Response Time slider setting.
            float gain    = s_inputGain;
            bool  hasGain = (gain != 1.0f);
            float invChan = 1.0f / channelCount;

            // v8.3.8: BlockCopy bulk-decode the byte buffer into a typed
            // scratch array based on the wave format. Eliminates per-sample
            // BitConverter overhead for ALL four backends.
            float[] floatScratch = null;
            short[] int16Scratch = null;
            int[]   int32Scratch = null;
            if (isFloat) {
                int floatsNeeded = bytesRecorded / 4;
                if (s_isFloatScratch.Length < floatsNeeded)
                    s_isFloatScratch = new float[System.Math.Max(floatsNeeded, s_isFloatScratch.Length * 2)];
                floatScratch = s_isFloatScratch;
                Buffer.BlockCopy(buffer, 0, floatScratch, 0, bytesRecorded);
            } else if (bytesPerSample == 2) {
                int shortsNeeded = bytesRecorded / 2;
                if (s_int16Scratch.Length < shortsNeeded)
                    s_int16Scratch = new short[System.Math.Max(shortsNeeded, s_int16Scratch.Length * 2)];
                int16Scratch = s_int16Scratch;
                Buffer.BlockCopy(buffer, 0, int16Scratch, 0, bytesRecorded);
            } else if (bytesPerSample == 4) {
                int intsNeeded = bytesRecorded / 4;
                if (s_int32Scratch.Length < intsNeeded)
                    s_int32Scratch = new int[System.Math.Max(intsNeeded, s_int32Scratch.Length * 2)];
                int32Scratch = s_int32Scratch;
                Buffer.BlockCopy(buffer, 0, int32Scratch, 0, bytesRecorded);
            }

            lock (s_fftLock)
            {
                int   writePos        = s_writePos;
                int   samplesSinceFft = s_samplesSinceFft;
                float peakSample      = s_peakSampleMax;
                float peakRolling     = s_peakRollingMax;
                float[] fftBuf        = s_fftBuf;

                for (int f = 0; f < frameCount; f++)
                {
                    float sum = 0f;
                    if (isFloat) {
                        // v8.3.8: read from BlockCopy-populated float scratch.
                        int fcBase = f * channelCount;
                        for (int ch = 0; ch < channelCount; ch++)
                            sum += floatScratch[fcBase + ch];
                    } else if (bytesPerSample == 2) {
                        // v8.3.8: read from BlockCopy-populated int16 scratch.
                        int fcBase = f * channelCount;
                        const float INV_INT16 = 1f / 32768f;
                        for (int ch = 0; ch < channelCount; ch++)
                            sum += int16Scratch[fcBase + ch] * INV_INT16;
                    } else if (bytesPerSample == 4) {
                        // v8.3.8: read from BlockCopy-populated int32 scratch.
                        int fcBase = f * channelCount;
                        const float INV_INT32 = 1f / 2147483648f;
                        for (int ch = 0; ch < channelCount; ch++)
                            sum += int32Scratch[fcBase + ch] * INV_INT32;
                    }
                    float mono = sum * invChan;
                    if (hasGain) {
                        mono *= gain;
                        if (mono >  1.0f) mono =  1.0f;
                        if (mono < -1.0f) mono = -1.0f;
                    }

                    float abs = mono < 0 ? -mono : mono;
                    if (abs > peakSample)  peakSample  = abs;
                    if (abs > peakRolling) peakRolling = abs;

                    // v8.3.7: incremental RMS — track sum-of-squares of the
                    // circular buffer. Subtract the OLD sample's square (the
                    // one we're about to overwrite) and add the NEW sample's
                    // square. Same arithmetic the old per-FFT scan did, just
                    // amortized into the per-sample cost.
                    float oldSample = fftBuf[writePos];
                    s_rmsSumSq += (mono * mono) - (oldSample * oldSample);
                    fftBuf[writePos] = mono;
                    writePos = (writePos + 1) & FFT_MASK;
                    samplesSinceFft++;
                    if (samplesSinceFft >= s_fftStride)
                    {
                        samplesSinceFft = 0;

                        // v8.2.7: log a live peak every 3 s of WALL-CLOCK time
                        // (was tick-count-based with a constant calibrated for
                        // HOP_SIZE=1024, which spammed the log at low HOP).
                        if (s_peakRollupAt == System.DateTime.MinValue) {
                            s_peakRollupAt = System.DateTime.Now.AddSeconds(3);
                        } else if (System.DateTime.Now >= s_peakRollupAt) {
                            Log(string.Format(
                                "peak (last ~3 s) = {0:F4}   lifetime peak = {1:F4}   {2}",
                                peakRolling, peakSample,
                                (peakRolling < 0.0005f ? "[SILENCE — endpoint delivering zeros]" :
                                 peakRolling < 0.05f    ? "[quiet audio]" :
                                                          "[LIVE AUDIO]")));
                            peakRolling      = 0f;
                            s_peakRollupAt   = System.DateTime.Now.AddSeconds(3);
                        }

                        if (s_activeClients > 0)
                        {
                            // DoFftAndPublish reads s_writePos directly (start =
                            // s_writePos at line 1165), so flush the cached writePos
                            // back BEFORE the call. Same for the peak counters since
                            // /peak HTTP handler reads them.
                            s_writePos        = writePos;
                            s_peakSampleMax   = peakSample;
                            s_peakRollingMax  = peakRolling;

                            // v8.2.4 PERF: time the RMS+silence-gate+FFT branch.
                            var perfSw = System.Diagnostics.Stopwatch.StartNew();
                            // v8.3.7: O(1) RMS read from the incremental
                            // sum-of-squares (maintained per sample above).
                            // Periodic full rescan corrects drift accumulated
                            // by repeated incremental updates over time.
                            s_rmsRescanCounter++;
                            if (s_rmsRescanCounter >= RMS_RESCAN_EVERY) {
                                s_rmsRescanCounter = 0;
                                double resync = 0.0;
                                for (int i = 0; i < FFT_SIZE; i++) resync += fftBuf[i] * fftBuf[i];
                                s_rmsSumSq = resync;   // reset drift
                            }
                            double rms = Math.Sqrt((s_rmsSumSq < 0 ? 0 : s_rmsSumSq) / FFT_SIZE);
                            // v9.6.6: scale outer gate by s_sensitivity for the same reason
                            // the inner gate at line ~1540 is scaled — see comment there.
                            // This outer gate is at SILENCE_RMS=1e-4 (8x lower than the inner
                            // 8e-4) so it's less likely to cause the user's "spectrum cuts
                            // off at low volumes" flicker, but consistency matters: both
                            // gates should respect the user's amplification intent.
                            float effSilenceRMS = SILENCE_RMS / Math.Max(0.5f, s_sensitivity);
                            bool silent = (rms < effSilenceRMS);
                            if (silent)  DecayEnvelopeOnly();
                            else         DoFftAndPublish(fmt.SampleRate);
                            perfSw.Stop();

                            double tickMs = perfSw.Elapsed.TotalMilliseconds;
                            s_perfTickCount++;
                            s_perfTotalMs += tickMs;
                            if (tickMs > s_perfMaxMs) s_perfMaxMs = tickMs;
                            if (tickMs > 5.0)  s_perfCountOver5++;
                            if (tickMs > 20.0) s_perfCountOver20++;
                            if (silent) s_perfSilentCount++; else s_perfFftCount++;
                            if (tickMs > 5.0) {
                                Log("[PERF] tick=" + tickMs.ToString("F1") + "ms" + (silent ? " [silence-decay]" : " [full FFT]"));
                            }
                            if (s_perfRollupAt == System.DateTime.MinValue) {
                                s_perfRollupAt = System.DateTime.Now.AddSeconds(60);
                            } else if (System.DateTime.Now >= s_perfRollupAt) {
                                Log(string.Format(
                                    "[PERF-ROLLUP 60s] ticks={0} (silent={1} full-fft={2}) mean={3:F2}ms max={4:F1}ms >5ms={5} >20ms={6}",
                                    s_perfTickCount, s_perfSilentCount, s_perfFftCount,
                                    s_perfTotalMs / System.Math.Max(1, s_perfTickCount), s_perfMaxMs,
                                    s_perfCountOver5, s_perfCountOver20));
                                s_perfTickCount = 0; s_perfTotalMs = 0; s_perfMaxMs = 0;
                                s_perfCountOver5 = 0; s_perfCountOver20 = 0;
                                s_perfSilentCount = 0; s_perfFftCount = 0;
                                s_perfRollupAt = System.DateTime.Now.AddSeconds(60);
                            }
                        }
                        // else: no clients → don't touch FFT state; s_latest
                        // stays at whatever it was when the last client left.
                    }
                }

                // Single write-back at end of buffer. (Hot peak counters were
                // already flushed before each DoFftAndPublish call above.)
                s_writePos        = writePos;
                s_samplesSinceFft = samplesSinceFft;
                s_peakSampleMax   = peakSample;
                s_peakRollingMax  = peakRolling;
            }
        }
        catch (Exception ex)
        {
            Log("OnData: exception " + ex.Message);
        }
    }

    // ── FFT & banding ─────────────────────────────────────────────────────
    // v8.3.8: single-precision FFT data arrays. Was double[]; switched to
    // float[] to halve memory bandwidth and let modern x64 CPUs use 8-wide
    // float SIMD (vs 4-wide double) when the JIT vectorizes the hot loops.
    // Inside FFT() the per-butterfly accumulator (wr/wi) stays double so
    // twiddle recurrence drift over 11 passes doesn't accumulate; only the
    // data values are stored as float. Output is quantized to byte at the
    // end of DoFftAndPublish so the precision loss is invisible.
    static float[] s_real = new float[FFT_SIZE];
    static float[] s_imag = new float[FFT_SIZE];
    // Persistent envelope smoothing — stops the bars from twitching every
    // frame. Attack is quicker than release so peaks snap up, decay slowly.
    static float[]  s_env  = new float[BAND_COUNT];

    // v6.5.2 PERFORMANCE: precomputed hot-path arrays. All filled ONCE at
    // startup (Hann window) or when sample rate first arrives (band
    // freq / tilt / bin-index tables). Avoids doing Math.Cos / Math.Pow /
    // Math.Log / Math.Sqrt inside the 750-Hz FFT loop — ~1M trig/pow
    // calls per second eliminated, dropping audio_spectrum.exe CPU from
    // ~10-15 % of one core to ~2-4 %.
    static readonly float[] s_hannWindow = InitHannWindow();   // v8.3.8: float (was double)
    // Band precompute tables (sized BAND_COUNT). Populated by EnsureBandPrecompute
    // the first time DoFftAndPublish runs (or when sample rate changes).
    static double[] s_bandF0       = new double[BAND_COUNT];
    static double[] s_bandF1       = new double[BAND_COUNT];
    static double[] s_bandFCenter  = new double[BAND_COUNT];
    static double[] s_bandTiltLin  = new double[BAND_COUNT];   // linear multiplier = 10^(tiltDb/20)
    // v8.3.7: precomputed combined scale factor = s_bandTiltLin[b] / REF_MAG.
    // The hot per-band loop previously did `tiltedMag = avg * tiltLin; norm =
    // tiltedMag * sensitivity / REF_MAG` = 2 mult + 1 div per band. The divide
    // by REF_MAG (constant 112.0) was a per-band waste; now baked into the
    // precomputed factor so the hot path becomes `norm = avg * factor[b] *
    // sensitivity` = 2 mults. Saves ~1 M divides/sec at HOP=24 (480 bands ×
    // 2000 FFT/sec). Repopulated by EnsureBandPrecompute alongside tiltLin.
    static double[] s_bandScaleOverRef = new double[BAND_COUNT];
    static int[]    s_bandBinLo    = new int[BAND_COUNT];      // floor(f0/binHz), clamped
    static int[]    s_bandBinHi    = new int[BAND_COUNT];      // ceil(f1/binHz), clamped
    static int[]    s_bandInterpLo = new int[BAND_COUNT];      // for sub-bin interp
    static int[]    s_bandInterpHi = new int[BAND_COUNT];
    static double[] s_bandInterpFrac = new double[BAND_COUNT];
    static bool[]   s_bandIsSubBin = new bool[BAND_COUNT];
    static int      s_precomputedSampleRate = 0;
    // Reused magnitude buffer — replaces `new double[half]` on every FFT
    // (was allocating 24 MB/sec of managed memory at 750 Hz FFT rate).
    static float[] s_mag = new float[FFT_SIZE / 2];   // v8.3.8: float (was double)

    // ── v9.0.0 Real-FFT (RFFT) ────────────────────────────────────────────
    // Audio input is real-valued. A standard complex FFT of N points wastes
    // half its work because the imaginary input is always zero and the
    // upper half of the output is just the conjugate of the lower half.
    // Real-FFT exploits this by:
    //   1. Packing N real samples into N/2 complex samples (even-indexed →
    //      real part, odd-indexed → imaginary part).
    //   2. Running an N/2-point complex FFT (half the butterflies).
    //   3. Applying a post-FFT untangle using twiddle factors to recover
    //      the N/2+1 unique bins of the true real-input FFT.
    // Net cost: ~50 % of the standard CFFT path. Output magnitudes for
    // bins 0..N/2-1 are bit-for-bit equivalent to what the CFFT-then-mag
    // path produced (verified at startup by RfftSelfTest below).
    //
    // Working buffers are HALF-SIZED — separate from s_real/s_imag so the
    // CFFT path stays available for the self-test and as a fallback.
    static readonly float[] s_realHalf = new float[FFT_SIZE / 2];
    static readonly float[] s_imagHalf = new float[FFT_SIZE / 2];
    // Precomputed twiddle factors for the RFFT post-processing untangle.
    // For bin k in 1..N/2-1 we need (cos(2πk/N), sin(2πk/N)). Table size
    // N/2 (= 1024 floats × 2 = 8 KB). Populated by EnsureRfftTwiddleTable.
    static float[] s_rfftCos = null;
    static float[] s_rfftSin = null;
    static bool    s_rfftReady = false;

    static void EnsureRfftTwiddleTable()
    {
        if (s_rfftCos != null) return;
        int N = FFT_SIZE;
        int Nhalf = N / 2;
        s_rfftCos = new float[Nhalf];
        s_rfftSin = new float[Nhalf];
        for (int k = 0; k < Nhalf; k++)
        {
            double theta = 2.0 * Math.PI * k / N;
            s_rfftCos[k] = (float)Math.Cos(theta);
            s_rfftSin[k] = (float)Math.Sin(theta);
        }
    }

    // Compute |X[k]|² for k=0..N/2-1 of the real-input FFT of `xn`, where
    // xn is the FFT input (already Hann-windowed). Writes directly into
    // s_mag (same buffer the CFFT path used). Magnitude (with sqrt) is
    // applied by the caller — we leave magSq form since some optimizations
    // could defer the sqrt; current callers do sqrt outside.
    //
    // NOTE: This function writes magnitudes (with sqrt), matching the
    // CFFT path's output format, so call-site code can swap one for the
    // other without further changes.
    static void RealFFTToMag(float[] xn)
    {
        int N = xn.Length;
        int Nhalf = N / 2;

        // Pack N real samples into N/2 complex: y[k] = x[2k] + j·x[2k+1]
        for (int k = 0; k < Nhalf; k++)
        {
            s_realHalf[k] = xn[2 * k];
            s_imagHalf[k] = xn[2 * k + 1];
        }

        // Half-size complex FFT in place
        FFT(s_realHalf, s_imagHalf);

        // Post-FFT untangle. Recover X[k] for k=0..N/2-1 from packed Y[k].
        // X[0] = DC = Re(Y[0]) + Im(Y[0])  (purely real).
        {
            float yr0 = s_realHalf[0];
            float yi0 = s_imagHalf[0];
            float dc  = yr0 + yi0;     // X[0].re; X[0].im = 0
            s_mag[0]  = dc < 0 ? -dc : dc;
        }

        // For k = 1..Nhalf-1:
        //   E = (Y[k] + conj(Y[Nhalf-k])) / 2     (even part)
        //   O = (Y[k] - conj(Y[Nhalf-k])) / 2     (odd part)
        //   X[k] = E + (-j · exp(-j·2πk/N) · O)
        // Expanded for real arithmetic with c = cos(2πk/N), s = sin(2πk/N):
        //   X[k].re = Er + (c·Oi - s·Or)
        //   X[k].im = Ei + (-s·Oi - c·Or)
        // Where Er = (Yk_re + Ym_re)/2,  Ei = (Yk_im - Ym_im)/2
        //       Or = (Yk_re - Ym_re)/2,  Oi = (Yk_im + Ym_im)/2
        // Ym = Y[Nhalf-k] (the mirror).
        for (int k = 1; k < Nhalf; k++)
        {
            int kk = Nhalf - k;
            float Yk_re = s_realHalf[k];
            float Yk_im = s_imagHalf[k];
            float Ym_re = s_realHalf[kk];
            float Ym_im = s_imagHalf[kk];

            float Er = 0.5f * (Yk_re + Ym_re);
            float Ei = 0.5f * (Yk_im - Ym_im);
            float Or = 0.5f * (Yk_re - Ym_re);
            float Oi = 0.5f * (Yk_im + Ym_im);

            float c = s_rfftCos[k];
            float s = s_rfftSin[k];

            float Xr = Er + (c * Oi - s * Or);
            float Xi = Ei + (-s * Oi - c * Or);

            s_mag[k] = (float)System.Math.Sqrt(Xr * Xr + Xi * Xi);
        }
    }

    // One-shot self-test at startup: feed a synthetic 440 Hz sine wave
    // through both the legacy CFFT path AND the RFFT path, compare the
    // magnitude spectra bin-by-bin, log the max relative difference. If
    // the diff exceeds 5 % anywhere → set s_rfftReady = false (production
    // path falls back to CFFT). Per the v9.0.0 procedure: Real-FFT must
    // produce essentially identical output or it gets rolled back.
    static void RfftSelfTest()
    {
        EnsureRfftTwiddleTable();
        EnsureFftTwiddleTable();

        const int N = FFT_SIZE;
        const int sampleRate = 48000;
        const float freqHz = 440.0f;

        // Generate Hann-windowed sine (matches what DoFftAndPublish would
        // hand to its FFT input prep loop)
        float[] xn = new float[N];
        for (int i = 0; i < N; i++)
        {
            double t   = (double)i / sampleRate;
            double sig = System.Math.Sin(2.0 * System.Math.PI * freqHz * t);
            xn[i] = (float)(sig) * s_hannWindow[i];
        }

        // CFFT path: produces 1024 magnitudes in cfftMag[]
        float[] cfftRe = new float[N];
        float[] cfftIm = new float[N];
        for (int i = 0; i < N; i++) { cfftRe[i] = xn[i]; cfftIm[i] = 0f; }
        FFT(cfftRe, cfftIm);
        float[] cfftMag = new float[N / 2];
        for (int i = 0; i < N / 2; i++)
            cfftMag[i] = (float)System.Math.Sqrt(cfftRe[i] * cfftRe[i] + cfftIm[i] * cfftIm[i]);

        // RFFT path: writes magnitudes directly into s_mag
        // Save & restore s_mag since this runs at startup before the audio
        // thread is doing real work — but be defensive anyway.
        var savedMag = (float[])s_mag.Clone();
        RealFFTToMag(xn);
        float[] rfftMag = (float[])s_mag.Clone();
        System.Array.Copy(savedMag, s_mag, s_mag.Length);

        // Compare. Skip bin 0 (DC, often near-zero, division blows up).
        double maxRelDiff   = 0.0;
        int    maxDiffBin   = -1;
        double maxRefMag    = 0.0;
        int    binsCompared = 0;
        for (int i = 1; i < N / 2; i++)
        {
            double a = cfftMag[i];
            double b = rfftMag[i];
            // Use a noise floor — bins below 0.001 are noise-dominated and
            // ratio comparison is meaningless. Compare absolute diff there.
            if (a > maxRefMag) maxRefMag = a;
            const double NOISE_FLOOR = 0.001;
            if (a < NOISE_FLOOR && b < NOISE_FLOOR) continue;
            binsCompared++;
            double rel;
            if (a > NOISE_FLOOR)
                rel = System.Math.Abs(a - b) / a;
            else
                rel = System.Math.Abs(a - b) / NOISE_FLOOR;
            if (rel > maxRelDiff) { maxRelDiff = rel; maxDiffBin = i; }
        }

        s_rfftReady = (maxRelDiff < 0.05);   // 5 % threshold per procedure
        Log(string.Format(
            "rfft self-test: 440Hz sine, max rel diff = {0:P3} at bin {1} (peak ref mag {2:F4}, {3} bins compared above floor) → RFFT {4}",
            maxRelDiff, maxDiffBin, maxRefMag, binsCompared,
            s_rfftReady ? "READY (using in production)" : "DISABLED (falling back to CFFT)"));
    }
    // Double-buffered output bands so we can swap s_latest atomically
    // (via volatile ref reassignment) without allocating each frame.
    static byte[]   s_bandsA = new byte[BAND_COUNT];
    // v6.8.7: scratch arrays for the multi-pass band processor.
    // s_targets holds the raw 0..255 target value per band BEFORE the
    // envelope smoothing. s_targetsSharp holds the spatially-sharpened
    // version (only filled for low bands). Reused across calls to
    // avoid per-FFT GC pressure.
    static float[]  s_targets      = new float[BAND_COUNT];
    static float[]  s_targetsSharp = new float[BAND_COUNT];
    // v6.8.8: per-band slow baseline tracking the SUSTAINED level over
    // ~1-2 seconds. Used by the bass transient expander to detect
    // "this is a steady bassline note vs a kick punching through".
    static float[]  s_baseline     = new float[BAND_COUNT];
    static byte[]   s_bandsB = new byte[BAND_COUNT];

    // v8.3.6: FFT twiddle factor table. Pre-computes the per-pass-size
    // (wpr, wpi) pairs that the FFT recurrence rotates through. Without
    // this, the FFT() function calls Math.Cos and Math.Sin once per
    // butterfly pass = 11 trig calls per FFT (log2(FFT_SIZE=2048)=11).
    // At 2000 FFT/sec that's 22 000 trig calls/sec eliminated. Indexed
    // by passLog2 (1..11 → array indices 1..11; index 0 unused).
    static double[] s_fftTwiddleWpr = null;   // length 12
    static double[] s_fftTwiddleWpi = null;
    static void EnsureFftTwiddleTable()
    {
        if (s_fftTwiddleWpr != null) return;
        s_fftTwiddleWpr = new double[12];
        s_fftTwiddleWpi = new double[12];
        for (int passLog2 = 1; passLog2 <= 11; passLog2++)
        {
            int    size  = 1 << passLog2;
            double angle = -2.0 * Math.PI / size;
            s_fftTwiddleWpr[passLog2] = Math.Cos(angle);
            s_fftTwiddleWpi[passLog2] = Math.Sin(angle);
        }
    }

    // v8.3.6: per-band gamma lookup table. The hot per-band loop in
    // DoFftAndPublish does Math.Pow(norm, gamma_b) — 480 bands ×
    // 2000 FFT/sec = 960 000 Pow calls/sec at the user's responseMs=0.5
    // setting. Pow is ~50 ns each ≈ 5 % CPU just for this one operation.
    // Replace with a 256-entry lookup per band (8-bit input precision is
    // sufficient since the output is a byte anyway), pre-multiplied by
    // 255 and clamped, so the hot path becomes a single array read +
    // Float32 store. Memory: 480 × 256 × 4 B = 480 KB (fits in L2).
    // Flat 1D layout for cache locality (band b indexes [b*256 .. b*256+255]).
    static float[] s_bandGammaLut = null;
    static void EnsureBandGammaLut()
    {
        if (s_bandGammaLut != null) return;
        // Mirrors the per-band gamma computation in DoFftAndPublish:
        //   const int    GAMMA_RAMP_END = 150;
        //   const double LOW_GAMMA  = 1.3;
        //   const double HIGH_GAMMA = 0.8;        // = OUTPUT_GAMMA
        //   gamma_b = HIGH_GAMMA;
        //   if (b < GAMMA_RAMP_END) gamma_b = LOW_GAMMA + (HIGH_GAMMA - LOW_GAMMA) * (b / GAMMA_RAMP_END);
        const int    GAMMA_RAMP_END = 150;
        const double LOW_GAMMA      = 1.3;
        const double HIGH_GAMMA     = 0.8;
        var lut = new float[BAND_COUNT * 256];
        for (int b = 0; b < BAND_COUNT; b++)
        {
            double gamma_b = HIGH_GAMMA;
            if (b < GAMMA_RAMP_END)
            {
                double t = (double)b / GAMMA_RAMP_END;
                gamma_b  = LOW_GAMMA + (HIGH_GAMMA - LOW_GAMMA) * t;
            }
            int rowStart = b * 256;
            for (int q = 0; q < 256; q++)
            {
                double n = q / 255.0;
                double v = Math.Pow(n, gamma_b);
                if (v < 0.0) v = 0.0;
                if (v > 1.0) v = 1.0;
                lut[rowStart + q] = (float)(v * 255.0);
            }
        }
        s_bandGammaLut = lut;
    }

    static float[] InitHannWindow()
    {
        // v8.3.8: returns float[]. Compute in double for full precision then
        // narrow to float on store — the window is constant after init so the
        // per-element narrowing happens once and the narrowed values stay good.
        var w = new float[FFT_SIZE];
        double denom = FFT_SIZE - 1;
        double twoPi = 2.0 * Math.PI;
        for (int i = 0; i < FFT_SIZE; i++)
            w[i] = (float)(0.5 * (1.0 - Math.Cos(twoPi * i / denom)));
        return w;
    }

    // Populate per-band precompute tables for the given sample rate.
    // Cheap (runs in ~1 ms); called once at startup, and again only if
    // sample rate ever changes (e.g. user switches capture device).
    static void EnsureBandPrecompute(int sampleRate)
    {
        if (s_precomputedSampleRate == sampleRate) return;
        // v6.8.6: minFreq 20 -> 30 Hz. User noted music basically never
        // has content below ~30 Hz (most bass synths bottom out at 30-40
        // Hz, kick drum fundamentals 50-80 Hz, sub-bass 30-50 Hz). The
        // sub-20 Hz region was wasted real estate on the visualizer
        // since those bins were always near-silent. Pulling minFreq up
        // to 30 Hz redistributes the 480 log-spaced bands across a
        // narrower range, so each bass band now covers slightly more
        // useful frequency = visibly more dynamic bass bars.
        double minFreq = 30.0;
        double maxFreq = Math.Min(16000.0, sampleRate / 2.0);
        double binHz   = (double)sampleRate / FFT_SIZE;
        int    half    = FFT_SIZE / 2;
        const double TILT_DB_PER_OCT = 3.0;
        const double TILT_REF_HZ     = 1000.0;
        double log2inv = 1.0 / Math.Log(2.0);
        for (int b = 0; b < BAND_COUNT; b++)
        {
            double t0 = (double)b / BAND_COUNT;
            double t1 = (double)(b + 1) / BAND_COUNT;
            double f0 = minFreq * Math.Pow(maxFreq / minFreq, t0);
            double f1 = minFreq * Math.Pow(maxFreq / minFreq, t1);
            double fC = Math.Sqrt(f0 * f1);
            double tiltDb  = TILT_DB_PER_OCT * Math.Log(fC / TILT_REF_HZ) * log2inv;
            s_bandF0[b]      = f0;
            s_bandF1[b]      = f1;
            s_bandFCenter[b] = fC;
            s_bandTiltLin[b] = Math.Pow(10.0, tiltDb / 20.0);
            // v8.3.7: precomputed combined factor for the per-FFT hot loop.
            // REF_MAG must match the constant in DoFftAndPublish (112.0).
            s_bandScaleOverRef[b] = s_bandTiltLin[b] / 112.0;
            bool subBin = (f1 - f0) < binHz * 1.2;
            s_bandIsSubBin[b] = subBin;
            if (subBin)
            {
                double binPos = fC / binHz;
                int bLo = (int)Math.Floor(binPos);
                int bHi = bLo + 1;
                if (bLo < 1)        bLo = 1;
                if (bHi < 1)        bHi = 1;
                if (bLo > half - 1) bLo = half - 1;
                if (bHi > half - 1) bHi = half - 1;
                s_bandInterpLo[b]   = bLo;
                s_bandInterpHi[b]   = bHi;
                s_bandInterpFrac[b] = binPos - Math.Floor(binPos);
            }
            else
            {
                int i0 = Math.Max(1,        (int)Math.Floor(f0 / binHz));
                int i1 = Math.Min(half - 1, (int)Math.Ceiling(f1 / binHz));
                if (i1 < i0) i1 = i0;
                s_bandBinLo[b] = i0;
                s_bandBinHi[b] = i1;
            }
        }
        s_precomputedSampleRate = sampleRate;
    }
    // v6.7.0: reverted to fixed envelope constants. The v6.6.9 adaptive
    // scheme scaled alpha by HOP_SIZE to keep "time constant" in ms
    // constant, but the math slowed per-FFT convergence dramatically
    // at small HOP (0.047 at HOP=4 vs old 0.85), which in practice
    // felt subtly different — user said "too fast at current lowest
    // speed" after the slider didn't propagate. Fixed constants were
    // what users approved at v6.6.8.
    const float ENV_ATTACK = 0.85f;
    const float ENV_DECAY  = 0.28f;

    // Called when the silence gate fires — pulls the envelope toward zero
    // so bars settle down when the user pauses / the audio graph goes
    // quiet, without paying for an FFT we'd throw away.
    // v6.5.2: uses the same A/B double buffer pattern as DoFftAndPublish.
    static void DecayEnvelopeOnly()
    {
        byte[] bands = (s_latest == s_bandsA) ? s_bandsB : s_bandsA;
        for (int b = 0; b < BAND_COUNT; b++)
        {
            s_env[b] *= 0.88f;            // slow decay toward zero
            if (s_env[b] < 0.5f) s_env[b] = 0f;
            int v = (int)s_env[b];
            if (v < 0)   v = 0;
            if (v > 255) v = 255;
            bands[b] = (byte)v;
        }
        s_latest = bands;
        System.Threading.Interlocked.Increment(ref s_frame);
        SignalAllSseClients();
    }

    static void DoFftAndPublish(int sampleRate)
    {
        // Ensure per-band precompute tables are valid for this sample rate.
        // Early-out if they already are (cheap branch).
        EnsureBandPrecompute(sampleRate);

        // FFT_MASK is now a class-level const (v8.2.8); was previously a local here.
        int start = s_writePos;

        // v7.0.6: cheap silence gate. Skip the FFT + multi-pass band
        // processor entirely when the input is near-silent (e.g. paused
        // music, app-idle, between tracks). RMS over the FFT window is a
        // ~5 KB scan — vs FFT + per-band tilt/compressor/gamma + bass
        // expander + spatial unsharp + envelope which together cost an
        // order of magnitude more. We still publish a frame so the
        // overlay's lerp keeps decaying smoothly toward zero, but we
        // produce that frame from the existing envelope state without any
        // FFT. Net effect: CPU goes near-zero during quiet moments and
        // recovers immediately the moment audio returns above threshold.
        //
        // v9.6.6: scale the gate by s_sensitivity. The static 0.0008 threshold
        // was the "spectrum cuts off when Spotify is at 1%" bug friends were
        // hitting through SteelSeries Sonar. Their digital RMS hovered around
        // 0.001-0.005 — right at the gate boundary — causing flicker as music
        // dynamics nudged the signal above/below 0.0008. Worse: cranking
        // sensitivity to 100x didn't help because s_sensitivity is applied at
        // line 1786 AFTER this gate runs, so the gate killed the signal before
        // sensitivity could amplify it. Fix: divide the threshold by
        // s_sensitivity. Semantically correct — when the user says "make me
        // see 100x more", the gate should be 100x more permissive too. At
        // sensitivity=1.0 the gate behaves as v7.0.6 (0.0008); at sens=100x
        // the gate is at 8e-6, well below any real signal floor for quiet
        // virtual-mixer scenarios. CPU optimization for true silence still
        // works because true silence is below 1e-7 (quantization noise floor)
        // even at 100x.
        const double BASE_SILENCE_RMS_THRESHOLD = 0.0008;   // ~ -62 dBFS at sensitivity=1.0
        double silenceThreshold = BASE_SILENCE_RMS_THRESHOLD / Math.Max(0.5, (double)s_sensitivity);
        double rmsSum = 0.0;
        for (int i = 0; i < FFT_SIZE; i++)
        {
            int idx = (start + i) & FFT_MASK;
            double s = s_fftBuf[idx];
            rmsSum += s * s;
        }
        double rms = Math.Sqrt(rmsSum / FFT_SIZE);
        if (rms < silenceThreshold)
        {
            // Decay every band's envelope and baseline toward zero, then
            // publish the current envelope state. We use the same
            // ENV_DECAY/BASELINE_DECAY constants from the active path so
            // the visible fall behaves identically — silence-skip is
            // strictly a CPU optimization, not a visual change.
            byte[] bandsSilent = (s_latest == s_bandsA) ? s_bandsB : s_bandsA;
            bool allZero = true;
            for (int b = 0; b < BAND_COUNT; b++)
            {
                s_baseline[b] *= 0.96f;       // matches BASELINE_DECAY
                s_env[b]      *= 0.72f;       // matches the typical fall α at silence
                if (s_env[b] < 0.5f) s_env[b] = 0f;
                int v = (int)s_env[b];
                if (v < 0) v = 0; else if (v > 255) v = 255;
                if (v != 0) allZero = false;
                bandsSilent[b] = (byte)v;
            }
            // v7.0.9: skip publishing once silence has fully decayed AND the
            // previous frame was already all-zero. The visible state hasn't
            // changed (the overlay's idle-skip handles steady zero already)
            // so there's no value in waking every SSE client to send the same
            // bytes again. Frame counter stays put → SSE loop sees no new
            // frame → it sleeps until either audio returns or a keep-alive
            // timeout. CPU goes truly zero during sustained silence.
            if (allZero && s_lastSilenceWasZero)
            {
                return;
            }
            s_lastSilenceWasZero = allZero;
            s_latest = bandsSilent;
            System.Threading.Interlocked.Increment(ref s_frame);
            SignalAllSseClients();
            return;
        }
        // Audio is back above threshold — clear the all-zero flag so the
        // next silence sequence publishes its first decaying frame.
        s_lastSilenceWasZero = false;

        // v6.3.3 (optimized v6.5.2): circular buffer read with PRECOMPUTED
        // Hann window and bitmask wraparound. s_writePos points at the
        // NEXT slot to overwrite (= OLDEST sample); read from there,
        // wrapping via `& FFT_MASK` instead of the much slower `%`. The
        // Hann window values come from s_hannWindow (precomputed ONCE at
        // startup) instead of computing cos() 8192 times per FFT.
        // v8.3.8: float arithmetic throughout (was double). s_fftBuf is
        // float[], s_hannWindow is float[], s_real/imag are float[].
        // v9.0.0: Hann-window the input first into s_real (length N). The
        // RFFT path consumes this directly (re-packs into N/2 complex
        // internally); the legacy CFFT fallback uses it as the real input
        // with imaginary set to zero.
        for (int i = 0; i < FFT_SIZE; i++)
        {
            int idx = (start + i) & FFT_MASK;
            s_real[i] = s_fftBuf[idx] * s_hannWindow[i];
        }

        int half = FFT_SIZE / 2;
        if (s_rfftReady)
        {
            // v9.0.0 Real-FFT path. Half the FFT compute of the legacy CFFT.
            // Writes magnitudes for bins 0..N/2-1 directly into s_mag.
            RealFFTToMag(s_real);
        }
        else
        {
            // Legacy CFFT path — used if RFFT self-test failed at startup.
            for (int i = 0; i < FFT_SIZE; i++) s_imag[i] = 0f;
            FFT(s_real, s_imag);
            for (int i = 0; i < half; i++)
            {
                float r = s_real[i];
                float im = s_imag[i];
                s_mag[i] = (float)Math.Sqrt(r * r + im * im);
            }
        }
        // After this point, s_mag[0..half-1] contains the magnitude
        // spectrum regardless of which path produced it. Alias for the
        // band-loop below (matches the original variable name).
        float[] mag = s_mag;

        // Log-spaced band indices: cover 20 Hz to 20 kHz (full audible
        // range in v6.0.5 — was 40 Hz to 16 kHz). The 20 Hz floor lets
        // sub-bass register on its own bars for users with enlarged OBS
        // sources who want to see the full kick-drum / reggae-bass band.
        // The 20 kHz ceiling captures cymbal shimmer + hi-hat air the old
        // 16 kHz cap missed. At 4096/48 kHz the bin width is 11.7 Hz so
        // band 0 (20-21 Hz) maps to a distinct bin from band 1 (21-22.5 Hz)
        // — no flat plateau at the low end even at BAND_COUNT=240.
        // v6.5.2: all per-band tables (frequencies, tilt factors, bin
        // index ranges, sub-bin interpolation params) are precomputed
        // in EnsureBandPrecompute. The band loop below only does a
        // handful of float ops per band — no Pow / Log / Sqrt inside.
        // v6.5.2: reuse A/B double buffer instead of `new byte[BAND_COUNT]`
        // on every call. Writer fills the non-current buffer, then swaps
        // s_latest via volatile assignment. SSE readers see either the
        // old buffer (complete) or the new buffer (complete) — never a
        // half-written one.
        byte[] bands = (s_latest == s_bandsA) ? s_bandsB : s_bandsA;
        for (int b = 0; b < BAND_COUNT; b++)
        {
            double avg;
            if (s_bandIsSubBin[b])
            {
                // Sub-bin band: linearly interpolate between pre-picked
                // bracket bins at the band's log-center frequency.
                int bLo = s_bandInterpLo[b];
                int bHi = s_bandInterpHi[b];
                double frac = s_bandInterpFrac[b];
                avg = mag[bLo] * (1.0 - frac) + mag[bHi] * frac;
            }
            else
            {
                // Wide band: average over all bins it covers.
                int i0 = s_bandBinLo[b];
                int i1 = s_bandBinHi[b];
                double sum = 0;
                for (int i = i0; i <= i1; i++) sum += mag[i];
                avg = sum / (i1 - i0 + 1);
            }

            // dB-ish compression + scaling.  Empirically, Hann-windowed
            // 2048-point FFT on 32-bit float samples at 48 kHz produces
            // band-avg magnitudes that land roughly in [-30, +50] dB during
            // normal music playback (louder peaks briefly hit +60).  Map
            // that window to [0, 1] so quiet passages start above the floor
            // AND loud peaks still have headroom before clamping.
            //
            // Previous version used (+90)/90 which treated 0 dB as 100% —
            // but 0 dB is NOT peak for this formula, it's the middle of
            // the music range, so every audible source pegged all 60 bars
            // to 255 = full height. That's what "way too maxed out" looked
            // like.
            //
            // v5.4: gentle spectral tilt to balance bass-heaviness.  Real
            // music has a ~1/f amplitude spectrum (pink noise is exactly
            // -3 dB/oct), so without compensation the bass bars always
            // dominate visually.
            // v6.3.5: doubled from +1.5 to +3.0 dB/oct (FULL pink-noise
            // compensation). Previous half-compensation left the highs
            // (8 kHz and above) too quiet at normal listening volumes —
            // users had to crank the music before cymbals / hi-hat air /
            // snare crack were visible on the spectrum. Full comp lifts
            // 16 kHz by +12 dB (was +6) and pulls 40 Hz down by -14 dB
            // (was -7), so the bar heights now roughly match what you'd
            // see on a flat reference EQ during typical mastered music.
            // v6.5.2: tilt linear multiplier is precomputed per-band in
            // s_bandTiltLin[b] (= 10^(tiltDb/20)). Skips the per-FFT Log
            // + Pow calls that dominated this loop's cost.

            // v6.4.0: LINEAR AMPLITUDE mapping. The dB-log formula was
            // inherently non-proportional to volume: user reported 5 %
            // music volume producing 50 % bar height, because log
            // compression makes quiet signals look much louder than they
            // are. User's explicit calibration target: proportional
            // response, so X% music volume → X% (or similar) bar height.
            //
            // New pipeline:
            //   1. Compute pink-noise tilt IN dB (unchanged).
            //   2. Convert tilt to a linear amplitude multiplier
            //      (10^(tiltDb/20)) and apply it to avg magnitude.
            //   3. Divide by REF_MAG to get 0..1 bar height.
            //
            // REF_MAG tuned empirically against the user's volume-to-bar
            // targets: 25 % vol → 33 % bar, 50 % → 66 %, 75 % → 100 %.
            // Iteration history:
            //   v6.4.0: REF_MAG = 20   — way too tall
            //   v6.4.1: REF_MAG = 50   — bass peaks at 25 % hitting 95 %
            //   v6.4.2: REF_MAG = 150  — 30 % vol still hitting 90-100 %
            //   v6.4.3: REF_MAG = 350  — right curve, average 5 dB low
            //   v6.4.4: REF_MAG = 200  — +5 dB boost, but bars hit
            //                              ceiling too aggressively
            //   v6.4.5: REF_MAG = 112 (+5 dB more from v6.4.4) + knee
            //                              compressor @ 0.5 / 4.3 —
            //                              user said "this looks better"
            //   v6.4.6: REF_MAG = 63, knee 0.6 / 40:1 — ceiling broken
            //   v6.4.7: REF_MAG = 63 + two-stage compressor — bars too
            //                              pumped up and peaks hard to
            //                              reach ceiling (user: "looks
            //                              like +50 dB, 2 versions ago
            //                              was better")
            //   v6.4.8: REVERT to v6.4.5 parameters exactly. Good.
            //   v6.4.9: v6.4.8 + output gamma 0.8. User reported that
            //                              at 30 % volume or below,
            //                              quiet bands weren't visible
            //                              at all even though the music
            //                              was audible. Root cause:
            //                              pure-linear amplitude mapping
            //                              means quiet content drops
            //                              into sub-visible percentages
            //                              (a 5 % bar is barely 2-3 px
            //                              tall at default card size).
            //                              Applied a gentle gamma 0.8
            //                              to the OUTPUT (after the
            //                              compressor) — boosts quiet
            //                              content significantly (5 %
            //                              → 9 %, 20 % → 30 %, 30 % →
            //                              42 %) while leaving peak
            //                              region almost unchanged
            //                              (80 % → 84 %, 100 % → 100 %).
            //                              Gamma curve preserves
            //                              monotonicity and the
            //                              ceiling stays reachable.
            // v6.8.2: re-tuned for KICK-DROP PUNCH. User feedback: when
            // a bassline starts the front of visualizer fills to 50-75 %,
            // then a kick only adds 25 % more — feels uncentered, not
            // punchy. Root cause was the v6.4.5 compressor squashing
            // peaks (knee=0.5, ratio=4.3:1) which was the OPPOSITE of
            // what kick-drop dynamic range needs. New numbers:
            //   REF_MAG  60 -> 112: sustained bass takes ~half as much
            //                       of the card. Same signal that hit
            //                       57 % now hits ~35 %.
            //   KNEE_T   0.5 -> 0.75: compressor only kicks in for the
            //                       HIGHEST peaks. Below 0.75 (most of
            //                       the music) bars are pure linear, so
            //                       a kick shoots up unobstructed.
            //   KNEE_R   4.3 -> 2.0: even when the compressor IS active
            //                       (extreme transients), it's gentle so
            //                       very loud kicks still reach 95-100 %.
            //   OUTPUT_GAMMA stays at 0.8: low-end visibility preserved.
            // Net effect: sustained bass ~35 %, ordinary kick ~76 %,
            // loud kick ~93 %, hardest spike clips at 100 %. ~40 % of
            // the card is now 'kick punch range' instead of ~17 %.
            // REF_MAG = 112.0 is now baked into s_bandScaleOverRef[b] in
            // EnsureBandPrecompute (v8.3.7) — kept here as documentation only.
            const double KNEE_T      = 0.75;
            // v7.0.6: KNEE_R 2.0 → 1.6. Compression above the knee is gentler,
            // so loud kicks/punches reach 100 % easier without changing where
            // the knee starts (sustained content stays unaffected — that lives
            // below KNEE_T in the linear range).
            const double KNEE_R      = 1.6;
            const double OUTPUT_GAMMA = 0.8;
            // v8.3.7: precomputed `s_bandScaleOverRef[b] = tiltLin[b] / REF_MAG`
            // collapses the previous 3-op chain (mul + mul + div) into 2 muls.
            // v6.9.3 sensitivity slider behaviour preserved exactly — applied
            // here as a multiplier alongside the precomputed factor.
            double norm = avg * s_bandScaleOverRef[b] * s_sensitivity;
            if (norm > KNEE_T) {
                norm = KNEE_T + (norm - KNEE_T) / KNEE_R;
            }
            if (norm < 0) norm = 0;
            // v9.9.0: smooth asymptotic limiter instead of hard clip.
            // Hard clip: norm=1.0→255, norm=1.1→255 — identical output → flat
            // wall at the low end when sensitivity is high. Smooth limiter maps
            // the range (0.90, ∞) → (0.90, 1.0) via x/(1+x) so adjacent bands
            // with similar but non-identical energy produce different byte values:
            //   norm=0.90→0.90, norm=1.0→0.95, norm=1.5→0.986, norm→∞→1.0.
            // Values ≤0.90 are unchanged — no regression for normal sensitivity.
            if (norm > 0.90) {
                double _lx = (norm - 0.90) / 0.10;
                norm = 0.90 + 0.10 * _lx / (1.0 + _lx);
            }
            if (norm > 1) norm = 1;
            // v8.3.6: PER-BAND GAMMA via lookup table. Was Math.Pow(norm,
            // gamma_b) which dominated this loop's CPU cost (5% CPU at
            // user's HOP=24 / 2000 FFT/sec settings × 480 bands). Now a
            // single array read per band — the table stores
            // Pow(q/255, gamma_b) * 255 clamped, with gamma_b ramping
            // from LOW_GAMMA(1.3) at band 0 to HIGH_GAMMA(0.8) at band
            // GAMMA_RAMP_END(150). 8-bit input precision is sufficient
            // since output is byte. Falls back to inline Pow if table
            // wasn't initialized (defensive).
            int q = (int)(norm * 255.0);
            if (q < 0)   q = 0;
            if (q > 255) q = 255;
            if (s_bandGammaLut != null) {
                s_targets[b] = s_bandGammaLut[b * 256 + q];
            } else {
                const int    GAMMA_RAMP_END = 150;
                const double LOW_GAMMA      = 1.3;
                const double HIGH_GAMMA     = OUTPUT_GAMMA;
                double gamma_b = HIGH_GAMMA;
                if (b < GAMMA_RAMP_END) {
                    double t = (double)b / GAMMA_RAMP_END;
                    gamma_b  = LOW_GAMMA + (HIGH_GAMMA - LOW_GAMMA) * t;
                }
                norm = Math.Pow(norm, gamma_b);
                if (norm < 0) norm = 0;
                if (norm > 1) norm = 1;
                s_targets[b] = (float)(norm * 255.0);
            }
        }

        // v6.8.7: SPATIAL HIGH-PASS / UNSHARP MASK on low bands.
        // Bass content has a smooth-rolling-curve appearance because
        // adjacent low-frequency bands share most of their FFT bin
        // data (sub-bin interpolation + low FFT freq resolution at
        // bass). Applying an unsharp-mask filter — subtract a fraction
        // of the local 5-band average from each band — emphasises the
        // PEAK of the kick instead of the wide skirt around it. Effect
        // tapers from MAX_SHARPNESS at band 0 to 0 at LOW_SHARP_END
        // so the transition into the mid-band region is invisible.
        const int   LOW_SHARP_END = 150;
        const float MAX_SHARPNESS = 0.7f;
        for (int b = 0; b < LOW_SHARP_END; b++)
        {
            int b0 = b - 2; if (b0 < 0) b0 = 0;
            int b1 = b + 2; if (b1 > BAND_COUNT - 1) b1 = BAND_COUNT - 1;
            float sum = 0f; int n = 0;
            for (int bb = b0; bb <= b1; bb++) { sum += s_targets[bb]; n++; }
            float smoothed = sum / n;
            float t = (float)b / LOW_SHARP_END;
            float strength = MAX_SHARPNESS * (1.0f - t);
            float v = s_targets[b] + strength * (s_targets[b] - smoothed);
            if (v < 0)   v = 0;
            if (v > 255) v = 255;
            s_targetsSharp[b] = v;
        }
        // Copy sharpened low-band values back into the target array.
        // Mid + high bands keep their original targets untouched.
        for (int b = 0; b < LOW_SHARP_END; b++) s_targets[b] = s_targetsSharp[b];

        // v6.8.8: BASS TRANSIENT EXPANDER. The user's complaint:
        // 'a sustained bassline fills the front of the visualizer to
        // 50-70 %, leaving no headroom for kick punches.' Bassline
        // and kick produce the SAME magnitude in those bands — a
        // static curve can't distinguish them. Solution: track a
        // slow baseline (EMA over ~1-2 s) per low band. Output is a
        // SMALL fraction of the baseline + a BOOSTED version of the
        // excess above baseline. Sustained content (where target ≈
        // baseline) shrinks dramatically; transient kicks (where
        // target >> baseline) get expanded above their natural peak.
        // Effect tapers from full at band 0 to 0 by band 100 so it
        // doesn't disturb the rest of the spectrum.
        const int   TX_END           = 100;
        const float BASELINE_DECAY   = 0.96f;   // ~25 FFT half-life @ 93 Hz = ~270 ms
        // v7.0.7: SUSTAINED_KEEP 0.20 → 0.25 (+2 dB on sustained basslines).
        // v7.0.6 dropped the original 0.35 by 5 dB to 0.20 — user said that
        // pushed sustained content too low, so adding 2 dB back puts the net
        // change at −3 dB from the v7.0.5 baseline. Linear factor 10^(2/20)
        // = 1.26 applied to 0.20 gives 0.25. Kicks/punches above the
        // baseline are unaffected — those go through TRANSIENT_BOOST.
        const float SUSTAINED_KEEP   = 0.25f;
        const float TRANSIENT_BOOST  = 2.0f;
        for (int b = 0; b < TX_END; b++)
        {
            // Update slow baseline (low-pass on the target stream).
            s_baseline[b] = BASELINE_DECAY * s_baseline[b] + (1f - BASELINE_DECAY) * s_targets[b];
            // Excess = how much the current target rises ABOVE the baseline.
            float excess = s_targets[b] - s_baseline[b];
            if (excess < 0f) excess = 0f;
            // Reshaped target: shrunk baseline + boosted excess.
            float reshaped = s_baseline[b] * SUSTAINED_KEEP + excess * TRANSIENT_BOOST;
            // Linear ramp: full effect at band 0, none at TX_END.
            float t = (float)b / TX_END;
            float strength = 1.0f - t;
            float blended = strength * reshaped + (1f - strength) * s_targets[b];
            if (blended < 0f)     blended = 0f;
            if (blended > 255f)   blended = 255f;
            s_targets[b] = blended;
        }

        // v6.8.6: per-band envelope decay scaling. Bass bands get up
        // to 1.6x faster decay than mids/treble, ramping linearly to
        // 1.0x at band 150. Combined with the v6.8.7 sharpening, bass
        // bars now show distinct kick-shaped peaks AND drop fast
        // between hits — 'punchy' look.
        const int LOW_RAMP_END = 150;
        for (int b = 0; b < BAND_COUNT; b++)
        {
            float target = s_targets[b];
            float envDecay_b = ENV_DECAY;
            if (b < LOW_RAMP_END) {
                float t = (float)b / LOW_RAMP_END;
                envDecay_b = ENV_DECAY * (1.6f - 0.6f * t);
            }
            float prev = s_env[b];
            float next = (target > prev)
                ? prev + (target - prev) * ENV_ATTACK
                : prev + (target - prev) * envDecay_b;
            s_env[b] = next;
            bands[b] = (byte)Math.Max(0, Math.Min(255, (int)next));
        }
        s_latest = bands;
        System.Threading.Interlocked.Increment(ref s_frame);
        SignalAllSseClients();
    }

    // Classic Cooley-Tukey radix-2 FFT, in-place on (real[], imag[]).
    // N must be a power of two. v8.3.8: data arrays are now float[] (was
    // double[]) for ~2× faster SIMD throughput and half memory bandwidth.
    // The per-butterfly accumulator (wr, wi) STAYS double — twiddle
    // recurrence drift over 11 passes × 1024 butterflies would lose ~3
    // significant figures in float, but in double the precision is never
    // an issue. Float×double mixed arithmetic promotes to double for the
    // multiply, then narrows on store to re[] / im[].
    static void FFT(float[] re, float[] im)
    {
        int n = re.Length;
        // Bit reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                float tr = re[i]; re[i] = re[j]; re[j] = tr;
                float ti = im[i]; im[i] = im[j]; im[j] = ti;
            }
        }
        // Butterfly passes. v8.3.6: precomputed twiddle wpr/wpi (Cos/Sin
        // for each pass-size cached in s_fftTwiddleWpr/Wpi at startup).
        int passLog2Cur = 0;
        for (int size = 2; size <= n; size <<= 1)
        {
            passLog2Cur++;
            double wpr, wpi;
            if (s_fftTwiddleWpr != null && passLog2Cur < s_fftTwiddleWpr.Length)
            {
                wpr = s_fftTwiddleWpr[passLog2Cur];
                wpi = s_fftTwiddleWpi[passLog2Cur];
            }
            else
            {
                double angle = -2.0 * Math.PI / size;
                wpr = Math.Cos(angle);
                wpi = Math.Sin(angle);
            }
            int half = size >> 1;
            for (int k = 0; k < n; k += size)
            {
                double wr = 1.0, wi = 0.0;
                for (int m = 0; m < half; m++)
                {
                    int a = k + m;
                    int b = a + half;
                    // Mixed precision: data is float, twiddle accumulator
                    // is double. The float values promote to double for
                    // the multiplies; results are narrowed back to float
                    // on store. Accumulated drift over the pass is
                    // negligible because wr/wi stay double end-to-end.
                    double rb = re[b], ib = im[b];
                    double tr = wr * rb - wi * ib;
                    double ti = wr * ib + wi * rb;
                    re[b] = (float)(re[a] - tr); im[b] = (float)(im[a] - ti);
                    re[a] = (float)(re[a] + tr); im[a] = (float)(im[a] + ti);
                    double tmp = wr;
                    wr = wpr * wr - wpi * wi;
                    wi = wpr * wi + wpi * tmp;
                }
            }
        }
    }

    // ── SSE server ────────────────────────────────────────────────────────
    // Binds to 127.0.0.1:4243. Only one endpoint — /spectrum — emits SSE
    // frames; anything else returns 404. We keep the binding narrow so
    // nothing on the network can hit this port.
    // v6.8.3: one-shot URL ACL self-registration. Called when
    // HttpListener.Start() throws "access denied" (HRESULT 5). On
    // unprivileged user accounts under group policies that lock down
    // Windows HTTP.sys, the system requires explicit URL ACL grants
    // before non-admin processes can listen. We attempt to register
    // for "Everyone" so any user on the machine can host. The netsh
    // process itself needs admin rights to add URL ACLs, so this
    // operation will fail silently for non-admin runs — but logs
    // tell us so users know to run once as admin OR for the MSI
    // installer to add the ACL during install (preferred path).
    static bool TryAddUrlAcl()
    {
        try
        {
            string[] urls = { "http://127.0.0.1:4243/", "http://localhost:4243/" };
            foreach (string url in urls)
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = string.Format("http add urlacl url={0} user=Everyone", url),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    Verb = "runas"   // request elevation (no-op if already elevated)
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) { Log("urlacl: netsh failed to spawn for " + url); continue; }
                    p.WaitForExit(3000);
                    string sout = p.StandardOutput.ReadToEnd();
                    string serr = p.StandardError.ReadToEnd();
                    Log(string.Format("urlacl: {0} → exit={1} out='{2}' err='{3}'",
                        url, p.ExitCode, sout.Trim(), serr.Trim()));
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Log("urlacl: registration threw — " + ex.Message);
            return false;
        }
    }

    static void StartHttp()
    {
        // v6.8.3: more robust startup. Some user environments (corporate
        // images, certain AV setups, Windows N editions, etc.) deny
        // HttpListener.Start() on 127.0.0.1 without URL ACL registration.
        // We now (1) add BOTH 127.0.0.1 and localhost prefixes so the
        // tray can hit either, (2) on failure, attempt a one-shot URL
        // ACL registration via netsh and retry, (3) log loudly with
        // the actual exception so users + their friends know what's
        // wrong from the log.
        var http = new HttpListener();
        http.Prefixes.Add("http://127.0.0.1:4243/");
        http.Prefixes.Add("http://localhost:4243/");
        try { http.Start(); }
        catch (HttpListenerException hex)
        {
            Log(string.Format("http: HttpListenerException on Start — code={0} ({1})", hex.ErrorCode, hex.Message));
            // Most common code is 5 (access denied) on Windows when the
            // URL prefix isn't registered. Try to register and retry.
            if (TryAddUrlAcl())
            {
                try {
                    var http2 = new HttpListener();
                    http2.Prefixes.Add("http://127.0.0.1:4243/");
                    http2.Prefixes.Add("http://localhost:4243/");
                    http2.Start();
                    http = http2;
                    Log("http: bound after URL ACL self-registration");
                } catch (Exception ex2) {
                    Log("http: still failed after URL ACL retry — " + ex2.Message);
                    return;
                }
            } else {
                return;
            }
        }
        catch (Exception ex)
        {
            Log("http: unexpected startup exception — " + ex.Message);
            return;
        }
        Log("http: listening on http://127.0.0.1:4243/ + http://localhost:4243/");

        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = http.GetContext(); }
                catch (Exception ex) { Log("http: GetContext ex " + ex.Message); break; }
                ThreadPool.QueueUserWorkItem(__ => HandleRequest(ctx));
            }
        });
    }

    // JSON-escape a string for inclusion in a JSON body. Handles the four
    // characters JSON requires us to escape — quote, backslash, newline, tab.
    // Tiny; we don't need a full JSON library for our one-field responses.
    static string JsonEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            if      (c == '\\') sb.Append("\\\\");
            else if (c == '\"') sb.Append("\\\"");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\t') sb.Append("\\t");
            else if (c == '\r') sb.Append("\\r");
            else if (c < 0x20)  sb.Append(string.Format("\\u{0:X4}", (int)c));
            else                sb.Append(c);
        }
        return sb.ToString();
    }

    // v5.0.3 — classify a device into a type hint so the tray dialog can
    // show smart descriptions.  Matters A LOT for virtual-mixer users: VB-
    // Audio Matrix / Voicemeeter / VB-Cable all behave very differently
    // from physical outputs when it comes to WASAPI loopback.  We return
    // a specific-enough type that the UI layer can pick the right copy.
    static string ClassifyDevice(string name, DataFlow flow)
    {
        if (string.IsNullOrEmpty(name)) return "unknown";
        string n = name.ToLowerInvariant();

        // VB-Audio Matrix — specific product, devices show up as
        // "Discord (VB-Audio Matrix VAIO)", "Main (VB-Audio Matrix VAIO)",
        // "Media (VB-Audio Matrix VAIO)" in Windows. Matrix routes audio
        // internally BEFORE the Windows shared mixer, so both loopback
        // (on render side) AND capture (on the paired capture side) come
        // back with zeros. Users need to target the physical endpoint
        // Matrix is routing TO.
        if (n.Contains("matrix vaio") || n.Contains("vb-audio matrix") || n.Contains("vb audio matrix"))
        {
            return flow == DataFlow.Capture ? "vbmatrix_capture" : "vbmatrix_render";
        }
        // Voicemeeter — Potato has B1/B2/B3 output buses, Banana has B1/B2,
        // base has B1. These appear as CAPTURE endpoints ("Voicemeeter Out
        // B1") and CONTAIN the final mix — correct target for Voicemeeter
        // users.  Render side ("Voicemeeter Input", "Voicemeeter AUX Input")
        // is where apps send audio; loopback on them is silent.
        if (n.Contains("voicemeeter"))
        {
            if (flow == DataFlow.Capture) return "voicemeeter_bus_out";
            return "voicemeeter_input";
        }
        // VB-Audio Cable ("Cable Input" is render, "Cable Output" is capture)
        if (n.Contains("vb-audio") && n.Contains("cable"))
        {
            return flow == DataFlow.Capture ? "vbaudio_cable_out" : "vbaudio_cable_in";
        }
        // Generic VB-Audio (Hi-Fi Cable, HiFi-Cable, Virtual-Cable variants)
        if (n.Contains("vb-audio") || n.Contains("vb audio") || n.Contains("hi-fi cable") || n.Contains("hifi cable"))
        {
            return flow == DataFlow.Capture ? "vbaudio_virtual_out" : "vbaudio_virtual_in";
        }
        // Generic virtual-audio signatures (Virtual Audio Cable, Synchronous
        // Audio Router, generic "virtual" in the name).
        if (n.Contains("virtual") || n.Contains("vac ") || n.Contains("synchronous audio router"))
        {
            return "virtual_other";
        }
        // Everything else: treat as physical audio hardware (Realtek, Audient,
        // Focusrite, Scarlett, Steinberg, USB Audio, HDMI, Speakers, etc.).
        return "physical";
    }

    static void HandleDevices(HttpListenerContext ctx)
    {
        try
        {
            MMDevice defaultRender = null;
            MMDevice defaultCapture = null;
            try { defaultRender  = s_enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console); } catch { }
            try { defaultCapture = s_enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); } catch { }
            string defaultRenderId  = defaultRender  != null ? defaultRender.ID  : "";
            string defaultCaptureId = defaultCapture != null ? defaultCapture.ID : "";

            var sb = new StringBuilder();
            sb.Append("{\"current\":\"").Append(JsonEscape(s_currentDeviceId ?? "")).Append("\",");
            sb.Append("\"backend\":\"").Append(JsonEscape(s_currentBackend ?? "wasapi_loopback")).Append("\",");
            sb.Append("\"default\":\"").Append(JsonEscape(defaultRenderId)).Append("\",");
            sb.Append("\"defaultCapture\":\"").Append(JsonEscape(defaultCaptureId)).Append("\",");
            sb.Append("\"devices\":[");
            bool first = true;

            // ------- WASAPI RENDER (loopback) -----------------------
            // Used by the "WASAPI Loopback" backend. One row per active
            // render endpoint. Captures the output mix of that endpoint.
            try
            {
                var devices = s_enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var d in devices)
                {
                    try
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        string type = ClassifyDevice(d.FriendlyName, DataFlow.Render);
                        sb.Append("{\"backend\":\"wasapi_loopback\",");
                        sb.Append("\"id\":\"").Append(JsonEscape(d.ID)).Append("\",");
                        sb.Append("\"name\":\"").Append(JsonEscape(d.FriendlyName)).Append("\",");
                        sb.Append("\"flow\":\"render\",");
                        sb.Append("\"type\":\"").Append(type).Append("\",");
                        sb.Append("\"isDefault\":").Append(d.ID == defaultRenderId ? "true" : "false").Append('}');
                    }
                    catch (Exception exRow) { Log("enum render: per-device failure: " + exRow.Message); }
                }
            } catch (Exception ex) { Log("enum render: " + ex.Message); }

            // ------- WASAPI CAPTURE (shared + exclusive/WDM-KS) -----
            // Same device list surfaces twice - once for shared-mode
            // capture (wasapi_input) and once for exclusive-mode
            // capture (wasapi_exclusive / WDM-KS). Users pick whichever
            // latency / mixing behavior they want.
            try
            {
                var devices = s_enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                foreach (var d in devices)
                {
                    try
                    {
                        string type = ClassifyDevice(d.FriendlyName, DataFlow.Capture);

                        // Shared-mode WASAPI input
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append("{\"backend\":\"wasapi_input\",");
                        sb.Append("\"id\":\"").Append(JsonEscape(d.ID)).Append("\",");
                        sb.Append("\"name\":\"").Append(JsonEscape(d.FriendlyName)).Append("\",");
                        sb.Append("\"flow\":\"capture\",");
                        sb.Append("\"type\":\"").Append(type).Append("\",");
                        sb.Append("\"isDefault\":").Append(d.ID == defaultCaptureId ? "true" : "false").Append('}');

                        // Exclusive-mode (WDM-KS) capture of same device.
                        sb.Append(',');
                        sb.Append("{\"backend\":\"wasapi_exclusive\",");
                        sb.Append("\"id\":\"").Append(JsonEscape(d.ID)).Append("\",");
                        sb.Append("\"name\":\"").Append(JsonEscape(d.FriendlyName)).Append("\",");
                        sb.Append("\"flow\":\"capture\",");
                        sb.Append("\"type\":\"").Append(type).Append("\",");
                        sb.Append("\"isDefault\":").Append(d.ID == defaultCaptureId ? "true" : "false").Append('}');
                    }
                    catch (Exception exRow) { Log("enum capture: per-device failure: " + exRow.Message); }
                }
            } catch (Exception ex) { Log("enum capture: " + ex.Message); }

            // ------- MME WAVE-IN -------------------------------------
            // Legacy waveIn devices. WaveInEvent.DeviceCount returns the
            // number of available drivers; DeviceNumber -1 = Wave Mapper
            // (default system input). On Realtek / Creative / many
            // consumer sound cards, Stereo Mix shows up here and is the
            // classic "capture the speakers" path for MME.
            try
            {
                // Wave Mapper row first (maps to whatever Windows is
                // using as default recording device).
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"backend\":\"mme\",");
                sb.Append("\"id\":\"-1\",");
                sb.Append("\"name\":\"Wave Mapper (default MME input)\",");
                sb.Append("\"flow\":\"capture\",");
                sb.Append("\"type\":\"mme_mapper\",");
                sb.Append("\"isDefault\":true}");

                int count = NAudio.Wave.WaveInEvent.DeviceCount;
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var caps = NAudio.Wave.WaveInEvent.GetCapabilities(i);
                        sb.Append(',');
                        sb.Append("{\"backend\":\"mme\",");
                        sb.Append("\"id\":\"").Append(i).Append("\",");
                        sb.Append("\"name\":\"").Append(JsonEscape(caps.ProductName ?? ("MME device " + i))).Append("\",");
                        sb.Append("\"flow\":\"capture\",");
                        sb.Append("\"type\":\"mme\",");
                        sb.Append("\"isDefault\":false}");
                    }
                    catch (Exception exRow) { Log(string.Format("enum mme: GetCapabilities({0}) failed: {1}", i, exRow.Message)); }
                }
            } catch (Exception ex) { Log("enum mme: " + ex.Message); }

            // ------- ASIO drivers ------------------------------------
            // GetDriverNames reads HKLM\SOFTWARE\ASIO for every
            // registered driver. Returns an empty array on systems
            // without any ASIO driver (most consumer PCs). We still
            // emit a zero-result marker entry so the UI can render a
            // helpful "No ASIO drivers found - install ASIO4ALL" row.
            try
            {
                string[] asioDrivers = NAudio.Wave.AsioOut.GetDriverNames() ?? new string[0];
                if (asioDrivers.Length == 0)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"backend\":\"asio\",");
                    sb.Append("\"id\":\"\",");
                    sb.Append("\"name\":\"(no ASIO drivers installed)\",");
                    sb.Append("\"flow\":\"capture\",");
                    sb.Append("\"type\":\"asio_none\",");
                    sb.Append("\"isDefault\":false}");
                }
                else
                {
                    foreach (var name in asioDrivers)
                    {
                        try
                        {
                            // Probe the driver for its actual channel count
                            // (cached after first hit). Emit one card per
                            // stereo channel pair so the user can pick
                            // which pair they routed audio to in the ASIO
                            // driver's own mixer/grid. Compound id is
                            // "driverName|channelOffset" - OpenCaptureForBackend
                            // parses it back out to configure AsioCaptureAdapter.
                            // Cap at 16 pairs (32 channels) per driver - VB-Matrix
                            // exposes 128 channels on its VASIO-128 variant which
                            // would produce 64 entries per driver and 500+ total
                            // entries in the dialog. 32 channels covers practical
                            // Matrix routing setups (Main/Discord/Media + aux).
                            const int MAX_PAIRS_PER_DRIVER = 16;
                            int chCount = GetAsioInputChannelCount(name);
                            int pairs   = System.Math.Max(1, System.Math.Min(MAX_PAIRS_PER_DRIVER, chCount / 2));
                            for (int p = 0; p < pairs; p++)
                            {
                                int ofs = p * 2;
                                int chA = ofs + 1;
                                int chB = ofs + 2;
                                string compoundId = name + "|" + ofs;
                                string displayName = (pairs > 1)
                                    ? name + "  -  Ch " + chA + "-" + chB
                                    : name;

                                if (!first) sb.Append(',');
                                first = false;
                                sb.Append("{\"backend\":\"asio\",");
                                sb.Append("\"id\":\"").Append(JsonEscape(compoundId)).Append("\",");
                                sb.Append("\"name\":\"").Append(JsonEscape(displayName)).Append("\",");
                                sb.Append("\"flow\":\"capture\",");
                                sb.Append("\"type\":\"asio\",");
                                sb.Append("\"isDefault\":false}");
                            }
                        }
                        catch (Exception exRow) { Log("enum asio: per-driver failure (" + name + "): " + exRow.Message); }
                    }
                }
            } catch (Exception ex) { Log("enum asio: " + ex.Message); }

            sb.Append("]}");
            byte[] raw = Encoding.UTF8.GetBytes(sb.ToString());
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(raw, 0, raw.Length);
        }
        catch (Exception ex)
        {
            Log("HandleDevices: " + ex.Message);
            ctx.Response.StatusCode = 500;
        }
        ctx.Response.Close();
    }

    static void HandleSetDevice(HttpListenerContext ctx)
    {
        try
        {
            string body;
            using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                body = sr.ReadToEnd();

            // v8.2.2 input validation. Previously this regex-extracted
            // backend/id and silently defaulted both to wasapi_loopback on
            // missing fields, returning 200 for ANY garbage body. Surfaced
            // by Phase B fuzz audit (finding 6.3). Now: reject malformed
            // input with 400 + a specific error message instead of silently
            // no-op'ing while pretending to succeed.
            string trimmed = body == null ? "" : body.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '{' || trimmed[trimmed.Length - 1] != '}')
            {
                ReplySetDeviceError(ctx, 400, "Body must be a JSON object");
                return;
            }
            var midx = System.Text.RegularExpressions.Regex.Match(body, "\"id\"\\s*:\\s*\"([^\"]*)\"");
            var mbck = System.Text.RegularExpressions.Regex.Match(body, "\"backend\"\\s*:\\s*\"([^\"]*)\"");
            if (!mbck.Success)
            {
                ReplySetDeviceError(ctx, 400, "Missing required field: backend");
                return;
            }
            if (!midx.Success)
            {
                ReplySetDeviceError(ctx, 400, "Missing required field: id");
                return;
            }
            string newBackend = mbck.Groups[1].Value;
            string newId      = midx.Groups[1].Value;

            // Backend must be one of the known strings. Keep this list in
            // sync with the switch in OpenCapture.
            var known = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase) {
                "wasapi_loopback", "wasapi_input", "wasapi_exclusive",
                "wdm_ks", "wdmks", "ks",
                "mme", "wavein", "asio"
            };
            if (string.IsNullOrEmpty(newBackend) || !known.Contains(newBackend))
            {
                ReplySetDeviceError(ctx, 400, "Unknown backend '" + newBackend + "' (valid: wasapi_loopback, wasapi_input, wasapi_exclusive, wdm_ks, mme, asio)");
                return;
            }

            Log(string.Format("set-device: requested backend='{0}' id='{1}'", newBackend, newId));
            s_currentBackend = newBackend;
            // KEEP "default" as a sentinel — and an empty id string is
            // equivalent to default — so ResolveTargetDevice can force the
            // system-default endpoint instead of using whatever the last
            // config file wrote.
            s_currentDeviceId = string.IsNullOrEmpty(newId) ? null : newId;
            s_deviceChangeEvent.Set();
            byte[] raw = Encoding.UTF8.GetBytes("{\"ok\":true,\"backend\":\"" + JsonEscape(s_currentBackend) + "\",\"id\":\"" + JsonEscape(s_currentDeviceId ?? "") + "\"}");
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(raw, 0, raw.Length);
        }
        catch (Exception ex)
        {
            Log("HandleSetDevice: " + ex.Message);
            ctx.Response.StatusCode = 500;
        }
        ctx.Response.Close();
    }

    static void ReplySetDeviceError(HttpListenerContext ctx, int status, string message)
    {
        try
        {
            Log("set-device: 400 — " + message);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/plain";
            byte[] raw = Encoding.UTF8.GetBytes(message);
            ctx.Response.OutputStream.Write(raw, 0, raw.Length);
        }
        catch { }
        try { ctx.Response.Close(); } catch { }
    }

    // v6.6.9: live-tune HOP_SIZE from customize.html's Response Time slider.
    // Accepts POST body {"hop":N} (samples) OR {"ms":X} (ms-at-48kHz converted).
    // Clamps to 1..2048 so FFT remains valid. Updates persist via the main
    // config.json path (customize also writes spectrum.responseMs there).
    static void HandleSetHop(HttpListenerContext ctx)
    {
        try
        {
            string body;
            using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                body = sr.ReadToEnd();
            int newHop = s_hopSize;
            var mh = System.Text.RegularExpressions.Regex.Match(body, "\"hop\"\\s*:\\s*(\\d+)");
            var mm = System.Text.RegularExpressions.Regex.Match(body, "\"ms\"\\s*:\\s*([0-9.]+)");
            if (mh.Success)
            {
                newHop = int.Parse(mh.Groups[1].Value);
            }
            else if (mm.Success)
            {
                double ms = double.Parse(mm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                newHop = (int)Math.Round(ms * 48.0);   // assumes 48 kHz; clamp covers mismatches
            }
            if (newHop < 1) newHop = 1;
            if (newHop > FFT_SIZE) newHop = FFT_SIZE;
            s_hopSize   = newHop;
            // v9.9.3: effective stride floored at FFT_MIN_STRIDE (8 ms).
            // Hop values below this produce no extra latency benefit
            // (SSE is the real bottleneck) but would spin 48 000+ FFTs/sec.
            s_fftStride = Math.Max(newHop, FFT_MIN_STRIDE);
            double effMs     = newHop     * 1000.0 / 48000.0;
            double strideMs  = s_fftStride * 1000.0 / 48000.0;
            Log(string.Format("set-hop: requested={0} ({1:F3} ms), effective stride={2} ({3:F3} ms, floor={4} samples)",
                newHop, effMs, s_fftStride, strideMs, FFT_MIN_STRIDE));
            byte[] raw = Encoding.ASCII.GetBytes("{\"ok\":true,\"hop\":" + newHop + ",\"ms\":" + effMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "}");
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(raw, 0, raw.Length);
        }
        catch (Exception ex)
        {
            Log("HandleSetHop: " + ex.Message);
            ctx.Response.StatusCode = 500;
        }
        ctx.Response.Close();
    }

    // v6.9.3: live-tune the Sensitivity multiplier from customize.html's
    // Sensitivity slider. Body: {"sensitivity":N} where N is 0.5..10.0
    // (clamped). Takes effect on the very next FFT frame — no restart,
    // no FFT state reset.
    static void HandleSetSensitivity(HttpListenerContext ctx)
    {
        try
        {
            string body;
            using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                body = sr.ReadToEnd();
            var m = System.Text.RegularExpressions.Regex.Match(body, "\"sensitivity\"\\s*:\\s*([0-9.]+)");
            if (m.Success)
            {
                float val = float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (val < 0.1f) val = 0.1f;
                // v9.6.5: bumped server-side clamp from 20.0 -> 100.0. Friends
                // streaming through SteelSeries Sonar / Voicemeeter / similar
                // virtual mixers were maxing 20x and still seeing nearly-flat
                // spectrum because Sonar's software volume slider sits at 5-10%
                // for headphone safety, multiplying the captured digital signal
                // by ~0.05-0.10. With s_inputGain hardcoded to 1.0x for
                // wasapi_loopback, that's 0.05-0.10x effective without any
                // user multiplier. Letting users push to 100x covers worst-
                // case quiet virtual mixer scenarios. Float-precision noise
                // floor is ~-150 dB, +40 dB of gain takes that to -110 dB —
                // still inaudible / invisible. The actual concern is
                // spectrum-bar saturation for normally-loud sources, which
                // self-corrects: the user just doesn't crank the slider.
                if (val > 100.0f) val = 100.0f;
                s_sensitivity = val;
                Log(string.Format("set-sensitivity: sensitivity={0:F2}x", val));
            }
            byte[] raw = Encoding.ASCII.GetBytes("{\"ok\":true,\"sensitivity\":" + s_sensitivity.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "}");
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(raw, 0, raw.Length);
        }
        catch (Exception ex)
        {
            Log("HandleSetSensitivity: " + ex.Message);
            ctx.Response.StatusCode = 500;
        }
        ctx.Response.Close();
    }

    static void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url.AbsolutePath.ToLowerInvariant();
            // CORS for the overlay page served from server.js (different port).
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
            ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
            // v6.7.0: handle CORS preflight for cross-origin POSTs (customize
            // at 4242 -> this server at 4243). Without this, the browser
            // aborts the actual POST because the preflight returned 404.
            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            if (path == "/spectrum")
            {
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers["Cache-Control"]      = "no-cache, no-transform";
                ctx.Response.Headers["Connection"]         = "keep-alive";
                ctx.Response.Headers["X-Accel-Buffering"]  = "no";
                // Fire frames at ~30 fps — the overlay interpolates every
                // rAF tick so visually it's 60 fps, and halving the SSE
                // rate cuts our HTTP serve cost in half.
                System.Threading.Interlocked.Increment(ref s_activeClients);
                long lastSent = -1;
                // v7.0.8: per-client AutoResetEvent. DoFftAndPublish (and the
                // silence-skip path) Sets every client's event right after
                // updating s_latest+s_frame, so this loop wakes within ~sub-ms
                // of the new frame being ready instead of waiting up to a full
                // SSE_INTERVAL_MS poll period. The 8 ms WaitOne timeout still
                // serves as keep-alive so the loop can't get permanently
                // blocked if a Set is somehow missed (and it's also where we
                // recheck shutdown conditions). Per-client events (instead of
                // a single shared one) so multiple clients (OBS + customize
                // preview) all wake simultaneously on every publish — Set
                // on a single AutoResetEvent only wakes ONE waiter.
                AutoResetEvent mySignal = new AutoResetEvent(false);
                lock (s_sseClientEventsLock) { s_sseClientEvents.Add(mySignal); }
                // v8.3.0: per-client SSE rate cap, driven by ?fps=N query
                // parameter. The Frame Rate slider in customize.html now
                // controls the actual SSE delivery rate (not just the
                // overlay's rAF cadence). Server clamps to [10, 2000] —
                // 2000 is the absolute ceiling because that's the FFT rate
                // ceiling at the lowest HOP setting (HOP=24 / 0.5 ms response
                // time). 10 is a reasonable floor (one frame every 100 ms).
                // Default 120 if no/invalid fps supplied (preserves the
                // v8.2.9 default for clients that don't set the query param).
                int  reqFps = 120;
                try {
                    string q = ctx.Request.Url.Query;   // includes leading '?' or empty
                    if (!string.IsNullOrEmpty(q) && q.StartsWith("?")) q = q.Substring(1);
                    foreach (string pair in q.Split('&')) {
                        int eq = pair.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = pair.Substring(0, eq);
                        string v = pair.Substring(eq + 1);
                        if (k == "fps") {
                            int parsed;
                            if (int.TryParse(v, System.Globalization.NumberStyles.Integer,
                                             System.Globalization.CultureInfo.InvariantCulture, out parsed)) {
                                if (parsed < 10)   parsed = 10;
                                if (parsed > 2000) parsed = 2000;
                                reqFps = parsed;
                            }
                            break;
                        }
                    }
                } catch { /* keep default */ }
                // v8.3.1: sub-millisecond throttle precision. v8.3.0 used
                // Stopwatch.ElapsedMilliseconds + WaitOne(int ms) which has
                // 1 ms quantization at best, so target rates 500-1500 all
                // landed around ~340 fps (the OS scheduler floor at 1 ms
                // wait granularity, even with timeBeginPeriod(1) active).
                // Now: track gaps in Stopwatch ticks (100 ns precision on
                // modern Windows) and use SpinWait for sub-ms residuals.
                long minGapTicks = (reqFps >= 2000)
                    ? 0L
                    : System.Diagnostics.Stopwatch.Frequency / (long)reqFps;
                Log("sse: client connected fps=" + reqFps
                    + " (min-gap=" + (minGapTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                    + "ms)");
                long lastSendTs = System.Diagnostics.Stopwatch.GetTimestamp() - minGapTicks;
                try { while (true) {
                    long f = System.Threading.Interlocked.Read(ref s_frame);
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    long sinceLast = now - lastSendTs;

                    // ── Send decision ────────────────────────────────────────
                    // Two modes:
                    //   • UNTHROTTLED (fps=2000 special-case, minGapTicks=0):
                    //     send strictly on fresh frames (signal-driven). Sending
                    //     a duplicate here would just spin at infinite rate.
                    //   • THROTTLED (any other fps):
                    //     send AT LEAST every minGapTicks regardless of whether
                    //     a fresh frame is available. Audio buffers arrive in
                    //     ~5 ms bursts (10 FFTs in <1 ms then 4 ms quiet), so a
                    //     "fresh-only" policy was capping output at burst rate
                    //     (~400 fps) even if the user asked for 1000. Allowing
                    //     duplicate transmits during quiet periods makes the
                    //     slider value the actual SSE rate. Cost: minor extra
                    //     localhost bandwidth (each frame is ~640 B; at 1000
                    //     fps that's 640 KB/s, trivial). The browser-side
                    //     redundant atob is the real ceiling at very high fps —
                    //     same root cause as the v8.2.9 1-fps overlay regression.
                    bool canSend = (minGapTicks == 0)
                        ? (f != lastSent)
                        : (sinceLast >= minGapTicks);

                    if (canSend) {
                        byte[] b = s_latest;
                        // v8.3.6: cache the base64-encoded SSE frame across
                        // duplicate sends. v8.3.3 enabled allow-duplicates so
                        // the slider rate is achieved during quiet windows;
                        // each duplicate previously regenerated the base64
                        // string from scratch. With the cache (process-wide
                        // shared, gated by reference-equality on s_latest),
                        // duplicate sends skip Convert.ToBase64String +
                        // GetBytes entirely. s_latest swaps between s_bandsA/B
                        // exactly once per FFT publish, so reference-equality
                        // is a perfect "is this the same frame as last time"
                        // signal. Multiple SSE clients all benefit from the
                        // shared cache for back-to-back identical frames.
                        byte[] raw;
                        var cachedBytes  = s_sseCacheBytes;
                        var cachedRaw    = s_sseCacheRaw;
                        if (cachedRaw != null && object.ReferenceEquals(cachedBytes, b)) {
                            raw = cachedRaw;
                        } else {
                            // v7.0.9: server emits `data: BASE64\n\n` directly.
                            string payload = "data: " + Convert.ToBase64String(b) + "\n\n";
                            raw = Encoding.ASCII.GetBytes(payload);
                            // Publish to cache — racy but safe (worst case two
                            // threads compute the same payload concurrently;
                            // the loser's value is overwritten and GC'd).
                            s_sseCacheBytes = b;
                            s_sseCacheRaw   = raw;
                        }
                        try
                        {
                            ctx.Response.OutputStream.Write(raw, 0, raw.Length);
                            ctx.Response.OutputStream.Flush();
                        }
                        catch { return; }   // client disconnected
                        lastSent = f;
                        lastSendTs = now;
                        continue;
                    }

                    // ── Wait strategy ────────────────────────────────────────
                    // v8.3.5: NO MORE SPIN. v8.3.3 used Thread.SpinWait at high
                    // fps for sub-ms precision but at fps=1000 (customize
                    // default) that meant a ~1 ms busy-spin between every send
                    // = ~95 % of one core for the SSE thread. User reported
                    // audio_spectrum.exe CPU jumping from 1 % to 8 %. The
                    // precision win wasn't worth the CPU cost — Windows can
                    // only render at monitor refresh anyway.
                    // Now: always use WaitOne (1 ms minimum). At high fps we
                    // accept slightly bursty pacing (Windows scheduler floor
                    // ~1-3 ms even with timeBeginPeriod(1)), but CPU stays
                    // near-zero on the wait path.
                    if (minGapTicks == 0) {
                        mySignal.WaitOne(SSE_INTERVAL_MS);
                    } else {
                        long remainingTicks = minGapTicks - sinceLast;
                        long remainingMs = (remainingTicks * 1000L) / System.Diagnostics.Stopwatch.Frequency;
                        int waitMs = (int)System.Math.Max(1L, System.Math.Min(remainingMs, (long)SSE_INTERVAL_MS));
                        mySignal.WaitOne(waitMs);
                    }
                } }
                finally {
                    lock (s_sseClientEventsLock) { s_sseClientEvents.Remove(mySignal); }
                    try { mySignal.Dispose(); } catch { }
                    System.Threading.Interlocked.Decrement(ref s_activeClients);
                }
            }
            else if (path == "/health")
            {
                byte[] raw = Encoding.UTF8.GetBytes("{\"ok\":true,\"frame\":" + System.Threading.Interlocked.Read(ref s_frame)
                    + ",\"backend\":\"" + JsonEscape(s_currentBackend ?? "wasapi_loopback") + "\""
                    + ",\"device\":\"" + JsonEscape(s_currentDeviceId ?? "") + "\"}");
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(raw, 0, raw.Length);
                ctx.Response.Close();
            }
            else if (path == "/peak")
            {
                // Live peak level of the currently-captured endpoint. Used
                // by the Audio Source dialog to show a VU-meter next to
                // each device — users can see at-a-glance which device is
                // actually receiving audio. Returns both instant (since
                // last /peak call) and session-lifetime peak.
                float rolling = s_peakRollingMax;
                float lifetime = s_peakSampleMax;
                byte[] raw = Encoding.UTF8.GetBytes("{\"rolling\":" + rolling.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"lifetime\":" + lifetime.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"device\":\"" + JsonEscape(s_currentDeviceId ?? "") + "\"}");
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(raw, 0, raw.Length);
                ctx.Response.Close();
            }
            else if (path == "/devices" && ctx.Request.HttpMethod == "GET")
            {
                // Enumerate ALL active audio render endpoints so the tray
                // can show a picker. Users often have a separate "media"
                // output and a "Discord" output; default capture grabs the
                // one marked as system default (often Discord's) which
                // produces the wrong bars.
                HandleDevices(ctx);
            }
            else if (path == "/set-device" && ctx.Request.HttpMethod == "POST")
            {
                // Switch live to a different endpoint. Body: {"id":"..."} or
                // {"id":"default"}. Persists to the Roaming config so the
                // choice survives restarts.
                HandleSetDevice(ctx);
            }
            else if (path == "/set-hop" && ctx.Request.HttpMethod == "POST")
            {
                // v6.6.9: live-tune the FFT hop size ('Response Time' slider
                // in customize.html). Body: {"hop":N} where N is samples,
                // 1..2048. Takes effect on the NEXT capture callback — no
                // restart needed, no FFT state reset.
                HandleSetHop(ctx);
            }
            else if (path == "/set-sensitivity" && ctx.Request.HttpMethod == "POST")
            {
                // v6.9.3: live-tune the magnitude multiplier ('Sensitivity'
                // slider in customize.html). Body: {"sensitivity":N}.
                HandleSetSensitivity(ctx);
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }
        catch (Exception ex)
        {
            Log("HandleRequest ex " + ex.Message);
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    // Bootstrap s_currentBackend + s_currentDeviceId from the persisted
    // Roaming config on startup. Supports old configs (pre-v5.3.0) that
    // only stored audioSpectrumDevice - in that case we default the
    // backend to wasapi_loopback so existing installations keep working
    // exactly as before.
    static void BootstrapFromConfig()
    {
        try
        {
            string cfgPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MastersFM", "config.json");
            if (!File.Exists(cfgPath)) return;
            string text = File.ReadAllText(cfgPath);

            var m1 = System.Text.RegularExpressions.Regex.Match(
                text, "\"audioSpectrumBackend\"\\s*:\\s*\"([^\"]*)\"");
            if (m1.Success && !string.IsNullOrEmpty(m1.Groups[1].Value))
                s_currentBackend = m1.Groups[1].Value;

            var m2 = System.Text.RegularExpressions.Regex.Match(
                text, "\"audioSpectrumDevice\"\\s*:\\s*\"([^\"]*)\"");
            if (m2.Success && !string.IsNullOrEmpty(m2.Groups[1].Value) && m2.Groups[1].Value != "default")
                s_currentDeviceId = m2.Groups[1].Value;

            // v6.6.9: restore the Response Time setting from last session.
            // Stored as "responseMs" under spectrum.{...} in config.json.
            // v6.8.5: clamp loaded value to a SAFE MIN (0.5 ms = HOP=24
            // ≈ 2000 FFTs/sec). Older configs from when the slider min
            // was 0.02 ms (HOP=1, 48000 FFTs/sec) would otherwise pin
            // CPU at 6 %+ on every fresh launch — and Master's FM users
            // who never touched the slider after upgrading wouldn't know
            // why. Logs the clamp so users can see what happened.
            var m3 = System.Text.RegularExpressions.Regex.Match(
                text, "\"responseMs\"\\s*:\\s*([0-9.]+)");
            if (m3.Success)
            {
                double ms = double.Parse(m3.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                const double MIN_SAFE_MS = 0.5;
                if (ms < MIN_SAFE_MS)
                {
                    Log(string.Format("bootstrap: responseMs={0} too low (would pin CPU); clamped to {1} ms", ms, MIN_SAFE_MS));
                    ms = MIN_SAFE_MS;
                }
                int hop = (int)Math.Round(ms * 48.0);
                if (hop < 1) hop = 1;
                if (hop > FFT_SIZE) hop = FFT_SIZE;
                s_hopSize = hop;
            }

            // v6.9.3: restore Sensitivity slider value. Stored as
            // "sensitivity" under spectrum.{...} in config.json. The regex
            // is intentionally non-path-specific (same approach as the
            // responseMs lookup above) — works as long as no other key
            // happens to also be called "sensitivity".
            var m4 = System.Text.RegularExpressions.Regex.Match(
                text, "\"sensitivity\"\\s*:\\s*([0-9.]+)");
            if (m4.Success)
            {
                float val = float.Parse(m4.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (val < 0.1f) val = 0.1f;
                if (val > 20.0f) val = 20.0f;
                s_sensitivity = val;
            }

            Log(string.Format("bootstrap: backend='{0}' deviceId='{1}' hopSize={2} ({3:F3} ms) sensitivity={4:F2}x",
                s_currentBackend, s_currentDeviceId ?? "default", s_hopSize, s_hopSize * 1000.0 / 48000.0, s_sensitivity));
        }
        catch (Exception ex) { Log("bootstrap: " + ex.Message); }
    }

    // .NET Framework's assembly loader is strict about version matching.
    // NAudio.Asio (compiled against netstandard2.0) references specific
    // versions of Microsoft.Win32.Registry / System.Buffers / etc. that
    // don't exactly match what the NuGet packages we shipped contain
    // (e.g. NAudio.Asio wants System.Buffers 4.0.2.0; we shipped 4.0.3.0).
    // Binding redirects in app.config would fix it, but we don't have an
    // app.config and generating one is fiddly. Programmatic AssemblyResolve
    // is simpler: on load-failure for one of our known facades, hand back
    // whatever DLL we ACTUALLY shipped alongside the exe - versions of
    // these assemblies are forward-compatible for the surface area
    // NAudio.Asio uses (registry I/O + ArrayPool + identity).
    static readonly string[] s_bundledFacades = new[] {
        "Microsoft.Win32.Registry",
        "System.Buffers",
        "System.Memory",
        "System.Numerics.Vectors",
        "System.Runtime.CompilerServices.Unsafe",
        "System.Security.AccessControl",
        "System.Security.Principal.Windows",
        "NAudio.Core",
        "NAudio.Wasapi",
        "NAudio.WinMM",
        "NAudio.Asio"
    };

    static System.Reflection.Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
    {
        try
        {
            var shortName = new System.Reflection.AssemblyName(args.Name).Name;
            foreach (var facade in s_bundledFacades)
            {
                if (string.Equals(shortName, facade, StringComparison.OrdinalIgnoreCase))
                {
                    // Look next to the exe first (install folder), then
                    // the current working directory (dev scenario).
                    string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    string[] searchDirs = exeDir != null ? new[] { exeDir, Environment.CurrentDirectory } : new[] { Environment.CurrentDirectory };
                    foreach (var d in searchDirs)
                    {
                        string candidate = Path.Combine(d, shortName + ".dll");
                        if (File.Exists(candidate))
                        {
                            Log("AssemblyResolve: " + args.Name + " -> " + candidate);
                            return System.Reflection.Assembly.LoadFrom(candidate);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log("AssemblyResolve: " + ex.Message);
        }
        return null;
    }

    // v6.5.2: request 1 ms system timer resolution. Windows default timer
    // is 15.625 ms (64 Hz), which means Thread.Sleep(2) actually sleeps
    // ~15 ms and our 2 ms SSE cadence was degrading to ~60 Hz in practice.
    // timeBeginPeriod(1) bumps the scheduler quantum to 1 ms so sub-frame
    // sleeps behave like their nominal values.
    [DllImport("winmm.dll", SetLastError = true)]
    static extern uint timeBeginPeriod(uint period);

    [STAThread]
    static int Main(string[] args)
    {
        InitLog();
        try { timeBeginPeriod(1); Log("timeBeginPeriod(1) applied (1 ms scheduler resolution)"); }
        catch (Exception ex) { Log("timeBeginPeriod failed: " + ex.Message); }
        Log("=== audio_spectrum v7.0.0 starting (multi-backend: WASAPI / WDM-KS / MME / ASIO) ===");
        // Install the AssemblyResolve handler BEFORE anything else touches
        // NAudio.Asio types - otherwise the loader will short-circuit on
        // first use with a FileNotFoundException that the handler never
        // sees. AppDomain.AssemblyResolve only fires after normal probing
        // fails, which for version-mismatched assemblies happens pre-TypeInit.
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        try
        {
            BootstrapFromConfig();
            // v8.3.6: precompute hot-path lookup tables before audio capture
            // starts firing OnData. Cheap (~1 ms total): 11 trig calls for the
            // FFT twiddle table + 480 × 256 = 122 880 Math.Pow calls for the
            // gamma LUT (the cost we're amortizing — done ONCE here instead of
            // per-FFT × per-band on the audio thread = 960 000 Pow/sec saved).
            EnsureFftTwiddleTable();
            EnsureBandGammaLut();
            EnsureRfftTwiddleTable();
            Log("precompute: FFT twiddle table + per-band gamma LUT + RFFT twiddle table ready");
            // v9.0.0: validate RFFT against CFFT with a synthetic 440 Hz sine.
            // Sets s_rfftReady to true if the spectra match within 5 % per bin;
            // false → DoFftAndPublish falls back to the legacy CFFT path.
            RfftSelfTest();
            StartCapture();
            StartHttp();
            // Block forever - the capture thread + http listener do the work.
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        catch (Exception ex)
        {
            Log("FATAL: " + ex);
            return 1;
        }
    }
}

// ===========================================================================
//  AsioCaptureAdapter
// ===========================================================================
// NAudio's AsioOut is the record-and/or-playback wrapper for ASIO drivers.
// It does NOT implement IWaveIn - its API is AudioAvailable-based. This
// adapter wraps AsioOut behind the same IWaveIn surface that WasapiLoopbackCapture,
// WasapiCapture and WaveInEvent expose, so the capture thread in audio_spectrum
// doesn't have to special-case ASIO.
//
// Threading: ASIO drivers fire AudioAvailable on their own thread. We
// convert the float-per-channel ASIO buffers into an interleaved byte
// buffer (32-bit float, stereo) and re-fire as a standard WaveInEventArgs.
// DataAvailable subscribers are invoked synchronously on that ASIO thread -
// same contract WaveInEvent / WasapiLoopbackCapture provide.
public class AsioCaptureAdapter : NAudio.Wave.IWaveIn
{
    // Driver name is kept so we can fully recreate AsioOut after a failed
    // init attempt. NAudio's AsioOut cannot be re-init'd after a failure
    // ("Already initialised this instance of AsioOut - dispose and create
    // a new one"), so the retry path must dispose + reconstruct.
    readonly string _driverName;
    readonly int    _inputChannelOffset;   // 0 = capture ch 1-2, 2 = ch 3-4, etc.
    NAudio.Wave.AsioOut _asio;
    readonly int     _channels;       // how many ASIO input channels we capture
    int              _sampleRate;     // mutable - StartRecording may pick a different rate if 48k/44.1 fail
    byte[]           _interleavedBuf; // reused each buffer update to avoid GC

    public NAudio.Wave.WaveFormat WaveFormat { get; set; }
    public event EventHandler<NAudio.Wave.WaveInEventArgs>  DataAvailable;
    public event EventHandler<NAudio.Wave.StoppedEventArgs> RecordingStopped;

    public AsioCaptureAdapter(string driverName, int inputChannelOffset = 0)
    {
        _driverName         = driverName;
        _inputChannelOffset = inputChannelOffset;
        _asio       = new NAudio.Wave.AsioOut(driverName);
        // Seed AudioSpectrum's channel-count cache with the real count so
        // the dialog stops showing "1 pair" for whichever driver we're
        // currently running on (that one can't be probed via a second
        // AsioOut construction - this is our one chance to record it).
        try { AudioSpectrum.RecordAsioChannelCount(driverName, _asio.DriverInputChannelCount); } catch { }
        // Cap at 2 channels - we mix to mono anyway for the FFT, no
        // point pulling 8+ channels from a pro audio interface.
        _channels   = System.Math.Min(2, System.Math.Max(1, _asio.DriverInputChannelCount - _inputChannelOffset));
        if (_channels < 1) _channels = 1;
        // Preferred rate - actual rate chosen in StartRecording via the
        // multi-rate retry loop (IsSampleRateSupported often lies, some
        // VB-Matrix variants claim 48 kHz is supported then reject
        // SetSampleRate with ASE_NoClock because the driver instance is
        // hard-locked to its current rate).
        _sampleRate = 48000;
        WaveFormat  = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _channels);
        _asio.InputChannelOffset = _inputChannelOffset;
        _asio.AudioAvailable += OnAsioAudio;
    }

    public void StartRecording()
    {
        // VB-Matrix VASIO-N drivers and some pro interfaces refuse
        // SetSampleRate on the rate their IsSampleRateSupported just
        // claimed. NAudio's AsioOut can't be re-Init'd after a failure
        // ("Already initialised this instance of AsioOut") so every
        // retry fully recreates the AsioOut. Iterates a wide range of
        // rates; the first one that BOTH passes IsSampleRateSupported
        // AND actually Init+Play's wins.
        int[] rates = new[] { 48000, 44100, 96000, 88200, 192000, 32000, 22050, 16000 };
        System.Exception lastEx = null;
        foreach (int r in rates)
        {
            try
            {
                if (!_asio.IsSampleRateSupported(r)) continue;
                _sampleRate = r;
                WaveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _channels);
                _asio.InitRecordAndPlayback(null, _channels, _sampleRate);
                _asio.Play();
                // Success - log the rate we landed on so the user / diag
                // stream can see which one the driver actually accepted.
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(
                            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                            "MastersFM", "audio_spectrum.log"),
                        string.Format("[{0:HH:mm:ss.fff}] ASIO '{1}' opened at {2} Hz, {3} ch{4}",
                            System.DateTime.Now, _driverName, _sampleRate, _channels, System.Environment.NewLine));
                }
                catch { }
                return;
            }
            catch (System.Exception ex)
            {
                lastEx = ex;
                // AsioOut is now in an unusable "already initialised"
                // state after a failed SetSampleRate. Dispose + recreate
                // so the next iteration gets a clean driver handle.
                try { _asio.AudioAvailable -= OnAsioAudio; } catch { }
                try { _asio.Dispose(); } catch { }
                try
                {
                    _asio = new NAudio.Wave.AsioOut(_driverName);
                    _asio.InputChannelOffset = _inputChannelOffset;
                    _asio.AudioAvailable += OnAsioAudio;
                }
                catch (System.Exception reinitEx)
                {
                    // If we can't even re-open the driver, propagate to
                    // the outer retry/fallback logic so we end up on
                    // WASAPI Loopback instead of spinning.
                    lastEx = reinitEx;
                    break;
                }
            }
        }

        // Every rate failed. Fire the stopped event and let the outer
        // capture thread drive the user to WASAPI Loopback.
        var h = RecordingStopped;
        var err = lastEx ?? new System.Exception("ASIO: no supported sample rate for driver '" + _driverName + "'");
        if (h != null) h(this, new NAudio.Wave.StoppedEventArgs(err));
        throw err;
    }

    public void StopRecording()
    {
        try { _asio.Stop(); } catch { }
        var h = RecordingStopped;
        if (h != null) h(this, new NAudio.Wave.StoppedEventArgs());
    }

    void OnAsioAudio(object sender, NAudio.Wave.AsioAudioAvailableEventArgs e)
    {
        try
        {
            int totalSamples = e.SamplesPerBuffer * _channels;
            int byteCount    = totalSamples * sizeof(float);
            if (_interleavedBuf == null || _interleavedBuf.Length < byteCount)
                _interleavedBuf = new byte[byteCount];

            // GetAsInterleavedSamples writes Channel0_S0, Channel1_S0,
            // Channel0_S1, ... into a managed float[]. We then BlockCopy
            // into our reusable byte buffer so DataAvailable fires with
            // the exact same layout WasapiLoopbackCapture produces
            // (interleaved 32-bit float PCM).
            var tmp = new float[totalSamples];
            e.GetAsInterleavedSamples(tmp);
            System.Buffer.BlockCopy(tmp, 0, _interleavedBuf, 0, byteCount);

            var h = DataAvailable;
            if (h != null) h(this, new NAudio.Wave.WaveInEventArgs(_interleavedBuf, byteCount));
        }
        catch (System.Exception ex)
        {
            // Don't throw through the ASIO callback - that can crash the
            // driver. Log and drop this buffer.
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(
                        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                        "MastersFM", "audio_spectrum.log"),
                    string.Format("[{0:HH:mm:ss.fff}] AsioCaptureAdapter: {1}{2}",
                        System.DateTime.Now, ex.Message, System.Environment.NewLine));
            }
            catch { }
        }
    }

    public void Dispose()
    {
        try { _asio.AudioAvailable -= OnAsioAudio; } catch { }
        try { _asio.Dispose(); } catch { }
    }
}

// ===========================================================================
//  WdmKsCaptureAdapter
// ===========================================================================
// Wraps WasapiCapture with an exclusive-first, shared-fallback policy so
// "WDM-KS / Exclusive" in the UI works even on virtual endpoints that don't
// support exclusive mode (VB-Matrix Media, Voicemeeter Out B*, VB-Cable
// Output, etc.). Previously those failed 3x then fell through to WASAPI
// Loopback, silently capturing the wrong device. Now the user's device
// choice sticks - just in shared mode if the driver refuses exclusive.
//
// The inner WasapiCapture can be Exclusive or Shared. Both implement
// IWaveIn with the same DataAvailable / RecordingStopped events. The
// adapter proxies events straight through to its own handlers so the
// outer capture thread doesn't need to know which mode it landed on.
public class WdmKsCaptureAdapter : NAudio.Wave.IWaveIn
{
    readonly NAudio.CoreAudioApi.MMDevice _device;
    NAudio.CoreAudioApi.WasapiCapture     _inner;

    public NAudio.Wave.WaveFormat WaveFormat { get { return _inner != null ? _inner.WaveFormat : null; } set { /* NAudio sets this internally */ } }
    public event EventHandler<NAudio.Wave.WaveInEventArgs>  DataAvailable;
    public event EventHandler<NAudio.Wave.StoppedEventArgs> RecordingStopped;

    public WdmKsCaptureAdapter(NAudio.CoreAudioApi.MMDevice device)
    {
        _device = device;
        // v8.2.3 — pre-create the inner WasapiCapture in the constructor so
        // WaveFormat is valid as soon as the adapter exists. NAudio's
        // WasapiCapture sets WaveFormat from the device's MixFormat in its
        // own constructor, so this also implicitly chooses the share-mode
        // wave format. StartRecording will keep this _inner if exclusive
        // succeeds, or dispose+rebuild as Shared if it doesn't. This makes
        // the adapter match the contract of the other backends
        // (WasapiCapture / WaveInEvent / AsioOut all populate WaveFormat
        // in their constructor) so the outer capture loop's
        // `var wf = capture.WaveFormat;` doesn't see null.
        _inner = MakeInner(NAudio.CoreAudioApi.AudioClientShareMode.Exclusive);
    }

    // Create a fresh WasapiCapture in the requested share mode, subscribe
    // our proxy handlers, and return it. Separate from StartRecording so
    // the exclusive-first / shared-fallback retry can dispose + recreate
    // cleanly between attempts.
    NAudio.CoreAudioApi.WasapiCapture MakeInner(NAudio.CoreAudioApi.AudioClientShareMode mode)
    {
        var cap = new NAudio.CoreAudioApi.WasapiCapture(_device, true, 20);
        cap.ShareMode = mode;
        cap.DataAvailable += (s, a) => {
            var h = DataAvailable;
            if (h != null) h(this, a);
        };
        cap.RecordingStopped += (s, a) => {
            var h = RecordingStopped;
            if (h != null) h(this, a);
        };
        return cap;
    }

    public void StartRecording()
    {
        string logPath = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "MastersFM", "audio_spectrum.log");

        // First attempt: Exclusive (using the inner pre-created in the
        // constructor). Driver may support it or reject with AUDCLNT_E_*.
        try
        {
            _inner.StartRecording();
            try
            {
                System.IO.File.AppendAllText(logPath,
                    string.Format("[{0:HH:mm:ss.fff}] WDM-KS: opened '{1}' in EXCLUSIVE mode{2}",
                        System.DateTime.Now, _device.FriendlyName, System.Environment.NewLine));
            }
            catch { }
            return;
        }
        catch (System.Exception ex)
        {
            // Exclusive failed. Log the error, dispose, try Shared.
            try
            {
                System.IO.File.AppendAllText(logPath,
                    string.Format("[{0:HH:mm:ss.fff}] WDM-KS: exclusive open on '{1}' failed ({2}) - falling back to SHARED mode{3}",
                        System.DateTime.Now, _device.FriendlyName, ex.Message, System.Environment.NewLine));
            }
            catch { }
            try { _inner.Dispose(); } catch { }
        }

        // Second attempt: Shared mode. This works on virtually every
        // WASAPI-enumerable capture endpoint, including virtual drivers.
        // If this also fails we propagate so the outer capture thread
        // falls back to WASAPI Loopback.
        _inner = MakeInner(NAudio.CoreAudioApi.AudioClientShareMode.Shared);
        try
        {
            _inner.StartRecording();
            try
            {
                System.IO.File.AppendAllText(logPath,
                    string.Format("[{0:HH:mm:ss.fff}] WDM-KS: opened '{1}' in SHARED mode (exclusive not supported){2}",
                        System.DateTime.Now, _device.FriendlyName, System.Environment.NewLine));
            }
            catch { }
            return;
        }
        catch (System.Exception ex2)
        {
            var h = RecordingStopped;
            if (h != null) h(this, new NAudio.Wave.StoppedEventArgs(ex2));
            throw;
        }
    }

    public void StopRecording()
    {
        if (_inner != null)
        {
            try { _inner.StopRecording(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_inner != null)
        {
            try { _inner.Dispose(); } catch { }
            _inner = null;
        }
    }
}
