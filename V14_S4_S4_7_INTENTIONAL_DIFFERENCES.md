# V14 Sub-stage 4.7 -- Intentional Differences from server.js

## ID-14: /art HTTP proxy -- streaming instead of buffering

**server.js:** Uses `r.arrayBuffer()` which buffers the entire image in memory before sending.

**C# port:** Uses `HttpCompletionOption.ResponseHeadersRead` + `CopyToAsync` to stream bytes directly from the upstream HTTP response to the client response body. No full-buffer of image data.

**Reason:** Explicitly required by CLAUDE_CODE_INSTRUCTIONS.md Rule 8 ("Stream the response, don't buffer the whole image").

**Impact:** None for correctness. Improves memory efficiency for large images. The response is otherwise identical (same status, same Content-Type, same bytes).

---

*Prior intentional differences (ID-1 through ID-13) are in V14_S4_S4_2_INTENTIONAL_DIFFERENCES.md through V14_S4_S4_5_INTENTIONAL_DIFFERENCES.md.*
