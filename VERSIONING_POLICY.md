# Master's FM — Versioning Policy

## Rules

This project uses semantic versioning with the following STRICT rules:

### Patch increments (MAJOR.MINOR.PATCH → MAJOR.MINOR.PATCH+1)
**Always.** Every routine update is a patch increment.

This includes:
- Bug fixes
- Performance improvements
- Small features
- Defensive code changes
- Audit-worker findings applied
- Anything that doesn't replace a whole subsystem

Example: 12.0.0 → 12.0.1 → 12.0.2 → ... → 12.0.9

### Minor increments (MAJOR.MINOR.PATCH → MAJOR.MINOR+1.0)
**Only when patch rolls over from .9.**

When the next patch would be `.10`, increment minor instead and reset patch to .0.

Example: 12.0.9 → 12.1.0 → 12.1.1 → ... → 12.1.9 → 12.2.0

### Major increments (MAJOR.MINOR.PATCH → MAJOR+1.0.0)
**Only for architectural refactors where a whole subsystem is replaced.**

Examples that QUALIFY:
- v11.x → v12.0.0: SMTC integration switched from polling to event-driven (whole subsystem replaced via new `MasterFM.SMTC.SMTCWatcher` C# class in tray_native.dll)

Examples that DO NOT qualify:
- Bug fixes, however many
- Performance improvements, however large
- New features that don't replace existing subsystems
- Audit-worker findings, however numerous
- Refactoring code style, file organization, or naming conventions
- Adding new audio backends (would still be a patch or minor — not replacing the SMTC subsystem)
- Adding new UI dialogs (patch)
- Adding a new platform integration (patch unless it replaces an existing one)

If unsure whether a change is "architectural enough" for major bump: **it's not.** Ship as patch or minor.

## Enforcement

- This policy is a HARD CONSTRAINT. It overrides any version suggestion from Ruflo agents, Claude Code sessions, or human suggestions in the moment.
- Future Ruflo coordinator agents and Claude Code sessions must read this file as part of their context.
- If a session proposes a version bump that violates this policy, the session must STOP and ask the human before proceeding.
- The `tray.ps1` `$script:APP_VERSION` string is the single source of truth at build time; the build pipeline reads it to name the MSI and write `version.json`.

## History

- **2026-05-04** — Policy established. Retroactive correction: v12.1.0 (in-flight, built locally, never pushed) → renumbered to v12.0.1. Bug A/B/C fixes (patch notes virtualization + first-launch hang resolution + defensive timer-disposal cleanup) shipped as v12.0.1.
- **v11.2.x history is grandfathered** — those releases (v11.2.1, v11.2.2, v11.2.3) are correct under the prior conventions and remain in PATCH_HISTORY unchanged.
- **v12.0.0 is the canonical major-bump example** — architectural SMTC refactor (polling → event-driven) replaced an entire subsystem, qualifying for the major bump. Future architectural refactors should look to v12.0.0's scope as the bar.
