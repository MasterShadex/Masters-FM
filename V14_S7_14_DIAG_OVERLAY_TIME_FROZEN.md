# V14_S7_14_DIAG_OVERLAY_TIME_FROZEN.md

**Stage:** 7.14 (read-only diagnosis of overlay time-frozen bug)
**Authored:** 2026-05-20 (post-Stage-7.13 closure at `41107a6`)
**HEAD:** `41107a6` (Stage 7.13 STEP 11 closure)
**Scope:** Diagnosis only. NO code changes. NO restart. NO rebuild. NO fixes.

---

## TL;DR

The overlay's current-position counter is frozen because the **tray heartbeat sends `isSeek=true` on every webhook payload** when SMTC reports a stuck position (Electron-MediaSession wrapper bug, e.g. SoundCloud-RPC). The server's B7 "seek" branch then mechanically re-pins `startedAt = now - frozen_pos` on every webhook, and the overlay's local clock `Date.now() - startedAt` resolves to that same frozen value forever.

Stage 7.12 Batch B Phase Q (`c2b98ea`, "B10 backward sync disabled for ALL sources") removed the *backward* correction path that previously produced a 30-second sawtooth from the same root cause. It did not address the **B7 / `isSeek` false-positive** path, which now fires on every heartbeat instead of every 30 s. Symptom changed from "snaps backward every 30 s" to "stuck at one value forever".

---

## 1. Reproduction (from log evidence)

Operator report:
- Customize.html LIVE PREVIEW current-position time: stuck at 0:01
- overlay.html in OBS: same, stuck at 0:01
- Real SoundCloud playback at 0:42 (confirmed by operator)
- Right-side (track duration 3:10): correct
- Only the left-side (current-position counter) is frozen

Log evidence captured 2026-05-20 14:29-14:33 with SoundCloud-RPC + a live track:

**`C:\Users\Master\AppData\Local\MastersFM\overlay.log`** -- tray heartbeat firing constantly with frozen position:
```
[14:29:41.623] [TRAY-CS] [Heartbeat] seek: drift=505ms pos=0ms expected=505ms thr=200
[14:29:41.925] [TRAY-CS] [Heartbeat] seek: drift=301ms pos=0ms expected=301ms thr=200
[14:29:42.298] [TRAY-CS] [Heartbeat] seek: drift=251ms pos=0ms expected=251ms thr=200
...
(continues at ~3-4 Hz for the entire 4-minute capture window)
```

Reading: `pos` here is **position advance per heartbeat** (not absolute position). Always 0 ms because SMTC's reported position is frozen. `expected` is the wall-clock-advance since last heartbeat. `drift = |pos - expected|`. The `drift > 200 ms` threshold trips → `isSeek = true`.

**`C:\Users\Master\AppData\Local\MastersFM\server.log`** (2.9 GB; tail) -- server B7 path firing on every webhook:
```
[14:33:33.707] [Program] Seek (0s jump) -- startedAt resynced to pos=155s
[14:33:34.606] [Program] Seek (1s jump) -- startedAt resynced to pos=155s
[14:33:34.831] [Program] Seek (0s jump) -- startedAt resynced to pos=155s
[14:33:35.039] [Program] Seek (0s jump) -- startedAt resynced to pos=155s
[14:33:35.327] [Program] Seek (0s jump) -- startedAt resynced to pos=155s
[14:33:35.927] [Program] Seek (1s jump) -- startedAt resynced to pos=155s
```

Notice:
- "Seek (Ns jump)" log line is unique to `WebhookHandler.cs` B7 branch (line 322-324)
- `drift7` is tiny (0-1 s) -- so the "seek" isn't a real seek, B7 is just being told `isSeek=true` repeatedly
- `pos=155s` is constant across many seconds of real time -- SMTC's reported position is genuinely frozen
- `pos=155s` matches *whatever SoundCloud-RPC's MediaSession last published* (it was `pos=1s` at the very start of the track; the operator's "0:01" report was from that early-track state)

The cadence is ~3-4 webhooks per second. Each one re-pins `startedAt`, so the overlay clock never gets a chance to tick forward.

## 2. Server-side state: does `/current` advance?

Four samples of `http://localhost:4242/current`, 10 s apart, parsed for the timing fields:

| Sample | Wall clock | `positionMs` | `startedAt` (epoch ms) | `isPlaying` |
|---|---|---|---|---|
| 1 | T+0 s | `None` (field absent) | 1779280290637 | `None` (field absent) |
| 2 | T+10 s | `None` | 1779280300588 | `None` |
| 3 | T+20 s | `None` | 1779280310588 | `None` |
| 4 | T+30 s | `None` | 1779280321292 | `None` |

Two observations:

1. **`/current` does NOT expose `positionMs`.** Clients have no positionMs to read directly. The overlay's only signal is `startedAt`.
2. **`startedAt` is advancing in real time.** Diff between samples is approximately equal to the 10 s sleep:
   - Sample 1 → 2: +9 951 ms
   - Sample 2 → 3: +10 000 ms
   - Sample 3 → 4: +10 704 ms

   `startedAt` is being *re-pinned* by webhook arrivals -- each new pin is `now - 155 000 ms`. As real time advances, `startedAt` advances with it (offset by a constant 155 000 ms). Therefore `Date.now() - startedAt` stays at 155 000 ms forever.

   This perfectly matches the "Seek (0s/1s jump) -- startedAt resynced to pos=155s" log spam.

## 3. Client-side state: does overlay.html tick?

`src/overlay.html` line **2021** is the position-rendering hot path:
```js
el = Math.max(0, Date.now() - lastTrack.startedAt);
```

That's the entire elapsed-time formula. No `positionMs`, no `setInterval`-driven counter that stores its own anchor -- it strictly reads `lastTrack.startedAt` every animation frame.

And `lastTrack.startedAt` is overwritten by **every** server-side push:

`src/overlay.html` line **2381** (inside the SSE-event handler):
```js
if (d.startedAt) lastTrack.startedAt = d.startedAt;
```

And line **2473** (inside another payload path):
```js
if (d.startedAt) lastTrack.startedAt = d.startedAt;
```

So with the server emitting a new `startedAt = now - 155 000 ms` every ~250 ms, the overlay's elapsed value resolves to exactly 155 000 ms forever. **Display = 2:35** (or whatever frozen value SMTC last reported -- 0:01 at track start).

The overlay does NOT have a "fallback to local tick" if `startedAt` looks stale. It blindly trusts the server, on the assumption that the server's `startedAt` is the source of truth.

## 4. Customize.html preview-pane logic

`src/customize.html` line **1680**:
```html
<div class="iframe-wrap" id="iframe-wrap">
```

`src/customize.html` line **2119** (`scaleIframe()` resizes the iframe). The preview pane is **a scaled iframe of `overlay.html`**, not a separately-rendered simulation. The preview content uses the exact same `Date.now() - startedAt` formula, against the same `startedAt` field served by the same `/current` and the same SSE stream.

Therefore the preview pane shows the same frozen position as the OBS overlay -- **NOT** a customize.html / Stage 7.13 bug. Stage 7.13 was pure CSS; it never touched any time-related code.

## 5. Root cause: where `isSeek=true` is being set on every payload

Two places fire `isSeek=true` from the tray side, both with the same logic:

### 5.1 `src/tray_csharp/Services/HeartbeatService.cs` lines 102-126

```csharp
bool isSeek = false;
if (_lastPosition.HasValue && track.Position.HasValue)
{
    var wallElapsedMs = (now - _lastTickUtc).TotalMilliseconds;
    var posAdvanceMs  = (track.Position.Value - _lastPosition.Value).TotalMilliseconds;
    var expectedMs    = track.IsPlaying ? wallElapsedMs : 0.0;
    var drift         = Math.Abs(posAdvanceMs - expectedMs);
    var seekThresholdForSource = SmtcEventBridge.IsBrowserLikeSource(track.Source)
        ? 1000.0
        : SeekThresholdMs;   // = 200 ms
    if (drift > seekThresholdForSource)
    {
        isSeek = true;
        _logger.Log($"seek: drift={drift:F0}ms pos={posAdvanceMs:F0}ms expected={expectedMs:F0}ms thr={seekThresholdForSource:F0}", Component);
    }
}
```

When SMTC's reported position is **frozen**:
- `posAdvanceMs ≈ 0` (position didn't move)
- `wallElapsedMs ≈ 250-1000 ms` (heartbeat cadence)
- `expectedMs = wallElapsedMs` (because `IsPlaying = true`)
- `drift = |0 - 250| = 250 ms` -- exceeds 200 ms threshold
- `isSeek = true` -- **false positive**

This is `the bug`. The intent of the seek-detection was "position jumped because user scrubbed". When position is *stuck* while wall clock advances, the symptom is identical (drift > threshold), but the semantic is completely different ("source is reporting stale data"), and the response should be different (don't re-pin startedAt; the previous startedAt is more accurate than a stale-position-derived one).

The heartbeat fires at the cadence visible in the log (~250-400 ms apart), so `isSeek=true` is sent at ~3-4 Hz to the server.

### 5.2 `src/tray_csharp/Detectors/SmtcEventBridge.cs` lines 328-340

Same logic, different entry point (event-driven from SMTC events instead of polled heartbeat):

```csharp
bool isSeek = false;
lock (_cacheLock)
{
    if (_prevPos.TryGetValue(saumid, out var prev) && effectivePosMs > 0 && prev.PositionMs > 0)
    {
        var posDeltaMs  = effectivePosMs - prev.PositionMs;
        var wallDeltaMs = (nowUtc - prev.ObservedUtc).TotalMilliseconds;
        var expectedMs  = isPlaying ? wallDeltaMs : 0.0;
        var jump        = Math.Abs(posDeltaMs - expectedMs);
        var seekJumpMs  = IsBrowserLikeSource(sourceName) ? 1000.0 : 100.0;
        if (jump > seekJumpMs) isSeek = true;
    }
    ...
}
```

Same trigger when position is frozen: `posDeltaMs ≈ 0`, `wallDeltaMs > 100 ms`, `jump > seekJumpMs` → `isSeek = true`.

### 5.3 Server-side response: `src/server_dotnet/WebhookHandler.cs` B7 branch (lines 312-325)

```csharp
if (isSeek && ct2["isPaused"]?.GetValue<bool>() != true && !b7Cooldown)
{
    long expectedPos = nowMs - ((long?)ct2["startedAt"]?.GetValue<long>() ?? nowMs);
    long drift7 = Math.Abs(expectedPos - positionMs);
    ct2["startedAt"] = JsonValue.Create(nowMs - positionMs);
    ct2Dirty = true;
    state.LastStartedAtUpdateMs = nowMs;
    logger.LogInformation("Seek ({DriftS}s jump) -- startedAt resynced to pos={PosS}s", ...);
}
```

The B7 branch mechanically re-pins `startedAt = nowMs - positionMs` whenever `isSeek=true`. It does NOT check whether the "seek" is plausible -- a drift7 of 0 ms is treated as a legitimate seek.

When the tray sends `isSeek=true` at 3-4 Hz with the same `positionMs = 155000` every time, B7 fires 3-4 Hz with `drift7 = 0`, re-pinning `startedAt` to the same value, and the overlay is permanently frozen at 2:35 (or whatever the stale SMTC value is).

## 6. Suspect commit list

Commits since the v12 baseline that touched any file in the time-progression path:

| Commit | File | Effect |
|---|---|---|
| `b9a319d` Phase M #3A | `WebhookHandler.cs` | Disabled B10 *backward* sync for browser-like sources. Eliminates the per-30s sawtooth for YouTube. Not the cause of THIS bug but related (it removed one of the safeguards). |
| `c2b98ea` Phase Q | `WebhookHandler.cs` | Disabled B10 *backward* sync for ALL sources. The operator's prime suspect. Confirmed: this commit removed the backward-snap that previously produced the sawtooth on SoundCloud. It did NOT introduce the freeze -- the freeze is on the B7 / `isSeek` path, which is unrelated. But Phase Q does mean: "now that backward sync is off, B7's false-positive-seek path is the only thing pinning startedAt to the stale pos value, and it fires constantly". |
| `8cf4c64` Phase C | `HeartbeatService.cs`, `SmtcEventBridge.cs` | "Last-mile latency cleanup (5 fixes)". One of the 5 fixes was "Phase C #7: detect seeks at the SMTC bridge so the server's B7-seek branch fires immediately instead of waiting for the 100 ms heartbeat to notice the drift" (per the comment at SmtcEventBridge.cs:324-327). This commit ADDED the SmtcEventBridge.cs isSeek logic (5.2 above). **Primary suspect for introducing the false-positive.** |
| `ec31b1f` Phase M | (browser-source heuristics) | Added `IsBrowserLikeSource` source-classification and raised the seek-detect threshold from 100 ms to 1000 ms for browser sources. Mitigates jitter-driven false positives on browser sources but does NOT address the *frozen-position* false positive (which presents as drift far larger than 1000 ms). |
| `31c49b7` Phases R + T | `audio_spectrum.cs` (heartbeat cadence) and visualizer | Audio backend latency + spectrum smoothing. Heartbeat cadence here is the audio-spectrum side; the tray heartbeat under question (HeartbeatService.OnTick) is a different fixed-interval. Not the cause. |

The Stage 7.13 commits (`01bd88c` through `41107a6`) are CSS-only edits to `customize.html` and documentation. They cannot affect time logic. **This bug existed before Stage 7.13 and is not a Stage 7.13 regression.**

## 7. Conclusion

**Root cause (confirmed):**

The tray's heartbeat *and* SMTC-event seek-detection both treat **"position frozen while wall clock advances"** as a seek (`isSeek=true`). When the playback source is an Electron MediaSession wrapper that stops calling `setPositionState` after initial publication (SoundCloud-RPC, certain YouTube/Twitch configurations, etc.), the SMTC-forwarded position is frozen for the lifetime of the track. The tray then flags `isSeek=true` on every heartbeat tick, the server's B7 branch mechanically re-pins `startedAt = now - frozen_pos` on every webhook, and `overlay.html`'s `Date.now() - startedAt` formula resolves to a constant frozen value for the entire track.

The operator-suspected commit `c2b98ea` (Phase Q, "B10 backward sync disabled") did NOT introduce this bug -- it removed the BACKWARD-snap that previously turned the same root cause into a 30-second sawtooth. The freeze is the residual symptom of the same SMTC-stale-position issue, now stripped of its prior sawtooth mitigation.

The likely originating commit for the false-positive `isSeek` logic in the SMTC bridge is `8cf4c64` (Phase C #7). The heartbeat-side mirror of the logic in `HeartbeatService.cs` predates the v14 work (no recent commit touched its drift formula; it has been there since Stage 7.8 or earlier).

## 8. Recommended fix shape (NOT IMPLEMENTED)

Two converging fixes; either alone would close the loop. The cleanest is to do both for defense-in-depth.

### 8.1 Tray-side fix (preferred, surgical): only flag isSeek when position actually MOVED

`HeartbeatService.cs` lines 102-126 and `SmtcEventBridge.cs` lines 328-340 both need the same guard. Pseudo-diff:

```csharp
// BEFORE
if (drift > seekThresholdForSource)
{
    isSeek = true;
}

// AFTER
bool positionMoved = Math.Abs(posAdvanceMs) > 100;  // == position changed by >100 ms
if (positionMoved && drift > seekThresholdForSource)
{
    isSeek = true;
}
```

Semantic: a "seek" requires position to have **moved unexpectedly** -- either forward by more than wall-elapsed (jump forward) or backward (jump backward). Position **stuck at the same value** while wall clock advances is a stale-source signal, not a seek; the previous `startedAt` is already correct and should not be disturbed.

This eliminates the per-tick false-positive cleanly. Real user-driven seeks (where position genuinely jumps to a new value) still fire isSeek=true. Genuine pause/resume is unaffected (`isPaused` handled in a separate B8 branch, not B7).

### 8.2 Server-side belt-and-braces: B7 drift7 sanity threshold

Even if a stale isSeek=true sneaks through (e.g. from a future source that triggers it differently), `WebhookHandler.cs` line 315 could ignore "seeks" with drift7 below a minimum:

```csharp
// BEFORE
if (isSeek && ct2["isPaused"]?.GetValue<bool>() != true && !b7Cooldown)
{
    ...
    ct2["startedAt"] = JsonValue.Create(nowMs - positionMs);
}

// AFTER
if (isSeek && ct2["isPaused"]?.GetValue<bool>() != true && !b7Cooldown)
{
    long expectedPos = nowMs - ((long?)ct2["startedAt"]?.GetValue<long>() ?? nowMs);
    long drift7 = Math.Abs(expectedPos - positionMs);
    if (drift7 < 500)
    {
        // == client and server already agree within 500 ms; this "seek"
        //    is the stale-SMTC false positive, not a real jump. Skip.
        return;
    }
    ct2["startedAt"] = JsonValue.Create(nowMs - positionMs);
    ...
}
```

This is a less-invasive change but addresses the symptom rather than the cause. Recommended as defense-in-depth only -- the primary fix should be at the tray side where the false `isSeek=true` is generated.

### 8.3 Optional: client-side stale-startedAt detection

`overlay.html` could detect "startedAt has not actually moved (in relative terms) for >N seconds despite isPlaying=true" and fall back to **its own** local-tick counter, ignoring further server updates until a real change occurs. This is the heaviest of the three fixes and not strictly needed if 8.1 lands -- only useful if there are *other* sources of frozen-startedAt the tray can't catch.

### 8.4 Recommended order

1. Land **8.1** (surgical, eliminates root cause, ~10 LOC across two files).
2. Add **8.2** as a defensive check in the same fix commit (~5 LOC).
3. Defer **8.3** unless 8.1 + 8.2 are not enough -- evaluate after live run.

## 9. Verification protocol for the eventual fix

Once a fix lands, the operator should:

1. Open SoundCloud-RPC (or any Electron MediaSession source confirmed to publish stale TimelineProperties.Position) and play a track.
2. Observe overlay.html in OBS (or customize.html preview pane). Current-position counter MUST advance approximately in step with wall-clock for the duration of the track.
3. Compare overlay value to source player value at the 10 s, 60 s, 180 s marks. Maximum acceptable drift: ±2 s (jitter from heartbeat cadence + SSE delivery).
4. Live seek the track to a new position (e.g. drag the SoundCloud progress bar to 0:30). Overlay MUST snap to the new position within 1 s. *This validates that real seeks still work after the false-positive fix.*
5. Pause and resume the track. Overlay MUST freeze on pause and resume from the same position. *This validates pause handling is unaffected.*
6. Check `overlay.log` for `[Heartbeat] seek` entries during normal playback. Pre-fix: ~3-4 Hz steady spam. Post-fix: should be approximately 0 Hz unless a real seek occurred.
7. Check `server.log` for `Seek (Ns jump) -- startedAt resynced` entries during normal playback. Pre-fix: ~3-4 Hz steady spam. Post-fix: should appear only on real user seeks.
8. Run for 10 minutes on a single track; overlay should reach a current-position value within 5 s of the track's actual `durationMs - 10s` (whatever's playing) at the 10-minute mark.

If all 8 pass, the fix is verified. The 2.9 GB `server.log` growth rate should also drop dramatically -- a meaningful side-effect indicator (currently ~5 lines per 250 ms; post-fix should be far less).

---

## Appendix A. Filesystem state at diagnosis time

| Path | Size | mtime | Notes |
|---|---:|---|---|
| `C:\Users\Master\AppData\Local\MastersFM\overlay.log` | 168 KB | 2026-05-20 14:30 | Tray log; used for sections 1, 5. |
| `C:\Users\Master\AppData\Local\MastersFM\server.log` | 2 910 MB | 2026-05-20 14:30 | Server log; sections 1, 3, 5.3 -- only tailed (file too large to read fully). The size itself is a symptom: the `[Program] Seek ... resynced to pos=Ns` line fires at 3-4 Hz, which is ~250-300 KB/min. The log will fill the disk in roughly 1-2 days at this rate. |
| `C:\Users\Master\AppData\Local\MastersFM\audio_spectrum.log` | 58 KB | 2026-05-20 14:33 | Spectrum log -- normal cadence; not implicated. |
| `C:\Users\Master\AppData\Local\MastersFM\customize.log` | 6 KB | 2026-05-20 14:25 | Customize webview log; no time entries. |

## Appendix B. Files read during diagnosis (no edits)

- `src/overlay.html` (lines 735, 1946, 1984, 2011-2021, 2133, 2155, 2379-2381, 2473, etc.)
- `src/customize.html` (lines 514, 521, 1680, 2119 -- iframe-wrap verification only)
- `src/server_dotnet/WebhookHandler.cs` (lines 30-50, 111-115, 161-205, 295-395)
- `src/tray_csharp/Services/HeartbeatService.cs` (lines 14, 95-145)
- `src/tray_csharp/Detectors/SmtcEventBridge.cs` (lines 320-365, 450-465)
- `src/tray_csharp/Services/WebhookClient.cs` (line 100)
- `src/tray_csharp/Detectors/TrackUpdate.cs` (line 23)

No file was modified.

## Appendix C. Verification of "diagnosis only" constraint

| Action | Performed? |
|---|---|
| Code change | NO |
| File write to `src/**` | NO |
| File write to `md/**` | NO |
| Process restart | NO |
| `_full_rebuild.ps1` run | NO |
| Log rotation / clear | NO |
| Server endpoint mutation (non-GET / non-`/current`) | NO (only `curl GET /current` -- read-only) |
| New files created | `V14_S7_14_DIAG_OVERLAY_TIME_FROZEN.md` (this file) -- the diagnostic deliverable itself |

---

*Diagnosis complete. Awaiting operator commission of a fix brief.*
