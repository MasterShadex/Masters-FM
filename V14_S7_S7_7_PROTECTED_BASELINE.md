# V14_S7_S7_7_PROTECTED_BASELINE.md

Stage 7.7 protected-file sha256 baseline (STEP 0.3, 2026-05-08 15:58).

| File | sha256 | Expected at STEP 14 |
|---|---|---|
| `src\tray.ps1` | `19011F0BD093CEA51CB34D053209F33FB3A37DE673777BAB34B5F8F26609533F` | UNCHANGED |
| `src\tray_native\tray_native.cs` | `6B9804A1AB70000652A2754E886BE3F05167F40EC136EB2CC6CDD62D8EFA9148` | UNCHANGED |
| `src\launcher.cs` | `291ED4C92B9BEA391BA9204323EA41BA60AD7903AF6E6D7BA9404E1056E0BD9D` | UNCHANGED |
| `src\server.js` | `C15ED9310CB33044A090878918DC2B89B3FB843901BA0F199D3092EF502A16AF` | UNCHANGED |
| `md\memory.md` | `9512DD8583396EBEC40E51042E7B79327C1FE85914A720F6457CC0C5EF55D92C` | CHANGED (per STEP 13.1 APPEND) |

---

## Process state at STEP 0

| Process | PID | Notes |
|---|---|---|
| `MastersFM_Tray.exe` (PS tray) | -- | NOT running |
| `MastersFM_Tray_v14.exe` (C# tray) | -- | NOT running |

Clean slate.

## Repo state at STEP 0

- HEAD = `8f5b932` (Stage 7.5C memory APPEND)
- All prior Stage 7 commits preserved (7.1, 7.1B, 7.3, 7.4, 7.2, 7.9, 7.5, 7.5B, 7.5C)
- Tag `v14.0.0-rc.1` at `44723fb` (untouched)

---

## Existing tray_csharp surface inventory

| Path | Status |
|---|---|
| `App.xaml`, `App.xaml.cs` | MODIFY (DI registrations + first-run logic) |
| `MainWindow.xaml`, `MainWindow.xaml.cs` | UNCHANGED |
| `Logger.cs`, `MastersFM_Tray_v14.csproj` | csproj receives EmbeddedResource line (1 minor item; per brief STEP 3.3 explicit allowance) |
| `Detectors/` | `SmtcEventBridge.cs` MODIFY (art extraction); other detectors UNCHANGED |
| `Services/` | `DiagnosticHeartbeat.cs` MODIFY (gc/priv/polls expansion); `TrackResolver.cs` MODIFY (ArtLruCache.Touch wiring); other services UNCHANGED |
| `Dialogs/` | NEW: `IDialogService.cs`, `DialogService.cs`, 5 dialog windows |
| `ViewModels/` | NEW: 6 viewmodels (Welcome, AudioDevice, Platforms, SetupWizard, About, ErrorDialog) |
| `Update/`, `Discord/`, `Tray/` | UNCHANGED |
