# Research #3 — Visualiser / FFT / PCM-bytes latency budget (post-Phase R)

**Trigger:** After Research #1 (Phase R) shipped audio-engine latency from ~10-150 ms down to 1-10 ms across all backends, the operator asked whether the *rest* of the chain (PCM accumulation → FFT → bands → SSE → JS decode → render → display) was hiding more latency worth squeezing.

**Short answer: yes — one big find (~11 ms), one medium find (~4-8 ms), several small ones (~1-2 ms each).** The largest is the Hann window's center-of-mass at FFT_SIZE=2048 — currently the single biggest unfixed latency in the entire pipeline.

---

## The full latency chain (measured + verified from code)

For ONE sample of audio leaving the user's speaker driver to actually moving a visualiser bar on the operator's local screen:

| # | Stage | Where in code | Cost | Notes |
|---|---|---|---|---|
| 1 | Audio engine → audio_spectrum buffer | (Phase R) | 7-10 ms (loopback) / 1-5 ms (ASIO) | OS-bound for shared loopback |
| 2 | Sample accumulation (s_fftStride trigger) | `audio_spectrum.cs:1152` | **1 ms** | FFT_MIN_STRIDE=48 @ 48 kHz |
| 3 | **Hann window center-of-mass** | `audio_spectrum.cs:1759-1763` | **~21 ms** | **This is the elephant.** FFT_SIZE/2 = 1024 samples = 21.3 ms |
| 4 | FFT compute (RFFT) | `RealFFTToMag` | 50-150 µs | Already optimized via Phase O/P |
| 5 | Magnitude → bands → compressor → smoothing | `audio_spectrum.cs:1789-2080` | ~100-300 µs | 480 bands, single pass |
| 6 | SSE client wakeup (per-client AutoResetEvent) | `audio_spectrum.cs:2740, SignalAllSseClients` | ~100-500 µs | sub-ms wake |
| 7 | base64 encode (cached for dups) | `audio_spectrum.cs:2833` | ~100 µs (first hit) / ~0 (cached) | Reused across duplicate sends |
| 8 | HttpListener Write + Flush | `audio_spectrum.cs:2843-2844` | ~1-2 ms | Localhost socket, kernel involved |
| 9 | Browser EventSource onmessage → stash | `overlay.html:2774` | ~10 µs | Just `_loopbackLatestRaw = e.data` |
| 10 | rAF wait (drawSpectrum) | `overlay.html:3355` | **0-16.67 ms** (avg ~8 ms @ 60 Hz) | Bound to display refresh |
| 11 | `_decodeLatestLoopback()` (atob + charCodeAt) | `overlay.html:2682` (sample) | ~150-300 µs | One decode per rAF |
| 12 | Rise-instant smoothing | `overlay.html:3434-3441` | ~20 µs | **Rise = instant, fall = exponential decay** |
| 13 | WebGL composite → monitor | `_glRender` | ~1-2 frames @ display rate | GPU pipeline buffer |

**Sum (60 Hz monitor, WASAPI Loopback, ASIO-quality numbers in parentheses):**
- 7 + 1 + 21 + 0.2 + 0.2 + 0.5 + 0.1 + 1.5 + 0.01 + 8 + 0.2 + 0.02 + 17 = **~56 ms** (50 ms on ASIO at 1ms backend)
- On 144 Hz: ~44 ms (39 ms on ASIO)
- On 240 Hz: ~40 ms (35 ms on ASIO)

The Hann window contributes **~37%** of the total budget. Nothing else exceeds 1 ms except the OS display refresh.

---

## Finding A — Hann window center-of-mass at FFT_SIZE=2048

### What it is
A Hann window is a cosine-squared bell curve, max in the middle, zero at the edges:
```
w[n] = 0.5 * (1 - cos(2π·n/N))
```
For FFT_SIZE=2048, the maximum weight is on sample 1024 — the *middle* of the window. Edge samples (newest at index 2047, oldest at index 0) are weighted **near zero**.

So if a drum kick lands at time T, that sample is at the trailing edge of the next 21 FFT triggers (assuming 1 ms hop):
- FFT @ T+0 ms: kick is the very newest sample, weight ~0 in the window.
- FFT @ T+10 ms: kick is at index 1568, weight ~0.55.
- **FFT @ T+21 ms: kick is at index 1024, weight = 1.0 (full).**
- FFT @ T+30 ms: kick is at index 576, weight ~0.45.
- FFT @ T+42 ms: kick is at index 0, weight ~0.

A peak detector watching the FFT magnitudes will see the maximum amplitude on the FFT triggered ~21 ms after the kick — not on the FFT triggered "right after" the kick.

### What this means in practice
- Bass-heavy transients (kicks, snares, drops) appear in the visualiser ~21 ms late.
- High-frequency transients (cymbal hits) are mostly *spread* across all the FFTs — the spectral content from the cymbal exists in the window for ~42 ms, peaks at +21 ms.
- The visualiser feels "lazy" on attack even though the upstream pipeline is now sub-10 ms.

### Three ways to reduce it

#### Option A1 — Shrink FFT_SIZE
| FFT_SIZE | Bin spacing @ 48 kHz | Hann center latency | Verdict |
|---|---|---|---|
| 2048 (current) | 23.4 Hz | 21.3 ms | today |
| 1024 | 46.9 Hz | 10.7 ms | **-10.6 ms; recommended** |
| 512 | 93.75 Hz | 5.3 ms | loses sub-bass detail |
| 256 | 187.5 Hz | 2.7 ms | spectrum loses character |

At 1024: bin spacing is 46.9 Hz. The lowest band (20 Hz) would span less than one bin → we already sub-bin interpolate there (`s_bandIsSubBin[b]` at line 1810). The sub-bin interpolation logic stays intact; only the integration of "wide" bands (mid+treble) changes — those have many bins each and won't visibly differ.

**Risk:** the band shape might subtly change because the spectral envelope smoothing is calibrated to 2048 bin density. Test in the actual customize preview before committing. ~5 LOC change: `const int FFT_SIZE = 1024;` and rebuild the precomputed `s_hannWindow`, `s_real`, `s_imag`, `s_mag` arrays.

**Where compressor / band tilt tuning would need to be checked:** REF_MAG constant + per-band gamma LUT in `EnsureBandGammaLut` (~lines 1700-1730 area). Halving FFT_SIZE halves the per-bin magnitudes (energy is divided across half as many bins, so each bin holds ~2x more, but the windowing normalization may shift), so we may need to re-tune REF_MAG by ~3 dB. Or leave it and trust the auto-compressor to adapt.

#### Option A2 — Asymmetric window (weight newest samples more heavily)
Replace the symmetric Hann with a one-sided window that puts max weight at the trailing edge (newest samples). Example: half-Hann.

```
w[n] = 0.5 * (1 - cos(π·n/N))   // half Hann, peaks at n=N (newest)
```
Latency: ~0 ms (newest samples weighted maximally).
Side effect: spectral leakage doubles. Bin "skirts" become wider — peaks in the FFT bleed into 2-3 adjacent bins instead of being tight.

For a music visualiser this is **probably fine** — we're already averaging adjacent bins into perceptual bands and smoothing the result. The visual cost is "bass peaks are slightly broader" which… might actually look better, not worse.

Effort: ~15 LOC (replace `InitHannWindow` with `InitTrailingWindow`, regenerate table).

#### Option A3 — Multi-resolution FFT (the pro analyser approach)
Run TWO FFTs in parallel:
- FFT_SHORT: 256 samples → fast response for high frequencies (~3 ms latency, 187 Hz resolution)
- FFT_LONG: 2048 samples → slow response for low frequencies (~21 ms latency, 23 Hz resolution)

Split bands by frequency: bands above 500 Hz get FFT_SHORT magnitudes; below 500 Hz get FFT_LONG. Bass stays detailed, treble stays snappy.

Cost: ~2x FFT compute (still well under 0.5 ms total). Code: ~80 LOC. Best perceptual result by far.

**Recommendation for Finding A:** Ship **A1 (FFT_SIZE=1024)** as a quick test, see if bass detail loss is noticeable. If yes, do **A3 (multi-resolution)**. **A2 (asymmetric)** is interesting but unconventional — keep in reserve.

---

## Finding B — `_decodeLatestLoopback` deferral on the client (~4-8 ms)

### Today's behaviour (`overlay.html:2756-2776` and `:3382`)
SSE arrives:
```js
_loopbackSSE.onmessage = e => {
    if (typeof e.data !== 'string' || !e.data) return;
    _loopbackLatestRaw  = e.data;          // stash; decoded by drawSpectrum
    _loopbackUpdatedAt  = performance.now();
};
```
Then on next rAF:
```js
function drawSpectrum() {
    ...
    _decodeLatestLoopback();   // atob + charCodeAt the latest stashed raw
    ...
}
```

This was a CPU optimization in v8.3.4: at SSE rate 1000 fps, decoding on every arrival was burning ~25% of one core because the rAF could only render 60-360 times/sec anyway. Decoding only at rAF cadence saved most of that.

### The hidden latency
- SSE arrives at time T (e.g., 4 ms past last rAF tick).
- Next rAF fires at T + ~13 ms (on a 60 Hz monitor, mid-frame).
- Decode runs at rAF: ~13 ms after SSE arrival.
- **Effective latency added by this defer: 0-16.67 ms (avg ~8 ms @ 60 Hz).**

If we decode on arrival, the `_loopbackBands` array is ready *immediately*. The same rAF still does the render, but it reads fresh bands instead of stale ones from the previous rAF tick.

### The fix
Decode on arrival, but only if more than 5 ms have passed since the last decode. That preserves the v8.3.4 CPU win against bursty arrivals while eliminating the latency floor.

```js
let _lastLoopbackDecode = 0;
_loopbackSSE.onmessage = e => {
    if (typeof e.data !== 'string' || !e.data) return;
    _loopbackLatestRaw = e.data;
    _loopbackUpdatedAt = performance.now();
    const now = performance.now();
    if (now - _lastLoopbackDecode >= 5) {
        _decodeLatestLoopback();   // decode now, bands are ready for next rAF
        _lastLoopbackDecode = now;
    }
};
```

At 60 Hz monitor + 100 fps SSE: decode runs ~10 times/sec on arrival (each ≥5 ms apart). Mostly the rAF will read freshly-decoded bands.

**Effort:** ~15 LOC. **Saving:** ~4-8 ms (avg).

---

## Finding C — HttpListener Write + Flush coalescing (~1-2 ms)

`HttpListener.Response.OutputStream.Write` + `.Flush()` on Windows actually translates to:
1. WriteFile to the HTTP.sys driver
2. Flush triggers TCP_NODELAY-equivalent dispatch

But on a localhost loopback adapter, Windows STILL applies some Nagle-style coalescing in HTTP.sys. The default kernel-side buffer can hold ~1-2 ms before flushing to the loopback queue.

### The fix
Set `BufferOutput = false` on the response before any Write:
```csharp
ctx.Response.BufferOutput = false;
```
And add `Connection: keep-alive, no-transform` headers (we have those).

Also: HTTP.sys has a registry tunable for loopback flush behaviour but I won't recommend touching that — it's a system-wide setting that affects all HTTP traffic. Just `BufferOutput = false` and an explicit `.Flush()` (which we already do) is sufficient.

**Effort:** 1 line. **Saving:** ~0.5-2 ms (Win10/11 vary; ~1 ms typical).

---

## Finding D — rAF cap is artificially capped at 120 fps for non-OBS (`overlay.html:3369`)

```js
const fpsCap = Math.max(30, Math.min(120, _cfg.spectrum?.fps ?? 120));
```

The customize slider lets users pick up to 240+ on the UI, but `drawSpectrum`'s `Math.min(120, ...)` cap silently drops them back to 120. For users on 144/240 Hz monitors who want the visualiser at their native rate, this is leaving display-side smoothness on the table — though crucially, it does NOT add latency (rAF is bounded by monitor refresh anyway; the JS cap just makes the loop skip extra ticks).

**Verdict:** Not a latency issue — but a smoothness gap. Could raise the cap to 240. Tiny perceptual win. Not a priority.

---

## Finding E — Browser Source FPS in OBS (the streaming-side wall)

When the visualiser is captured inside OBS as a browser source, the browser source has its own configured FPS (default 30, often 60). Within CEF the rAF is throttled to that. So:

- OBS browser source @ 30 fps → adds 0-33 ms (avg 16 ms) latency on top of everything else
- OBS browser source @ 60 fps → adds 0-16.67 ms (avg 8 ms)

This is **outside our control** — it's an OBS config setting. We already document FPS=60 in our docs. No code change applicable; just user education. Mention in the customize hint.

---

## Finding F — SSE_INTERVAL_MS keep-alive wait (`overlay.html` mentions 8 ms)

The server's SSE WaitOne uses `SSE_INTERVAL_MS = 8` as the per-cycle timeout when waiting for new frames. The signal-driven path Sets the event immediately on FFT publish, so this 8 ms is the worst-case wait only when no FFT publish has fired (i.e., silence-decay path).

During music: signal fires every ~1 ms → SSE thread wakes within hundreds of microseconds → write → next wait. The 8 ms only matters during silence (no music playing, decay-only frames). Not relevant for the perceived latency on music transients.

**Verdict:** Already optimal during music. No change.

---

## Combined latency table — post-Phase R vs proposed Phase S (Research #3)

| Stage | Phase R (today) | Phase S (proposed) | Δ |
|---|---|---|---|
| 1. Backend (loopback) | 7-10 ms | 7-10 ms | 0 |
| 2. Sample accumulation | 1 ms | 1 ms | 0 |
| 3. Hann window center | **21 ms** | **10 ms** (FFT 1024) or **3 ms** (multi-res) | -10 to -18 ms |
| 4-8. FFT + bands + SSE | ~2 ms | ~2 ms | 0 |
| 9. SSE write coalescing | ~1.5 ms | ~0.5 ms (BufferOutput=false) | -1 ms |
| 10. rAF wait (60 Hz) | 8 ms avg | 8 ms avg | 0 (display-bound) |
| 11. JS decode timing | ~8 ms (deferred) | ~0-2 ms (eager) | -6 ms |
| 12. Smoothing rise | 0 (already instant) | 0 | 0 |
| 13. WebGL composite + monitor | ~17 ms | ~17 ms | 0 (display-bound) |
| **TOTAL (60 Hz, loopback)** | **~56 ms** | **~38 ms** (FFT 1024) / **~31 ms** (multi-res) | -18 to -25 ms |
| **TOTAL (60 Hz, ASIO)** | **~50 ms** | **~32 ms** (FFT 1024) / **~25 ms** (multi-res) | -18 to -25 ms |
| **TOTAL (144 Hz, ASIO)** | **~39 ms** | **~21 ms** / **~14 ms** | -18 to -25 ms |

Below ~25 ms, the perceptual latency is essentially "instant" for non-musicians (musicians can detect down to ~6-10 ms; for stream viewers and casual use, ~30 ms feels real-time).

---

## Recommendation order (smallest-effort-first)

1. **Finding B (eager SSE decode)** — ~15 LOC JS change. ~4-8 ms saved. **No risk to bass detail or spectral character.** Do first.
2. **Finding C (BufferOutput = false)** — 1 line C# change. ~1 ms saved. Trivial. Do alongside B.
3. **Finding A1 (FFT_SIZE = 1024)** — ~5 LOC C# change + REF_MAG re-tune if needed. ~10 ms saved. **Some risk to bass spectral detail — needs visual A/B against the current build.** Ship as a try-and-see; operator decides if bass still looks right.
4. **Finding A3 (multi-resolution FFT)** — ~80 LOC. ~17 ms saved (vs A1's 10 ms). Best perceptual result. **Defer until after A1 is verified** — if A1 looks fine, A3 is overkill. If A1 looks too coarse in the bass, ship A3.

Total realistic Phase S saving: **~15-25 ms** off the current visible latency.

---

## What this research DOES NOT fix

- **Monitor refresh wait (~8-17 ms).** Display-bound; only solvable by buying a 240+ Hz monitor. Not a code change.
- **OBS browser-source rAF cap.** User-config. Not a code change.
- **Stream / encoder / network buffering.** Way past our process. Twitch/YouTube/etc. impose seconds, not ms.
- **Loopback shared-mode engine period (~7-10 ms).** OS-bound. See Research #1.

---

## Verification approach (when shipping any of these)

The operator can A/B by:
1. Capture a known-percussive song (drum-heavy hardcore drop).
2. Side-by-side: current overlay window + a tone-generator at known time.
3. Visual estimate of bar-to-audio lag is hard to eyeball at ~50 ms but **easy at ~25 ms** — at 25 ms the bars feel locked to the audio; at 50 ms they feel slightly behind. So Phase S should be perceptible if shipped in full.

There's no automated test — this is a perceptual evaluation by the operator's ear / eye.

---

## Status

- Research delivered, no code changes applied.
- Awaiting operator approval per finding (B, C, A1, A3) before any Phase S implementation.
- Phase R already shipped and active in audio_spectrum v7.1.0.
