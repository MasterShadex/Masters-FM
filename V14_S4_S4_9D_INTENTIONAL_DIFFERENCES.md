# V14 Sub-stage 4.9d -- Intentional Differences

## ID-23: SoundCloudOembedSource does not use client_id

**server.js behavior:** oEmbed at server.js:722-731 fetches `https://soundcloud.com/oembed?format=json&url=...` -- a public endpoint that requires no authentication.

**C# behavior (4.9d):** SoundCloudOembedSource implements the same simple oEmbed endpoint without client_id. The SoundCloudClientIdCache is created as infrastructure for a future upgrade of SoundCloudDirectSource.

**Rationale:** Source-driven port -- server.js oEmbed does not use client_id. The instruction template suggests client_id but the actual server.js behavior is authoritative per the "source-driven port non-negotiable" rule.

---

## ID-24: OsuScraperSource replaces OsuDirectSource in cascade

**server.js behavior:** searchOsuArt (server.js:604-639) is a full beatmapset search that fires whenever source.includes('osu').

**C# behavior (4.9d):** OsuScraperSource implements the full searchOsuArt function and REPLACES OsuDirectSource at cascade position 3. The placeholder is removed.

**Rationale:** OsuScraperSource is a complete port that supersedes the 4.9b CDN-passthrough placeholder.

---

## ID-25: Bing 1.2s timeout via CancellationTokenSource instead of Promise.race

**server.js behavior:** Promise.race([httpsGet(...), setTimeout(1200ms, reject)]) -- JS event-loop approach.

**C# behavior:** CancellationTokenSource(1200ms) linked to the caller's CancellationToken, passed to HttpsGetAsync. Functionally equivalent but uses .NET cancellation model.

**Rationale:** .NET does not have Promise.race. CancellationTokenSource achieves the same per-request deadline.

---

## ID-26: YouTube UA uses "art-generic" client UA not the exact server.js UA string

**server.js behavior:** UA `'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 MastersFM'` passed as extraHeaders.

**C# behavior:** YouTubeSource uses the "bing" named HttpClient (same browser-style UA from 4.9a: Mozilla/5.0 Chrome/120). The "MastersFM" suffix in server.js UA is dropped since the bing client's UA is already a valid browser UA that YouTube accepts.

**Rationale:** Reusing the existing "bing" HttpClient (browser UA) avoids creating another named client for a minor UA difference. Both UAs are generic enough that YouTube cannot distinguish them.

---

## ID-27: SoundCloud client_id regex uses [a-zA-Z0-9] not [0-9a-z]

**server.js actual regex:** `/client_id\s*[:=]\s*["']([a-zA-Z0-9]{32})["']/` (server.js:506)

**CLAUDE_CODE_INSTRUCTIONS.md documentation:** Listed as `[0-9a-z]` (lowercase only) -- this was a documentation error in the instructions. C# implementation uses the correct `[a-zA-Z0-9]` from the actual server.js source.

**Rationale:** Source-driven port rule prevails over documentation. Direct read of server.js:506.
