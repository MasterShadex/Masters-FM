# V14_S7_S7_8_LOG.md

Stage 7.8 — OBS Integration + 7.6 leftovers + memory baseline reconciliation.

---

## STEP 0 — Backup checkpoint

| Field | Value |
|---|---|
| Backup dir | `_BACKUPS_2026-05-08_20-21_S7_8_PRE` |
| Timestamp | 2026-05-08T20:21 |
| HEAD at backup | `7d5856c` — Stage 7.6 STEP 17: final report |
| src_snapshot files | 901 |
| src_snapshot bytes | ~317.2 MB |
| src_snapshot.zip entries | 949 |
| src_snapshot.zip size | 110.57 MB |
| md_snapshot files | 8 |
| md_snapshot bytes | ~236.7 KB |
| ZIP integrity | OK (949 entries readable) |

Robocopy exit codes: src=1 (files copied), md=1 (files copied) — both SUCCESS.

---

## STEP 0 — Pre-condition verification

| Check | Expected | Actual | Status |
|---|---|---|---|
| git status clean except version.json | YES | version.json modified-unstaged; .claude/ internal tracking files | PASS |
| HEAD = `7d5856c` | YES | `7d5856c` | PASS |
| Tag `v14.0.0-rc.1` at `44723fb` | YES | confirmed | PASS |
| sha256 tray.ps1 | `19011F0B...` | `19011F0B...` | PASS |
| sha256 tray_native.cs | `6B9804A1...` | `6B9804A1...` | PASS |
| sha256 launcher.cs | `291ED4C9...` | `291ED4C9...` | PASS |
| sha256 server.js | `C15ED931...` | `C15ED931...` | PASS |
| sha256 memory.md | `1F8E8002...` (baseline) | `C3202132...` | INTENTIONAL DIFF — Stage 7.6 STEP 16 APPEND (114134 bytes) |
| `_full_rebuild.ps1` flag-off ($UseDotnet8TrayCs=$false) | REBUILD DONE OK | REBUILD DONE OK @ 20:27:14 | PASS |
| `_full_rebuild.ps1` flag-on ($UseDotnet8TrayCs=$true) | WPF tray built OK | `MastersFM_Tray_v14.exe 160KB; 36784.6KB total; REBUILD DONE OK` @ 20:31:03 | PASS |
| C# tray launches | PID starts; logs OnStartup | PID=15448; `[TRAY-CS] Application.OnStartup begin` logged; single-instance exit (PS tray running) | PASS |

All pre-conditions met. Memory.md SHA256 diff is intentional (Stage 7.6 STEP 16 APPEND committed as `de080ea`).

---

## STEP 7 — Dual-build regression verification

| Path | Result | EXE | Total dist |
|---|---|---|---|
| Flag-off ($UseDotnet8TrayCs=$false) | REBUILD DONE OK @ 20:50:29 | MastersFM_Tray.exe OK | n/a |
| Flag-on ($UseDotnet8TrayCs=$true) | REBUILD DONE OK @ 21:01:29 | MastersFM_Tray_v14.exe 160 KB | 36841.7 KB total |

### Binary delta vs Stage 7.6 end

| Artifact | 7.6 end | 7.8 post-STEP6 | Delta |
|---|---:|---:|---|
| MastersFM_Tray_v14.dll | 0.809 MB | 0.859 MB | **+50 KB (+6.2%)** |
| Total dist | 35.92 MB | 35.978 MB | +58 KB (+0.16%) |

DLL delta (+50 KB / +6.2%) is within the expected 30-80 KB range for OBS service + JSON wiring. Just above the 5% documentation threshold — noted here per brief S7 requirement. Total dist delta (+58 KB) is well within the +2 MB safety floor.

Verdict: **DUAL-BUILD REGRESSION VERIFICATION PASS**.

---

## STEP 8 — Dialog-cycle smoke regression

| Field | Value |
|---|---|
| Run timestamp | 2026-05-08T19:04:43Z — 2026-05-08T19:09:09Z (~4.4 min) |
| Smoke PID | 33940 |
| Exit code | 0 |
| Raw output | `%LOCALAPPDATA%\MastersFM\dialog_smoke_REGRESSION_20260508_190443.json` |
| PS tray state | Stopped before run; mutex free |

See `V14_S7_S7_8_SMOKE_REGRESSION.md` for full analysis.
