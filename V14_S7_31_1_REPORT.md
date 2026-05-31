# V14 Stage 7.31.1 -- REPORT (CLOSED -- operator PASS) -- auto-update now works for friends

Cert-pinning fix for the 7.31 HARDEN-FIRST verdict. Operator gate: PASS. Strikes 0/3. ONLY
`src/tray_csharp/Update/UpdateCheckService.cs` changed. NO real GitHub (remote untouched, ahead 344).
NO version bump (14.0.0). NO protected / source-HTML changes.

## The problem (from 7.31)
The auto-updater's `VerifyAuthenticode` required the downloaded MSI's signer chain to BUILD
(`chain.Build`). The publisher cert is SELF-SIGNED (`CN=MasterShadex`), so the chain only builds on a
machine that trusts it as a root. Friends don't -> `UntrustedRoot` -> every legit MSI rejected ->
auto-update never reached anyone but the operator.

## The fix (Pin-A: thumbprint + public-key pin)
`VerifyAuthenticode` now:
  1. `X509Certificate.CreateFromSignedFile(msi)` -- throws on an unsigned file (-> reject).
  2. Requires the signer's **thumbprint** == `4B1660FC0B77F55C7D47B3B9010C873E5CC2B2BF` AND its
     **RSA public key** == the pinned 540-hex SPKI (both OrdinalIgnoreCase).
  3. Keeps the `CN=MasterShadex` guard (defense-in-depth).
  4. NO `chain.Build`, NO machine-Trusted-Root dependency -> verifies identically on every machine.
PRESERVED UNCHANGED: the SHA256 gate (the real file-integrity anchor, catches tampering/mismatch),
failure-delete-revert, downgrade + pre-release rejection, the whole state machine + install path.
NOT loosened to "any valid signature" -- a different/forged/unsigned signer is still rejected.

## Verification
- WPF tray compiles 0 warnings / 0 errors; cold rebuild SE2 clean (VBCSCompiler pre-killed, WPF +
  server publish OK, REBUILD DONE OK); pinned-updater tray + server relaunched + HTTP 200; install
  customize.html SHA == source 33D09CDF.
- Two-sided friend-sim (empty-root .NET 8, evidence/s7_31_1/friend_sim.txt):
    our signed MSI : OLD chain.Build=False (the 7.31 bug)  ->  NEW pinned=TRUE   ACCEPT
    wrong-cert     : NEW=False (thumbprint mismatch)                             REJECT
    unsigned       : NEW=False (no signature)                                    REJECT
    tampered/SHA   : SHA256 gate mismatch -> delete+revert                       REJECT
  Throwaway negative-test cert created + signtool-signed a copy + removed (0 remaining); MasterShadex
  signing cert intact; temp scaffolds torn down.

## Integrity (only UpdateCheckService.cs changed)
- 4 protected (tray.ps1 / tray_native.cs / launcher.cs / server.js) + 3 source HTML (customize
  33D09CDF / legacy 7E98377D / overlay 9A7CC817): ALL UNCHANGED.
- UpdateCheckService.cs new SHA256: E4B653DEB65E15D3AEB38082EC78636AA89260EC98E2E23B36722A4EDA04048A.
- version.json 14.0.0 (no bump). origin/main UNTOUCHED; no push/tag/release ran.

## What this enables
On the NEXT real release, friends' trays will ACCEPT the signed MSI and auto-update -- no machine
trust-store change needed. Auto-update is now functional end-to-end for friends.

## Release is operator-only (NEVER Ruflo)
Shipping a real update is the operator's manual flow: build (`_full_rebuild.ps1`) -> create a GitHub
Release tag v<ver> + upload the MSI -> `_push_update.ps1` (commits version.json + `git push origin
main`). Ruflo never touches the real GitHub.

## Recommended before relying on it for a real release (optional)
A real SECOND machine (a friend's PC or a fresh VM without CN=MasterShadex in any store) running an
actual end-to-end auto-update is the gold-standard confirmation. The empty-root friend-sim is a
faithful proxy and PASSED, but a live second-machine test removes all doubt.

## MAINTENANCE NOTE (important)
The pin is tied to the CURRENT signing cert (valid to 2031-04-21). If that cert is ever regenerated
(expiry, or deleted from CurrentUser\My so _sign_msi.ps1 mints a new one), BOTH PinnedThumbprint and
PinnedPublicKeyHex in UpdateCheckService.cs MUST be updated or the updater will reject the new MSIs.

## Commits (3) + closure
`ba3875a` STEP 0 / `08fc516` STEP 1 / `f5b056e` STEP 2+3 / (+ this closure). v14.0.0 stable.
