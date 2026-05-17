# Research #3 ADDENDUM — Deeper pass (operator-requested second look)

> Append-only addendum to `research_3_visualiser_latency.md`. The first pass was correct but didn't go deep enough into the FFT-publish pipeline and the smoothing/render side. Re-reading the code I found seven more findings. The first one is **actually bigger than Finding B** in terms of perceived attack snap.

---

## Finding G — Server-side `ENV_ATTACK = 0.85` is a hidden rise-smoother (~1-3 ms perceived attack lag)

Looking at `audio_spectrum.cs:1638` and the band-envelope loop at lines 2066-2080:

```csharp
const float ENV_ATTACK = 0.85f;
const float ENV_DECAY  = 0.28f;
...
float prev = s_env[b];
float next = (target > prev)
    ? prev + (target - prev) * ENV_ATTACK    // rise: 85% of the way per FFT
    : prev + (target - prev) * envDecay_b;   // fall: 28-45% per FFT
s_env[b] = next;
```

This is a per-FFT IIR smoother. With ENV_ATTACK = 0.85 at our **1-ms FFT cadence** (Phase P):

| FFT tick after transient hits Hann peak | s_env value (% of true peak) |
|---|---|
| 0 (kick FFT) | 85% |
| +1 ms | 97.75% |
| +2 ms | 99.66% |
| +3 ms | 99.95% |

So a kick that genuinely peaks the FFT at 100% magnitude only **shows as 85% on the first published frame**. Subsequent FFTs push it asymptotically over ~3-4 ms.

**The kicker:** the client (`overlay.html:3438`) has rise-INSTANT logic — `(tgt >= cur) ? tgt : ...`. It will follow the server's 85% → 97% → 99% sequence faithfully, never compensating. The client smoothing is fine; the server smoothing is the bottleneck.

**Worse:** This 0.85 constant was tuned for the OLD HOP_SIZE=512 (~10.7 ms) cadence where 0.85 attack meant ~57% in 10 ms — visually fine because the OS could only render at 60Hz anyway. At our **current 1-ms FFT cadence the same constant becomes near-useless** — it just lops 15% off the first peak frame, then converges so fast it doesn't even do meaningful smoothing.

**Fix:**
```csharp
const float ENV_ATTACK = 1.0f;   // truly instant rise
```
The client's `(tgt >= cur)` branch handles any "snap" visual. The server smoother is dead weight for rise post-Phase P.

The fall path stays at 0.28 (or 1.6x for bass) — that one still does meaningful work because the client only does exponential decay on fall.

- **LOC:** 1 line change
- **Saving:** ~1-3 ms perceived attack lag, on **every transient, every band**
- **Risk:** none — bars hit their target value 1 frame faster
- **Why this might be more impactful than A1 (FFT_SIZE shrink):** A1 affects only the Hann-window window time. G affects the **bar rise time once the FFT delivers it**. Together they compound.

---

## Finding H — ASIO sample-rate preference order is backwards for latency (~10 ms on supported drivers)

`audio_spectrum.cs:3293` lists ASIO rate fallback as:
```csharp
int[] rates = new[] { 48000, 44100, 96000, 88200, 192000, 32000, 22050, 16000 };
```

Current logic picks **48 kHz first** if supported. But higher sample rates **shrink the Hann window's wall-clock latency** without changing FFT_SIZE:

| Sample rate | FFT_SIZE=2048 Hann center | Bin spacing |
|---|---|---|
| 44.1 kHz | 23.2 ms | 21.5 Hz |
| **48 kHz (today)** | **21.3 ms** | **23.4 Hz** |
| 96 kHz | **10.7 ms** | 46.9 Hz |
| 192 kHz | 5.3 ms | 93.75 Hz |

96 kHz halves the Hann latency. Bin-spacing trade-off is identical to halving FFT_SIZE, but **CPU per-FFT is unchanged** (still a 2048-point RFFT).

Cost: more sample-decode work (96k vs 48k samples/sec). The BlockCopy bulk-decode path scales linearly and isn't CPU-bound today.

**Fix:**
```csharp
int[] rates = new[] { 96000, 88200, 48000, 44100, 192000, 32000, 22050, 16000 };
```
Or more aggressively `{ 96000, 192000, 88200, 48000, ... }` for max latency reduction.

- **LOC:** 1 line
- **Saving:** ~10 ms on ASIO when 96 kHz is supported
- **Risk:** bin spacing doubles → mild sub-bass blur (same as FFT_SIZE shrink)
- **Scope:** **ASIO only.** WASAPI Loopback/Input/Exclusive are locked to device mix format (rebuilds would be needed and shared-mode loopback doesn't allow it anyway).
- **Driver support:** RME/Focusrite/UA/MOTU all support 96k natively. ASIO4ALL inherits underlying WDM caps. Voicemeeter/VB-Matrix usually expose 96k as a configurable internal rate. Worst case: driver refuses 96, falls through to 48 → same as today.

---

## Finding I — Per-band attack tuning (alternative to G)

Today's code already varies DECAY per band (line 2069-2073, bass gets 1.6x faster). Attack is uniform at 0.85. Combined with Finding G's "rise should be instant":

```csharp
const float ATTACK_BASS   = 0.85f;   // some smoothing on sustained bass
const float ATTACK_TREBLE = 1.00f;   // instant for transients
for (int b = 0; b < BAND_COUNT; b++) {
    float t = (b < LOW_RAMP_END) ? (float)b / LOW_RAMP_END : 1.0f;
    float attackB = ATTACK_BASS + (ATTACK_TREBLE - ATTACK_BASS) * t;
    float prev = s_env[b];
    float next = (target > prev)
        ? prev + (target - prev) * attackB
        : prev + (target - prev) * envDecay_b;
    s_env[b] = next;
}
```

**Rationale:** Bass content (kicks, sub) is mostly sustained — a 1-3 ms rise smoother prevents micro-pumping on near-constant bass. Treble (hi-hats, sibilants) is the OPPOSITE — fast transient where any smoothing is perceptually visible.

- **LOC:** ~10 LOC
- **Saving:** ~1-2 ms perceived attack on treble; bass stability preserved
- **Verdict:** alternative to G. Pick ONE — either G (simple, uniform 1.0) or I (split). If unsure, ship G first; if bass starts looking jittery on hardcore basslines, switch to I.

---

## Finding J — MMCSS-attach the SSE serving thread (for tail-latency under load)

Phase R attached the **capture** thread to MMCSS "Pro Audio." But the SSE thread serving `/spectrum` (the one inside `HandleSpectrumSse`-equivalent at line ~2727) is a `HttpListener` worker running at default priority.

Under heavy CPU load (operator gaming, encoding video, OBS x264 ultrafast on a busy machine), the SSE thread can be preempted up to ~16 ms. The capture thread keeps publishing into `s_latest`, but the SSE thread might not WAKE to send them for ~16 ms.

This is a tail-latency outlier — invisible during light idle use, very visible when the operator's streaming with a CPU-heavy encoder.

**Fix:** In the SSE handler entry (around line 2727), call `AvSetMmThreadCharacteristicsW("Pro Audio", ...)` on entry, revert on exit. Same pattern as the capture thread in Phase R.

```csharp
if (path == "/spectrum")
{
    uint sseTaskIdx = 0;
    System.IntPtr sseMmcss = AvSetMmThreadCharacteristicsW("Pro Audio", ref sseTaskIdx);
    try
    {
        // ... existing SSE handler body ...
    }
    finally
    {
        if (sseMmcss != System.IntPtr.Zero) AvRevertMmThreadCharacteristics(sseMmcss);
    }
}
```

The DllImports are already declared at class scope in Phase R — we can reuse them.

- **LOC:** ~10 lines
- **Saving:** 5-15 ms tail-latency under load (no change under light load — when nothing's contending, default priority is fine)
- **Risk:** none — Windows allows multiple Pro Audio threads
- **Why it matters more than it looks:** operator's primary use case is streaming. When they're streaming, the encoder is the heaviest CPU consumer on the box. This finding specifically protects the visualizer thread from the encoder.

---

## Finding K — `requestAnimationFrame` cap at 120 fps starves 144/240 Hz displays

`overlay.html:3369`:
```js
const fpsCap = Math.max(30, Math.min(120, _cfg.spectrum?.fps ?? 120));
```

On 144 Hz: rAF fires every 6.94 ms. The cap → effectively skips every other tick = ~72 fps actual. On 240 Hz: rAF every 4.17 ms; cap → ~60 fps actual.

Original purpose of the cap: limit CPU on slow machines. But:
1. We're using WebGL (fixed per-frame cost — texSubImage2D + 6-vertex drawArrays — < 0.5 ms)
2. The render loop has zero per-frame heap allocation
3. rAF caps itself at monitor refresh anyway → no runaway risk

**Fix:**
```js
const fpsCap = Math.max(30, Math.min(240, _cfg.spectrum?.fps ?? 240));
```

- **LOC:** 1 line
- **Saving:** ~2-4 ms (display latency) on 144 Hz monitors, ~7 ms on 240 Hz
- **Risk:** slight CPU uptick on high-refresh displays (operator can dial back via existing slider)
- **Scope:** standalone overlay window on operator's own monitor. OBS browser source is capped by OBS's setting anyway, so no effect there.

---

## Finding L — Client `fallHalfGL` floor of 15 ms (`overlay.html:3430`)

```js
const fallHalfGL = 15 + smoothGL * 335;
```

Even at smoothing=0 (slider min), fall has 15 ms half-life. From peak to silence takes ~75 ms (5 half-lives to ~3% of peak). On rapid drum content (hardcore at 200 BPM, ~150 ms between kicks), bars never fully empty between hits — the "afterglow" of one kick is still bleeding into the next.

This isn't an attack-latency issue (rise = instant), but a **freshness** one: a fast drum pattern's individual hits visually blur together.

**Fix options:**
1. Lower the floor to 5 ms (mild twitch visible on sustained content; cleaner drums)
2. Lower to 0 ms (true snap; might look strobey on slow synth pads)
3. Toggle: "Punch mode" slider position that goes 0→15ms while regular mode is 15→350ms

**Recommended:** Lower floor to 5 ms; expose 0-15 ms range explicitly if operator wants the snap. Compatible with existing smoothing slider semantics.

- **LOC:** 1 character (`15` → `5`) + maybe a slider remap
- **Saving:** Perceptual freshness on drum-heavy content (not a strict ms metric from source)
- **Risk:** Slight visual jitter on slow sustained content

---

## Finding M — Wasted FFT compute inside one OnData burst (CPU only, not latency)

`audio_spectrum.cs:1152-1234`: when OnData arrives with N samples and `FFT_MIN_STRIDE=48`, multiple FFTs fire back-to-back inside the same OnData call. WASAPI loopback's ~10 ms engine period → ~480 samples per OnData → ~10 FFTs per OnData, all computed at the same wall-clock instant.

The SSE client thread (fps=2000 mode, overlay's default since v9.8.1) only sends on `s_frame` change. During the burst:
- FFT #1 fires → s_frame++, signal Set
- SSE thread wakes, reads s_latest at that moment (FFT #1's bands), encodes, writes
- FFTs #2-10 fire while SSE thread is mid-write → s_latest overwritten 9 times → 9 redundant signals
- SSE thread completes write, sees s_frame changed, loops, reads s_latest (= FFT #10), encodes, writes

Effectively **2 frames per OnData burst are delivered** (the first and last). The middle 8 are computed-then-overwritten.

**Latency implication:** ZERO. The visualizer sees FFT #10 (the freshest) which is what we want.

**CPU implication:** ~8× wasted FFT work per OnData burst. Each FFT is ~50-150 µs → ~400-1200 µs wasted per OnData → at ~100 OnData/sec = ~40-120 ms wasted CPU/sec = **~4-12% of one core**.

**Fix option:** Inside the OnData loop, only call `DoFftAndPublish` for the LAST FFT in the burst. Bookkeeping (s_writePos, s_samplesSinceFft, peak counters) still needs to run for all FFT trigger boundaries to keep the circular buffer position correct.

```csharp
// Pseudocode for the change at line ~1152
samplesSinceFft++;
if (samplesSinceFft >= s_fftStride)
{
    samplesSinceFft = 0;
    // ... peak rolling logic unchanged ...
    if (s_activeClients > 0)
    {
        // Phase S Finding M: defer the FFT publish to the last trigger in this OnData burst.
        // Compute remaining frames; only publish if this is the LAST trigger before OnData ends.
        int framesRemaining = frameCount - f - 1;
        bool isLastFftInBurst = framesRemaining < s_fftStride;
        if (isLastFftInBurst)
        {
            s_writePos       = writePos;
            s_peakSampleMax  = peakSample;
            s_peakRollingMax = peakRolling;
            // RMS gate + DoFftAndPublish as before
        }
        // else: still mark the trigger boundary, but skip the FFT compute
        // (the next burst's first FFT will pick up where we left off)
    }
}
```

- **LOC:** ~15 LOC restructure
- **Saving:** 4-12% CPU on music content
- **Latency:** ZERO change (still publishes the freshest FFT)
- **Risk:** subtle — need to confirm SSE behavior across rapid SSE-rate changes (e.g., when customize preview opens a second client at fps=60 while overlay is at fps=2000). Test path: open overlay, leave running, then open customize → verify both clients still receive correct frames.
- **Why ship anyway:** ~10% CPU freed up means more headroom for higher FFT cadence (e.g., FFT_MIN_STRIDE 48 → 24 = 0.5 ms cadence) at the same total CPU cost, OR just lower idle/active CPU. Operator's call.

---

## Updated combined latency table — Phase R + every Finding

| Stage | Phase R only | + G (just attack=1) | + G+C+H+B+K | + above + A1 | + above + A3 (multi-res) |
|---|---|---|---|---|---|
| 1. Backend (loopback) | 7-10 ms | 7-10 | 7-10 | 7-10 | 7-10 |
| 2. Sample accumulation | 1 ms | 1 | 1 | 1 | 1 |
| 3. Hann center | 21 ms | 21 | 21 | 10 | 3 (treble) / 10 (bass) |
| 3.5 Server attack haircut | **2 ms** | **0** | **0** | **0** | **0** |
| 4-8. FFT + bands + SSE | 2 ms | 2 | 2 | 2 | 2 |
| 9. SSE write coalesce (C) | 1.5 ms | 1.5 | 0.5 | 0.5 | 0.5 |
| 10. rAF wait (60Hz) | 8 ms | 8 | 8 | 8 | 8 |
| 10b. rAF cap relief (K) | (n/a on 60Hz) | — | — | — | — |
| 11. JS decode (B) | 8 ms | 8 | 0 | 0 | 0 |
| 12. WebGL composite + monitor | 17 ms | 17 | 17 | 17 | 17 |
| **TOTAL @ 60 Hz / loopback** | **~57 ms** | **~55 ms** | **~46 ms** | **~35 ms** | **~28 ms** (treble) |
| **TOTAL @ 144 Hz / ASIO 96k** | **~33 ms** | **~31 ms** | **~16 ms** | n/a (similar) | **~11 ms** (treble) |
| **TOTAL @ 240 Hz / ASIO 96k** | **~28 ms** | **~26 ms** | **~12 ms** | n/a | **~9 ms** (treble) |

The "treble" entries reflect multi-res pyramid catching high-freq transients via the short FFT (256 → 2.7 ms Hann). Bass content stays at the long FFT.

---

## Final ranking — smallest effort, biggest perceptual return on top

| # | Finding | LOC | Saving | Risk | Notes |
|---|---|---|---|---|---|
| 1 | **G** — `ENV_ATTACK = 1.0f` | **1 line** | ~2 ms on every transient, every band | none | best ratio in the entire research |
| 2 | **C** — `Response.BufferOutput = false` | 1 line | ~1 ms | none | trivial |
| 3 | **B** — Eager SSE decode w/ 5ms throttle | ~15 LOC JS | ~4-8 ms | none | medium effort, real gain |
| 4 | **H** — Reorder ASIO rate try (96k first) | 1 line | ~10 ms on ASIO | mild bin-spacing | huge for ASIO users |
| 5 | **K** — rAF cap 120 → 240 | 1 line | 2-7 ms on 144/240 Hz | minimal | trivial |
| 6 | **J** — MMCSS-attach SSE thread | ~10 LOC | 5-15 ms tail under load | none | matters under streaming load |
| 7 | **L** — `fallHalfGL` floor 15 → 5 | 1 char | freshness on drums | mild jitter | optional |
| 8 | **I** — Per-band attack split | ~10 LOC | ~1-2 ms (treble) | none | alternative to G |
| 9 | **M** — Skip burst-redundant FFTs | ~15 LOC | 4-12% CPU; 0 latency | needs testing | CPU not latency |
| 10 | **A1** — FFT_SIZE 2048 → 1024 | ~5 LOC + retune | ~10 ms (all bands) | bass blur | best non-rebuild gain on Hann |
| 11 | **A3** — Multi-resolution FFT pyramid | ~80 LOC | ~17 ms (treble) | none if done right | best perceptual outcome |

---

## Recommended bundles

### Phase S "free wins" — zero risk, ~20 ms saved
**G + C + B + K (+ H if ASIO user).** All ~1 dev hour total. Single-line or near-single-line changes. Ships behind no toggles, no fallbacks needed.

Expected outcome on 60Hz / WASAPI Loopback: **57 → ~37 ms** end-to-end. On 144Hz / ASIO 96k: **33 → ~13 ms**. Below perceptual threshold for stream viewers; meaningful improvement for musicians.

### Phase S "looks great" — add A1, retune REF_MAG if needed
G + C + B + K + H + A1. Drops FFT_SIZE to 1024. **~12 more ms saved** but needs visual A/B against bass-heavy content. If sub-bass detail looks too coarse, stay on A1 OR ship A3 instead.

### Phase S "best perceptual outcome" — ship A3
G + C + B + K + H + A3. Multi-resolution FFT pyramid. **Treble feels instant** (Hann latency ~3 ms for high-freq content); bass stays at 10-21 ms which is already imperceptible for sustained content. ~80 LOC investment.

### Phase S "load-resilient streamer config" — add J
Bundle + J. Protects against encoder-induced tail latency. Critical for the operator's primary use case (live streaming). Not visible under light use; saves 5-15 ms under heavy CPU load.

---

## Things I confirmed are NOT latency contributors (skip these)

- **WebGL render path** — `texSubImage2D` + 6-vertex drawArrays. Sub-ms. Already optimized.
- **Base64 encode/decode** — ~100 µs each side. Cached on server for dups.
- **Spatial sharpening** (`s_targetsSharp`) — spatial-only operation, no temporal lag.
- **Compressor knee** (REF_MAG) — instantaneous mapping.
- **Bass transient expander** — 270 ms baseline tracker for behavior shaping, not latency.
- **HTTP listener thread pool** — instance created once at startup.
- **AutoResetEvent wake-up** — sub-ms with timeBeginPeriod(1) active.
- **Sample-format conversion** — bulk BlockCopy decode (v8.3.8), not per-sample BitConverter.
- **`_normPeak` autogain** — affects amplitude scale, not bar timing.
- **Cooley-Tukey FFT compute** — ~50-150 µs at 2048-point. Trivial.

These all looked suspicious on a first read but turned out to be either already-instantaneous or only-amplitude-affecting.

---

## What still constrains us after every finding shipped

- **Loopback shared-mode engine period (~7-10 ms)** — OS-bound. Microsoft won't allow IAudioClient3 with LOOPBACK. Only escapes: ASIO/exclusive, or a kernel-mode bypass we won't ship.
- **Monitor refresh (~8-17 ms on 60 Hz)** — display hardware. Operator buying a 144/240 Hz monitor moves this needle.
- **OBS browser-source rAF** — capped by OBS config. We document `60` as the recommended setting; the user has to apply it.
- **Stream/encoder buffering** — way past audio_spectrum. Not in scope.

---

## Status

- Addendum delivered. **No code changes applied.**
- Phase R already shipped (v7.1.0).
- Awaiting operator approval per finding.
- Recommended ship order: **G → C → B → H → K**, then evaluate visuals, then decide on A1 vs A3.
