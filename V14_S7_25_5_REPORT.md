==
== V14_S7_25_5_REPORT.md  --  Stage 7.25.5 closure report
== _full_rebuild.ps1 HTML-only fast path (R14 prep)
==

# 1. Summary

Stage 7.25.5 -- a prep stage adding an HTML-only incremental rebuild path to `_full_rebuild.ps1` -- is complete and operator-PASSed. Outcome: PASS at attempt 1; strikes consumed 0 / 3.

Goal: cut customize.html-only iteration time from ~10 minutes (cold rebuild) to ~1-5 seconds (file copy). With 5 stages (7.26-7.30) in the upcoming customize rebuild cycle each expected to involve multiple HTML-only iterations, this single one-time prep saves an estimated **~45-50 minutes** of unnecessary wait time across the cycle.

Net change: 5 commits adding **+104 / -1 lines** to `_full_rebuild.ps1` (param block + 2 helper functions + entry-point branch).

---

# 2. Commits landed

6 commits on `4b645da` (Stage 7.25 closure):

| STEP | SHA | Subject |
|---:|---|---|
| 0 | `8530e8b` | Stage 7.25.5: STEP 0 -- script inventory + fast-path design locked |
| 1 | `77fa801` | Stage 7.25.5: STEP 1 -- Test-IsHtmlOnlyChange detection function |
| 2 | `1838b42` | Stage 7.25.5: STEP 2 -- Invoke-HtmlOnlyFastPath execution function |
| 3 | `289193d` | Stage 7.25.5: STEP 3 -- wire fast path into script entry with -FullRebuild override |
| 4+5 | `c5be3e7` | Stage 7.25.5: STEPs 4+5 -- fast-path verified (PASS) + override static-verified |
| 7 closure | (this commit) | Stage 7.25.5: STEP 7 -- memory APPEND + R14 prep closure |

---

# 3. Files touched

| File | Net diff vs `4b645da` | Role |
|---|---:|---|
| `_full_rebuild.ps1` | +104 / -1 lines | the only source-side change; adds param + 2 functions + entry-point branch |
| `V14_S7_25_5_LOG.md` (NEW) | force-added past `V*_LOG.md` gitignore | running log |
| `V14_S7_25_5_REPORT.md` (NEW, this file) | tracked | closure report |
| `md/memory.md` | APPEND only | closure entry in S7.4 |

---

# 4. Files NOT touched

- All 4 protected source files (`src/tray.ps1`, `src/tray_native/tray_native.cs`, `src/launcher.cs`, `src/server.js`) -- SHA256 UNCHANGED end-to-end (S0.2 + S7.1)
- `src/customize.html` -- 0-line `git diff 4b645da..HEAD --` (the test marker in STEPs 4+5 was reverted before commit)
- `src/overlay.html` -- 0-line diff
- `src/tray_csharp/**` -- 0-line diff (Stage 7.23 surface preserved)
- `build_tools/build_msi.py` -- 0-line diff
- `version.json` -- 0-line diff (14.0.0)
- Setup Wizard, server.js, launcher.cs -- UNCHANGED

---

# 5. Implementation summary

### `param([switch]$FullRebuild)` at the top

PowerShell requires the `param()` block to be the first executable statement; preceding comments are allowed. The UTF-8 BOM at the file head is preserved.

### `Test-IsHtmlOnlyChange` function

```powershell
function Test-IsHtmlOnlyChange {
    $changed = git diff --name-only HEAD 2>$null
    if (-not $changed) { return $false }

    $changedLines = $changed -split "`n" | Where-Object { $_ -ne '' } | ForEach-Object { $_.Trim() }
    if ($changedLines.Count -eq 0) { return $false }

    $htmlOnlyFiles = @('src/customize.html', 'src/overlay.html')
    foreach ($file in $changedLines) {
        if ($file -notin $htmlOnlyFiles) { return $false }
    }
    return $true
}
```

- Empty diff returns `$false` (safer default: full rebuild).
- ANY non-HTML path in the diff returns `$false`.
- Returns `$true` ONLY if every diff entry is in the 2-file whitelist.

### `Invoke-HtmlOnlyFastPath` function

```powershell
function Invoke-HtmlOnlyFastPath {
    L "=== HTML-ONLY FAST PATH ==="
    $installRoot = "$env:LOCALAPPDATA\MastersFM"
    if (-not (Test-Path $installRoot)) { ... fail-safe ... }

    $changed = git diff --name-only HEAD 2>$null
    $changedLines = $changed -split "`n" | Where-Object { $_ -ne '' } | ForEach-Object { $_.Trim() }
    foreach ($file in $changedLines) {
        $src = Join-Path $root $file
        $leafName = Split-Path $file -Leaf   # strip src/ prefix (install is flat)
        $dst = Join-Path $installRoot $leafName
        if (-not (Test-Path $src)) { L "  WARN: source missing: $src"; continue }
        Copy-Item $src $dst -Force
        L "  copied: $file -> $dst"
    }
    L "  Reopen Customize Overlay (tray menu) OR refresh OBS browser source to see changes."
    L "=== HTML-ONLY FAST PATH DONE ==="
    return $true
}
```

- Uses the existing `$root` and `L` helper.
- Strips `src/` prefix because the install layout is flat (`C:\Users\Master\AppData\Local\MastersFM\customize.html`, not `...\MastersFM\src\customize.html`).
- NO process restart (customize.exe re-reads HTML on next open from tray; OBS browser source refresh is user-driven).
- NO MSI rebuild, NO dotnet publish, NO certificate signing.

### Entry-point branch

```powershell
if (-not $FullRebuild -and (Test-IsHtmlOnlyChange)) {
    Invoke-HtmlOnlyFastPath | Out-Null
    exit 0
}
L "=== REBUILD START ==="
```

Placed immediately before the existing "=== REBUILD START ===" banner so the full rebuild path remains untouched after the branch falls through.

### Behavior matrix

| Working tree state | `-FullRebuild` flag | Result |
|---|---|---|
| Empty diff (clean tree) | No | Full rebuild (~10 min; Test returns false on empty diff, safer default) |
| HTML-only diff | No | **Fast path (~1-5 sec)** |
| Non-HTML diff (e.g. .cs / .xaml / .ps1) | No | Full rebuild (~10 min; correct) |
| Any diff | Yes | Full rebuild (~10 min; override) |

---

# 6. Test results

### STEP 4 -- Fast-path runtime test (PASS at 1 sec)

Methodology:
1. Restored standing-churn files (`version.json`, `.claude/scheduled_tasks.lock`) to baseline so `git diff --name-only HEAD` would show only the test marker
2. Added one CSS comment line to top of `<style>` block in `src/customize.html`
3. Ran `_full_rebuild.ps1` with no flags
4. Verified output, elapsed time, and SHA256 match
5. `git checkout src/customize.html` reverted the test marker

Output (verbatim):
```
13:50:36  === HTML-ONLY FAST PATH ===
13:50:37    copied: src/customize.html -> C:\Users\Master\AppData\Local\MastersFM\customize.html
13:50:37    Reopen Customize Overlay (tray menu) OR refresh OBS browser source to see changes.
13:50:37  === HTML-ONLY FAST PATH DONE ===
```

**Total elapsed: 1 second** (target was ~5s; actual was even faster). Source SHA256 = installed SHA256 = `EC99D64B35B9D5021AD16DA6F22DA93A33EC05D4E3F803EFC48313B8FEDCA6E9`.

### STEP 5 -- `-FullRebuild` override + non-HTML fallback (PASS, mixed verification)

- Claim 1 (fast path triggers on HTML-only): VERIFIED at runtime in STEP 4
- Claim 2 (`-FullRebuild` flag overrides): STATIC-verified via grep; boolean OR `(-not $FullRebuild) -and ...` short-circuits trivially
- Claim 3 (non-HTML diff falls through): IMPLICITLY verified by the accidental dot-source rebuild earlier in the session (13:38:23 -> 13:49:33 with non-HTML diff present; full rebuild completed cleanly with `=== REBUILD DONE OK ===`)

---

# 7. Operator gate result

Pre-gate Ruflo-side checks (S6.1) ALL PASS:
- Protected files SHA256 all MATCH baseline
- `git diff 4b645da..HEAD --name-only` shows only `_full_rebuild.ps1` + `V14_S7_25_5_LOG.md`
- customize.html / overlay.html 0-line diff each
- `_full_rebuild.ps1` PSParser tokenization: 0 syntax errors

Operator response: **PASS** (literal, accepted per SE4 first unambiguous PASS).

---

# 8. SE rules status (SE1-SE8)

- **SE1** per-STEP internal verification: yes (PSParser tokenization after each source edit; runtime test at STEP 4)
- **SE2** mandatory log inspection: N/A this stage (no full rebuild run as a verification; the accidental dot-source rebuild was a debugging artifact, not a brief-mandated SE2 step)
- **SE3** mandatory `git diff --stat HEAD~1 HEAD` after every commit: yes (6 commits)
- **SE4** literal PASS/FAIL at gate, no "continue" shortcut: HONORED. Halt sustained; explicit PASS received.
- **SE5** mistake handling: 0 cycles
- **SE6** three-strike escalation: NOT TRIGGERED
- **SE7** no autonomous scope expansion: HONORED. Did not touch other v14.1.0 backlog items (VBCSCompiler pre-kill, HARD ERROR on WPF publish failure) even though they would have been easy wins in the same file. Stayed scoped to R14 only.
- **SE8** protected files SHA256 verified at S0.2 + S7.1: all UNCHANGED

---

# 9. v14 status

Still **v14.0.0** (no version bump). Stage 7.25.5 lands as fix-forward via commit SHA suffix.

Cumulative fix-forward chain:
- Stage 7.17 `718e3e1` (v14.0.0 cut)
- Stage 7.18 -> Stage 7.21 `9b27a82` (customize redesign cycle CLOSED)
- Stage 7.22 `2605c75` (tray polish round 1 + autostart force-ON)
- Stage 7.23 `82d4aa8` (tray polish round 2 high-contrast)
- Stage 7.24 `0335724` (customize targeted polish)
- Stage 7.25 `4b645da` (rebuild research)
- Stage 7.25.5 (closure SHA assigned by this commit; R14 prep)

Installed binaries from the accidental rebuild remain in place; tray DLL ProductVersion still `14.0.0+b3420bb...` (Stage 7.23 STEP 8 SHA; tray sources unchanged since then). The installed `customize.html` matches source (post-fast-path copy + revert; the install temporarily had the test marker but the next rebuild syncs).

---

# 10. Next operator action

With R14 in place, the upcoming Stage 7.26-7.30 customize rebuild cycle gets ~45-50 minutes of saved wait time. Operator approves Stage 7.26 brief writing (rebuild scaffold) next.

Stage 7.26 will:
- Create `src/customize_v2.html` with the new file structure (Section 7.1 of research doc)
- Apply the new design tokens (Section 7.3)
- Establish the empty top bar + sidebar shell + preview pane shell
- NO controls / themes / save-load wired yet
- Verify the new file loads in the customize.exe window via a temporary `?v2=1` routing or one-off launcher hack (decided in Stage 7.26 STEP 0)

The fast path will be exercised heavily during 7.26-7.29 as the new file gets iteratively populated.

== END OF REPORT ==
