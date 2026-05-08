# V14_S7_S7_10_INTERRUPT_DIAGNOSIS.md

Stage 7.10 INTERRUPT -- SetupWizard defect diagnosis (Defects A + B)

---

## S1.1 -- First-run gate diagnosis (Defect A)

### Gate location

`src/tray_csharp/App.xaml.cs` `ScheduleFirstRunCheck()` (line ~297):

```csharp
bool seen = _configService.GetWelcomeSeen();
if (seen) { /* skip wizard */ return; }
// else: schedule SetupWizard show in 200ms
```

Gate reads: `IConfigService.GetWelcomeSeen()`.

### GetWelcomeSeen implementation

`src/tray_csharp/Services/ConfigService.cs` lines 209-220:

```csharp
public bool GetWelcomeSeen()
{
    var seenVersion = GetValue<string>("welcome_seen_version");
    if (string.IsNullOrEmpty(seenVersion)) return false;
    var currentVersion = GetCurrentAppVersion();
    return string.Equals(seenVersion.Trim(), currentVersion, StringComparison.Ordinal);
}
```

`GetCurrentAppVersion()` reads `AssemblyInformationalVersionAttribute`
(`"14.0.0-rc.1+stage7.1B.skeleton"`), strips the `+` suffix, returns
`"14.0.0-rc.1"`.

### Stored value in config.json

`%APPDATA%\MastersFM\config.json` (written by PS tray):
```json
"welcome_seen_version": "v14.0.0-rc.1"
```

The PS tray always prefixes version strings with `"v"`.

### Root cause A1 -- v-prefix mismatch

Ordinal comparison:
```
string.Equals("v14.0.0-rc.1", "14.0.0-rc.1", StringComparison.Ordinal)
```
= **FALSE** -- leading `"v"` breaks the match.
`GetWelcomeSeen()` returns `false` on every launch.
Wizard is scheduled 200ms after startup regardless of
`welcome_seen=true` in config.

### Root cause A2 -- Redundant ViewModel SetValue overwrites with full InformationalVersion

`src/tray_csharp/ViewModels/SetupWizardViewModel.cs` Finish path
(lines 96-101 -- only reachable IF Defect B is also fixed):

```csharp
_config.SetWelcomeSeen(true);        // correctly writes "14.0.0-rc.1"
var ver = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion ?? ...;    // "14.0.0-rc.1+stage7.1B.skeleton"
_config.SetValue("welcome_seen_version", ver); // OVERWRITES with full build string
```

`SetWelcomeSeen(true)` correctly writes `"14.0.0-rc.1"` via
`GetCurrentAppVersion()`. The immediately-following `SetValue` call
overwrites it with the raw `InformationalVersion` including the `+`
build-metadata suffix. On the next launch `GetCurrentAppVersion()`
returns `"14.0.0-rc.1"` (stripped), which does not equal
`"14.0.0-rc.1+stage7.1B.skeleton"` -- wizard re-shows.

### Flag identity

Gate reads: `welcome_seen_version` (string).
Wizard's success path writes: `welcome_seen_version` (same key) --
via `SetWelcomeSeen(true)` then overwritten by redundant `SetValue`.
Legacy `welcome_seen` bool is also written by `SetWelcomeSeen(true)`;
the gate does NOT read this bool (it reads only `welcome_seen_version`).

### Summary

Two compounding mismatches. A1: PS tray wrote `"v14.0.0-rc.1"`;
C# gate compares against `"14.0.0-rc.1"` (no `"v"`) -- ordinal
fail. A2: ViewModel Finish path redundantly overwrites the correctly
formatted value with the raw InformationalVersion including `+`
build metadata -- ordinal fail on subsequent launches. Either flaw
alone is sufficient to keep the wizard showing on every launch.

---

## S1.2 -- Wizard navigation buttons diagnosis (Defect B)

### XAML bindings (SetupWizardWindow.xaml footer)

| Button  | XAML Command binding     | XAML IsEnabled binding     |
|---------|--------------------------|----------------------------|
| Skip    | `SkipCommand`            | (always enabled)           |
| Back    | `BackCommand`            | `CanGoBack` (false step 1) |
| Forward | **`NextAsyncCommand`**   | `CanGoForward` (true)      |

### Generated command names (CommunityToolkit.Mvvm 8.4.2)

From the RelayCommand source generator: for async methods, the
`"Async"` suffix is stripped from the method name before appending
`"Command"`. The ViewModel declares:

| Method                                          | Generated property |
|-------------------------------------------------|--------------------|
| `[RelayCommand] private void Skip()`            | `SkipCommand`      |
| `[RelayCommand] private void Back()`            | `BackCommand`      |
| `[RelayCommand] private async Task NextAsync()` | **`NextCommand`**  |

### Root cause B

XAML binds the forward button to `NextAsyncCommand`.
CommunityToolkit.Mvvm 8.4.2 generates `NextCommand` (strips `Async`).
`NextAsyncCommand` does not exist as a property on the ViewModel.
WPF silently ignores the broken binding -- `Command` resolves to
`null` -- the button appears enabled (`CanGoForward=true`) but
click events produce no action.

Why Skip works: XAML `SkipCommand` matches generated `SkipCommand`.
Why Back appears non-functional on step 1: `CanGoBack=false`
(initial state) disables the button. Back's command binding
`BackCommand` is correct; the button would work on steps 2/3 if
the user could reach them.

---

## S1.3 -- Cut decision

Both defects have minimal fixes:

**Defect A:**
1. Fix `ConfigService.GetWelcomeSeen()`: normalize the stored version
   before ordinal comparison -- strip leading `"v"` (case-insensitive)
   to handle PS-tray-written values.
2. Fix `SetupWizardViewModel.NextAsync()` Finish path: remove the four
   redundant lines that re-read `InformationalVersion` and call
   `_config.SetValue("welcome_seen_version", ver)` directly.
   `SetWelcomeSeen(true)` already handles the write correctly.
3. Fix `SetupWizardViewModel.Skip()`: persist gate flag so Skip also
   clears the re-show gate (per brief S2.3 persistence semantics).
   Removes the "wizard re-shows after Skip" failure mode.

**Defect B:**
4. Fix `SetupWizardWindow.xaml`: change
   `Command="{Binding NextAsyncCommand}"` to
   `Command="{Binding NextCommand}"` (one line).

No new services, no new NuGets, no XAML surface changes.
Wizard surface (3-step Welcome/Audio/Platforms layout) is preserved.

---

## Evidence

| Item | Finding |
|---|---|
| `config.json` `welcome_seen_version` | `"v14.0.0-rc.1"` (v-prefix, PS tray) |
| `GetCurrentAppVersion()` return | `"14.0.0-rc.1"` (no v, no +) |
| `ConfigService.GetWelcomeSeen()` comparison | ordinal; fails on v-prefix |
| ViewModel Finish path extra SetValue | writes `"14.0.0-rc.1+stage7.1B.skeleton"` |
| CTMVVM 8.4.2 RelayCommand on `NextAsync` | generates `NextCommand` |
| XAML forward button binding | `NextAsyncCommand` (wrong) |
| XAML Skip binding | `SkipCommand` (correct -- works) |
| XAML Back binding | `BackCommand` (correct -- disabled step 1) |
| overlay.log line 48 | `[Bootstrap] welcome-seen=False` (gate returning false) |
| overlay.log line 70 | `first-run check: scheduling setup wizard in 200ms` |

---

## STEP 1 commit

Commit: `7b3eaae`
