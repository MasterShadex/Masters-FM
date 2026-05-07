# V14 — Master's FM .NET 8 Migration Plan

**Status:** Planning document. No source code modified. Synthesizes V14_RES_1 through V14_RES_5.
**Current shipped version:** v12.0.1 (event-driven SMTC, art-stuck deadlock fixed, patch-notes virtualization).
**Question this answers:** Should we recompile Master's FM in a better language, starting from v12.0.1?
**Author voice:** Honest. The research was the easy part. Some of the conclusions below are uncomfortable.

---

## TL;DR

- A full .NET 8 rewrite is technically the right end-state. The two highest-value wins (eliminating ~600 lines of WinRT reflection in `tray_native.cs`, eliminating ~80ms/tick PowerShell interpreter overhead) are real and durable.
- A full rewrite "in one push" is **not** viable. The institutional knowledge accreted across v9.5.0 → v12.0.1 (15+ hard-won fixes catalogued in R2) is too easy to silently lose. v9.9.4 alone took multiple ship cycles to find; redoing that work blind is the realistic worst case.
- A **staged migration** is viable — but it is a **3-month, 200–300 hour** commitment, not a weekend project. v12.0.0 alone took roughly two focused weeks of evening work plus tester churn for a single subsystem (SMTC). The rewrite touches **eight** subsystems.
- The recommended next step is **Stage 1 only** (`launcher.cs` → .NET 8 + `.csproj` build infrastructure). It is fully reversible, ships under v12.x patches, and gives a real-world calibration of the multi-stage cost before any deeper commitment.

---

## Section 1 — Migration scope assessment

### Realistic effort estimate (full migration)

R5 estimates the `tray.ps1` port alone at **800–1,100 engineering hours**. R4 estimates the build pipeline migration at **14–24 hours**. R3 estimates each non-tray C# component at "low" (single-digit hours each, recompile + TFM bump). Adding integration testing, regression sweeps, tester rounds, and inevitable bug cycles:

| Slice | Best case | Realistic | Bad surprises |
|---|---|---|---|
| Build pipeline + 4 small C# exes (launcher, customize, audio_spectrum, install_bootstrapper) | 20h | 35h | 60h |
| `server.js` → ASP.NET Core (optional) | 40h | 80h | 140h |
| `tray.ps1` → C# (the main event) | 200h | 400h | 700h |
| `tray_native.cs` → CsWinRT (folded into main app) | 20h | 40h | 80h |
| MSI / packaging audit (PowerShell SDK deps, .NET 8 runtime prereq) | 10h | 25h | 50h |
| Integration testing, tester rounds, regression bug-fixing | 40h | 100h | 200h |
| **Total** | **~330h** | **~680h** | **~1,230h** |

**Calibration vs. v12.0.0:** v12.0.0 (event-driven SMTC) was a single subsystem. It took ~2 weeks of evening sessions + 3–5 follow-up patch versions (v12.0.1 art-stuck regression). Extrapolating: a full rewrite touches ~8 subsystems of comparable or greater complexity. That puts a "realistic 680h" estimate inside the believable range. "Best case 330h" assumes zero institutional-knowledge loss, which is not how rewrites actually go.

### Comparison of all four alternatives

| # | Option | Pros | Cons | Verdict |
|---|---|---|---|---|
| 1 | **Do NOT migrate** — keep PS 5.1 + tray_native.dll C# split | Zero risk to shipped behavior. v12.0.0 already moved the hot path (SMTC) to C# via SMTCWatcher — the worst PS perf problem is already solved. PS 5.1 is supported on Windows 11 indefinitely. | tray.ps1 is 9,424 lines and growing. Each new feature pays a ~80ms/tick PS interpreter tax. PS 5.1 scope bugs (v11.2.0 hosted-runspace `$script:` bug) keep biting. WebClient deprecation. `vercel/pkg` is archived. | **Defensible but stagnating.** |
| 2 | **Partial migration** — port heaviest PS hotspots to C#, keep rest | Targets specific pain (e.g., tick loop, detector chain). Each port is small and reversible. | Hybrid PS+C# is what we already have. Extending it makes the system MORE complex, not less. The PS↔C# boundary becomes a moving target with each port. | **Not recommended** — adds entropy. |
| 3 | **Full staged migration** (one component at a time, validated each stage) | Each stage independently shippable. Each stage reversible. Institutional knowledge preserved per-stage. Tester pain is bounded. Versioning stays under v12.x patches until the final stage. | 3-month timeline. Build pipeline lives in two worlds (csc.exe + dotnet) for the duration. Discipline required to not abandon mid-stage. | **Recommended IF the user wants to migrate at all.** |
| 4 | **Full rewrite from scratch in one push** | Clean architecture. No legacy-hybrid awkwardness. Single conceptual leap. | Highest risk by far. The 15+ behaviors in R2 are easy to silently lose. v9.9.4 took multiple production cycles to find — a from-scratch port reproduces that bug surface. 6+ weeks of NO shippable progress. Tester drought. | **Strongly NOT recommended.** |

### Recommendation

**Choose Option 1 (do not migrate) or Option 3 (staged migration). Reject Options 2 and 4.**

Within those: the technical case for migrating is real (R3 quantifies ~600 LOC of reflection that dissolves; R5 quantifies ~80ms/tick interpreter tax). The case for NOT migrating is also real (v12.0.0 already grabbed the biggest perf win; the system is currently stable; the user has been shipping fixes for weeks and is fatigued).

**The honest answer is: the user should run Stage 1 as a calibration probe before committing to the full path.** Stage 1 is small enough to validate in a single session, gives concrete data on whether the .csproj/dotnet-publish pipeline is pleasant or painful, and is reversible without affecting any other component. After Stage 1, the user has real data to decide between Options 1 and 3.

---

## Section 2 — Staged migration plan

Each stage below is **independently shippable** (the app continues to work and can be released as a v12.x patch), **reversible** (the prior csc.exe-built artifact still works if the new one regresses), and **validated** before moving to the next stage. Dependency order follows R1's graph: leaves first, central hub (`server.js`) before the things that depend on it, `tray.ps1` last.

### Stage 1 — `launcher.cs` → .NET 8 + introduce `.csproj` build infrastructure

- **Migrates:** `src/launcher.cs` (518 LOC, MastersFM.exe — Job Object, AUMID, child-process spawning).
- **Why now:** Smallest component. No runtime coupling to anything else (just spawns child processes). Establishes the `.csproj` + `dotnet publish` pipeline that every later stage reuses. R1 ranks `launcher.cs` as low-dependency-fan-in; R3 calls the integrations (P/Invoke, AUMID, Job Object) "identical on .NET 8".
- **Effort:** 6–10 hours best case; 16–20 realistic (most of the cost is establishing the .csproj template, not the port).
- **Validation:**
  - `dotnet publish -r win-x64 --self-contained false -p:PublishReadyToRun=true` produces `MastersFM.exe`.
  - Replace MSI-installed exe; verify: app launches, server.exe + audio_spectrum.exe + MastersFM_Tray.exe all spawn, all die when launcher exits (Job Object), AUMID grouping works in Task Manager, single-instance mutex still blocks second launch.
  - All three tester machines confirm clean launch, kill, relaunch over 24 hours.
- **Rollback:** Keep the csc.exe-built `MastersFM.exe` in the build_tools folder. `_full_rebuild.ps1` switches between the two via a flag for one or two release cycles.
- **Tester impact:** None visible. The launcher is invisible to users; behavior must be byte-for-byte equivalent.
- **Ship as:** v12.1.0 (or v12.0.2 if treated purely as plumbing).

### Stage 2 — `audio_spectrum.cs` → .NET 8

- **Migrates:** `src/audio_spectrum.cs` (3,307 LOC). NAudio 2.2+ already supports net8.0-windows natively (R3, R4).
- **Why now:** Self-contained exe. HTTP/SSE interface (port 4243) is the only coupling — overlay.html and customize.html don't care what produced the bytes. R4 says removing the netstandard.dll facade actually simplifies the build script. Risk is low; complexity is medium.
- **Effort:** 12–20 hours. The port itself is recompile + TFM bump; the time goes into MSI file-list audit (NAudio 2.2's net8 dependency tree may differ from the netstandard2.0 bundle currently shipped) and ASIO STA-thread regression testing.
- **Validation:**
  - `audio_spectrum.exe` launches, opens port 4243, serves the same `{f, b}` SSE frame shape.
  - All four backends still work: WASAPI loopback (default), MME WaveIn, WDM-KS exclusive, ASIO (Voicemeeter test).
  - Overlay.html and customize.html spectrum preview render identically.
  - 8-hour soak: no handle leak, CPU < 2% idle, < 5% during playback.
- **Rollback:** Drop the v12.0.1 `audio_spectrum.exe` back into the install folder. Zero coupling to other migrated components.
- **Tester impact:** Minimal (potential 1–2-hour "Voicemeeter still works?" check from ASIO testers).
- **Ship as:** v12.2.0.

### Stage 3 — `customize.cs` + `install_bootstrapper.cs` → .NET 8

- **Migrates:** `src/customize.cs` (204 LOC, WebView2 host) and `src/install_bootstrapper.cs` (241 LOC, MSI extractor + cert importer).
- **Why now:** Both are tiny, both are leaves. WebView2 NuGet 1.0.2210+ supports net8.0-windows directly (R3). install_bootstrapper has zero runtime coupling to the app (R1 ranks it #2 most replaceable).
- **Effort:** 6–10 hours combined.
- **Validation:**
  - customize.exe loads localhost:4242/customize, frameless rounded window, all controls work.
  - install_bootstrapper.exe extracts MSI, imports cert, runs msiexec, app launches. Test on a clean VM.
- **Rollback:** Keep the csc.exe outputs as fallback for one release cycle.
- **Tester impact:** Customize preview testers verify window behavior; first-time-install testers verify the bootstrapper.
- **Ship as:** v12.3.0.

### Stage 4 — `server.js` → ASP.NET Core minimal API (the central dependency)

- **Migrates:** `src/server.js` (1,610 LOC) → C# minimal API. `src/discord_rpc.js` (337 LOC) → ported to `System.IO.Pipes.NamedPipeClientStream` OR kept as a Node sidecar (decision below).
- **Why now:** server.js is R1's "central hub" — every frontend and tray.ps1 depends on its HTTP contract. Migrating it before tray.ps1 means the new server's contract can be validated against the still-PowerShell tray, which is far safer than migrating both at once.
- **Critical decision point:** **Should `discord_rpc.js` be ported to C# or kept in Node?**
  - **Keep in Node:** discord_rpc.js is 337 lines, zero runtime dependencies, works perfectly. Spawn it as a tiny sidecar.exe (still pkg'd) called by the new ASP.NET server via a local TCP/named-pipe interface. Minimal risk. Adds a process.
  - **Port to C#:** Use `Lachee.DiscordRPC` NuGet (R3). Cleaner architecture. ~20 hours additional effort. Eliminates Node dependency entirely.
  - **Recommendation:** Port to C# via `Lachee.DiscordRPC`. The point of Stage 4 is eliminating Node. A Node sidecar defeats that.
- **Effort:** 60–100 hours. This is the second-largest stage. SSE, config CRUD with deep-merge, art fetching from Deezer/iTunes/MusicBrainz/SoundCloud/osu/YouTube/Bing scraping, preset management, /update progress streaming, /version endpoint, manifest/PWA endpoint, discord RPC integration.
- **Validation:**
  - All endpoints respond byte-for-byte equivalent to server.js (run side-by-side, diff responses on a corpus of recorded test cases).
  - tray.ps1 (still PowerShell at this stage) POSTs to /current and behavior is unchanged.
  - overlay.html SSE loop reconnects cleanly across restarts.
  - customize.html GET/POST /overlay-config round-trip preserves all fields.
  - Discord RPC: track changes within rate limit, no orphaned sessions.
  - 24-hour soak with all five trackers active: no leaks, no SSE disconnects, no Discord rate-limit violations.
- **Rollback:** `_full_rebuild.ps1` ships either server.exe (Node pkg'd) or server.exe (.NET 8 published) based on a flag. Both produce a binary of the same name.
- **Tester impact:** High. Every tester exercises this stage.
- **Ship as:** v13.0.0 (justifies major bump — see Section 5).

### Stage 5 — `tray_native.cs` → folded into the new tray app via CsWinRT

- **Migrates:** `src/tray_native.cs` (835 LOC). The SMTCWatcher class (~430 LOC of reflection) collapses to ~60 LOC of idiomatic CsWinRT (R3, R5).
- **Why now:** Prerequisite for Stage 7. SMTCWatcher is currently consumed by tray.ps1 via Add-Type; in the new C# app, it becomes internal classes. The reflection-based AttachEvent/DetachBinding plumbing is replaced by `+=` / `-=` operators on first-class WinRT events. The three concurrency invariants (burst window, per-session 250ms cooldown, 750ms SessionsChanged coalescing) MUST be preserved verbatim — see Section 3.
- **Effort:** 30–50 hours. Most goes to validating that the simplified CsWinRT version produces identical event ordering and timing under stress (soundcloud-rpc skip storm).
- **Validation:**
  - The new code is dropped into a stand-alone test harness; identical SMTC stress test (rapid skip with soundcloud-rpc + Spotify) produces identical event counts and latencies as tray_native v12.0.1.
  - Memory profile across 12-hour soak: no handle accumulation, no thread accumulation.
  - 2-arg AsTask cancellation: induce SERVERCALL_RETRYLATER, verify no proxy leak (the v9.9.4 regression scenario).
- **Rollback:** Stage 5 produces no shipped artifact on its own — it's a library that Stage 7 consumes. Revert by not depending on it in Stage 7.
- **Tester impact:** None at this stage; testing happens with Stage 7.
- **Ship as:** Internal milestone. No release.

### Stage 6 — `tray_launcher.cs` dissolves (no migration; deletion in Stage 7)

- **Migrates:** Nothing. R1 explicitly notes `tray_launcher.cs` "dissolves — its only job is to host PowerShell, and if tray.ps1 is replaced, this shim is simply removed."
- **Why now (placeholder):** Documenting the deletion. The `Microsoft.PowerShell.SDK` NuGet (R4) would only be needed if we kept tray.ps1 at this stage; we don't.
- **Effort:** 0 hours. Deletion is part of Stage 7's cutover.
- **Validation:** None — deletion is verified by Stage 7 working without it.
- **Rollback:** N/A.

### Stage 7 — `tray.ps1` → C# / .NET 8 application (the big one)

- **Migrates:** `src/tray.ps1` (9,424 lines, 7,900 logic lines per R5).
- **Why now:** Last because every prerequisite must be in place — `server.js` (Stage 4) for the /current contract; SMTCWatcher in CsWinRT (Stage 5) for SMTC; the .csproj infrastructure (Stage 1).
- **Sub-stages within Stage 7** (each ~independently shippable as a beta):
  - 7a. Skeleton: WinForms shell, single-instance mutex, AUMID, Application.Run, log infrastructure, config read/write, HttpClient. Ships nothing user-visible. ~30h.
  - 7b. SMTC integration via Stage 5 watcher; Get-SMTCNowPlaying equivalent; webhook to /current. App is now feature-equivalent to v12.0.1 minus UI. ~60h.
  - 7c. Tray icon + custom owner-draw rounded menu form (R5: "the largest single UI block"). ~80h.
  - 7d. All 10 detectors (osu, Spotify, browser media, SoundCloud, WMP variants, VLC). R5: "translates line-for-line" but volume is the cost. ~80h.
  - 7e. OBS integration (WebSocket auth, scene injection, direct JSON injection). ~30h.
  - 7f. Auto-updater (download, SHA-256, Authenticode, msiexec helper, progress window). ~40h.
  - 7g. Welcome dialog + patch-notes virtualized panel (R2 #11). ~20h.
  - 7h. Auto-start (IShellLink COM), platform/audio dialogs, balloon notifications. ~20h.
  - 7i. WMP COM + UIAutomation detector (R5: "ugliest single function"). ~40h.
  - 7j. Diagnostics, CANARY, GC flush, slow-tick reporting. ~15h.
  - 7k. Full integration regression: all 15 R2 behaviors verified. ~40h.
- **Effort:** 400–700 hours total for Stage 7 (consistent with R5's 800–1,100h "everything else included").
- **Validation:** Every R2 behavior individually verified — see Section 3 matrix. Three weeks of parallel testing alongside v12.x.
- **Rollback:** Until Stage 7 ships, v12.x with tray.ps1 remains the supported branch. The new C# tray ships as `MastersFM_Tray.exe` (replacing the PowerShell host of the same name) only after full validation.
- **Tester impact:** Maximum. This is the moment the user-facing app changes runtime.
- **Ship as:** v14.0.0.

### Stage 8 — Build pipeline + MSI consolidation

- **Migrates:** `_full_rebuild.ps1` from `csc.exe` orchestration to `dotnet publish` orchestration; `build_msi.py` FILES list audit for the new dependency tree (PowerShell SDK assemblies removed, .NET 8 runtime prereq check added to INSTALL.bat).
- **Why now:** After Stage 7. Until then `_full_rebuild.ps1` lives in two worlds — that's intentional and correct.
- **Effort:** 14–24 hours per R4.
- **Validation:** Clean-VM install + run + uninstall on Windows 10 22H2 and Windows 11 24H2.
- **Rollback:** Revert script.
- **Tester impact:** Smoke test on first-time-install machines.
- **Ship as:** v14.0.1 (or rolled into v14.0.0).

---

## Section 3 — Behavior preservation matrix

Cross-reference of R2's 15 critical behaviors against the stages where they migrate. **High-risk** items are flagged for explicit preservation tests.

| # | Behavior | Version | Migrates in stage | New C# expression | At-risk? |
|---|---|---|---|---|---|
| 1 | WinRT 2-arg AsTask CancellationToken overload | v9.9.4 | Stage 5 | `await op.AsTask(cts.Token)` — first-class via CsWinRT. Reflection cache deletes. | **HIGH RISK if a developer writes bare `await op` somewhere.** Mandatory lint/code-review rule: every WinRT await must take a CancellationToken. |
| 2 | SAUMID as cache key (not GetHashCode/RCW identity) | v11.2.1 | Stage 5 + Stage 7b | All session-keyed dictionaries use `session.SourceAppUserModelId` (string). Same as today. | **HIGH RISK** — easy to "improve" to `Dictionary<GlobalSystemMediaTransportControlsSession, T>` and reintroduce the leak. Code review flag. |
| 3 | TryGetMediaPropertiesAsync cleanup in `finally` + 500ms rate limit | v11.2.3 | Stage 7b | `try { await s.TryGetMediaPropertiesAsync(); } finally { _firedThisTick.Remove(saumid); }` + a `_lastFiredMs[saumid]` 500ms gate. | **HIGH RISK** — both constraints must coexist; v11.2.2 dropped one and broke art. Pinned regression test. |
| 4 | SMTC event subscription via reflection / AttachEvent | v12.0.0 | Stage 5 | `manager.SessionsChanged += handler;` — token managed by CsWinRT. The expression-tree delegate plumbing dissolves. | Low risk for the binding itself; HIGH risk for the lifecycle invariants (item below). |
| 5 | SMTC event subscription lifecycle (RCW recycling, no leaks) | v12.0.0 | Stage 5 | Same per-session subscribe/unsubscribe lifecycle. The RCW-identity check (`!ReferenceEquals(existing, s)` for soundcloud-rpc session recycling) MUST remain. | **HIGH RISK** — "modernization" might delete the ReferenceEquals check assuming WinRT identity is stable. It is not for soundcloud-rpc. |
| 6 | Burst window suppression (800ms) | v12.0.0 | Stage 5 | Same `_burstUntilTicks` field, same suppression of ALPC reads in handlers. | **HIGH RISK** — easy to drop "for cleanliness". Pinned regression test (synthetic 8-event burst → ≤1 ALPC read). |
| 7 | Art cache 200-entry LRU | v11.0.0 | Stage 7b | `LruCache<string, string>` (single class with synchronized entry point — NOT two separate Queue+Dictionary structures that can drift). | Low — actually safer in C#. |
| 8 | Deferred album art two-phase webhook | v9.9.9 | Stage 7b | `await thumbnail.OpenReadAsync()` etc. — collapses 4-state machine to 8 lines. The two-phase webhook contract (track-now, art-later) MUST be preserved on the wire. | **MEDIUM RISK** — if developer "simplifies" to a single webhook with art inline, server-side and overlay.html consumers break. Documented contract. |
| 9 | SMTC manager held for app lifetime + 5-min silence fallback | v12.0.0 | Stage 5 | Identical: acquire once, hold, watchdog timer re-initializes if `LastEventUtc > 5min` and a known-playing source exists. | **MEDIUM RISK** — easy to drop the watchdog. Pinned test: kill SMTC service, verify recovery. |
| 10 | WMP Deezer async fire-and-poll | v9.9.9 | Stage 7d | `await httpClient.GetStringAsync(url, ct)` — fire-and-poll dissolves. Cache "no match" explicitly (not as null). | Low. |
| 11 | Patch notes owner-draw virtualized panel | v12.0.1 | Stage 7g | Same clip-rect owner-draw pattern. `DoubleBuffered` may be settable directly via subclass on .NET 8 WinForms (no reflection needed). | Low — non-functional regression at worst. |
| 12 | Auto-update msiexec single-string with double-backtick quoting | v11.1.8 | Stage 7f | `ProcessStartInfo.ArgumentList.Add(...)` — array form quotes correctly. The PS here-string escaping ceremony deletes. | Low — C# handles this correctly natively. |
| 13 | SMTC transition guard 750ms | v9.9.9 B5 | Stage 5 (legacy fallback path only) | Kept in fallback path only; primary path is event-driven. | Low. |
| 14 | GetSessions 500ms staleness guard | v11.2.2 Fix 2 | Stage 5 (legacy fallback only) | Same. | Low. |
| 15 | Get-SMTCNowPlaying 300ms wrapper | v11.2.2 Fix 1 | Stage 7b (deleted) | Native C# is fast enough; not needed. The per-session rate limit (item 6) is separate and must remain. | Low — but verify item 6 not dropped along with this. |

**Behaviors most likely to silently regress:** #1, #2, #3, #5, #6. All five are in the SMTC critical path (Stage 5). All five must have **pinned regression tests** that fail if the invariant is broken — not just observation-based testing.

---

## Section 4 — Risk register

Likelihood × Severity scored 1 (low) to 5 (high). Mitigations specific.

| # | Risk | L | S | L×S | Mitigation |
|---|---|---|---|---|---|
| 1 | WinRT behavior differences (PS reflection vs. CsWinRT direct) lose subtle event ordering | 3 | 5 | 15 | Stage 5 includes a side-by-side stress harness: feed identical SMTC event streams to v12.0.1 SMTCWatcher and the new CsWinRT version; assert event ordering and timing match within 50ms. |
| 2 | 2-arg AsTask overload regression — bare `await op` reintroduces v9.9.4 handle leak | 3 | 5 | 15 | Code review rule + a CI lint that greps for `await\s+\w+Async\(\)` on WinRT types and fails if no `AsTask(ct)`. Manual 12-hour soak with soundcloud-rpc skip storm before each Stage 5/7 ship. |
| 3 | SAUMID cache key regression — developer keys on session object | 2 | 5 | 10 | All cache types in Stage 5 are `Dictionary<string, T>` not `Dictionary<Session, T>`. Pinned unit test: feed two distinct RCWs with same SAUMID → cache size stays 1. |
| 4 | Performance regression vs. v12.0.0 (somehow C# is slower than C#-via-watcher + PS) | 1 | 4 | 4 | Unlikely — R5 documents an 80ms/tick PS interpreter savings. Benchmark CPU + tick latency at every Stage 7 sub-stage; require ≤ v12.0.1 baseline. |
| 5 | New bugs during port — institutional knowledge from v9.5.0–v12.0.1 silently lost | 4 | 5 | 20 | **Highest-likelihood risk.** R2 catalog is the regression-test specification. Each of the 15 behaviors gets a dedicated test case that fails if that fix is undone. No Stage 7 sub-stage ships without all relevant R2 tests passing. |
| 6 | Build pipeline complexity — csc.exe → dotnet SDK; MSI file list audit misses files | 3 | 3 | 9 | Stage 1 establishes the .csproj template under v12.x patches before any high-risk component migrates. R4's 14–24h estimate is pipeline-only; treat that as a separate sub-stage with its own validation. |
| 7 | PS 5.1 → PS 7 incompatibility in tray.ps1 if we kept it on PowerShell SDK NuGet | 4 | 4 | 16 | **Sidestepped** by Stage 7's plan to replace tray.ps1 entirely (not host it under PS 7). PowerShell SDK NuGet is NOT introduced. |
| 8 | Node.js `pkg` (vercel/pkg archived) — server.exe build breaks before Stage 4 | 2 | 3 | 6 | Switch to `@yao-pkg/pkg` (community fork, drop-in replacement, R3) at Stage 0 / immediately as a v12.x patch. Independent of all .NET 8 work. |
| 9 | Timeline / energy cost — user is already fatigued from v11.x→v12.x churn | 4 | 4 | 16 | Stage 1 as calibration (≤ 20h). Stop after Stage 1 if it took 2× the estimate or felt bad. The plan must allow a real "stop here" at every stage boundary. |
| 10 | Tester disruption — v14.0.0 testers will stress on Stage 7 ship | 3 | 3 | 9 | Stages 1–6 ship as v12.x patches with no user-visible behavior change. Only Stage 7 disrupts testers, and it does so once with a major version bump that signals intentional churn. |
| 11 | Discord RPC port to Lachee.DiscordRPC introduces rate-limit bug | 2 | 3 | 6 | Stage 4 keeps discord_rpc.js as fallback for one release cycle (parallel implementations behind a config flag). |
| 12 | `.NET 8 Desktop Runtime` prereq breaks first-run for users without internet | 2 | 4 | 8 | INSTALL.bat downloads runtime if missing (R4). Bundle the offline installer in the desktop-bundle ZIP. |

**Top 3 risks by L×S:** #5 (institutional-knowledge loss, 20), #7 (PS 7 compat, 16 — sidestepped by design), #9 (timeline/fatigue, 16). The plan addresses all three explicitly.

---

## Section 5 — Versioning approach

Per the existing VERSIONING_POLICY (memory: "major bump reserved for architectural refactors where a whole subsystem is replaced"):

| Stage | Version |
|---|---|
| 0 (pkg → @yao-pkg/pkg, build-only) | v12.0.2 patch |
| Stage 1 (launcher.cs → .NET 8) | v12.1.0 minor |
| Stage 2 (audio_spectrum → .NET 8) | v12.2.0 minor |
| Stage 3 (customize + bootstrapper → .NET 8) | v12.3.0 minor |
| Stage 4 (server.js → ASP.NET Core) | **v13.0.0 major** — entire server subsystem replaced |
| Stage 5 (tray_native → folded into Stage 7 app) | internal — no ship |
| Stage 6 (tray_launcher dissolves) | internal — no ship |
| Stage 7 (tray.ps1 → C#) | **v14.0.0 major** — main engine replaced |
| Stage 8 (build pipeline cleanup) | v14.0.1 patch |

**Branch strategy:**

- **Do not** create a `mastersfm-net8` long-lived parallel branch. Long-lived branches rot. The user is solo and will not maintain two branches.
- **Do** feature-flag: `_full_rebuild.ps1` builds either the legacy or new artifact for each component during overlap windows (e.g., during Stage 1's tester period). One main branch; one build script with toggles.
- The **only** time a parallel branch is justified is during Stage 7 sub-stages 7a–7j, where the new C# tray exists alongside tray.ps1 in the same checkout but is built into a separate exe (`MastersFM_Tray_v14.exe` perhaps) for tester opt-in. After 7k validation, the cutover is a single commit deleting tray.ps1 and renaming.

**When the major bump triggers:**

- v13.0.0 at Stage 4 ship — server subsystem replacement is exactly what the policy calls out.
- v14.0.0 at Stage 7 ship — tray engine replacement is the largest architectural change in the project's history.

---

## Section 6 — Recommended next step

**Tomorrow morning, do this:**

1. **Ship v12.0.2** with `pkg` swapped to `@yao-pkg/pkg`. Pure build-tooling change. Zero runtime risk. Removes a known dead dependency. ~1–2 hours total.
2. **Then Stage 1 only:** create `src/launcher.csproj` targeting `net8.0-windows`, port `launcher.cs` (it likely compiles unchanged), set up `dotnet publish -r win-x64 --self-contained false -p:PublishReadyToRun=true`, integrate into `_full_rebuild.ps1` behind a `-UseDotnet8Launcher` flag, ship v12.1.0.
3. **Then stop.** Spend at least one full week running v12.1.0 in production across all tester machines before deciding whether to continue. Use that week to evaluate: was Stage 1 pleasant? Did the .csproj/dotnet-publish flow feel like the right tool? Did MSI integration cause any surprises? Were there any silent behavior changes?

**Reasons not to skip Stage 1 even if the user decides not to continue:**
- launcher.cs gets a modern build pipeline (R4: PublishReadyToRun cuts cold-start ~30–50%).
- Sets up `.csproj` infrastructure that future contributors / future-self benefit from regardless of whether the rest of the migration happens.
- Concrete data on real cost vs. estimated cost — calibrates all subsequent stage estimates.

**If after Stage 1 the user decides not to continue:** v12.x with tray.ps1 + tray_native.dll + .NET 8 launcher is a perfectly defensible long-term steady state. The hot path (SMTC) is already in C# (v12.0.0 watcher). The interpreter tax in tray.ps1 is real but bounded. This is a legitimate "stop and ship" outcome.

**If after Stage 1 the user decides to continue:** the calibration data drives the decision on whether to do Stages 2–8 over 3 months or to accept the partial migration as the new steady state.

---

## Section 7 — Estimated total cost

| Stage | Best | Realistic | If bad surprises |
|---|---|---|---|
| Stage 0 (pkg fork) | 1h | 2h | 6h |
| Stage 1 (launcher) | 6h | 16h | 30h |
| Stage 2 (audio_spectrum) | 12h | 20h | 40h |
| Stage 3 (customize + bootstrapper) | 6h | 12h | 24h |
| Stage 4 (server.js → ASP.NET Core, with Discord RPC port) | 60h | 100h | 180h |
| Stage 5 (tray_native → CsWinRT library) | 30h | 50h | 90h |
| Stage 6 (tray_launcher deletion) | 0h | 0h | 0h |
| Stage 7 (tray.ps1 → C#, all sub-stages 7a–7k) | 400h | 600h | 900h |
| Stage 8 (build pipeline) | 14h | 24h | 50h |
| Tester rounds, regression cycles, integration glue | 40h | 100h | 200h |
| **TOTAL** | **~570h** | **~925h** | **~1,520h** |

**Calibration vs. v12.0.0:**
- v12.0.0 (event-driven SMTC, the largest single architectural change to date) consumed roughly 80–120 engineering hours including the v12.0.1 patch round.
- Stage 7 alone is **5–10× v12.0.0**. That is a sober number, not an inflated one — Stage 7 touches every subsystem v12.0.0 touched plus all the others.

**Best-case (570h) means:** No bad surprises. Every stage's estimate hits its lower bound. CsWinRT works exactly as documented. WinForms on .NET 8 has no DPI / rendering surprises. PowerShell SDK is never needed (it isn't, in this plan). Approx 14–15 person-weeks of focused work. Solo: ~3.5 calendar months at 40 productive hours/week, or ~7 calendar months at 20 productive hours/week (evenings + weekends).

**Realistic (925h) means:** Each stage hits its midpoint. One of Stages 4 or 7 has a hard week. ~23 person-weeks. Solo: 6 calendar months at 40h/wk; 12 calendar months at 20h/wk.

**Bad-surprise (1520h) means:** WinRT projection has an undocumented behavior difference. CsWinRT 2.x has a memory-leak bug that surfaces under stress. WMP UIA detector port takes 3× the estimate. ~38 person-weeks. Solo: 9–18 calendar months.

For a single-developer-evenings cadence, the realistic case is **~12 months** to v14.0.0 ship.

---

## Final word

The technical case for migration is real. The honest assessment is that the migration is also large and risky, and the user has just shipped v12.0.0 and v12.0.1, which between them solved the worst PerfMon-visible problems in the system. The best decision is rarely "do everything"; it is often "do the small thing that gives you data, then decide".

Stage 1 is that small thing. Run it. Then decide.

---

MIGRATION REQUIRES USER DECISION — after running Stage 1 (launcher.cs → .NET 8 + .csproj infrastructure, ~16h realistic), should we proceed through Stages 2–8 (~900h additional, 12-month solo evenings cadence, v14.0.0 endpoint), stop at partial migration, or revert to v12.x as the long-term steady state?
