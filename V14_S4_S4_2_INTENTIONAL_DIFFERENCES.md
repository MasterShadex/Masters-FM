# V14 Sub-stage 4.2 -- Intentional Differences from Node.js Baseline

These are known, accepted differences between the .NET 8 server and the Node.js
baseline captures in V14_S4_P1_BASELINE_CAPTURES/. Each difference is documented
with the reason it is acceptable.

---

## ID-1: Content-Length (Kestrel) vs Transfer-Encoding: chunked (Node.js)

**Affects:** All endpoints.

**Baseline:** Node.js `http.createServer` does not set Content-Length by default;
responses use `Transfer-Encoding: chunked`.

**Live (.NET 8):** Kestrel sets `Content-Length: N` when the response size is known
at write time (all synchronous responses in sub-stage 4.2 are buffered). No
`Transfer-Encoding` header appears.

**Why acceptable:** Both are valid HTTP/1.1 transfer mechanisms. Clients (browsers,
OBS, tray.ps1 poller) treat them identically. Content-Length is the preferred form
when size is known; it allows clients to display download progress and detect
truncated responses.

---

## ID-2: Keep-Alive header absent

**Affects:** All endpoints.

**Baseline:** Node.js sends `Keep-Alive: timeout=5` on every response.

**Live (.NET 8):** Kestrel manages keep-alive at the transport layer without
advertising the timeout in the response header. Connections are still kept alive
by default for HTTP/1.1.

**Why acceptable:** The `Keep-Alive` header is informational; its absence does not
affect connection reuse behavior. Clients that rely on persistent connections
(overlay, customize, tray poller) continue to work correctly.

---

## ID-3: GET /current body is `null` in sub-stage 4.2

**Affects:** GET /current only.

**Baseline:** Returns the full current track JSON object (populated during the Phase 1
capture session when the legacy server had received POST /webhook data).

**Live (.NET 8):** Returns `null` (4 bytes, the JSON literal). currentTrack is
initialized to null and stays null because POST /webhook is not yet implemented.

**Why acceptable:** Sub-stage 4.2 is a read-only endpoint pass. POST /webhook (sub-stage
4.4) will populate currentTrack, after which GET /current will return track data.
The response format (Content-Type: application/json, status 200, no Cache-Control)
is otherwise byte-equivalent to the baseline structure.

**Resolution:** Sub-stage 4.4 (webhook).

---

## ID-4: bootId value in GET /version

**Affects:** GET /version only.

**Baseline:** `{"bootId":1777880701695}` (captured at Phase 1 session startup).

**Live (.NET 8):** Different value per server restart (e.g. `{"bootId":1777991372439}`).

**Why acceptable:** By design. SERVER_BOOT_ID is set at startup via
`DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`. The overlay uses this to detect
server restarts (different bootId triggers location.reload). The JSON structure,
Content-Type, and Cache-Control are byte-equivalent; only the numeric value differs.

---

## ID-5: update-status ts/timestamp field value

**Affects:** GET /update-status only.

**Baseline:** `ts` field reflects the time the baseline was captured.

**Live (.NET 8):** `ts` field reflects the current tray.ps1 write time.

**Why acceptable:** Data-level difference; the file is read raw from disk each request
and both baseline and live follow the same byte structure (UTF-8 BOM + JSON). The
format, encoding, and response headers are equivalent.

---

## Summary

| ID   | Category              | Endpoints Affected | Severity |
|------|-----------------------|--------------------|----------|
| ID-1 | Transport encoding    | All                | None     |
| ID-2 | Keep-Alive header     | All                | None     |
| ID-3 | /current body (4.2)   | /current           | None (design)  |
| ID-4 | bootId value          | /version           | None (design)  |
| ID-5 | update-status ts      | /update-status     | None (data)    |

All differences are transport-level, design-level, or data-level. No structural
or behavioral differences that would break clients.
