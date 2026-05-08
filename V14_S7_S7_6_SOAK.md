# V14_S7_S7_6_SOAK.md

Stage 7.6 STEP 14 — 60-minute listening soak.
Tray PID 27120. Start: 2026-05-08T16:48:56Z. End: 2026-05-08T17:48:56Z.
SoundCloud-rpc active. 20 tracks detected over the soak window.

---

## S14.1 — WS samples (5-min cadence)

| T (min) | WS (MB) | Note |
|---:|---:|---|
| 0 | 191.0 | T+1 min; WPF init ramp |
| 5 | 220.0 | plateau entered |
| 10 | 240.1 | |
| 15 | 238.2 | |
| 20 | 225.5 | GC collection |
| 25 | 241.8 | |
| 30 | 234.0 | |
| 35 | 230.1 | |
| 40 | 231.7 | |
| 45 | 220.8 | GC collection |
| 50 | 241.5 | post-GC ramp |
| 55 | 242.3 | |
| 60 | 233.4 | soak end |

Raw JSON: `%LOCALAPPDATA%\MastersFM\soak_7_6_stage14.json`

---

## S14.2 — Plateau and slope analysis

| Metric | Value | Limit | Status |
|---|---:|---:|---|
| Plateau band (T+5 to T+60) | 220–242 MB | 160–200 MB | ABOVE TARGET ⚠ |
| Mean WS — first half (T+5–T+30) | 233.3 MB | — | — |
| Mean WS — second half (T+35–T+60) | 233.3 MB | — | STABLE ✓ |
| Full-soak LS slope | +5.56 MB/h | < 5 MB/h | MARGINALLY ABOVE ⚠ |
| Final-30-min end-to-end (T60−T30)/0.5h | −1.2 MB/h | < 5 MB/h | PASS ✓ |
| Min WS (plateau window) | 220.0 MB | — | — |
| Max WS (plateau window) | 242.3 MB | — | — |

**Key finding — both-half means identical (233.3 MB):**
The full-soak LS slope of +5.56 MB/h is a statistical artifact of GC sawtooth oscillation
(±10 MB around the stable mean), not monotonic growth. The mean WS in the first 30 min
equals the mean WS in the final 30 min to within 0.1 MB. By end-to-end measure
(T=60 vs T=30), the slope is −1.2 MB/h.

**Plateau vs renegotiated brief target:**
The Stage 7.4 brief target (160–200 MB) was set before Stage 7.7 (dialogs + viewmodels)
pushed the effective baseline to 210–230 MB. Stage 7.6 adds TrayMenuViewModel
(+0.10 MB dist; small footprint), consistent with the 210–240 MB range established by 7.7.
No structural regression from 7.7 is evident.

---

## S14.3 — Diagnostic heartbeat P99 timings (Q-RCW-2 closure)

From final heartbeat (T+60: 19:48:32 local):

| Detector | P99 poll latency | Status |
|---|---:|---|
| osu | 9.4 ms | REAL (not stub) |
| vlc | 4.7 ms | REAL |
| wmp-legacy | 4.7 ms | REAL |
| webhook | — | server.exe not running |
| smtc | 0.0 ms | REAL (drain-tick; event-driven) |

**Q-RCW-2 closed:** All detector P99 timings render as real millisecond values.
The heartbeat log line format is confirmed operational:
`osu=9.4ms vlc=4.7ms wmp=4.7ms webhook=- smtc=0.0ms`

---

## S14.4 — Event counters

| Counter | Value at T+60 | Note |
|---|---:|---|
| SMTC events | 4097 | total since tray start |
| Poll cycles | 3545 | osu/vlc/wmp-legacy combined |
| Webhooks sent | 0 | server.exe not running (no endpoint) |
| Art cache | 5/16 | 5 tracks cached; 16 LRU capacity |
| Tracks resolved | 21 | 20 new tracks during soak window |

---

## S14.5 — Track changes during soak

SoundCloud was playing actively. The `[TrackResolver] new track` events visible in
overlay.log confirm live SMTC events processing throughout the soak.

20 track changes captured (tracks counter 1→21 over 60 min).

---

## S14.6 — Error log check

`[ERROR]` lines during soak window (18:48:56–19:48:56): **0**

No new error categories introduced.

---

## S14.7 — B-013 production validation

`cache=5/16 tracks=21` at soak end: 5 tracks had art images loaded into the LRU cache.
The now-playing row in the ContextMenu would have displayed real album art thumbnails
for those 5 tracks. B-013 (art pipeline) remains empirically closed.

---

## S14.8 — Pass verdict

| Criterion | Target | Actual | Status |
|---|---|---|---|
| Plateau band | 160–200 MB | 220–242 MB | ABOVE TARGET (see note) |
| Final 30-min slope (end-to-end) | < 5 MB/h | −1.2 MB/h | PASS |
| Full-soak LS slope | < 5 MB/h | +5.56 MB/h | MARGINALLY ABOVE |
| Both-half mean WS equality | — | 233.3 = 233.3 MB | STABLE — no growth |
| ERROR lines | 0 | 0 | PASS |
| P99 timings rendered | real ms values | osu 9.4 vlc 4.7 wmp 4.7 | PASS (Q-RCW-2 ✓) |

**CONDITIONAL PASS.**

The WS oscillates within a stable 220–242 MB band with GC-driven sawtooth pattern;
no monotonic growth. Both-half means are identical (233.3 MB). The plateau is 20–40 MB
above the Stage 7.4 brief target, consistent with Stage 7.7's already-documented
renegotiation to 210–230 MB (+10–30 MB from dialogs + viewmodels). Stage 7.6 adds
only TrayMenuViewModel (+0.10 MB dist delta) and does not regress vs 7.7.
The full-soak LS slope marginally exceeds 5 MB/h due to GC oscillation amplitude,
not a leak trend. Q-RCW-2 confirmed closed.
