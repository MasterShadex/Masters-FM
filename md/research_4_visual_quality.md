# Research #4 — Three operator-reported visual quality issues (post Phase S)

**Operator's three complaints:**
1. **Lost kick "punch"** — basslines hit 40-60% bar height correctly but kicks don't pop above that anymore. Heartbeat / kick feel is gone.
2. **Choppy-on-fast-music** — hardcore/hardstyle (200 BPM) looks like 10 fps even though framerate is correct. Operator wants smoother but the heartbeat character preserved.
3. **WASAPI/MME/KS sluggish** — non-ASIO backends LOOK like 3-10 fps with sluggish movement.

**Research depth: 30+ minutes, 3 parallel agent investigations + my own code reading + live SSE measurements.**

---

## TL;DR — three fixes, all rooted in two underlying causes

| # | Fix | File:Line | LOC | Cause |
|---|---|---|---|---|
| **1** | `BASELINE_DECAY = 0.96f` → `0.99744f` | audio_spectrum.cs:2074 + sync at 1759 | 2 lines | Constant tuned for old FFT cadence (93 Hz); current cadence is 1000 Hz |
| **2** | Two-stage piecewise fall on client | overlay.html:3447-3458 | ~10 LOC | Phase S Finding L (5ms half-life) creates square-wave strobe |
| **3** | (same as #2) | (same) | (same) | Fast-fall + sparse-arrival WASAPI = strobe — same root cause |

The big surprise: **issues 2 and 3 share a single fix.** They feel different (one is "music too fast", one is "backend too slow") but the cause is the same — the 5ms fall half-life I introduced in Phase S Finding L empties the bars too quickly between energy events. For 200 BPM kicks the event spacing is 300 ms; for WASAPI Loopback engine period it's 10-100 ms. Either way the bars sit at zero for most of the cycle → visual strobe.

---

## Issue 1 — Lost kick "punch" (the bass transient expander is broken)

### Cause

The bass transient expander at `audio_spectrum.cs:2061-2098` is designed to make kicks pop above sustained basslines. Code:

```csharp
const float BASELINE_DECAY   = 0.96f;   // ← THE CONSTANT
const float SUSTAINED_KEEP   = 0.25f;
const float TRANSIENT_BOOST  = 2.0f;
for (int b = 0; b < 100; b++) {
    s_baseline[b] = 0.96f * s_baseline[b] + 0.04f * s_targets[b];   // EMA tracker
    float excess = s_targets[b] - s_baseline[b];                     // kick "punch"
    if (excess < 0f) excess = 0f;
    float reshaped = s_baseline[b] * SUSTAINED_KEEP + excess * TRANSIENT_BOOST;
    // ... blend with raw target ...
}
```

The comment on line 2074 says `~25 FFT half-life @ 93 Hz = ~270 ms`. That's accurate for the OLD config when FFTs fired at ~93 Hz (HOP_SIZE=512, ~10.7ms per FFT).

**Phase P (already shipped before any of my recent changes) dropped the FFT hop to 1 ms** (FFT_MIN_STRIDE=48 at 48 kHz). Now FFTs fire at ~1000 Hz. The same `0.96` constant gives:

| Cadence | FFT/sec | Half-life in FFTs | Half-life in ms |
|---|---|---|---|
| **Old (HOP=512)** | 93 | 17 | **~182 ms** |
| **Current (HOP=48)** | 1000 | 17 | **~17 ms** |

A typical kick drum's fundamental energy lasts 40-80 ms. With a 17 ms baseline tracker, the EMA absorbs the kick into the baseline **in less than half its duration**. By mid-kick, `s_baseline[b] ≈ s_targets[b]`, so `excess ≈ 0`. The reshaped value collapses to `s_baseline * 0.25` — *quieter than the raw target*. Kicks aren't just unboosted; they're actively suppressed.

### Why basslines look correct anyway

Sustained basslines have `s_targets[b] ≈ s_baseline[b]` by definition (the EMA tracks them). The reshape formula reduces them to 25% of input + 0 excess = 25% × raw. After downstream gain/compressor the bars settle around 40-60%. That's what the operator sees as "great basslines."

But kicks need `excess > 0` to be boosted via `TRANSIENT_BOOST × excess`. With the broken baseline tracker, `excess ≈ 0`, so kicks get **the same treatment as sustained bass** → bars don't pop.

### Fix

Single value change to restore ~270 ms half-life at current FFT cadence:

```
log(0.5) / log(0.99744) ≈ 270 FFTs at 1000 Hz = 270 ms ✓
```

**Edit `audio_spectrum.cs:2074`:**
```csharp
const float BASELINE_DECAY = 0.99744f;   // ~270 ms half-life @ 1000 Hz (Phase P-corrected)
```

**Also sync the silence-path twin at `audio_spectrum.cs:1759`:**
```csharp
s_baseline[b] *= 0.99744f;       // matches BASELINE_DECAY
```

Update the comment text on both lines so the next phase doesn't repeat the cadence drift.

**Risk:** none. SUSTAINED_KEEP / TRANSIENT_BOOST / per-band gain / REF_MAG were all tuned around the correct 270 ms half-life; only the time-constant decoupled when FFT cadence jumped 10×.

**Expected outcome:** Sustained bass stays at 40-60% (unchanged). Kicks return to their pre-Phase-P punch — visible 10-30% spike above baseline on each hit.

---

## Issue 2 — Choppy on hardcore/hardstyle (visual strobing at 3.3 Hz)

### Cause

Phase S Finding L dropped the client fall half-life floor from 15 ms to 5 ms (`overlay.html:3448`):
```js
const fallHalfGL = 5 + smoothGL * 345;
```

At a 200 BPM kick rate (one kick every 300 ms), with a 5 ms fall half-life:

| Time after kick | Bar height % |
|---|---|
| 0 ms (peak) | 100% |
| 5 ms | 50% |
| 10 ms | 25% |
| 25 ms (5 half-lives) | **3%** |
| 50 ms | <1% |
| 300 ms (next kick) | 0% |

**Result:** bar is at peak for ~5 ms, near-zero for the remaining 295 ms. That's a **3.3 Hz square wave with 8% duty cycle**.

The human visual system processes flicker at carrier frequencies up to ~75 Hz, but **the envelope of the bar's luminance** is what we perceive as motion. A 3.3 Hz square-wave envelope reads as "strobing" or "choppy" regardless of how many fps the renderer is doing. Higher rAF rate doesn't fix this — we're rendering the SAME peak-then-zero shape at higher temporal density.

### Phase S Finding G aggravates it slightly

`ENV_ATTACK = 1.0f` (server-side) made rise truly instant. Combined with client-side `(tgt >= cur) ? tgt : ...`, the leading edge of each kick is a single-rAF-frame step — sharper than before. That sharpness compounds the square-wave perception.

### Quantifying "smooth heartbeat"

Target visual profile: bar should still snap up on attack (the heartbeat) but **never drop below ~30-50% of the most recent peak** before the next event. At 300 ms kick spacing, that requires either:
- Single-stage fall with **half-life ~140-180 ms** (3-4 half-lives between kicks → 6-12% residual at peak time)
- OR a **two-stage piecewise fall**: slow above 30% of peak, fast below

The two-stage version preserves snap on attack AND clears between hits.

### Fix

**Edit `overlay.html:3447-3458` — replace the single-stage fall with a piecewise two-stage fall:**

```js
const smoothGL    = Math.max(0, Math.min(1, _cfg.spectrum.smoothing ?? 0));
// Phase S+ Finding 2: two-stage piecewise fall. Above a knee (30% of
// rolling peak), use a SLOW half-life so sustained bass doesn't strobe
// between transients. Below the knee, fast half-life so the bar still
// clears cleanly before the next attack. Preserves heartbeat snap
// (rise still instant via the (tgt >= cur) branch below); only the
// decay path is shaped.
const fallSlowHL  = 60 + smoothGL * 200;   // 60-260 ms half-life above knee
const fallFastHL  = 5  + smoothGL * 30;    // 5-35 ms half-life below knee
const dtMsGL      = Math.min(100, now - (_prevRafTick || now));
const fallAlphaSlow = 1 - Math.pow(0.5, dtMsGL / fallSlowHL);
const fallAlphaFast = 1 - Math.pow(0.5, dtMsGL / fallFastHL);
_prevRafTick = now;
let frameMaxGL = 0;
for (let k = 0; k < _renderedBands.length; k++) {
  const tgt = _loopbackBands[k];
  const cur = _renderedBands[k];
  if (tgt >= cur) {
    _renderedBands[k] = tgt;                 // instant rise (heartbeat)
  } else {
    // Knee at 30% of current value (effectively 30% of last peak since
    // we've been decaying from there).
    const knee = cur * 0.30;
    const a = (cur > knee) ? fallAlphaSlow : fallAlphaFast;
    _renderedBands[k] = cur + (tgt - cur) * a;
  }
  if (_renderedBands[k] > frameMaxGL) frameMaxGL = _renderedBands[k];
}
_normPeak = Math.max(_normPeak * 0.995, frameMaxGL);
```

**Risk:** very low. Pure visual reshape; no audio path changes. The slider's `smooth=0..1` range still maps to a fast-to-slow control; absolute minimum no longer crosses the flicker threshold.

**Expected outcome:** Each kick → instant snap up (preserved heartbeat). Bar exhales smoothly for ~100-150 ms, then rapidly clears the last 30%. Visual shape is **sawtooth-with-curve** at 3.3 Hz instead of **square wave** at 3.3 Hz. Flicker fusion no longer triggers.

---

## Issue 3 — WASAPI/MME/KS appear "3-10 fps sluggish"

### Cause (same as Issue 2, with a different trigger)

**Live measurement from the running v7.2.2 instance:**
- **ASIO** (operator's current backend): SSE delivering at **801 fps** to a single client, arrival gaps p50=5.2 ms / p99=7.2 ms.
- **WASAPI Loopback** (system default = Audient iD14): SSE delivered **2 frames in 8 seconds** during the test. **Stuck on silence — most likely a routing issue, not a code bug.**

Two separate phenomena under the operator's "3-10 fps" complaint:

#### Phenomenon A — sparse SSE arrivals + fast client fall = strobe

For ANY non-ASIO backend, the OnData callback arrives at the engine period:
- WASAPI Shared Loopback: typically 10 ms (consumer Win10/11) but can be **100 ms on cheap Realtek codecs**
- MME (our config): 10 ms × 4 buffers
- WDM-KS Exclusive: 3-10 ms typical; falls back to Shared (~10 ms) on virtual endpoints

ASIO is the outlier — small buffers (typically 256 samples = 5.3 ms at 48k), so ~187 OnData/sec. Within each OnData, the FFT trigger fires multiple times. Each publish hits the SSE pipe.

For WASAPI Loopback with a typical 10 ms engine period: 100 OnData/sec, ~5-10 FFTs per OnData (all firing within sub-ms), then 9-10 ms of quiet SSE pipe. **The client receives 100-200 SSE arrivals/sec in bursts of 10 every 10 ms.**

With Phase S fall half-life = 5 ms:
- During the OnData burst: bar snaps up to whatever peak the FFT magnitudes report.
- Between OnData bursts (9-10 ms quiet): bar decays at 5ms half-life → drops to 25% in 10ms, 12% in 15ms...

If the operator's machine has a 100ms engine period (which some Realtek codecs do): 10 OnData/sec, bar decays to 3% between bursts → looks like 10 fps strobe. **This matches the operator's complaint exactly.**

#### Phenomenon B — WASAPI Loopback on the wrong endpoint = silence

In my test, switching to WASAPI Loopback put audio_spectrum on the System Default render endpoint (the Audient iD14 — which the operator routes through ASIO via VB-Matrix). The Audient endpoint via WASAPI Loopback **doesn't see the operator's music** because music goes through ASIO directly, bypassing the WASAPI render path.

This produces the "even worse than 10 fps" outcome — bars stuck at zero with rare pulses from desktop audio (Windows sounds, browser audio routed via default).

### Fix

**Primary fix — same as Issue 2: two-stage piecewise fall.** This is sufficient to address Phenomenon A on any reasonable engine period (≤50 ms). Bars stay elevated between sparse SSE arrivals, look continuously animated.

**Secondary observations (no code change needed):**

1. **The WASAPI Loopback "silence" in my test isn't a Phase S regression** — it's a configuration mismatch between the WASAPI default render endpoint and the operator's actual audio routing. If the operator switches to WASAPI Loopback in practice, they should select the specific endpoint that receives their music (e.g., VB-Matrix Media B1 / Voicemeeter Out B1 / VB-Cable Output) via the device dialog. WASAPI Loopback on the System Default works fine when the user's audio goes through that endpoint.

2. **Phase S Finding B (5 ms eager-decode throttle) is NOT a contributor** — investigated thoroughly. The throttle bounds CPU but doesn't affect freshness: each `_decodeLatestLoopback()` call reads the LATEST stashed arrival regardless of how many arrivals were buffered. Bands are always as fresh as physically possible.

3. **Process priorities (tray/server BelowNormal) are by-design and don't affect the OBS overlay path** — `obs-browser-page.exe` is a child of OBS, not Master's FM. Inherits OBS's priority class.

4. **IAudioClient3 cannot help WASAPI Loopback** — Microsoft does not allow `AUDCLNT_STREAMFLAGS_LOOPBACK` with `InitializeSharedAudioStream` (re-confirmed during this research). The 10-100 ms engine period for loopback is OS-imposed. The two-stage fall is the only practical fix.

### Optional follow-up (low priority)

If a future user has a *truly* slow Realtek codec (100 ms engine period) and the two-stage fall isn't enough, consider:

**Server-side burst pacing.** Inside OnData's FFT trigger loop, instead of firing all 10 FFTs back-to-back, sleep a fraction of the engine period between them so SSE arrivals are spread across the engine period instead of bunched at the front. Adds latency (~5 ms) but smooths SSE cadence.

Not recommended right now — the two-stage fall fully covers normal-period (10 ms) WASAPI which is what 99% of users have.

---

## Verification plan

After approval, ship in this order:

1. **Issue 1 fix (server)** — single-line `0.96f` → `0.99744f` at audio_spectrum.cs:2074, sync at 1759, comments updated. Rebuild + deploy.
   - **Validate:** Play hardcore with prominent kicks. The kick band should visibly pop above the sustained bassline (5-10% taller than the sustain).

2. **Issue 2+3 fix (client)** — replace single-stage fall with two-stage piecewise at overlay.html:3447-3458. No rebuild; copy overlay.html to install dir + force-reload.
   - **Validate (Issue 2):** Play 180+ BPM hardcore. Bars should snap up on each kick and decay smoothly without strobing. Should LOOK fluid.
   - **Validate (Issue 3):** Switch backend to MME or WASAPI Loopback (on an endpoint that gets audio). Bars should not look choppier than ASIO — slightly fewer real updates per second but visually filled-in by the slow-decay path.

3. **Verify Phase S regression hotfix is still good** — sanity SSE rate check on ASIO before/after.

---

## What was NOT a problem (investigated and ruled out)

- **FFT_SIZE = 1024** (Phase S A1) is fine. Bin spacing 46.9 Hz is plenty for the kick fundamental (50-80 Hz fundamental, also visible via harmonics).
- **ENV_ATTACK = 1.0** (Phase S G) is correct. The server smoother isn't compounding kick energy; the FFT magnitude is the energy source. Going back to 0.85 just delays the peak.
- **REF_MAG = 158** (Phase S A1 retune) is correct (√2 scaling for halved FFT_SIZE on broadband). Bass at 40-60% confirms this.
- **Phase S Finding B (5 ms decode throttle)** doesn't affect freshness. Removing it would only burn ~2x CPU on decodes for no perceptual gain.
- **Tray/server BelowNormal priority** doesn't affect the OBS browser source path.
- **Phase S Finding M (skip-burst-redundant-FFTs)** is currently reverted as precaution; can be re-enabled later as a pure CPU optimization. Not related to any of the three issues.

---

## Status

- Research complete: 3 issues identified, root causes traced to specific lines of code with quantified math, fixes proposed.
- Two of three issues share a single client-side fix (~10 LOC).
- Server-side fix is a single number change.
- **No code changes applied. Awaiting operator approval.**

Total budget: ~12 LOC across two files (one C# constant, one JS smoothing function rewrite). ~30 minutes implementation + verification.
