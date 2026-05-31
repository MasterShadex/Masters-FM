# V14 Stage 7.31 -- REPORT (CLOSED -- operator "Close 7.31") -- VERDICT: HARDEN-FIRST

Update-path audit + Probe 1 (Authenticode chain-trust). READ-ONLY stage: NO source/protected/HTML
changes, NO version bump (14.0.0 throughout), NO GitHub push/tag/release (remote untouched, local
ahead 340). Strikes 0/3. Probes 2-3 descoped by operator after Probe 1 delivered the verdict.

## The update mechanism (full flow -- previously undocumented)

Custom auto-updater; the ENGINE is entirely in the WPF tray (`src/tray_csharp/Update/
UpdateCheckService.cs`), a 7-state machine. server.js is RETIRED legacy; launcher.cs is a process
host only; server_dotnet's GET /update + /update-status + /version are UI/status surfaces only.

PUBLISH (operator -- the ONLY GitHub-touching path):
  1. bump version in version.json; run `_full_rebuild.ps1` -> builds + SIGNS the MSI with a
     SELF-SIGNED cert `CN=MasterShadex, O=MasterShadex` (build_tools/signing/_sign_msi.ps1,
     CurrentUser\My) + STAMPS version.json { version, msi_url=releases/download/v<ver>/Masters-FM-V<ver>.msi,
     msi_sha256 (of the signed MSI), autoInstall=false }.
  2. operator MANUALLY creates a GitHub Release (tag v<ver>) + uploads the MSI.
  3. `_push_update.ps1` -> flips autoInstall=true (unless -NoAutoInstall) -> commits version.json ->
     **git push origin main**. <-- the only `git push`; the single action that triggers everyone.

CLIENT (friend's WPF tray, every 6h + on-demand "Check for Updates"):
  - GET https://raw.githubusercontent.com/MasterShadex/Masters-FM/main/version.json (cache-busted).
  - SemVer compare vs the tray assembly's version; newer-stable-only (pre-release + downgrade refused).
  - download msi_url -> %LOCALAPPDATA%\MastersFM\downloads\*.partial.
  - 3-GATE verify: (1) SHA256 == manifest msi_sha256 ; (2) Authenticode chain.Build + CN=MasterShadex ;
    (3) atomic rename. Then msiexec /i /quiet /norestart + tray shutdown. autoInstall=true auto-chains.
  - any failure (bad JSON / 404 / truncated / hash / signature) -> delete partial + revert; never bricks.
BLAST RADIUS: a push to main reaches every installed friend within ~6h.

## Dry-run executed: Probe 1 (Authenticode chain-trust) -- operator-approved "just Probe 1 first"

Replicated VerifyAuthenticode exactly against the actual signed MSI (MastersFM_Setup.msi, the
v14.0.0 candidate; V12.0.1 past release identical). Evidence: evidence/s7_31/probe1_authenticode.txt.
  - Signature Valid; signer CN=MasterShadex; SELF-SIGNED (Issuer==Subject).
  - chain.Build [this machine]      = True  (only because CN=MasterShadex is in this box's
    CurrentUser\Root + LocalMachine\Root -- a false positive).
  - chain.Build [friend-sim no-root]= False (UntrustedRoot), via net8.0 X509ChainTrustMode.
    CustomRootTrust with an empty trust store = a friend's clean machine.
  - => a friend's VerifyAuthenticode returns FALSE.

## VERDICT: HARDEN-FIRST

The auto-update path is SAFE (a release will NOT brick friends -- reject -> delete -> revert) but
NON-FUNCTIONAL for friends: their tray rejects every legitimately-signed MSI at the Authenticode
gate because the self-signed publisher cert isn't a trusted root on their machines. Auto-update has
almost certainly NEVER completed end-to-end for anyone but the operator (whose box trusts the cert).

WHAT THIS MEANS FOR RELEASING NOW:
  - Pushing to GitHub is not dangerous, but friends will not receive the update via the app.
  - To deliver v14 today: send friends the MSI to reinstall manually (the first-install path), OR
    do 7.31.1 first so auto-update works.

## HARDEN-FIRST punch list (Stage 7.31.1 -- NOT done here)

1. [RECOMMENDED] Pin the publisher cert in UpdateCheckService.VerifyAuthenticode: validate the
   downloaded MSI's signer against the app's KNOWN publisher cert (compare thumbprint, OR set it as
   a CustomRootTrust root) instead of requiring the machine's root store to build the chain. Keeps
   the "only our cert" guarantee; no admin, no system trust-store pollution. UpdateCheckService.cs
   is editable WPF-tray code (NOT a protected file). Verify by re-running the friend-sim post-fix.
2. [ALT] Install MastersFM_publisher.cer into the friend's Trusted Root at first install (re-enable
   the disabled bootstrapper's cert step) -- needs admin for LocalMachine\Root; AV-sensitive.
3. [PROPER] Obtain a real CA-issued code-signing cert (Certum pending) -- chain builds on any
   machine + improves SmartScreen. Bigger lift.
Also confirm the manifest-decision logic + download/SHA fail-safe behavior (Probes 2-3, not run this
stage) as part of 7.31.1 verification.

## Integrity (read-only stage)
- 4 protected + 3 source HTML SHA256: ALL MATCH the S0.2 baseline (unchanged).
- version.json 14.0.0 (no bump). origin/main UNTOUCHED (local ahead 340; no push/tag/release ran).
- No msiexec executed; throwaway net8.0 probe lived in %TEMP% and was torn down.

## Commits (2, since 60e2ebc) -- plus closure
`77a8209` STEP 0 audit / `c37c4a4` Probe 1 verdict. (+ this closure commit.)
