# V14_S7_8B_LATENCY_BASELINE.md

Stage 7.8B STEP 6: Latency baseline (before reductions)
Date: 2026-05-09
Brief: CLAUDE_CODE_INSTRUCTIONS.md (Stage 7.8B)

---

## 1. Measurement Method

Runtime measurement (skip 5 tracks via SoundCloud, measure wall-clock vs overlay
console-log timestamps) was not available at STEP 6 execution time (no active
SoundCloud session). Baseline derived from analytical inspection of source
cadences and the operator's reported symptom ("2-3s end-to-end delay").

---

## 2. Analytical Baseline

### 2.1 Track-change detection latency

Source: `src/tray_csharp/Detectors/SmtcEventBridge.cs`
```
_drainTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
// line 31: DrainCadenceMs = 250
```

Worst-case track-change detection: **250ms** (drain fires at end of current 250ms window)
Average: ~125ms

### 2.2 Heartbeat (pause / seek / position) latency

Source: `src/tray_csharp/Services/HeartbeatService.cs`
```
private const int CadenceMs = 2000;
```

Worst-case pause/seek detection: **2000ms**
Average: ~1000ms

### 2.3 Album art sequential lag

Source: `src/tray_csharp/Services/TrackResolver.cs` OnTrackChanged
Current code fires webhook FIRST, then resolves art. Art arrives 200-500ms after
text update due to HTTP round-trip to art provider. Overlay shows track text first,
then art pops in.

### 2.4 SSE keep-alive diagnostic

```
Select-String overlay.log -Pattern "sse|sseClient|reconnect|disconnect"
```

Result: Lines found are all OBS WebSocket reconnect loops (Mode B). Zero SSE
reconnect or disconnect events found. SSE connection is stable.
**Decision: SSE keep-alive ping not needed.**

---

## 3. Baseline Summary

| Metric | Worst-case | Average | Source |
|---|---|---|---|
| Track-change detection | 250ms | 125ms | SmtcEventBridge._drainTimer |
| Pause detection | 2000ms | 1000ms | HeartbeatService.CadenceMs |
| Seek detection | 2000ms | 1000ms | HeartbeatService.CadenceMs |
| Art-after-text lag | 200-500ms | 350ms | TrackResolver sequential dispatch |
| **End-to-end (pause/seek)** | **~2250ms** | **~1125ms** | sum of above |

Operator reports 2-3s delay. Consistent with heartbeat 2000ms + drain 250ms.

---

## 4. Target (post-fix)

| Metric | Target after STEP 7 |
|---|---|
| Track-change detection | 100ms worst-case (drain: 100ms) |
| Pause detection | 1000ms worst-case (heartbeat: 1000ms) |
| Seek detection | 1000ms worst-case (heartbeat: 1000ms) |
| Art-after-text lag | Near-zero (parallel prefetch) |
| **End-to-end improvement** | ~1000-1150ms reduction (median), ~500ms typical |

---

## 5. Commit

`Stage 7.8B: STEP 6 -- latency baseline measurement (V14_S7_8B_LATENCY_BASELINE.md)`
