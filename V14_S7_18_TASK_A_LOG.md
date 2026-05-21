# Stage 7.18 Task A -- Start-on-login default fix (running log)

**Brief:** `CLAUDE_CODE_INSTRUCTIONS.md` (Stage 7.18, ~22 KB / 556 lines; Task A spans STEPs 0-4)
**Baseline HEAD:** `fe07049` (Stage 7.17 closure -- v14.0.0 cycle closed)
**Author:** Ruflo (Claude) -- autonomous brief execution
**Started:** 2026-05-21

---

## STEP 0 -- Checkpoint + diagnosis

### S0.0 Pre-conditions (all PASS)

| Check | Expected | Observed |
|---|---|---|
| HEAD | `fe07049` | `fe07049ff65ce40e3dfeee69391770d209f0667f` |
| `version.json` version | `14.0.0` | `14.0.0` |
| Installed DLL `ProductVersion` | `14.0.0+718e3e1...` | `14.0.0+718e3e17bbf180834ed46fcaa2833c1e719715e9` |
| Protected files SHA256 | match baseline | all 4 MATCH |

### S0.1 Disk backup

```
G:\Project Folder\Master FM\_BACKUPS_2026-05-21_18-11_S7_18_PRE\
  src_snapshot\      <-- full src/ tree (29 top-level items)
  head_sha.txt       <-- fe07049ff65ce40e3dfeee69391770d209f0667f
```

### S0.2 Where Start-on-login default lives

#### Architecture

Start-on-login uses the **Windows Startup folder shortcut** mechanism (NOT the `HKCU\...\Run` registry key):

- **Implementation:** `src/tray_csharp/Services/AutoStartService.cs` (200 lines)
- **Interface:** `src/tray_csharp/Services/IAutoStartService.cs`
- **Shortcut path:** `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Master's FM.lnk`
- **`IsEnabled`** -> returns `File.Exists(_lnkPath)`
- **`Enable()`** -> creates the `.lnk` via `WshShell` COM with target = `MastersFM.exe` (launcher)
- **`Disable()`** -> deletes the `.lnk`
- **`Toggle()`** -> calls Enable or Disable based on current state

WiX installer: **does NOT** create the shortcut. No `RegistryValue` element, no `Shortcut` element targeting Startup folder. The first-run logic in `App.xaml.cs` is responsible.

#### Default-on first-run logic

`src/tray_csharp/App.xaml.cs:278-296`:

```csharp
// Default autostart ON for v14 rc3+. Uses a fresh flag key
// (autostart_defaulted_v14rc3) so it re-applies once for every user,
// including those whose earlier v14 default-on never produced a
// working shortcut.  Flag written once; subsequent runs honour the
// user's explicit choice.
try
{
    var defaulted = _configService?.GetValue<bool>("autostart_defaulted_v14rc3", false) ?? false;
    if (!defaulted)
    {
        if (!_autoStartService.IsEnabled)
        {
            _autoStartService.Enable();
            _logger.Log("AutoStart defaulted ON (v14 rc3 first run)", "AutoStart");
        }
        _configService?.SetValue("autostart_defaulted_v14rc3", true);
    }
}
catch (Exception ex) { _logger.LogErr("AutoStart default-on", ex, "AutoStart"); }
```

Intent: on first launch of v14 rc3+ tray, if autostart isn't already on, enable it AND set the flag so it won't keep re-firing on every launch (preserving user's later choice to disable).

#### Tray menu wiring

- `src/tray_csharp/ViewModels/TrayMenuViewModel.cs:79`: `_isAutoStartEnabled = _autoStartService.IsEnabled;` (initial read in constructor)
- `src/tray_csharp/ViewModels/TrayMenuViewModel.cs:130`: `_autoStartService.StateChanged += (_, enabled) => IsAutoStartEnabled = enabled;` (live update on Enable/Disable)
- `src/tray_csharp/ViewModels/TrayMenuViewModel.cs:220-226`: `ToggleAutoStart` command calls `_autoStartService.Toggle()`
- `src/tray_csharp/MainWindow.xaml:178`: `<MenuItem IsChecked="{Binding IsAutoStartEnabled, Mode=OneWay}" ...>` (menu visual reads VM state)

So the menu's checkmark accurately reflects `.lnk` file existence in the Startup folder.

### S0.3 Root cause -- Case C (read-path / one-time flag never resets)

Read the operator's machine's actual config at `%APPDATA%\Roaming\MastersFM\config.json`:

```json
{
  "autostart_user_optout": false,
  "autostart_defaulted_on_v199": true,
  "autostart_defaulted_on_v14": true,
  "welcome_seen_version": "14.0.0-rc.3",
  "welcome_seen": true,
  "autostart_defaulted_v14rc3": true
}
```

Three generations of default-on flags are all `true`:
- `autostart_defaulted_on_v199` (v1.9.9 era)
- `autostart_defaulted_on_v14` (v14.0.0 alpha era)
- `autostart_defaulted_v14rc3` (v14 rc3 era -- the current gate)

The Stage 7.17 dual-build install at 2026-05-21 00:41:54 launched the tray. `AutoStartService.IsEnabled` reported `False` (no `.lnk` existed). The default-on block at App.xaml.cs:283-296 ran but was SHORT-CIRCUITED by `defaulted = true` (the existing rc3 flag in config). Enable() was never called. Menu showed UNCHECKED.

Confirmed via tray log `C:\Users\Master\AppData\Local\MastersFM\overlay.log`:

```
[2026-05-21 00:41:55.033] [TRAY-CS] [AutoStart] AutoStartService initialized; lnk=...\Master's FM.lnk; initial state=False
[2026-05-21 00:41:55.033] [TRAY-CS] [AutoStart] initial state=False
   <-- no "AutoStart defaulted ON (v14 rc3 first run)" log line -->
   <-- no subsequent Enable() call -->
[2026-05-21 16:53:06.306] [TRAY-CS] [Tray] TrayMenu: AutoStart toggle -> True
[2026-05-21 16:53:06.382] [TRAY-CS] [AutoStart] enabled; lnk=... -> target=...\MastersFM.exe
```

The 16:53:06 line is the operator manually toggling autostart on via the tray menu -- ~16 hours AFTER the install. Default-on did NOT happen automatically.

The same scenario applies to new users in two flavors:

**Flavor A: brand-new user with no prior config**
- No `.lnk` exists
- No flags in config
- App.xaml.cs:283-296 fires: defaulted=false, IsEnabled=false, Enable() runs, `.lnk` created, flag set true
- Menu shows CHECKED on first open
- THIS WORKS CORRECTLY (no fix needed for this case).

**Flavor B: returning user upgrading from rc3 to v14.0.0** (the operator's case + anyone who tested rc3)
- No `.lnk` (it was deleted during install OR user disabled it OR Enable() in rc3 failed silently)
- `autostart_defaulted_v14rc3` already `true` from rc3
- App.xaml.cs:283-296 fires: defaulted=true, block SHORT-CIRCUITS
- Enable() NEVER runs
- Menu shows UNCHECKED forever (until user manually toggles)
- THIS IS THE BUG.

This matches **Case C** from the brief: "Default value exists somewhere but the read path doesn't honor it -> fix the read path." Specifically: the read path's one-time flag is keyed to a stale generation (rc3). v14.0.0 introduces a new generation that should re-apply the default-on once.

### S0.4 Recommended fix shape

**Smallest possible diff:** bump the flag key from `autostart_defaulted_v14rc3` to `autostart_defaulted_v14_0_0`. Two-line change in App.xaml.cs (GetValue + SetValue), plus update the comment at lines 278-282 to reflect the new generation. **One file edited.**

Exact proposed diff:

```diff
- // Default autostart ON for v14 rc3+. Uses a fresh flag key
- // (autostart_defaulted_v14rc3) so it re-applies once for every user,
- // including those whose earlier v14 default-on never produced a
- // working shortcut.  Flag written once; subsequent runs honour the
+ // Default autostart ON for v14.0.0+. Uses a fresh flag key
+ // (autostart_defaulted_v14_0_0) so it re-applies once for every user,
+ // including those who upgraded from rc3 and never got the working
+ // shortcut.  Flag written once; subsequent runs honour the
  // user's explicit choice.
  try
  {
-     var defaulted = _configService?.GetValue<bool>("autostart_defaulted_v14rc3", false) ?? false;
+     var defaulted = _configService?.GetValue<bool>("autostart_defaulted_v14_0_0", false) ?? false;
      if (!defaulted)
      {
          if (!_autoStartService.IsEnabled)
          {
              _autoStartService.Enable();
-             _logger.Log("AutoStart defaulted ON (v14 rc3 first run)", "AutoStart");
+             _logger.Log("AutoStart defaulted ON (v14.0.0 first run)", "AutoStart");
          }
-         _configService?.SetValue("autostart_defaulted_v14rc3", true);
+         _configService?.SetValue("autostart_defaulted_v14_0_0", true);
      }
  }
  catch (Exception ex) { _logger.LogErr("AutoStart default-on", ex, "AutoStart"); }
```

5 line edits, 1 file (`src/tray_csharp/App.xaml.cs`). Matches the established pattern (per the existing comment) of bumping the flag for each generation. Same trade-off as before: users who EXPLICITLY DISABLED autostart on rc3 will see it re-enabled on v14.0.0 install; this trade-off was already accepted by the original author per the comment ("re-applies once for every user, including those whose earlier v14 default-on never produced a working shortcut").

### S0.5 Alternatives considered (rejected for smallest-diff preference)

| Alternative | Diff size | Why rejected |
|---|---|---|
| Remove flag-gating entirely; check `autostart_user_optout` instead | 2 files (App.xaml.cs + TrayMenuViewModel.cs to wire `autostart_user_optout` on Toggle) | More invasive; introduces a NEW config flag with new semantics; would require wiring tray-menu Toggle to write the opt-out flag |
| Add a WiX `Shortcut` element creating `.lnk` on install | 1 file (`MastersFM.wixproj` / `.wxs`) | WiX-managed shortcuts get DELETED on MSI uninstall, which would interfere with the operator's preserved-state expectation; also breaks the "user disables, .lnk goes, user reinstalls" flow |
| Add a `HKCU\Run` registry write at install | 1 WiX file | Same uninstall-cleanup concerns; also changes the mechanism (Startup folder -> Run key), a bigger conceptual change |

### S0.6 Commit

Files staged: `V14_S7_18_TASK_A_LOG.md` only. No source edits in STEP 0.

Commit message: `Stage 7.18: STEP 0 -- start-on-login default diagnosis (Task A pre-fix)`
