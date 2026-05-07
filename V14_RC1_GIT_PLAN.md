# V14_RC1_GIT_PLAN.md

STEP 8 commit + tag plan. **HALT for Orken approval before executing.**

Generated: 2026-05-07 ~20:30 (during 6h soak window).

## Strategy

Per brief STEP 8.2: V14 work is uncommitted bulk in working tree; commit boundaries are not
cleanly recoverable from working tree alone. Falling back to **one cumulative commit** per
brief allowance:

> "v14.0.0-rc.1: Stages 1-3 + 4 + 5 cumulative migration to .NET 8"

Plus a **separate second commit** for `md/memory.md` APPEND (per STEP 7), kept distinct so the
memory append history is clean.

Then a third action: annotated tag `v14.0.0-rc.1` on the latest commit with summary message.

No push (per directive 1=b). Hand off `git push` + `gh release create` to Orken in STEP 9.

## Commit 1 -- "v14.0.0-rc.1: Stages 1-3 + 4 + 5 cumulative migration to .NET 8"

### Modified files (must include)

```
M  _full_rebuild.ps1            # RC1 ship state header + DiscordRPC.dll fix + flag finalization
M  build_tools/build_msi.py     # SemVer pre-release strip + DiscordRPC.dll/Newtonsoft.Json.dll FILES
M  src/install_bootstrapper.cs  # Stage 3b .NET 8 port (Environment.ProcessPath fix)
M  src/launcher.cs              # Stage 1 .NET 8 launcher
M  src/tray.ps1                 # ALLOWLISTED: APP_VERSION + 1 PatchNotes entry only
```

### Deleted files (stage with `git add` to record deletion)

```
D  src/tray_native.cs           # moved to src/tray_native/tray_native.cs in Stage 5.1
```

### New (untracked) source files (must include)

```
src/Directory.Build.props
src/launcher.csproj
src/audio_spectrum.csproj
src/customize.csproj
src/install_bootstrapper.csproj
src/server_dotnet/                    (entire directory; ~30+ .cs files for Stage 4)
src/tray_native/                      (Stage 5.1 moved location: csproj + tray_native.cs)
test-ps51-load.ps1                    (PS5.1 validation harness from Stage 5.1)
```

## Doc allowlist (commit) per addition #5

These document the V14 work and ship for the public record:

```
V14_NET8_MIGRATION_PLAN.md
V14_RES_1_INVENTORY.md
V14_RES_2_BEHAVIORS.md
V14_RES_3_INTEGRATIONS.md
V14_RES_4_BUILD.md
V14_RES_5_POWERSHELL.md

V14_S4_P1_ENDPOINT_INVENTORY.md
V14_S4_P1_OPEN_QUESTIONS_RESOLVED.md
V14_S4_P1_PORT_PLAN.md
V14_S4_P1_SSE_CONTRACT.md
V14_S4_P1_BASELINE_CAPTURES/                  (38 baseline capture files)
V14_S4_S4_1_LAUNCHER_DIAGNOSIS.md
V14_S4_S4_2_INTENTIONAL_DIFFERENCES.md
V14_S4_S4_3_INTENTIONAL_DIFFERENCES.md
V14_S4_S4_4_INTENTIONAL_DIFFERENCES.md
V14_S4_S4_5_INTENTIONAL_DIFFERENCES.md
V14_S4_S4_7_INTENTIONAL_DIFFERENCES.md
V14_S4_S4_8_INTENTIONAL_DIFFERENCES.md
V14_S4_S4_9A_INTENTIONAL_DIFFERENCES.md
V14_S4_S4_9BC_INTENTIONAL_DIFFERENCES.md
V14_S4_S4_9D_INTENTIONAL_DIFFERENCES.md
V14_S4_S4_9E_INTENTIONAL_DIFFERENCES.md
V14_S4_S4_10_INTENTIONAL_DIFFERENCES.md

V14_S5_P1_TRAY_NATIVE_INVENTORY.md
V14_S5_P1_CSWINRT_RESEARCH.md
V14_S5_P1_PORT_PLAN.md
V14_S5_P1_RISKS.md
V14_S5_P1_QUESTIONS.md
V14_S5_P1_LOG.md
V14_S5_P1_FINAL_REPORT.md
V14_S5_S5_1_INTENTIONAL_DIFFERENCES.md
V14_S5_S5_1_LOG.md
V14_S5_S5_1_FINAL_REPORT.md
V14_S5_S5_4_5_LOG.md
V14_S5_S5_4_5_FINAL_REPORT.md
V14_S5_VALIDATION_CHECKLIST.md

V14_S6_P1_TRAY_LAUNCHER_INVENTORY.md
V14_S6_P1_DEPENDENCY_MAP.md
V14_S6_P1_DISSOLUTION_PLAN.md
V14_S6_P1_RISKS.md
V14_S6_P1_QUESTIONS.md
V14_S6_P1_LOG.md
V14_S6_P1_FINAL_REPORT.md

V14_RC1_VERSION_BUMP_AUDIT.md
V14_RC1_FLAGS_FINALIZED.md
V14_RC1_AUTOUPDATE_VERIFICATION.md      (after redaction sweep -- check for directive language)
V14_RC1_VALIDATION.md
V14_RC1_HOTFIX_PLAYBOOK.md
V14_RC1_GIT_PLAN.md                     (this file -- meta but useful)
V14_RC1_FINAL_REPORT.md                 (written at STEP 12; commit then if produced)
RELEASE_NOTES_v14.0.0-rc.1.md
TESTER_ANNOUNCEMENT_v14.0.0-rc.1.md
```

## Doc denylist (LOCAL ONLY, do NOT commit) per addition #5

These contain working-state language, directive ratifications, internal usernames, or
findings that are appropriate for the working tree but not for a public-repo permanent
record:

```
V14_RC1_PREFLIGHT.md            (contains directive ratification + internal-style language)
V14_RC1_SECRET_AUDIT.md         (contains username "Master" + F:\ path findings; redundant publish)
V14_RC1_HALT_REPORT.md          (working-state document; superseded by VALIDATION.md + FINAL_REPORT)
V14_RC1_MEMORY_APPEND_DRAFT.md  (working file; the actual append goes into md/memory.md in Commit 2)
V14_RC1_SOAK_BASELINE.json      (telemetry snapshot; not a deliverable)
_validation_sampler.ps1         (transient working file from Stage 5.5 sampler experiment)
```

## STEP 8 EXCLUDE (also do NOT commit)

```
version.json                    (per Addition A: leave at v12.0.1 on main; primary auto-update safety)
md/memory.md                    (separate Commit 2 after STEP 7 APPEND applied)
.claude/                        (claude-internal session state; not a project artifact)
CLAUDE.md                       (project-instruction file; review before include; if no V14 changes, skip)
```

## Commit 2 -- "v14.0.0-rc.1: memory.md APPEND"

```
M  md/memory.md                 (one-liner CHANGELOG entry inserted after line 175 ## CHANGELOG header)
```

The body of the entry is `V14_RC1_MEMORY_APPEND_DRAFT.md`; applied verbatim into memory.md
between the `## CHANGELOG` header and the existing v13.0.0 entry.

## Tag

```
git tag -a v14.0.0-rc.1 <last-commit-sha> -m "v14.0.0-rc.1: cumulative .NET 8 migration RC. See RELEASE_NOTES_v14.0.0-rc.1.md."
```

## Pre-commit verification (run before staging)

1. `git status -- version.json` -> must show `modified:` in unstaged (proves it'll be excluded)
2. `git status -- md/memory.md` -> must show `modified:` in unstaged (Commit 2 will pick it up)
3. `Get-AuthenticodeSignature MastersFM_Setup.msi` -> Status=Valid CN=MasterShadex
4. soak monitor PID 3148 still alive (or completed cleanly)

## Halt point

**Per addition #5: HALT at STEP 8 with this proposed list for Orken approval BEFORE running
any `git add` / `git commit` / `git tag`.** No mutation occurs in STEP 8 until Orken
green-lights the allowlist/denylist split.

## On approval, exact commands

```powershell
cd "G:\Project Folder\Master FM"

# Stage Commit 1: source + build pipeline + new source dirs + V14 docs
git add _full_rebuild.ps1
git add build_tools/build_msi.py
git add src/install_bootstrapper.cs
git add src/launcher.cs
git add src/tray.ps1
git add -u src/tray_native.cs           # records the deletion
git add src/Directory.Build.props
git add src/launcher.csproj
git add src/audio_spectrum.csproj
git add src/customize.csproj
git add src/install_bootstrapper.csproj
git add src/server_dotnet/
git add src/tray_native/
git add test-ps51-load.ps1

# Allowlist V14 docs (in batches; copy from list above)
git add V14_NET8_MIGRATION_PLAN.md V14_RES_*.md
git add V14_S4_*.md V14_S4_P1_BASELINE_CAPTURES/
git add V14_S5_*.md
git add V14_S6_*.md
git add V14_RC1_VERSION_BUMP_AUDIT.md V14_RC1_FLAGS_FINALIZED.md V14_RC1_AUTOUPDATE_VERIFICATION.md
git add V14_RC1_VALIDATION.md V14_RC1_HOTFIX_PLAYBOOK.md V14_RC1_GIT_PLAN.md
git add RELEASE_NOTES_v14.0.0-rc.1.md TESTER_ANNOUNCEMENT_v14.0.0-rc.1.md
# (V14_RC1_FINAL_REPORT.md added after STEP 12 produces it)

# Verify version.json + memory.md still unstaged
git status -- version.json md/memory.md   # both should show "modified:" with no staging marker

# Commit 1
git commit -m "v14.0.0-rc.1: Stages 1-3 + 4 + 5 cumulative migration to .NET 8"

# Apply STEP 7 memory APPEND (Edit the file with the one-liner from the draft)
# (Edit done via Edit tool, not git)

# Commit 2
git add md/memory.md
git commit -m "v14.0.0-rc.1: memory.md APPEND"

# Tag
git tag -a v14.0.0-rc.1 -m "v14.0.0-rc.1: cumulative .NET 8 migration RC; see RELEASE_NOTES_v14.0.0-rc.1.md"

# Final status check
git status                             # should be clean modulo version.json + denylist files
git log --oneline -3                  # should show the two new commits
git tag -l v14.0.0-rc.1               # confirm tag
```

End of git plan.
