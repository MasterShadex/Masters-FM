# V14 Sub-stage 4.5 -- Intentional Differences

**Date:** 2026-05-06
**Sub-stage:** 4.5 (config endpoints + preset management)

---

## ID-11: POST /save-overlay-config uses straight replacement, not deep-merge

**Description:** The CLAUDE_CODE_INSTRUCTIONS.md spec says "Replace `cfg.overlay` with
deep-merge(current cfg.overlay, incoming patch)". The actual server.js (line 1494) does
`cfg.overlay = overlay` -- a straight REPLACEMENT of the entire overlay field.

**Reason for straight replacement:** The customize page sends the full overlay config
object on every save (not a partial patch), so merging is unnecessary. Deep-merge is
used only in `migrateConfig()` to fill in NEW default keys across version upgrades.

**C# implementation:** `cfg["overlay"] = incomingOverlay` (straight replacement per server.js).

**Impact:** Identical behavior for all current usage patterns.

---

## ID-12: POST /reload-config -- Discord RPC reload stubbed (4.10 deferred)

**Description:** server.js reads discord_rpc settings and calls discord.init() or
discord.destroy() if they changed. The C# implementation reads config.json and logs
"Config reloaded" but does not act on Discord RPC settings (Discord RPC port is 4.10).

**Impact:** Reload-config returns 200 OK and logs correctly; no Discord effect until 4.10.

---

## ID-13: GET /overlay-config -- no config_default.json merge at GET time

**Description:** server.js `GET /overlay-config` reads `cfg.overlay || {}` and injects
`liveAudioVisualizer` directly from cfg. It does NOT deep-merge with config_default.json
at GET time (that happens only in `migrateConfig()` at server startup).

**C# implementation:** Same -- reads cfg.overlay, injects liveAudioVisualizer, no merge.

---

## ID-1 through ID-10 (inherited from 4.2/4.3/4.4)

See V14_S4_S4_2_INTENTIONAL_DIFFERENCES.md, V14_S4_S4_3_FINAL_REPORT.md,
and V14_S4_S4_4_INTENTIONAL_DIFFERENCES.md.
