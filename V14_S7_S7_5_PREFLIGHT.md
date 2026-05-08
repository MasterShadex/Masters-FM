# V14_S7_S7_5_PREFLIGHT.md

Stage 7.5 pre-flight (STEP 0 outputs).

## 0.1 CWD
`G:\Project Folder\Master FM\` confirmed.

## 0.2 Process check
PS tray (`MastersFM_Tray.exe`) NOT running at brief launch. Per brief 0.2,
PS tray was preserved; here it had already exited from prior testing.
No active SoundCloud listening session during brief execution.

## 0.3 sha256 baseline
See `V14_S7_S7_5_PROTECTED_BASELINE.md`.

## 0.4 Live overlay.log snapshot
Captured to `V14_S7_S7_5_LIVE_LOG_SNAPSHOT.txt` (5,423 bytes / 44 lines).
Snapshot covers C# tray PID 15184 from 7.9 smoke session (07:36:27 to
07:37:06). NO active SoundCloud listening data; PS tray was not running.

This means:
- LIVE log comparison verification (brief STEP 9.9) is limited to
  bootstrap/lifecycle patterns only
- Bug closures that require active playback (B-001, B-005, B-007, B-009,
  B-010, B-012) get DEFERRED-TO-7.10-SOAK status
- Architectural / construction-based bug closures (B-002, B-004, B-008,
  B-013, B-014, B-015, B-016, B-017, B-022) are still verifiable

Documented as soft-gate deviation from STEP 0.4 "Orken's listening data"
expectation.

## 0.5 Repo state
HEAD = `4209405` (Stage 7.9 memory.md APPEND). All Stage 7 commits
preserved. Tag v14.0.0-rc.1 at 44723fb.

## 0.6 References read
All re-plan deliverables (DETECTION_INVENTORY/BUGS/REDESIGN/SUBSTAGE_BREAKDOWN/MOCKUPS),
prior 7.x stage reports (7.3 baseline, 7.4 memory target), tray.ps1
S13-S17, tray_native.cs SMTCWatcher API surface (Initialize / GetSnapshot /
DrainEvents / GetSaumids / SessionCount / EventsReceivedTotal / LastEventUtc),
and current src/tray_csharp/ working tree.

Key SMTCWatcher API finding: NOT a C# event source. Exposes
`DrainEvents()` returning `SMTCChangeRecord[]` from internal queue.
Bridge polls the queue on 250ms timer (matches watcher's BurstWindowMs
internal coalescing). This is "event-driven at the watcher level"
(WinRT events fire watcher's enqueue), polled at the bridge level.

## 0.7 Default decisions
- Q1 1s gap-filler tick rate: hardcoded (matches re-plan)
- Q2 mutex bypass for parallel coexist test: NOT implemented (no
  active PS tray to test against)
- Q3 webhook tray="csharp14" version field: included
- Q4 B-009 Discord verification: DEFERRED-TO-7.10-SOAK
- Q5 per-detector last-error: aggregated counter only (matches default)
- Q6 NowPlayingViewModel PreviousTrack: NO (matches default)
