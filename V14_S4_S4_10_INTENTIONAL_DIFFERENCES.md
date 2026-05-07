# Sub-stage 4.10 Intentional Differences
**Date:** 2026-05-06

Intentional behavioral differences between `DiscordRpcService.cs` / `DiscordRpcThrottle.cs`
and the reference `src/discord_rpc.js`.

---

| ID | Source | C# port | Reason |
|----|--------|---------|--------|
| ID-32 | discord_rpc.js sets `type: 2` (LISTENING) in the raw IPC `SET_ACTIVITY` frame | `BuildPresence()` does not set `ActivityType` | Discord ignores the `type` field for RPC-sourced activities in the Discord client UI — it renders as "Playing" regardless. Lachee's `RichPresence` has a `Type` property but setting it has no visible effect and creates a compile-time dependency on an undocumented enum value. Omitting it matches actual Discord behavior. |
| ID-33 | discord_rpc.js reconnect: 5 s first retry, then 10 s subsequent (`scheduleReconnect`) | `DiscordRpcService` uses a fixed 30 s reconnect loop via `BackgroundService.ExecuteAsync` | Lachee fires `OnConnectionFailed` / `OnClose` events but does not expose the underlying pipe handle lifecycle. A 30 s polling loop is simpler, predictable, and sufficient — Discord restarts take ~5-10 s and users do not expect sub-second reconnect. |
| ID-34 | discord_rpc.js clears `_lastDiscordSig` inside `pushDiscord` before every `setActivity` call | C# resets `_lastSig` to `""` on READY (reconnect) and on `ReloadConfigAsync` (config change). Normal dedup is sig+age comparison | The JS pattern clears sig on every actual send because it has no age-refresh; the C# port uses a 30 s `DedupMaxAgeMs` self-heal so a full clear is only needed after reconnect/reconfigure, not every send. Both implementations ensure Discord self-heals from missed frames. |
| ID-35 | discord_rpc.js has a single `pendingState` object that is flushed synchronously on READY | C# stores `_pendingPresence` (last queued while disconnected) and flushes it through `_throttle.Queue()` on READY | Routing through the throttle ensures the pending flush is also rate-limited, preventing a burst if READY fires immediately after a Queue call. |

