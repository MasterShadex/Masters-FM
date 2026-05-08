# V14_S7_S7_5C_SOAK_VARIANT_2_IDLE.md

Stage 7.5C Workstream 3, Variant 2 -- idle/paused soak.

PID 11756. Launched 2026-05-08 12:07:16. SoundCloud was paused via
keybd_event VK_MEDIA_PLAY_PAUSE (0xB3) sent at 12:07:05, ~11 seconds
before the tray launched. SoundCloud bridge (soundcloud-rpc) still
running but reporting paused state. 47 minutes wall-clock.

Purpose: determine whether the ramp-then-plateau pattern observed in
listening soaks is correlated with WinRT event traffic or whether
it's runtime-warm-up regardless of input.

---

## 1. Heartbeat timeseries

| t (min) | clock | WS (MB) | threads | handles | events | polls | tracks |
|---:|---|---:|---:|---:|---:|---:|---:|
| 1 | 12:08 | 118.6 | 17 | 560 | 3 | 59 | 1 |
| 2 | 12:09 | 118.6* | -- | -- | 3 | -- | 1 |
| 3 | 12:10 | 125.7 | 11 | 548 | 3 | 179 | 1 |
| 4 | 12:11 | 129.1 | 10 | 545 | 3 | 239 | 1 |
| 5 | 12:12 | 132.9 | 14 | 548 | 3 | 299 | 1 |
| 6 | 12:13 | 136.4 | 13 | 545 | 3 | 359 | 1 |
| 7 | 12:14 | 140.1 | 14 | 554 | 3 | 418 | 1 |
| 8 | 12:15 | 143.6 | 11 | 548 | 3 | 478 | 1 |
| 9 | 12:16 | 146.7 | 11 | 548 | 3 | 538 | 1 |
| 10 | 12:17 | 150.2 | 13 | 548 | 3 | 598 | 1 |
| 11 | 12:18 | 155.7 | 12 | 545 | 3 | 658 | 1 |
| 12 | 12:19 | 158.5 | 10 | 540 | 3 | 718 | 1 |
| 13 | 12:20 | 158.5 | 10 | 540 | 3 | 778 | 1 |
| 14 | 12:21 | 158.6 | 11 | 543 | 3 | 838 | 1 |
| 15 | 12:22 | 159.3 | 11 | 543 | 3 | 897 | 1 |
| 16 | 12:23 | 159.3 | 10 | 540 | 3 | 957 | 1 |
| 17 | 12:24 | 159.3 | 11 | 543 | 3 | 1017 | 1 |
| 18 | 12:25 | 159.3 | 10 | 540 | 3 | 1077 | 1 |
| 19 | 12:26 | 159.3 | 10 | 540 | 3 | 1137 | 1 |
| 20 | 12:27 | 159.3 | 11 | 543 | 3 | 1197 | 1 |
| 21 | 12:28 | 161.7 | 10 | 540 | 3 | 1256 | 1 |
| 22 | 12:29 | 162.3 | 10 | 540 | 3 | 1316 | 1 |
| 23 | 12:30 | 162.3 | 10 | 540 | 3 | 1376 | 1 |
| 24 | 12:31 | 162.3 | 10 | 540 | 3 | 1436 | 1 |
| 25 | 12:32 | 162.3 | 10 | 540 | 3 | 1496 | 1 |
| 26 | 12:33 | 162.1 | 10 | 537 | 3 | 1556 | 1 |
| 27 | 12:34 | 162.1 | 10 | 537 | 3 | 1615 | 1 |
| 28 | 12:35 | 162.8 | 10 | 537 | 3 | 1675 | 1 |
| 29 | 12:36 | 166.0 | 10 | 537 | 3 | 1735 | 1 |
| 30 | 12:37 | 166.7 | 11 | 540 | 3 | 1795 | 1 |
| 31 | 12:38 | 166.9 | 11 | 540 | 3 | 1855 | 1 |
| 32 | 12:39 | 166.9 | 10 | 537 | 3 | 1915 | 1 |
| 39 | 12:46 | 166.9 | 10 | 537 | 3 | 2333 | 1 |
| 40 | 12:47 | 166.3 | 10 | 540 | 3 | 2393 | 1 |
| 45 | 12:52 | 166.3 | 10 | 540 | 3 | 2692 | 1 |
| 47 | 12:54 | 166.3 | 10 | 540 | 3 | 2812 | 1 |

(*) The t+2 entry interpolated from log; events in t+2-3 column show
constancy.

---

## 2. Critical observations

### 2.1 events=3 STATIC for entire soak

The cumulative event counter held at exactly 3 for all 47 minutes.
Those 3 were the startup events (InitialEnumeration + 2 initial
captures). NO MediaPropertiesChanged, NO PlaybackInfoChanged, NO
TimelinePropertiesChanged, NO SessionsChanged events fired during
the soak.

This confirms SoundCloud's paused state is being honoured by SMTC
and that the watcher is correctly idle when no events arrive.

### 2.2 tracks=1 STATIC for entire soak

Only the initial track was registered. No track changes (because
no playback to advance through tracks).

### 2.3 polls increment as expected

Polls counter incremented steadily at +60/min throughout (1/sec
gap-filler arm running normally). At t+47: polls=2812 (from polls=59
at t+1; 2753 polls / 47 min ~= 58.6/min, matching cadence).

### 2.4 Threads + handles oscillation MUCH SMALLER

Idle soak: threads 10-14, handles 537-560.
Listening soaks: threads 10-35, handles 540-740 (during track-skip
bursts).

The thread pool churn during listening was driven by event-handler
fanout. Idle, it stays minimal.

---

## 3. Phase analysis

### Phase 1: ramp (t+1 to t+12)

WS grew 118.6 -> 158.5 MB. +39.9 / 11 min = **218 MB/h ramp**.
This matches the listening soak ramp rate almost exactly.

CRITICAL: the ramp happens with ZERO event traffic, ZERO track
changes. The ramp is NOT caused by WinRT event handling.

Most likely cause: .NET 8 tiered JIT promotion + initial heap
expansion + Microsoft.Windows.SDK.NET.dll memory mapping warm-up.
These happen on a wall-clock + first-call schedule independent of
input volume.

### Phase 2: first plateau (t+13 to t+27)

WS held at 158-159 MB band for 14 minutes with zero deviation. This
is a STABLE state.

### Phase 3: small step (t+28)

WS jumped 159.3 -> 161.7 in one minute (+2.4 MB). This is a
much smaller version of the listening soak's t+17 step jump. Probably
.NET 8 Gen2 GC heap expansion or a JIT tier recompilation.

### Phase 4: drift to ~166 (t+28 to t+30)

WS drifted 162.3 -> 166.7 over 3 minutes.

### Phase 5: locked plateau (t+31 onwards)

WS held at 166-167 MB band for 16+ minutes.

| Window | Δ MB | Δ min | MB/h |
|---|---:|---:|---:|
| t+31 to t+47 | -0.6 | 16 | -2.3 (slight decay) |
| t+39 to t+47 | -0.6 | 8 | -4.5 (slight decay) |
| t+45 to t+47 | 0 | 2 | 0 |

**Idle plateau is RIGID at 166-167 MB.**

---

## 4. Comparison: idle vs listening plateau

| Soak | Plateau (MB) | Plateau time-to-reach (min) | Late MB/h |
|---|---:|---:|---:|
| STEP 1 listening | 198-203 | ~17 (post step jump) | 1.4 |
| Variant 1 listening | 168-171 | ~30 | 1.4 |
| Variant 2 idle | 166-167 | ~31 | 0 |

Variant 2 idle plateau is ~3-4 MB BELOW Variant 1 listening plateau.

The 3-4 MB difference is consistent with:
- Track-resolver state for the 14-28 unique tracks observed in
  listening (each TrackUpdate is small but accumulates state)
- Event-handler thread state (the threadpool ramps up/down in
  bursts during listening)
- Snapshot RCW retention for active sessions (4 RCW slots per
  SAUMID, value-types around them)

This 3-4 MB is the ENTIRE marginal cost of WinRT event handling
under the active workload. The bulk of the ~170 MB plateau is NOT
event-driven; it's runtime infrastructure (WPF + WPF-UI + CSWinRT
projection + .NET 8 heap).

---

## 5. Key takeaway

**The 7.5B observation of "216 MB/h sustained growth" was NOT a
B-001-pattern leak.** Both idle and listening soaks demonstrate the
same ramp-then-plateau pattern. The plateau happens REGARDLESS of
event activity. The plateau level is dominated by runtime
infrastructure, not by the watcher's RCW or event handling.

If track-change-correlated leak existed (B-001-pattern), Variant 2
idle WS would have stayed near the t+1 baseline (~120 MB) while
Variant 1 listening grew. Instead, Variant 2 idle reached its OWN
plateau at 166 MB, only 4 MB below Variant 1 listening's 170 MB
plateau. The marginal cost of WinRT event handling under the
active workload is just 4 MB of additional steady-state allocation.

The C# tray's ~170 MB plateau hits the high end of the 7.4 brief's
renegotiated 160-200 MB target band. This is the correct steady-
state cost of the WPF + WPF-UI + CSWinRT architecture.

---

End of Variant 2 idle soak.
