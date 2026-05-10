# V14 Stage 7.7B-FIX -- Run Log

## STEP 0 -- Checkpoint Backup

**Timestamp:** 2026-05-10 03:47 UTC  
**Backup dir:** `G:\Project Folder\Master FM\_BACKUPS_2026-05-10_03-47_S7_7B_FIX_PRE\`

### Pre-conditions

| Check | Result |
|---|---|
| `git status` clean (tracked files) | YES -- only md/memory.md modified (untracked: old diagnostic files) |
| HEAD commit | `727e309` -- fix(7.7B): remove WindowStartupLocation Setter crash |
| version.json | `14.0.0-rc.2` -- CORRECT |
| `_full_rebuild.ps1` available | YES |

### Backup Contents

| Step | Result |
|---|---|
| `src_snapshot` robocopy | 671 files, 106 dirs, 130.38 MB |
| `md_snapshot` robocopy | 8 files, 1 dir, 262.4 KB |
| `installed_pre_fix` robocopy | 409 files, 85 dirs, 88.52 MB |
| `src_snapshot.zip` | 41.5 MB, 706 entries -- integrity OK |
| `git_log_pre_fix.txt` | 10 commits written |

### Pre-existing Crash History

- **Crash 1:** `TextOptions.TextFormattingMode` in Typography.xaml DisplayTextStyle -- attached property used as Setter.Property, not a DP. Fixed in commit `43e0796`.
- **Crash 2:** `WindowStartupLocation` in AppDialogStyle.xaml -- CLR property, not a DP. Fixed in commit `727e309`.
- **Crash 3 (known, not yet fixed):** `AccentBarDuration` StaticResource referenced in AppDialogStyle.xaml ControlTemplate but not defined anywhere in Theme dictionaries.

### STEP 0 Status: COMPLETE

---

## STEP 1 -- Exhaustive Theme Audit

*(To be filled after audit run)*

---

## STEP 2 -- Fix All Audit Findings

*(To be filled after fixes)*

---

## STEP 3 -- Clean Install + Dual Verification

*(To be filled after verification)*

---
