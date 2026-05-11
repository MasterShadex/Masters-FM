# Diagnosis: Issue 5 -- Customize Overlay Visually Unchanged Since v12

## Reproduction (operator-confirmed)
Operator reports Customize Overlay window (opened via tray menu "Customize overlay") looks
visually identical to v12. Expected it to have received the Stage 7.7B visual rebuild treatment.

---

## Source-of-truth analysis

### Confirmation that customize.html was never touched in Stage 7.x

**File:** `src/customize.html`

Inspection of the file confirms a v12-era design system:
- CSS custom properties use raw hex values (`--bg: #08080f`, `--accent: #9333ea`)
- Font stack: `'Inter', system-ui, sans-serif` -- NOT Segoe UI (the v14 design system font)
- Layout uses flat CSS classes (`topbar`, `sidebar`, `panel`) -- no WPF design system integration
- No reference to the Stage 7.7B brand design tokens (Surface1/2, TextPrimary/Secondary, BrandPurpleBase, AccentBarGradient, etc.)

The v12 customize.html is a self-contained browser-based UI (HTML/CSS/JS served by server.exe). It has its own styling independent of the WPF design system.

### Brief lineage confirmation

Every Stage 7.7B brief explicitly declared these files OUT OF SCOPE:

Stage 7.7B Brief (original): "customize.html and overlay.html are server-side surfaces. They are explicitly out of scope for this brief. They form their own visual workstream."

Stage 7.7B-FIX Brief and Stage 7.7B FINAL Brief: Same carve-out. No commits in Stage 7.x history touched `src/customize.html`, `src/overlay.html`, or related server-side templates.

Current RELEASE_NOTES_v14.0.0-rc.3.md (Known Issues section, line 63-64):
"Customize Overlay (server-side) and overlay.html have not received the visual rebuild treatment in rc.3; planned for rc.4 or v14.1.0"

This is already documented as a known issue in the shipped release notes.

### Design system gap

The Customize Overlay is a full-featured UI with:
- CSS variables (`--bg`, `--accent`, `--text-muted`, `--sidebar-bg`, etc.)
- Inter font (Google Fonts CDN)
- Multiple CSS classes for layout, components, animations

A visual rebuild to match Stage 7.7B design tokens would require:
- Replacing Inter font with Segoe UI (to match WPF dialogs) OR keeping Inter (it's a web surface)
- Replacing flat CSS with the v14 color palette (`--bg: #0d0d1a`, `--accent: #8c46ff`, etc.)
- Updating card/panel styles to match Surface1/Surface2 token equivalents
- Updating button styles to match the v14 button design
- This is a medium-to-large effort (css/html/js rewrite): ~4-8 hours Ruflo

---

## Root cause

**No bug.** The Customize Overlay was intentionally deferred from the Stage 7.7B visual rebuild. All briefs since Stage 7.6 explicitly excluded `customize.html` and `overlay.html` from scope. The current state (v12 visual) is the expected state per every brief that shipped code.

The operator's expectation is reasonable -- v14 ships with a fully rebuilt tray UI and dialogs, but the browser-based Customize Overlay still looks like v12. From a user's perspective, this is an inconsistency in the product experience even if it was a planned scope deferral.

---

## Operator expectation vs. brief scope

| | Operator expectation | Brief scope |
|---|---|---|
| Tray dialogs | v14 design system | YES -- delivered in Stage 7.7B |
| Context menu | v14 design system | YES -- delivered in Stage 7.6 |
| Customize Overlay | v14 design system | NO -- explicitly deferred |
| overlay.html | v14 design system | NO -- explicitly deferred |

The release notes already acknowledge this gap. But the operator seeing it during real-world testing confirms it matters for the final product experience.

---

## Fix complexity

**Large** (full visual rebuild of a web-based UI, separate workstream from WPF):
- Requires redesigning CSS variables, typography, component styles
- Must coordinate with server-side asset serving (customize.exe / server.exe serve the file)
- No WPF code changes -- pure HTML/CSS/JS work
- Estimated: 4-8 hours Ruflo + operator review

---

## Recommendation

**This is a scope decision for the operator, not a Ruflo-decision fix.**

Two options:
1. **Schedule as Stage 7.12 (separate brief, before v14.0.0 GA release).** If this inconsistency is unacceptable for public release, schedule a dedicated brief for customize.html + overlay.html visual rebuild. The brief already has release notes saying "planned for rc.4 or v14.1.0" -- revise that to rc.4 if the operator wants it before publish.
2. **Accept as-is for v14.0.0 release (remain on v12 UI).** The visual inconsistency is minor and functional. Update the release notes known-issues section to be explicit that customize.html is the v12 UI and will be updated in a post-1.0 release.

**Recommendation:** Option 2 for the immediate rc.3/rc.4 cycle. The Customize Overlay is functional and the operator-reported issue is visual only. It doesn't block music detection, OBS integration, or any P0 issue. Schedule it as Stage 7.12 alongside or after the P1 fixes.

---

## Verification after fix (when scheduled)

1. Open Customize Overlay via tray menu
2. Confirm the visual style matches v14 design system: dark background, brand-purple accents, Segoe UI or Inter fonts with v14 color tokens, consistent with tray dialogs
3. Confirm all existing customize functionality (overlay position, font, color, layout options) still works
