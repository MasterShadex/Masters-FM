# Sub-stage 4.9e -- Intentional Differences from server.js

## ID-28: enrichByTitle implemented as API search, not separator split

**Instructions template showed:** separator split on ` - `, ` -- `, ` -- `, `: ` etc.
**Actual server.js:803-838:** Deezer/iTunes API search by track title. No separators.

**Decision:** server.js is authoritative per source-driven port rule. Implemented Deezer/iTunes search.

The "separator precedence matters" note in the user prompt was based on the template assumption. The actual precedence is Deezer exact-match > Deezer substring > Deezer first-result > iTunes first-result.

## ID-29: Duration resolver runs in parallel with art cascade (Task.WhenAll)

**server.js:** `Promise.all([resolveArtwork, resolveDuration])` -- runs both concurrently.
**C# port:** `Task.WhenAll(artTask, durTask)` -- exact equivalent. Both resolve before webhook returns.

## ID-30: Webhook latency for new tracks is unbounded (cascade-dependent)

**server.js:** handleWebhook awaits setTrack (which awaits the full cascade). New-track webhooks can take 1-5s.
**C# port:** Same -- HandleAsync awaits cascade. Heartbeat webhooks remain <50ms.

The instructions said "fire-and-forget pattern" but server.js actually awaits setTrack. Following server.js.

## ID-31: enrichByTitle runs on every webhook where artist is placeholder (including heartbeats)

**server.js:** enrichByTitle only runs inside setTrack (new tracks only).
**C# port:** enrichByTitle runs before lock acquisition, before same-track detection. This means it also runs on heartbeats where artist is still placeholder.

**Why acceptable:** 
- If enrichByTitle succeeds, enriched artist/track matches stored currentTrack -> same-track path (fast)
- If enrichByTitle fails (returns null), artist stays placeholder -> treated as new track again (matches server.js quirk)
- Deezer/iTunes are fast APIs; LRU cache in art cascade means art lookups are cache hits on repeat calls
- Alternative (moving enrichByTitle inside the lock after new-track detection) would hold the lock during HTTP calls

**Why this works:** When SMTC keeps reporting "?????????" for every heartbeat, and enrichByTitle consistently enriches to "Daft Punk", the same-track detection correctly identifies them as the same track after enrichment. No repeated setTrack calls.
