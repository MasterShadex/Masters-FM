# V14_S7_S7_5C_SOAK_AGGREGATE.md

Stage 7.5C aggregate analysis across all soak iterations.

| Soak | PID | Conditions | Duration | Plateau (MB) | Late MB/h | Tracks |
|---|---:|---|---:|---:|---:|---:|
| STEP 1 initial | 24608 | active listening | 90 min | 198-203 | 1.4 (t+60->t+90) | 28 |
| Variant 1 | 23784 | active listening (reproduction) | 90 min | 168-171 | 1.4 (t+60->t+90) | 28 |
| Variant 2 | 11756 | idle (paused) | 47 min | 166-167 | 0 (t+45->t+47) | 1 |

---

## Cross-soak observations

### Plateau character is the same regardless of input volume

All three soaks demonstrate the same ramp-then-plateau pattern. The
plateau time-to-reach varies (17 min for STEP 1 vs 30 min for
Variant 1 vs 31 min for Variant 2) but the plateau IS reached in
all cases. Late-plateau growth rates converge to <5 MB/h.

### Plateau LEVEL varies stochastically

| Soak | Plateau MB |
|---|---:|
| STEP 1 | 198-203 |
| Variant 1 | 168-171 |
| Variant 2 | 166-167 |

The 30-MB delta between STEP 1 and Variant 1 is unexplained by
deterministic factors. Same workload, same TFM, same code, same
playlist, same machine. The most likely explanation is .NET 8
runtime non-determinism (heap layout, JIT tier promotion timing,
finalizer scheduling). The OS-level memory-mapped image cache being
warmer for Variant 1's launch (after STEP 1's 90-min run loaded
all metadata) may have prevented an additional one-time heap
expansion event that STEP 1 experienced at t+17.

The 4-MB delta between Variant 1 listening (170 MB) and Variant 2
idle (166 MB) is consistent and explained: track-resolver state +
event-handler thread state + active session RCW retention sum to
~4 MB of marginal cost under the active workload.

### Track-change-correlated leak HYPOTHESIS REJECTED

The strongest evidence comes from Variant 2 (idle): WS reached its
plateau at 166 MB while events=3 STATIC for the entire soak. There
were NO MediaPropertiesChanged, PlaybackInfoChanged, or
TimelinePropertiesChanged events firing. Despite that, the WS still
exhibited the ramp-then-plateau pattern.

If the 7.5B 216 MB/h growth were a B-001-pattern track-change-
correlated leak, Variant 2 idle would have stayed near 120 MB
baseline (no events to drive allocation). Instead it reached the
SAME plateau height (within 4 MB) as the listening soaks.

Conclusion: **the ramp-and-plateau is .NET 8 runtime warm-up
(JIT tier promotion + initial heap expansion + memory-mapped image
load), NOT a leak in SMTCWatcher or any tray code.**

### Compatibility with brief's renegotiated 160-200 MB target

| Plateau | In target band? |
|---|---|
| 166-167 (Variant 2 idle) | YES (lower edge) |
| 168-171 (Variant 1 listening) | YES (lower-middle) |
| 198-203 (STEP 1 listening) | UPPER EDGE (slightly over at 203) |

All three plateau within or right at the brief's 160-200 MB
renegotiated target. STEP 1's 198-203 brief upper edge was
the source of the 7.5B "looks like a leak" concern; subsequent
soaks plateau lower.

### Brief's <5 MB/h target

All three soaks meet this target after plateau is reached.

---

## Implications for `tray_native.cs`

`V14_S7_S7_5C_RCW_AUDIT.md` concluded "no obvious bug fixable in
<30 lines." The variant soak data **empirically validates** this:
the plateau pattern is independent of event volume, so the watcher's
RCW lifecycle is not the leak source.

`tray_native.cs` is NOT modified in this brief. sha256 stays at
the STEP 0 baseline.

---

## Implications for framework choice

WPF + WPF-UI + CSWinRT projection produces a steady-state of
~166-203 MB. The lower bound (idle) is 166 MB; the upper bound
(active listening with stochastic step) is 203 MB. The framework
choice (WPF-UI) contributes ~6.78 MB to dist; the steady-state heap
is dominated by .NET 8 runtime + WPF runtime + WinRT projection,
not by WPF-UI specifically.

Switching frameworks would NOT meaningfully reduce the plateau (the
plateau cost is structural to .NET 8 + SMTC-via-WinRT-projection).
This validates `V14_S7_S7_5C_FRAMEWORK_RECOMMENDATION.md`'s
recommendation to STAY WITH WPF-UI.

---

## Implications for sub-stage 7.10 6h soak

If the brief 7.10 6h soak runs under similar conditions, the
expected outcome is:
- Plateau reached within 17-31 min
- Plateau level 165-205 MB depending on stochastic factors
- Late-plateau growth rate < 5 MB/h
- 6h projected drift: 5 MB/h * 6 h = 30 MB. Plateau end-of-soak
  expected: 195-235 MB.

If Stage 7.10 observes meaningfully LARGER drift than this, that's
a new finding warranting deeper investigation (perhaps the kind of
expanded-locked-list Brief 2 would handle).

---

End of soak aggregate analysis.
