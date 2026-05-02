# V10.0.4 — Final Report

## Status

| Item | Status |
|------|--------|
| v10.0.3 backup | ✅ `C:\_BACKUPS_v10\Master FM_v10_0_3` (4663 files, 234MB) |
| v10.0.3 MSI preserved | ✅ `C:\_BACKUPS_v10\Master's FM V10.0.3.msi` |
| v10.0.4 MSI built | ✅ `G:\Project Folder\Master FM\Master's FM Install\MastersFM_Setup.msi` (12.3MB) |
| v10.0.4 MSI signed | ✅ Valid — CN=MasterShadex, SHA-256 verified |
| GitHub Release v10.0.4 | ✅ https://github.com/MasterShadex/Masters-FM/releases/tag/v10.0.4 |
| MSI asset uploaded | ✅ `Masters-FM-V10.0.4.msi` (12.9MB) |
| version.json on main | ✅ v10.0.4, autoInstall=true, commit 6ccf0f0 |
| version.json live | ✅ raw.githubusercontent.com returns v10.0.4 |
| v10.0.4 installed locally | ❌ INTENTIONAL — user stays on v10.0.3 for test |

## The Visible Change

**File:** `src/tray.ps1`  
**What changed:** `$script:APP_VERSION` bumped from `"v10.0.3"` to `"v10.0.4"`  
**What you'll see:** Open the tray menu — the header line reads `"Master's FM  ·  v10.0.4"` (was v10.0.3)

No other code changes. This is a version-bump-only build to give the update test a verifiable result.

## How to Run the Test

1. You are currently running **v10.0.3** — confirm by clicking the tray icon (header says "Master's FM  ·  v10.0.3")
2. In the tray menu, click **"Check for Updates"**
3. Wait ~10-30 seconds:
   - A balloon notification should appear: **"Update available: v10.0.4"** (or similar)
   - Because `autoInstall=true`, the MSI should download and install silently in the background
   - The tray icon will disappear briefly, then reappear (LaunchApp CA restarts it)
4. After restart, click the tray icon
5. **Success:** Header reads `"Master's FM  ·  v10.0.4"`

## Failure Modes and Diagnosis

| Symptom | Likely cause |
|---------|-------------|
| Nothing happens at all | `_updateUserCheck` flag bug (v10.0.3 had a fix for this; should be resolved) |
| Balloon says "You're on the latest version" | Version comparison broken — 10.0.4 not reading as newer than 10.0.3 |
| Balloon says "Update available" but nothing installs | `autoInstall` flag not being read, or MSI download failing |
| Tray restarts but version still shows v10.0.3 | `APP_VERSION` constant not updated in the v10.0.4 build (shouldn't happen — verified before build) |
| Balloon shows error | Check `%LOCALAPPDATA%\MastersFM\startup.log` for the error |

## version.json (live on GitHub main)

```json
{"msi_sha256":"7ebe6a7a930b3e987211cdcfa97f76b9a7c312de531fb77410499f14bed1a962","autoInstall":true,"version":"10.0.4","msi_url":"https://github.com/MasterShadex/Masters-FM/releases/download/v10.0.4/Masters-FM-V10.0.4.msi"}
```

## Sworn Statement

- Source files modified: `src/tray.ps1` (version + PATCH_HISTORY only), `version.json` (version + sha256 + autoInstall)
- No build pipeline edits (`_full_rebuild.ps1`, `build_msi.py`, `_sign_msi.ps1`, `INSTALL.bat`, `build_tools\`)
- No new runtime dependencies
- All v10.0.3 fixes preserved (checking-state handler, userCheck flag, balloon tip, tooltip flash, Authenticode fix)
- v10.0.4 NOT installed locally — user's tray is running v10.0.3

---

`V10.0.4 SHIPPED FOR TESTING — User is on v10.0.3, v10.0.4 is on GitHub. Test by clicking Check for Updates.`
