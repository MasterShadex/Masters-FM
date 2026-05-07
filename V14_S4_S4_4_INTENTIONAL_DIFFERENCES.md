# V14 Sub-stage 4.4 -- Intentional Differences

**Date:** 2026-05-06
**Sub-stage:** 4.4 (POST /webhook handler)

Differences between the .NET 8 webhook handler and the Node.js server.js handler that
are intentional, documented, and approved.

---

## ID-7: Art/Duration resolution stubs (4.9 deferred)

**Description:** `ResolveArtworkAsync` returns `webhookArt` directly (no cascade).
`ResolveDurationAsync` returns the webhook-supplied duration (no SoundCloud/Deezer/iTunes lookup).

**Affected behavior:** B11 (art retry) calls the stub; new-track art resolution calls the stub.

**Impact:** Overlay shows webhook-supplied art only (or empty if webhook sends none).
Sub-stage 4.9 replaces stubs with the full cascade.

**Evidence:** Both stubs are marked `// TODO sub-stage 4.9: replace stub`.

---

## ID-8: Discord RPC push omitted (4.10 deferred)

**Description:** The JS `setTrack` function calls `pushDiscord()` before and after art resolution.
The C# `SetTrackSynchronous` does NOT call any Discord push -- that is sub-stage 4.10.

**Impact:** Discord rich presence shows no track until 4.10 implements DiscordRPC.

---

## ID-9: Artist enrichment (enrichByTitle) omitted (future sub-stage)

**Description:** The JS `setTrack` checks `isPlaceholderArtist` and calls `enrichByTitle` for
unknown artists. The C# implementation just trims the artist name and proceeds.

**Impact:** VLC/WMP tracks with "?????????" artist won't get artist lookup. No impact on Spotify/SoundCloud.

---

## ID-10: Response body is "OK" text/plain, no Content-Type header

**Description:** The Node.js server responds with just `res.end('OK')` -- no Content-Type set.
Kestrel may or may not add a Content-Type header for plain text responses.

**Resolution:** Write raw "OK" bytes to Response.Body (same as 4.2 endpoints) to avoid Kestrel
adding charset=utf-8 or Content-Type automatically.

---

## ID-1 through ID-6 (inherited from 4.2/4.3)

See V14_S4_S4_2_INTENTIONAL_DIFFERENCES.md and V14_S4_S4_3_FINAL_REPORT.md.
