# V14 Sub-stage 4.9a -- Intentional Differences from server.js

## ID-16: httpsGet error handling -- resolve-empty vs reject

**server.js:** `httpsGet` rejects the promise on error (network error, timeout). Each call site wraps in a try/catch that catches the rejection.

**C# port (HttpHelpers.HttpsGetAsync):** Returns `string.Empty` on any exception or non-success status. Absorbs errors at the helper level, eliminating repetitive try/catch at every call site.

**Reason:** Cleaner call-site code; the behavior visible to callers (empty string on failure) is identical to the server.js pattern (catch → skip to next source). The C# port goes one level up in absorbing the error.

---

## ID-17: httpsGet -- named HttpClient UA vs merged extraHeaders

**server.js:** `httpsGet` merges a default `{'User-Agent': 'Mozilla/5.0'}` with optional `extraHeaders` at call time. MusicBrainz is called without extra headers (gets `Mozilla/5.0` UA).

**C# port:** Each named HttpClient has a pre-configured UA header. `musicbrainz` client uses `MastersFM/1.7` UA (per MusicBrainz rate-limit policy documented in port plan). This is a policy-compliance difference.

**Reason:** MusicBrainz's API terms require an identifying User-Agent. The server.js `Mozilla/5.0` technically violates this policy.

---

*Prior intentional differences (ID-1 through ID-15) are in earlier INTENTIONAL_DIFFERENCES files.*
