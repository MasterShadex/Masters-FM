# V14_S7_S7_5C_PREFLIGHT.md

Stage 7.5C autonomous overnight research pre-flight (STEP 0).

## Time budget

- Brief launch: 2026-05-08 ~08:59 (right after 7.5B closed)
- Hard MINIMUM: 4h wall-clock (do not shut down before ~12:59)
- Hard CAP: 6h wall-clock (~14:59)

## 0.1 -- 0.2 CWD + processes

CWD: `G:\Project Folder\Master FM\` confirmed.

Process state captured in `V14_S7_S7_5C_PROTECTED_BASELINE.md`:
- C# tray NOT running
- PS tray NOT running
- SoundCloud Electron desktop app running (13192 main + 11 helpers)
- soundcloud-rpc bridge running (PID 29680) -- this is what feeds
  SMTC sessions for the C# tray to subscribe to

## 0.3 sha256 baseline

5 protected files snapshotted in
`V14_S7_S7_5C_PROTECTED_BASELINE.md`. Re-verified at STEP 10.

## 0.4 SoundCloud playback state

soundcloud-rpc is running. The Electron SoundCloud window has Orken's
auto-play playlist loaded and is producing audio that gets reflected
into SMTC. Identical context to 7.5B's 13-min soak (which produced
the ~216 MB/h growth observation).

## 0.5 Repo state

- HEAD = `a07c171`
- Stage 7.5B commits both present (`326059a` + `a07c171`)
- All prior Stage 7 commits preserved
- `v14.0.0-rc.1` tag at `44723fb` (untouched)

## 0.6 References reviewed

- Brief `CLAUDE_CODE_INSTRUCTIONS.md` (full)
- `V14_S7_S7_5B_FINAL_REPORT.md`, `_SOAK_RESULTS.md`,
  `_LIVE_OBSERVATIONS.md` (just authored at end of 7.5B; in-context)
- `V14_S7_REPLAN_DETECTION_BUGS.md` (full read; B-002 / B-004 / B-008
  / B-016 architectural lessons captured)
- `V14_S7_REPLAN_WPF_LOCK.md` (size noted; will read in detail before
  STEP 5)
- `src/tray_native/tray_native.cs` (full read; SMTCWatcher source)
- `src/tray_csharp/Detectors/SmtcEventBridge.cs` (full read)
- `md/memory.md` (CHANGELOG; v11.0.0-v12.0.1 detection-layer arc)

## 0.7 Default decisions

Per brief OPEN QUESTIONS PRE-EXECUTION:

- Q1 INCONCLUSIVE-soak follow-up: **default yes** (run second soak iteration)
- Q2 Multiple RCW bugs: **default fix simplest, document the rest**
- Q3 Bundling waste >5 MB: **default document, do NOT trim**
  (out of locked-list)
- Q4 Framework recommendation conclusion: **document only; no execution**
- Q5 Time-budget cutoff at 5h45m: **default stop, finalize commits**
- Q6 Genuine unsafe situation (Bitdefender flag, broken SDK):
  **default HALT with halt report**

## Workstream plan

**Workstream 1** (60-90 min initial soak + conditional RCW investigation)
- Launch C# tray
- Sample at t=0,5,10,15,20,25,30,35,40,45,50,55,60,75,90 min
- Decision tree at t=60-90:
  - <5 MB/h growth: PLATEAU; skip RCW
  - 5-50 MB/h: INCONCLUSIVE; second iteration
  - >50 MB/h: LEAK CONFIRMED; RCW investigation

**Workstream 2** (parallel during soak: dist audit + framework
recommendation document; NO execution)

**Workstream 3** (variant soaks to fill remaining wall-clock to 4h)

## Sampling strategy

Tray's `DiagnosticHeartbeat` writes to
`%LOCALAPPDATA%\MastersFM\overlay.log` at 60s cadence with the format:

```
[ts] [INFO ] [TRAY-CS] [Diagnostic] heartbeat: ws=XXX,YMB threads=XX
  handles=XXXX ring=20 events=XXX polls=XXX webhooks=X cache=X/X tracks=X
```

Periodic checks parse the log to extract the timeseries; no need to
poll the process directly.

## Em-dash + BOM constraints

- All deliverable docs UTF-8 no BOM
- All authored content uses `--` instead of em-dash (verify via
  PowerShell `-match '[—–]'` on every file before commit)
- If `tray_native.cs` is modified: same rules; the existing file uses
  em-dash characters in comments at lines 1, 8-12, 19-21, 28-29,
  41-42, 50-52, 99-100, 210-225 etc. -- those are pre-existing and
  out of scope; ANY new content I add must use ASCII hyphens

## sha256 / BOM verification cadence

- STEP 0.3 (now): baseline captured
- STEP 9: pre-commit verification
- STEP 10: post-commit final verification
