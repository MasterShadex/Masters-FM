# rc.3 publication hold

**Status:** rc.3 GitHub Release is staged as DRAFT and NOT published.

**Reason:** Operator conducted real-world testing of the installed rc.3 build and
identified 10 issues that need investigation and fixing before public release. The
work cycle is paused at the publish-gate; the draft on GitHub stays in place; the
git tag `v14.0.0-rc.3` on remote stays in place; no testers have been notified.

**Issues identified (full diagnosis in V14_S7_11_DIAG_*.md):**
1. Left-click tray icon opens menu on wrong monitor
2. Tray menu missing icons in front of several text items
3. Random bar/text misalignment across dialogs (Audio Source tab labels cut off, etc)
4. KS and ASIO support missing/incomplete in Audio Source dialog
5. Customize Overlay visual identical to v12; never changed
6. Patch Notes menu item opens Setup Wizard instead of Patch Notes
7. View Log menu item opens folder instead of opening log file directly
8. Check for Updates window opens on wrong monitor
9. OBS overlay add/remove still does not actually take effect in OBS after restart
10. Discord RPC does not work

**Next steps:**
- Stage 7.11 diagnoses all 10 issues (this brief, read-only)
- Per-issue fix briefs land one at a time with operator hands-on verification per fix
- Soak only after all fixes confirmed
- rc.3 publication decision after fixes land OR rebuilt rc.3 supersedes the current draft

**Unchanged on remote:**
- Tag `v14.0.0-rc.3` (do NOT delete without explicit operator instruction)
- Main branch commits (kept as-is for tag stability)
- GitHub release (sits as DRAFT pending operator decision)