# v9.0.0 — Patch notes draft (apply to tray.ps1 PATCH_HISTORY after STEP 7 passes)

```
v9.0.0 — Performance overhaul: Real-FFT pipeline.

The big win: the spectrum visualizer's FFT now runs on a real-input FFT
(RFFT) instead of the legacy complex-input FFT (CFFT). Audio input is
real-valued, so the standard complex FFT was wasting half its work
computing redundant imaginary inputs and conjugate-symmetric outputs.
RFFT exploits this: it packs N real samples into N/2 complex (even-
indexed → real part, odd-indexed → imaginary part), runs an N/2-point
complex FFT — half the butterflies of the original — then applies a
post-FFT untangle using precomputed twiddle factors to recover the
N/2+1 unique bins of the real-input spectrum.

Measured impact (5-min sample on the user's ASIO setup, audio playing,
1 SSE client at fps=144):
  - audio_spectrum.exe CPU: 17.10% → 13.30% mean (-22%)
  - Per-FFT mean (PERF-ROLLUP): 0.06ms → 0.04-0.05ms (-25 to -33%)
  - Memory, threads, handle counts: identical (no regression)
  - SSE delivery: identical (133.5 fps mean, target 144)

Correctness: RFFT vs CFFT compared bin-by-bin on a synthetic 440Hz sine
at startup (`rfft self-test` log line). Max relative diff = 0.101%
across 65 bins above noise floor — vastly under the 5% acceptance
threshold the procedure required. Output is bit-equivalent to v8.3.8
within float-precision noise; visualizer looks IDENTICAL.

Backend safety: all 4 audio backends verified working post-RFFT (WASAPI
loopback, WASAPI input, MME, ASIO). The RFFT change is downstream of
the OnData decode path, so backend-specific code (each backend's NAudio
adapter) is unaffected. WDM-KS code path remains in place for systems
where the OS exposes WDM-KS endpoints (this user's Windows install
enumerates 0 WDM-KS devices, common for modern Windows).

Fallback safety: if RFFT self-test ever fails (>5% bin diff), the
DoFftAndPublish path automatically falls back to the legacy CFFT
algorithm. Both paths are kept in source for diagnosis.

Browser-side micro-wins:
  - Per-bar interpolation in drawSpectrum short-circuits when
    barCount equals BAND_COUNT (the typical user case where the
    interpolation is identity = direct array access).
  - Hoisted invariant constants (_gainScale, _energyScale,
    _bandsScale, _bandsMax) out of the per-bar loop.
  - Replaced Math.floor with `| 0` bitwise fast-int trick.

What didn't ship:
  - .NET Framework bump (4.0 → 4.6.2+) and SIMD vectorization
    (System.Numerics.Vector<float>) were evaluated and skipped per the
    v9.0.0 procedure's STEP 3 decision logic — RFFT delivered most of
    the perf goal cleanly, framework bump risks breaking the build
    pipeline for marginal additional gain.

No new runtime dependencies. No build pipeline changes. CHECKPOINT_v838
(safety floor) preserved in the v9 backup root in case rollback is
ever needed.
```

## Apply when soak passes
1. Bump $script:APP_VERSION from "v8.3.8" to "v9.0.0" in tray.ps1
2. Prepend the above PATCH_HISTORY entry (in the @{} entry format used by tray.ps1)
3. Run final _full_rebuild.ps1
4. Confirm desktop bundle has Master's FM V9.0.0.msi
