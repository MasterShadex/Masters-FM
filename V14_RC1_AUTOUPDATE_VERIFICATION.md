# V14_RC1_AUTOUPDATE_VERIFICATION.md

CRITICAL HALT GATE: STEP 4. Determine whether v14.0.0-rc.1 will auto-push to general testers.
Verification start: 2026-05-07 ~17:25.

## Summary verdict

**NO HALT REQUIRED.** The auto-update path will NOT push v14.0.0-rc.1 to v12.0.1 testers.
The protection mechanism is incidental (PowerShell `[version]` cast strictness rejects SemVer
pre-release suffixes) but is a robust silent-no-op: the exception is caught and state returns
to idle, no balloon, no download, no install. Tester opt-in to RC1 happens only via manual
GitHub Pre-release page download.

## STEP 4.1 -- /update-status endpoint in server_dotnet (Program.cs:398-423)

The endpoint reads `%TEMP%\mastersfm_update_status.json` as raw bytes (preserves UTF-8 BOM
written by tray.ps1) and returns it. Cache-Control: no-store. Fallback (file missing):
`{"state":"idle","version":null,"progress":0,"bytesDown":0,"bytesTotal":0,"current":null,"ts":0}`.

The .NET 8 endpoint mirrors the legacy server.js `/update-status` byte-for-byte. The endpoint
itself does NOT compare versions, query GitHub, or push updates. It is purely a status-relay
for the in-progress download/install state machine that lives in tray.ps1.

## STEP 4.2 -- Legacy /update-status in server.js (line 1324-1334)

Identical behaviour. Reads `os.tmpdir()/mastersfm_update_status.json` and returns it. Same
fallback. The .NET 8 port preserves this exactly.

## STEP 4.3 -- Client-side polling code (tray.ps1)

The actual auto-update logic is in `tray.ps1`, not in the server. Key locations:

### 4.3.1 Manifest URL (tray.ps1:5188)
```
$global:_updateManifestUrl = 'https://raw.githubusercontent.com/MasterShadex/Masters-FM/main/version.json'
```

This fetches the **raw `version.json` from the main branch HEAD on GitHub** -- NOT the GitHub
Releases API. The "Pre-release" flag on a GitHub release does NOT affect this fetch path.
**This was the brief's biggest flagged risk:** "Pre-release flag on the release page may not
prevent the auto-update path from serving RC1." Confirmed: the path bypasses Releases entirely.

### 4.3.2 Poll interval (tray.ps1:5730)
```
$intervalMs = 1 * 60 * 60 * 1000   # 1 hour (v10.2.3: was 6 hours)
```

Every running tester checks once per hour.

### 4.3.3 Version comparison (tray.ps1:5742-5746)
```
$json   = $global:_updateCheckTask.Result | ConvertFrom-Json
$remote = [version]($json.version)
$local  = [version]($script:APP_VERSION.TrimStart('v'))
if ($remote -gt $local) {
    ...prompt user to update or auto-install...
}
```

**THE PROTECTION MECHANISM:** PowerShell's `[version]` type accelerator strictly requires
numeric `Major.Minor.Build[.Revision]` format. It does NOT parse SemVer pre-release suffixes.

- `[version]"14.0.0-rc.1"` -> throws `Cannot convert value "14.0.0-rc.1" to type "System.Version"`
- `[version]"14.0.0"` -> 14.0.0 (parses cleanly)
- `[version]"12.0.1"` -> 12.0.1

When v12.0.1 testers poll a version.json containing `"version":"14.0.0-rc.1"`:
1. Line 5744: `$json` parses successfully (it's valid JSON)
2. Line 5745: `[version]("14.0.0-rc.1")` THROWS `RuntimeException`
3. Control jumps to the catch block at line 5783-5785
4. `$global:_updateState = 'idle'` resets the state
5. `LogErr 'Poll-UpdateCheck manifest' $_` logs the exception (visible only in transcript.log)
6. `finally` block at 5786-5790 clears `$_updateUserCheck` and disposes the completed task
7. NO balloon shown, NO download started, NO install scheduled

**Net effect:** old testers see a silent log entry and remain on v12.0.1. They do NOT auto-update.

### 4.3.4 Auto-install behaviour (tray.ps1:5751, 5758)

```
$global:_updateAutoInstall = [bool]$json.autoInstall
...
if ($global:_updateAutoInstall) { Start-UpdateDownload }
```

These lines are INSIDE the `if ($remote -gt $local)` block (line 5747). If the `[version]`
cast throws BEFORE line 5747 evaluates, the autoInstall path is never entered. Even if
version.json sets `"autoInstall":true`, the silent-fail prevents any auto-install.

This is also why version.json's `autoInstall` is conventionally `false` (and is `false` in the
current build pipeline output): even setting `true` would be ineffective for testers running
older versions whose [version] cast cannot parse RC1.

## STEP 4.4 -- Where "current latest version" comes from

Three sources, in order of authority:

1. **`https://raw.githubusercontent.com/MasterShadex/Masters-FM/main/version.json`** -- the
   single source of truth for tester auto-update polls. Generated locally by `_full_rebuild.ps1`
   from `tray.ps1`'s `$script:APP_VERSION` and pushed to main when committed.

2. **GitHub Releases tag** -- testers can manually browse to
   `https://github.com/MasterShadex/Masters-FM/releases` and download the .msi. The `--prerelease`
   flag on `gh release create` ensures RC1 is not the "Latest release" link target. Manual
   download is the intended RC1 install path.

3. **`/update-status` endpoint** -- relays in-progress state from the running tray's
   download/install state machine. Not a source of new versions; just status reporting.

## STEP 4.5 -- Determination

**With version.json bumped to "14.0.0-rc.1" and pushed to main:**

- Existing v12.0.1 testers: poll fails [version] cast, silent no-op, no auto-update. SAFE.
- Future RC1 testers (post-manual-install): poll fails their own version's [version] cast (also
  "14.0.0-rc.1"), so RC1 testers ALSO won't auto-update via this path until a future stable
  version is pushed. This means RC2 / stable v14.0.0 cannot ship via this auto-update path
  while the version remains in pre-release form. **For stable v14.0.0:** push version.json
  with `"version":"14.0.0"` (no suffix) and the [version] cast will compare cleanly across
  all installed versions.
- Manual download from Releases page: serves only the asset the tester clicks. Pre-release
  tag on the release ensures RC1 is not the default-link target.

**Conclusion: PROCEED.** RC1 ship to GitHub is safe. The [version] cast strictness acts as a
natural pre-release filter for the auto-update path. Document this in the final report so
future-Ruflo knows to swap to a clean numeric version when promoting RC -> stable.

## Open question for stable v14.0.0 (NOT RC1-blocking)

When promoting RC1 to stable v14.0.0 (after tester feedback), the auto-update path needs to
handle the version comparison correctly. Options:

- **A.** Bump from `v14.0.0-rc.1` -> `v14.0.0` directly. Old testers' [version] cast: "12.0.1"
        vs "14.0.0" cleanly compares; auto-update fires. RC1 testers' [version] cast:
        "14.0.0-rc.1" still fails for THEIR local APP_VERSION; but the `$remote = [version]("14.0.0")`
        succeeds, and PowerShell evaluates `[version](throws)` -> exception -> caught -> idle.
        So RC1 testers would NOT auto-update to v14.0.0 either. They'd need to manually download.
        This is acceptable: RC1 testers are a small opt-in group who can be manually nudged to
        download stable v14.0.0 via Discord.

- **B.** Patch tray.ps1 in stable v14.0.0 to handle SemVer pre-release suffixes (strip `-X.Y`
        before [version] cast, OR use a custom comparator). This makes future RC -> stable
        transitions automatic. Worth doing in stable v14.0.0 ship.

For RC1: neither option is needed. RC1 ships safely with the existing [version] cast behavior.

## ADDITION A (post-halt directive) -- primary safety: version.json unchanged on main

Verified directly via `git show HEAD:version.json`:

```
{"msi_sha256":"551011ad360226942cf9133027e1f208ea57a1b7ad772b0127409a3104596973",
 "autoInstall":true,
 "version":"12.0.1",
 "msi_url":"https://github.com/MasterShadex/Masters-FM/releases/download/v12.0.1/Masters-FM-V12.0.1.msi"}
```

The version.json on `origin/main` (last committed at `84cd20e release v12.0.1`) contains
`"version":"12.0.1"`. The working tree has been regenerated by the build pipeline to
`"version":"14.0.0-rc.1"` but is NOT in the STEP 8 commit set. The published version.json on
GitHub raw URL stays at v12.0.1.

Updated safety stack (in priority order):

1. **PRIMARY:** version.json on `origin/main` is left at v12.0.1. Old v12.0.1 testers polling
   `https://raw.githubusercontent.com/MasterShadex/Masters-FM/main/version.json` see
   `version=12.0.1`, identical to their local `$script:APP_VERSION="v12.0.1"`. The `[version]`
   cast succeeds for both ($remote=12.0.1, $local=12.0.1), comparison is `12.0.1 -gt 12.0.1`
   = $false, NO update prompt, NO download, NO install. RC1 is invisible to the auto-update
   path because the version.json that drives it has not changed.
2. **SECONDARY (backstop):** if version.json IS accidentally pushed with `version=14.0.0-rc.1`,
   the `[version]` cast at `tray.ps1:5745` throws on the pre-release suffix; exception is
   caught at line 5783-5785; state returns to idle; no balloon, no download, no install.

Both layers must fail simultaneously for an RC1 leak to general testers. Primary is a
deliberate omission from the commit (low-risk discipline); secondary is structural (PowerShell
[version] strictness). Addition A makes the discipline explicit so that a future committer
does not accidentally `git add version.json` along with other RC1 changes.

## STEP 8 commit-set exclusion (verified)

The STEP 8 commit set MUST NOT include `version.json`. Pre-commit verification: run
`git status -- version.json` and confirm it shows as `modified:` in "Changes not staged for
commit." If it appears in the staged/committed set, halt and revert the staging.

End of auto-update verification (with Addition A appended).
