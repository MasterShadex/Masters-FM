# V14 Sub-stage 4.9b+c -- Intentional Differences

## ID-18: Art cascade passes cleaned artist/track to API sources (not raw+cleaned)

**server.js behavior:** resolveArtwork tries [raw, cleanArtist/cleanTrack] for each of Deezer, iTunes, and MusicBrainz (the for-loop over variants).

**C# behavior:** ArtCascade.ResolveAsync cleans artist/track once before the cascade loop and passes only cleaned values to each source. Each source makes one API call, not two.

**Rationale:** The instruction template explicitly shows this simplification. Most tracks where raw != cleaned benefit from cleaning (feat. stripping, etc.), so cleaning first covers the common case. The uncommon case where raw works but cleaned fails is accepted. This halves the API calls per cascade.

---

## ID-19: SoundCloud/osu sources are CDN-passthrough placeholders (not full search)

**server.js behavior:** When source is "soundcloud", calls searchSoundCloudArt(artist, track) which scrapes SoundCloud's API using a scraped client_id. When source includes "osu", calls searchOsuArt which scrapes osu.ppy.sh. Both are full search implementations.

**C# behavior (4.9b+c):** SoundCloudDirectSource returns webhookArt unchanged if it's already a SoundCloud CDN URL. OsuDirectSource does the same for osu CDN URLs. The full search implementations are deferred to sub-stage 4.9d.

**Rationale:** The client_id scraper and HTML scrapers are fragile, deserve their own brief (4.9d). The placeholder preserves cascade structure and handles the common case where the webhook already provides a CDN URL.

---

## ID-20: Art LRU cache is a C# addition (not in server.js)

**server.js behavior:** No art cache. Every resolveArtwork call runs the full cascade.

**C# behavior:** ArtCascade.ResolveAsync checks LruCache before running sources. Cache key: cleanedArtist+"|||"+cleanedTrack. Non-empty results are cached; empty results are not.

**Rationale:** Per V14 port plan enhancement #7. Prevents redundant API calls for the same track (e.g. periodic overlay refreshes).

---

## ID-21: Duration resolver is standalone (not tied to currentTrack state)

**server.js behavior:** resolveDuration checks currentTrack.duration > 0 at entry (skips if already known) and calls trySet() which validates isSameTrack() before setting. Tightly coupled to global server state.

**C# behavior:** DurationResolver.ResolveAsync is pure -- takes artist/track, returns long ms or 0. No global state. The caller decides whether to use the result.

**Rationale:** The .NET server uses WebhookHandler request context, not global state. The caller (future 4.9e hookup) compares to webhook-supplied duration.

---

## ID-22: SmtcFallbackSource deferred to 4.9d

**server.js behavior:** If all sources fail and smtcThumb was set (data: URI), returns smtcThumb as last resort (server.js:772-776).

**C# behavior (4.9b+c):** SmtcSource only handles the "browser platform + smtcThumb" early-return path. The fallback path (try everything, then return smtcThumb) requires access to the original webhookArt after all other sources fail. This will be SmtcFallbackSource in 4.9d.

**Rationale:** The cascade orchestrator passes webhookArt to each source. Adding SmtcFallbackSource as a terminal source in 4.9d is clean -- it simply checks if webhookArt is a data: URI.
