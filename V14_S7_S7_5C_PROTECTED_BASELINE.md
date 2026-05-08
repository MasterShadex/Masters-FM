# V14_S7_S7_5C_PROTECTED_BASELINE.md

Stage 7.5C protected-file sha256 baseline (STEP 0.3, 2026-05-08 08:59).

| File | sha256 | Expected at STEP 10 |
|---|---|---|
| `src\tray.ps1` | `19011F0BD093CEA51CB34D053209F33FB3A37DE673777BAB34B5F8F26609533F` | UNCHANGED |
| `src\tray_native\tray_native.cs` | `6B9804A1AB70000652A2754E886BE3F05167F40EC136EB2CC6CDD62D8EFA9148` | UNCHANGED unless RCW fix lands (CONDITIONAL) |
| `src\launcher.cs` | `291ED4C92B9BEA391BA9204323EA41BA60AD7903AF6E6D7BA9404E1056E0BD9D` | UNCHANGED |
| `src\server.js` | `C15ED9310CB33044A090878918DC2B89B3FB843901BA0F199D3092EF502A16AF` | UNCHANGED |
| `md\memory.md` | `02DEFA70A601841F3F2B3B9EAD664D608AEF15B65CF01C311FB4167074A3E9D8` | CHANGED (per STEP 7B APPEND) |

The brief unlocks `tray_native.cs` CONDITIONALLY -- only if the RCW
audit identifies a clear bug fixable in <30 lines. Documented
prominently if changed.

---

## Process state at STEP 0

| Process | PID | Notes |
|---|---|---|
| `MastersFM_Tray.exe` (PS tray) | -- | NOT running |
| `MastersFM_Tray_v14.exe` (C# tray) | -- | NOT running |
| `SoundCloud` (Electron desktop app) | 13192 (main) + 11 helpers | Running; 1.19 GB main process WS (normal Electron) |
| `soundcloud-rpc` (SMTC bridge) | 29680 | Running (MainWindowTitle="soundcloud-rpc") |
| `AudientAppLauncher` | 10992 | Audio interface software; unrelated |

soundcloud-rpc IS running which means SMTC sessions will populate as
soon as the C# tray initializes. Same setup as 7.5B's 13-min soak
(which observed the ~216 MB/h growth).

---

## Repo state at STEP 0

- HEAD = `a07c171` (Stage 7.5B memory APPEND)
- Includes `326059a` (Stage 7.5B skeleton)
- All prior Stage 7 commits preserved
- Tag `v14.0.0-rc.1` at `44723fb` (untouched)

---

## Dist state

- `dist/tray_csharp_release/MastersFM_Tray_v14.exe` 163,840 bytes (8 May 08:20 build from 7.5B)
- 21 files in dist; `Microsoft.Windows.SDK.NET.dll` 24.9 MB dominant
- Total dist size: 35.34 MB (per 7.5B audit)

---

LIVE log snapshot before this brief: `overlay.log` last modified 08:33:27
(end of 7.5B soak). `smtc_watcher.log` last modified 08:32:45 (end of
7.5B). Both will receive new content during 7.5C soak iterations.
