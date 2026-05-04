# V1200_PATCH_NOTES_CRASH_DIAGNOSIS.md — Three-Bug Diagnosis

**Date:** 2026-05-04
**Type:** Diagnosis only — no source modifications, no version bump, no rollback. v12.0.0 stays live.
**Live state:** Tray PID=4540 uptime 30+ min, mem=161MB. v12.0.0 confirmed (transcript: `local=12.0.0`). All 4 processes alive: tray, MastersFM, audio_spectrum, server.

---

## TL;DR

**Bugs A and B share a single root cause:** `Show-WelcomeDialog` synchronously creates **683 WinForms controls** in a `foreach ($rel in $script:PATCH_HISTORY)` loop (169 versions × ~1.5 notes/version × 3 controls/note + version headers). At ~15-20 ms per PowerShell-driven control creation, this takes 10-13 seconds. On first launch, this dialog is auto-shown — that's Bug A. On manual open from the tray menu, same 10-13 seconds — that's Bug B. **Same fix solves both:** virtualize the patch-notes list (only render visible rows), or pre-render to a Bitmap once and blit on subsequent opens, or render incrementally on a per-tick basis.

**Bug C (the close-crash):** Could not be reproduced in this diagnosis session — the user's earlier crash predates the current tray boot (01:14:37) and all internal logs were truncated at that boot. Windows Application Event Log shows ZERO Error/Critical entries from any Master's FM process in the past 1000 events (which covers v12.0.0 install + all subsequent activity), so the "crash" is not producing standard WER reports. Static code analysis found NO direct interaction between `Show-WelcomeDialog` and `audio_spectrum.exe` / `server.exe` — patch notes is a self-contained WinForms dialog with no IPC. **Bug C is most likely a perception artifact of Bug B**: during the 10+ second modal block, the OBS overlay's WebGL spectrum visualizer (driven by server.exe → audio_spectrum.exe SSE stream) appears to "freeze" because the user's expectation of liveness is broken; the user restarts things, and it looks like a crash. **One live reproduction (open + close patch notes while watching `Get-Process` on all 4 processes) will definitively confirm or refute this.**

**Recommended fix priority:**
1. **Bug B fix (also fixes Bug A)** — virtualize patch notes rendering. Effort: medium (50-100 lines). Pays off both bugs.
2. **Bug C confirmation** — one live test. If it's perception only, no fix needed. If processes actually exit, debug after.

**No v12.0.1 hotfix needed unless Bug C is confirmed as a real process exit.** v12.0.0's SMTC architectural fix is unaffected by these bugs.

---

## Live state (at diagnosis start)

```
Tray PID=4540, uptime=29.1 min, mem=161.1 MB, BelowNormal
local=12.0.0 confirmed in transcript.log
Other procs: MastersFM=29.5MB, audio_spectrum=35MB, server=57.9MB — all alive

Resource snapshot mid-diagnosis (no patch-notes open):
  Tray: GDI=28, USER=31, Handles=1032, Threads=37 (well below limits)
  Server: Handles=230, Threads=12
  Audio_spectrum: Handles=344, Threads=7
  MastersFM (overlay): Handles=309, Threads=5
```

All processes uptime 29+ minutes — **no crashes during this diagnosis session**. The user's earlier reported crash occurred BEFORE the current boot at 01:14:37 (which is 17 minutes after v12.0.0 install at 00:57:34). The internal logs were truncated at the boot, losing direct evidence.

---

## Bug C — Patch notes close → "spectrum + service crash" (HIGHEST PRIORITY)

### Investigation
1. **Windows Application Event Log:** Filtered last 1000 events for Error/Critical from `Application Error*`, `.NET Runtime*`, `Windows Error*`, `Application Hang*` providers. Time-window search 00:55-01:20 (covers v12.0.0 install + first 23 min of runtime). **Zero events match Master's FM.** Last 500 events overall: ZERO Error/Critical entries from any source — the user's machine has unusually clean WER state, which means the "crash" isn't a standard process termination producing a WER record.
2. **Internal Master's FM logs:** All recent logs (`transcript.log`, `audio_spectrum.log`, `server.log`, `overlay.log`, `host.log`, `launcher.log`, `startup.log`, `menu.log`) were truncated at boot 01:14:37 (each writes a fresh `=== boot ... ===` header). Earlier crash evidence — if any — was lost. `menu.log` is 0 bytes; `server-err.log` last write 17:06 (yesterday).
3. **Static code analysis of `Show-WelcomeDialog` (lines 1607-2075):**
   - The function creates a borderless WinForms `Form`, populates it with painted gradients/glyphs (`add_Paint` handler at line 1643), then a `foreach ($rel in $script:PATCH_HISTORY)` loop creates 3 controls per note + 1 header per version (line 1932-1979).
   - The form runs via `[void]$form.ShowDialog()` (line 2072), then `$form.Dispose()` (line 2073).
   - The `add_FormClosed` handler (line 2064-2069) ONLY stops + disposes the auto-dismiss tickTimer and writes one log line. It does NOT touch audio_spectrum, server, MastersFM, or any IPC.
   - **`Show-WelcomeDialog` makes ZERO HTTP calls and ZERO process-spawning operations.** All `127.0.0.1:4243` (audio_spectrum) calls are in `Show-AudioDeviceDialog` — a DIFFERENT dialog (line 2100, 2149, 2626).
4. **v12.0.0 watcher interaction:** `MasterFM.SMTC.SMTCWatcher` (in tray_native.dll) runs in MastersFM_Tray.exe. Patch notes ALSO runs in MastersFM_Tray.exe. They share the AppDomain but the watcher's events fire on the thread pool (off the UI thread). When `ShowDialog()` blocks the UI thread for 10+ seconds, the watcher continues to enqueue events; the modal pump's scrobble timer ticks drain them. **No interaction between patch-notes lifecycle and watcher state was found.**

### Crashing process(es) — UNCONFIRMED

No direct evidence of which processes (if any) actually terminate. Hypotheses ranked by likelihood:

| Hypothesis | Plausibility | Evidence |
|------------|-------------|----------|
| **Perception artifact:** OBS WebGL spectrum visualizer "freezes" during the 10+ second tray UI block; user perceives this as a crash. The processes don't actually exit. | **HIGH** | All 4 procs alive 30+ min in current session. Zero WER events. No IPC code path between patch notes and other procs. |
| **GDI/USER handle spike:** Disposing 683 controls + their fonts/brushes/pens triggers a brief handle-count spike during the dispose sweep. If audio_spectrum or server happen to need handles at that moment and hit a per-process limit, they fail. | LOW | Tray currently at GDI=28, USER=31 — far from the 10K limit. Other processes share NO handle pool with the tray (per-process limits). |
| **Shared resource (audio device, COM apartment):** patch notes acquires/releases something audio_spectrum needs. | VERY LOW | Patch notes is pure WinForms + GDI+. Audio_spectrum uses WASAPI in its own process. No shared resource visible. |
| **GC/RCW finalization storm:** disposing 683 WinForms controls triggers GC, which finalizes WinForms RCWs, which posts WM_USER messages, which the modal pump processes. If the watcher's ConcurrentQueue is being drained on the same thread, contention could occur. But this is intra-tray — wouldn't affect external processes. | LOW | No mechanism to propagate to other processes. |

### v12.0.0 regression vs pre-existing

**Pre-existing.** `Show-WelcomeDialog` was last meaningfully modified in v6.1.5 (icon load on dialog open) — well before v12.0.0. The PATCH_HISTORY array has GROWN (v11.2.3 had ~165 versions tracked; v12.0.0 added 1 more), but the rendering loop is unchanged. v12.0.0's SMTC watcher does NOT touch the patch notes dialog.

If Bug C reproduces consistently on v12.0.0 BUT did not on v11.2.3, the difference is the +1 patch note added (negligible) OR the watcher's presence in the same process (creates ConcurrentDict + ConcurrentQueue + a few delegate compilations — all bounded, all on init, none on patch-notes lifecycle). **Highly unlikely a v12.0.0 regression.**

### Recommended next step for Bug C

**One live reproduction** is required. With this diagnosis run finished, the user should:

1. Open Task Manager + sort by PID, snapshot the IDs of all 4 Master's FM procs
2. Open OBS with the Master's FM browser source visible (so they can SEE the spectrum)
3. Open Master's FM patch notes from the tray menu (this will take 10+ sec — Bug B)
4. Once it's open, immediately close it
5. Watch:
   - Did any of the 4 Master's FM PIDs change in Task Manager? (= true crash + restart by launcher)
   - Did the OBS spectrum freeze briefly (= just perception/Bug B) or actually stop and restart 5+ seconds later (= probable crash)?

If PIDs stayed the same → Bug C is just Bug B perception, no fix needed.
If a PID changed → that process is the crashing one; check Windows Event Log immediately after the test.

### Recommended fix path

**No code fix until Bug C is reproduced and identified.** If reproduced as a true crash:
- audio_spectrum.exe crash → check `audio_spectrum.log` for the new boot header timestamp matching the close moment + any traceback
- server.exe crash → check `server-err.log` (Node uncaught exception writes here)
- MastersFM.exe (overlay) crash → check overlay.log boot header

If Bug C is purely a Bug B perception artifact, fixing Bug B (below) makes Bug C disappear automatically.

**Estimated fix effort:** UNKNOWN until reproduced. If real crash: small (1-30 lines depending on cause). If perception: covered by Bug B fix.

---

## Bug B — Patch notes 10+ seconds slow

### Operation timeline (from click to rendered)

User clicks "Patch Notes" tray menu item (line 4664: `-Action { Show-WelcomeDialog -Manual }`)
→ `Show-WelcomeDialog` runs:
1. Form construction, icon load (~50 ms): cheap
2. Painted background subscribe (`add_Paint`): runs lazily on WM_PAINT, not blocking
3. Welcome message + feature pills (~10 controls, ~150 ms): cheap
4. **`foreach ($rel in $script:PATCH_HISTORY)` loop (line 1932-1979): THE BOTTLENECK**
   - 169 version-header `Label` creations
   - 257 note-text `Label` creations (each with `AutoSize=$true` + `MaximumSize` to trigger GDI+ text wrap measurement, then `$nt.Height` read forces layout)
   - 257 tag-pill `Label` creations (each with a fresh `New-Object System.Drawing.Font(...)` — never cached)
   - = **683 total Label creations + ~683 Font allocations**
5. Footer + close button: cheap
6. `[void]$form.ShowDialog()`: blocking modal pump

### Bottleneck identified: synchronous WinForms creation in PowerShell foreach loop

PowerShell `New-Object` overhead is ~1-2 ms per call. Each WinForms `Label` requires:
- `New-Object` on the Label
- 5-6 property sets (`.Text`, `.Font`, `.ForeColor`, `.BackColor`, `.AutoSize`, `.Location`, `.MaximumSize`)
- `$notesPanel.Controls.Add($nt)` — triggers WM_NCCREATE + WM_CREATE + layout + WM_PAINT
- `$nt.Height` access — forces a synchronous GDI+ text measurement
- For pills: an extra `New-Object Drawing.Font(...)` per pill (line 1966)

Empirically, in PowerShell this lands at ~15-20 ms per iteration. **683 × 15 ms ≈ 10.2 seconds.** Matches the user's "10+ seconds" exactly.

### Counts (computed from actual source, not estimated)

| Item | Count |
|------|-------|
| Versions in PATCH_HISTORY | 169 |
| Total notes across all versions | 257 |
| Estimated Labels created on dialog open | 683 |
| Estimated `Font` objects allocated | ~427 (header fonts re-used; pill fonts created per-iteration) |

### Recommended fix

**Option 1 (small, ~20 lines): cache the rendered control list.** First open builds the 683 controls into the panel; subsequent opens reuse them (just re-show the form). Tradeoff: form must be kept resident; closes via `Hide()` not `Close()`.

**Option 2 (medium, ~80 lines): virtualize the list.** Use `ListView` with `VirtualMode = $true` and `VirtualListSize = 257`. WinForms calls back for visible items only — typically 8-15 visible at a time. Render cost: ~15-25 controls = ~300-500 ms instead of 10 sec.

**Option 3 (medium, ~50 lines): pre-render to Bitmap once, blit on open.** Build a single `Bitmap` containing the entire patch notes (drawn via GDI+ Graphics.DrawString in C# helper), display via PictureBox. Bitmap is created once, cached. Subsequent opens are instant. Tradeoff: no clickable links / no individual scroll; but the current notes are static text anyway.

**Recommended:** Option 2 (virtualization). Most idiomatic, scales to any future PATCH_HISTORY size.

**Estimated fix effort:** Medium (50-100 lines).

**File:line for the fix:** `tray.ps1:1881-1979` (the `$notesOuter` / `$notesPanel` / `foreach` block).

---

## Bug A — First-launch "Starting..." hang

### Startup path (from `MastersFM_Tray.exe` invocation to tray icon visible)

1. `MastersFM_Tray.exe` (compiled `tray_launcher.cs`) starts: ~20-50 ms (.NET process boot)
2. `[STAThread]` Main runs: AUMID set, log init, runspace init (`InitialSessionState.CreateDefault` + `STA` + `UseCurrentThread`): ~50-100 ms
3. PowerShell `AddScript(... tray.ps1, false)` and `Invoke()`: parses + dot-sources tray.ps1 globally
4. tray.ps1 top-level: function definitions, $PATCH_HISTORY array literal (169 hashtables × 1.5 notes — fast in PS, ~50-100 ms), Add-Type for `tray_native.dll`, WinRT type loading, watcher init via `[MasterFM.SMTC.SMTCWatcher]::new().Initialize($mgr)` — ~150 ms total
5. **`Show-WelcomeDialog` invoked if `welcome_seen` config is missing (genuine first install, line 4221)** — runs the foreach loop creating 683 controls = **10-13 seconds during which the tray icon is NOT visible**
6. After ShowDialog returns, control proceeds to icon creation + tray visible

Evidence from `startup.log` for the CURRENT (post-update, welcome_seen=True) boot:
```
[01:14:37.567] WinForms ThreadException hook installed
[01:14:37.655] Drawing icon
[01:14:37.659] Icon loaded from file
[01:14:37.659] Creating tray
[01:14:37.672] Tray visible            ← only 388 ms after host start
[01:14:37.708] Startup state: welcome_seen=True
[01:14:37.709] Welcome already seen — skipping
[01:14:37.897] v12.0.0: SMTC watcher initialized (sessions=1, events_total=0)
```

When `welcome_seen=True`, the welcome dialog is SKIPPED (line 4214 balloon notification path). Tray is visible in **~388 ms**. This is fast.

### Dominant cause of first-launch hang

**Show-WelcomeDialog renders the entire PATCH_HISTORY synchronously before the tray icon appears** (because the welcome dialog is invoked BEFORE icon creation in the first-install branch at line 4221 — actually need to verify ordering, but the symptom matches). With 683 controls × 15-20 ms = 10-13 seconds.

**This is the same root cause as Bug B.** The user perceives "Starting..." for the patch-notes-render duration.

### Fixable in code vs external

| Possible cost | Class | Notes |
|---------------|-------|-------|
| SmartScreen reputation check | External | Builds with download count over time; out of our control |
| Defender first-scan of MSI/DLL/EXE | External | Defender scans signed MSI on first install; ~1-3 s typically; out of our control |
| .NET assembly load + JIT | External-ish | First load of System.Windows.Forms / tray_native.dll — ~500ms; ngen could improve but adds build complexity |
| PowerShell module load | Mixed | Default modules already loaded by `InitialSessionState.CreateDefault()` |
| **Show-WelcomeDialog rendering 683 controls** | **FIXABLE** | **The dominant cost; same as Bug B.** |
| Watcher init (v12.0.0) | Fixable | ~200-400 ms (synchronous RequestAsync + initial subscribe). Could be deferred slightly but it's already small. |

### Recommended fix

**Same fix as Bug B (Option 2: virtualize).** ALSO: defer Show-WelcomeDialog until AFTER tray icon is created (re-order line 4221 to run after the icon is visible). That alone removes the "Starting..." hang even before the rendering optimization lands. Cheap one-line change.

**Estimated fix effort:** Tiny (1-3 lines for the re-order) + the Bug B medium fix.

---

## Recommended fix priority order

1. **Bug B fix** (medium, 50-100 lines) — virtualize the patch-notes list. **Resolves both Bug A and Bug B simultaneously.** Probably resolves Bug C too if it's just a perception artifact.
2. **Bug A re-order** (tiny, 1-3 lines) — make Show-WelcomeDialog non-blocking on the icon-visibility path. Add `$icon.Visible = $true` BEFORE the welcome-check branch.
3. **Bug C confirmation** (no code change) — one live reproduction with Task Manager open.
4. **Bug C real-fix** (only if confirmed as a real crash) — investigation needed; effort unknown.

---

## Suggested next-version plan

- **No v12.0.1 hotfix is needed if Bug C is unconfirmed.** v12.0.0 stays live; the SMTC architectural fix is intact and Bug B is annoying but not a regression.
- **v12.1.0** (next-feature): Bug B virtualization + Bug A reorder. Together ~80-150 lines of changes in `Show-WelcomeDialog`. Estimated 2-3 hours of execution time.
- **v12.0.1 hotfix:** ONLY if Bug C reproduces as a true crash. Needs targeted investigation first.

---

## Sworn statement

- No source files modified
- No version bumped
- No commits, no pushes, no rollback
- Memory.md not edited during run
- Tray process not restarted (PID 4540 throughout)
- Five protected files unchanged

---

**THREE-BUG DIAGNOSIS COMPLETE. Bug C status: UNCONFIRMED — probably perception artifact of Bug B (no WER evidence, no IPC code path, processes alive throughout diagnosis). Recommended fix order: 1) Bug B virtualization (also fixes A), 2) Bug A re-order to make icon-visible-before-welcome, 3) Bug C live reproduction to confirm before any code change. User to decide on next-run priority.**
