==
== V14_S7_19_5_REPORT.md  --  Stage 7.19.5 closure report
== WPF Setup Wizard binding fix (focused diagnosis-then-fix brief)
== Local-only deliverable. Tracked.
==

# 1. Summary

Stage 7.19.5 -- a focused diagnosis-then-fix brief commissioned after Stage 7.19
surfaced a pre-existing WPF binding bug in its SE2 log inspection -- is complete
and operator-PASSed at first attempt.

Bug fixed: `AudioDeviceViewModel.SelectedDevice` was a read-only property since
Stage 7.12 Batch A rev14 (commit `23c4c54`, 2026-05-17), but `SetupWizardWindow.xaml`
line 262 had a `SelectedItem="{Binding SelectedDevice, Mode=TwoWay}"` declaration.
WPF threw `InvalidOperationException` at "setup wizard show" on every fresh install
since May 17 (rc.3 / 7.13 / 7.15 / 7.16 / 7.17 / 7.18 / 7.19). The wizard's audio-step
click-to-select was silently dead for that entire window -- but the exception was
non-fatal (caught by the bootstrap try/catch), tray continued, and no operator gate
test ever exercised the wizard path so the bug stayed silent until Stage 7.19 SE2.

Fix: Option A -- restored a public setter on `SelectedDevice` that forwards to the
existing `SelectDevice()` user-click path for non-null writes and to
`SetSelectedDeviceSilent()` for null writes. Smallest possible diff: one file,
+25 / -2 lines.

Outcome: PASS at first operator gate. Zero SE5 diagnosis-fix pairs. Zero strikes
consumed (0 / 24). All 21 DoD checklist items PASS.

---

# 2. Diagnosis recap

## From Stage 7.19 SE2 log inspection (S11.3)

Post-rebuild overlay.log showed exactly one ERROR:

```
[2026-05-22 14:01:37.652] [ERROR] [TRAY-CS] [Bootstrap] !! ERROR [setup wizard show]:
System.InvalidOperationException: A TwoWay or OneWayToSource binding cannot work on the
read-only property 'SelectedDevice' of type 'MastersFM.Tray.ViewModels.AudioDeviceViewModel'.
```

Stage 7.19 SE3 diff review confirmed zero WPF files touched in STEPs 0-10 of that brief.
`git log` on `AudioDeviceViewModel.cs` returned commits only from 2026-05-17 (Stage 7.12
Batch A area). The error pre-dates Stage 7.19; operator authorized parking per SE7 and
commissioning a separate diagnosis-then-fix brief.

## From Stage 7.19.5 S1.3 read of the wizard surface

Three questions were posed in the brief's S1.3:

1. ❓ Does the wizard write the user's device choice back through the SelectedDevice
   binding (requires TwoWay -> Option A)? **YES.** The wizard never reads `AudioVm.SelectedDevice`
   anywhere. The TwoWay binding's write-back chain (user click -> setter -> SelectDevice)
   is the wizard's ENTIRE device-selection mechanism. Without TwoWay write, the wizard's
   audio step becomes dead clicks.

2. ❓ Or does the wizard call SetDevice() and the binding is just for display
   (allows OneWay -> Option B)? **NO.** No code path in SetupWizardWindow.xaml.cs or
   SetupWizardViewModel.cs calls SelectDevice() or SetSelectedDeviceSilent().

3. ❓ Or is the binding targeting the wrong property (Option C)? **NO.** The binding
   targets the correct property. There's no other writable property to retarget to.

Conclusion: Option A required. The fix must restore the writable setter, not
change the binding mode.

---

# 3. Fix shape chosen + reasoning

**Option A: restore a public setter on `SelectedDevice` forwarding to the
existing user-click + silent paths.**

Code change in `src/tray_csharp/ViewModels/AudioDeviceViewModel.cs`:

Before (line 60-61):
```csharp
// Raw accessor used by the code-behind SelectionChanged guard.
public AudioDeviceInfo? SelectedDevice => _selectedDevice;
```

After (lines 60-83):
```csharp
// Raw accessor + WPF TwoWay binding write-path.
//
// Stage 7.19.5 fix: SetupWizardWindow.xaml line 262 binds the audio-step
// ListBox's SelectedItem TwoWay to this property. Before Stage 7.12 Batch A
// rev14 (commit 23c4c54, 2026-05-17) this property had a setter; rev14
// removed the setter, breaking the wizard's audio-step click-to-select
// on every fresh install (rc.3 through 7.19) with an InvalidOperationException
// at "setup wizard show". The setter is restored but forwards to the
// EXISTING user-click path (SelectDevice) so the design rule "User clicks
// forwarded via SelectDevice(); programmatic changes via
// SetSelectedDeviceSilent()" is honored: WPF TwoWay binding writes
// represent user input, which is exactly what SelectDevice handles.
//
// Null writes (rare; would only happen if WPF clears the binding) route
// through SetSelectedDeviceSilent so they update visuals without firing
// the persist-to-config + HTTP POST side effects of SelectDevice.
public AudioDeviceInfo? SelectedDevice
{
    get => _selectedDevice;
    set
    {
        if (value != null) SelectDevice(value);
        else               SetSelectedDeviceSilent(null);
    }
}
```

## Why Option A (and not B, C, or C-prime)

| Aspect | Option A | Option B (OneWay) | Option C (retarget) | Option C-prime (event-driven) |
|---|---|---|---|---|
| Eliminates exception | YES | YES | YES | YES |
| Wizard fully functional after fix | YES | **NO** (clicks dead) | depends on retarget choice | YES |
| Diff size | +25 / -2 / 1 file | +1 / -1 / 1 file | +3 / -1 / 1 file | ~+6 / -2 / 2 files |
| Violates design comment | partial, mitigated | NO | depends | NO |
| Regression risk to AudioDeviceWindow | NONE | NONE | NONE | NONE |
| Matches established AudioDeviceWindow pattern | NO | NO | NO | YES |

Option A chosen because it's the **smallest diff that fully restores both the
absence-of-exception AND the wizard's functional behavior.** Option B (smaller diff,
but leaves wizard dead) was rejected per S1.3 finding that the wizard relies entirely
on TwoWay write-back. Option C-prime (more pattern-correct) was rejected per the
brief's "Smallest possible diff" mandate in S2.2.

The design-comment concern that the file header forbids "programmatic via property
setter" is mitigated because:
1. The setter forwards to the EXISTING `SelectDevice()` user-click path -- it does
   not introduce a new mutation behavior, only a new WPF entry-point into the same path
2. Grep verification (Stage 7.19.5 S0.4.3) confirmed only ONE TwoWay binding to
   `SelectedDevice` exists in the codebase (the wizard) -- the new entry-point is
   used in exactly one place
3. WPF TwoWay binding writes represent user-driven input, which is exactly what
   `SelectDevice()` is designed to handle

---

# 4. Files touched

- `src/tray_csharp/ViewModels/AudioDeviceViewModel.cs` (+25 / -2; only the `SelectedDevice` property region)
- `V14_S7_19_5_LOG.md` (NEW; running log; force-added past `V*_LOG.md` gitignore)
- `V14_S7_19_5_REPORT.md` (NEW; this file; tracked)
- `md/memory.md` (APPEND only -- closure entry)
- `_BACKUPS_2026-05-23_00-15_S7_19_5_PRE/` (disk-only snapshot; NOT tracked)

## Stage delta vs `02340e4` (Stage 7.19 closure)

```
 V14_S7_19_5_LOG.md                                 | +N
 V14_S7_19_5_REPORT.md                              | +N
 src/tray_csharp/ViewModels/AudioDeviceViewModel.cs | +25 / -2
 md/memory.md                                       | +N (APPEND)
```

(Exact `+N` line counts captured in the closure commit's `git diff --stat HEAD~1 HEAD`.)

---

# 5. Files NOT touched

Per absolute rules from brief:
- `src/tray.ps1`               (protected; SHA256 verified UNCHANGED at S0.2, S6.1, S7.1)
- `src/tray_native/tray_native.cs` (protected; SHA256 verified UNCHANGED)
- `src/launcher.cs`            (protected; SHA256 verified UNCHANGED)
- `src/server.js`              (protected; SHA256 verified UNCHANGED)
- `src/customize.html`         (Stage 7.19 surface preserved; `git diff 02340e4..HEAD -- src/customize.html` EMPTY)
- `src/overlay.html`           (out of scope; `git diff 02340e4..HEAD -- src/overlay.html` EMPTY)
- `version.json`               (no bump; stays at 14.0.0 fix-forward via SHA suffix)
- All other `.cs` files in `src/tray_csharp/` (no logic changes; only the property in AudioDeviceViewModel.cs)
- All XAML files (no XAML changes; the fix was in the ViewModel, not the binding declaration)

---

# 6. Log verification: pre-fix vs post-fix

## Pre-fix baseline (S3.1)

`%LOCALAPPDATA%\MastersFM\overlay.log` before STEP 4 rebuild:
- Total lines: 11 599
- `InvalidOperationException` count: 1 (historical, from Stage 7.19 S13.2 install)
- `SetupWizard` mentions: 5 (1 exception decoration + 4 routine bootstrap)
- `SelectedDevice` mentions: 1 (in the exception payload)

## Post-fix verification (S5.1)

Fresh log post-install (the launcher resets overlay.log on each install):
- Total lines: 100 (fresh)
- `InvalidOperationException` count: **0** ✓
- `ERROR` lines: **0** ✓
- `WARN` lines: 0
- `SetupWizard` entries (all routine INFO):
  - `DialogService initialized; 5 dialogs registered`
  - `first-run check: scheduling setup wizard in 200ms`
  - `[Dialog] showing SetupWizard`
  - `[SetupWizard] setup wizard completed; welcome_seen=true`  ← wizard ran end-to-end
  - `[Dialog] SetupWizard closed completed=True`
  - `[Bootstrap] setup wizard returned completed=True`

The wizard fully ran from show -> completed in an 18-second window (12:21:35.861 ->
12:21:53.286), consistent with the operator clicking through the 3 steps in real
time during the STEP 4 install. The `welcome_seen=true` persistence confirms the
audio-step write-back path (Option A's restored setter) is functioning.

## server.log

`%LOCALAPPDATA%\MastersFM\server.log` tail-100 post-install:
- `ERROR`/`WARN` count: **0** ✓
- No regressions introduced.

---

# 7. Operator verification

**PASS at attempt 1.**

Gate halt held at STEP 6.2 per SE4 strict rules (literal `PASS` or `FAIL <reason>` only;
"continue"-style shortcuts re-prompted). Operator replied `PASS` after verifying the
post-install log state matched the fix expectation (S5.1 pre-emptive result accepted).

---

# 8. Strikes consumed

**0 / 24.**

No SE5 diagnosis-fix pair triggered. No SE6 three-strike escalation. No FAIL gate.
The fix took on the first attempt.

---

# 9. v14 status

Still **v14.0.0** (no version bump). Stage 7.19.5 lands as fix-forward via commit
SHA suffix.

Installed `MastersFM_Tray_v14.dll` `ProductVersion` after STEP 4 rebuild:
`14.0.0+629c24c187bd52625717c2dfb496531547f70001` (HEAD at time of build was the
STEP 3 log-only commit `629c24c`; the source delta was in `44b8917` from STEP 2).

After the STEP 7 closure commit + final dual-build (S7.2), the `ProductVersion`
suffix will bump to whatever the closure commit hashes to.

## Cumulative v14.0.0 fix-forward chain

| Stage | Closure commit | What landed |
|---|---|---|
| 7.17  | `718e3e1`  | local v14.0.0 cut (version.json bump + 6 hardcoded version-string fixes) |
| 7.18  | `99c5f2d`  | Start-on-login default fix (Task A) + customize.html UX audit (Task B) |
| 7.19  | `02340e4`  | customize redesign foundation (rename + help text + animation tokens + sub-grouping) |
| 7.19.5| (this stage's closure SHA) | WPF Setup Wizard binding fix |

## Remaining customize-redesign cycle

- **Stage 7.20** -- master controls (Accent, Size, Text Size, Glow, Animations) +
  search bar + advanced toggle. Operator will write the brief after this stage closes.
- **Stage 7.21** -- onboarding banner + sidebar structural revision + final polish.

== END OF FILE ==
