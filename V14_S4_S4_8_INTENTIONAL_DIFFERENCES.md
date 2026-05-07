# V14 Sub-stage 4.8 -- Intentional Differences from server.js

## ID-15: Concurrent /screenshot requests -- reject instead of replace

**server.js:** `_ssScreenshot.pending = { ... }` replaces any existing pending unconditionally. The first
request's HTTP connection stays open indefinitely (its `done()` closure is orphaned, never called).
This is a resource leak.

**C# port:** `TryRegisterScreenshot()` returns `false` if a pending already exists. The GET /screenshot
handler returns 409 with body `"Screenshot already in progress"`.

**Reason:** TCS-based design makes replacing non-trivial (the first TCS must be cancelled). The reject
behavior is cleaner, avoids state leaks, and is what the CLAUDE_CODE_INSTRUCTIONS.md design specifies.

---

*Prior intentional differences (ID-1 through ID-14) are in earlier INTENTIONAL_DIFFERENCES files.*
