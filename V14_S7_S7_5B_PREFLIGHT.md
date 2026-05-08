# V14_S7_S7_5B_PREFLIGHT.md

Stage 7.5B pre-flight (STEP 0 outputs).

## 0.1-0.2 CWD + processes
`G:\Project Folder\Master FM\` confirmed. PS tray NOT running at brief
launch. soundcloud-rpc background process WAS running (discovered
during STEP 3 -- it's the source of the live SMTC session that the C#
tray detected).

## 0.3 sha256 baseline
See `V14_S7_S7_5B_PROTECTED_BASELINE.md`.

## 0.4 LIVE log snapshot
`V14_S7_S7_5B_LIVE_LOG_SNAPSHOT.txt` (6,229 bytes / 52 lines; covers
prior 7.5 smoke; PS tray not running data).

## 0.5 Repo state
HEAD = `a9ec49e` (Stage 7.5 memory.md APPEND). All Stage 7 commits
preserved. Tag v14.0.0-rc.1 at 44723fb.

## 0.6 References read
All listed; SmtcEventBridge.cs current implementation reviewed for
the reflection-based AcquireSmtcManagerSync method that needs
replacement with direct WinRT usage.

## 0.7 Default decisions
- Q1 TFM choice: `net8.0-windows10.0.19041.0` (Windows 10 2004)
- Q2 webhook byte-equivalence vs PS: NOT VERIFIED (PS not running)
- Q3 Discord RPC during soak: respect current config (Discord enabled per default)
- Q4 TFM rollback path: `net8.0-windows10.0.17763.0` documented (not tested)
- Q5 B-001 verification during 10-min soak: not formal target; observe and document
