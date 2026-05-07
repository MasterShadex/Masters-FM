# V14 Researcher 5 — PowerShell Behavior Catalog

**Source file**: `src/tray.ps1` — 9,424 lines, PowerShell 5.1  
**Support files analyzed**: `src/tray_launcher.cs`, `src/tray_native.cs`  
**Date**: 2026-05-04  

---

## Scale Assessment

Approximate line-count breakdown by category (some overlap):

| Category | Approx. Lines | Notes |
|---|---|---|
| PATCH_HISTORY data array | ~1,450 | Lines 261–1,442. Pure data, no logic. |
| Global variable declarations | ~80 | `$script:*` and `$global:*` at module level |
| WinForms UI — tray menu, forms, dialogs | ~1,200 | Custom menu form, audio device dialog, platforms dialog, update window, welcome dialog |
| SMTC / WinRT integration | ~800 | Await-WinRT, Get-SMTC*, Find-SMTCSession, thumbnail extraction state machine |
| Scrobble tick loop + detector chain | ~500 | scrobbleTimer.Tick, Invoke-Detector, epoch/position logic |
| Music source detectors (10 detectors) | ~1,000 | Get-OsuNowPlaying, Get-SpotifyNowPlaying, Get-SMTCNowPlaying, Get-BrowserMedia, Get-SoundCloud, Get-WMPNowPlaying (COM/SMTC/UIA/Title), Get-VLC |
| OBS integration (WebSocket + JSON) | ~450 | Add-OBSBrowserSource, Add-OBSBrowserSourceDirect, Remove-OBSBrowserSourceDirect, Start-OBSExitWatcher, Try-AddToOBS |
| Auto-updater | ~350 | Show-UpdateWindow, Invoke-UpdateCheck, Start-UpdateDownload, Install-Update, Poll-UpdateCheck, Write-UpdateStatus |
| Config read/write helpers | ~200 | Get-UserCfgPath, Save-ConfigField, Get-PlatformsConfig, Save-PlatformsConfig |
| HTTP client / webhook | ~100 | Send-WebhookAsync, pollTimer HTTP polling |
| Process launch + monitoring | ~150 | Server start, audio_spectrum.exe start, launcher PID guard |
| Auto-start / startup shortcut | ~120 | Set-AutoStart, New-AutoStartShortcut (COM WScript.Shell), Invoke-AutoStartMigration |
| WMP COM + UIAutomation (UIA) | ~400 | Get-WMPNowPlayingCOM, Get-WMPNowPlayingUIA, Initialize-UIA, _walkUIA |
| Logging, diagnostics, CANARY | ~200 | Log, LogErr, EarlyLog, Dump-DiagnosticState, CANARY block |
| Initialization / startup sequencing | ~200 | Add-Type calls, SMTC init, watcher init, tray creation |

**Total non-patch-history logic lines**: ~7,900  

**Straight/translatable vs PS-specific estimate**:
- ~55% is "straightforward logic" — control flow, string manipulation, math, JSON I/O, HttpClient calls, process management. Translates to C# almost line-for-line.
- ~45% involves PS-specific patterns that need rethinking: WinRT interop plumbing, `.GetNewClosure()` event handlers, `$script:/$global:` scope tricks, `Add-Type` dynamic compilation, `ConvertFrom-Json` PSCustomObject quirks, pipeline return-value semantics, WScript.Shell COM for shortcut creation.

---

## Patterns that translate cleanly to C#

| Pattern | Location in tray.ps1 | C# equivalent |
|---|---|---|
| `[System.IO.File]::AppendAllText(...)` | Lines 49, 141, 188, 237 | `File.AppendAllText(...)` — identical |
| `[System.IO.File]::WriteAllText(...)` | Lines 54, 114, 195, 1559 | `File.WriteAllText(...)` — identical |
| `[System.IO.File]::ReadAllText(...)` | Lines 4397, 4453 | `File.ReadAllText(...)` — identical |
| `[System.IO.Directory]::CreateDirectory(...)` | Line 45 | `Directory.CreateDirectory(...)` — identical |
| `[System.IO.Path]::Combine(...)` | Throughout | `Path.Combine(...)` — identical |
| `[System.Environment]::GetFolderPath(...)` | Lines 44, 4390 | `Environment.GetFolderPath(...)` — identical |
| `[System.Threading.Mutex]` single-instance guard | Lines 61–72 | `new Mutex(false, "Global\\...")` — identical |
| `[System.Diagnostics.Stopwatch]::StartNew()` | Lines 5779, 8702 | `Stopwatch.StartNew()` — identical |
| `[System.Diagnostics.Process]::GetCurrentProcess()` | Lines 9340, 9359 | `Process.GetCurrentProcess()` — identical |
| `[System.Text.Encoding]::UTF8.GetBytes(...)` | Lines 8939, 9266 | `Encoding.UTF8.GetBytes(...)` — identical |
| `[System.Security.Cryptography.SHA256]::Create()` | Lines 3575–3578 | `SHA256.Create()` — identical |
| `[System.Net.Http.HttpClient]` + `PostAsync` / `GetStringAsync` | Lines 5318–5334, 5205 | `HttpClient` in .NET 8 — identical, and `await` replaces fire-and-poll |
| `ConvertTo-Json` / `ConvertFrom-Json` | Throughout | `System.Text.Json.JsonSerializer.Serialize/Deserialize` or `Newtonsoft.Json` |
| `[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()` | Throughout | `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` — identical |
| `[GC]::Collect(2, [GCCollectionMode]::Forced)` | Line 9406 | `GC.Collect(2, GCCollectionMode.Forced)` — identical |
| `[System.Collections.Generic.Queue[string]]` | Line 129 | `Queue<string>` — identical |
| `[System.Collections.Hashtable]::new()` + `.Clear()` | Lines 8730–8731 | `Dictionary<K,V>` — identical semantics |
| `[System.Collections.Generic.List[string]]::new(16)` | Line 8734 | `List<string>(16)` — identical |
| `Start-Process` for child processes | Lines 5074, 5083, etc. | `Process.Start(ProcessStartInfo)` — identical |
| `Get-Process -Id $id` for PID lookup | Line 8786 | `Process.GetProcessById(id)` — identical |
| `Get-CimInstance Win32_Process` for parent PID | Lines 5109–5110 | WMI via `ManagementObjectSearcher` or `GetParentProcess` via NtQueryInformationProcess P/Invoke |
| `netstat -ano` port check | Lines 5058–5066 | `IPGlobalProperties.GetActiveTcpConnections()` — cleaner |
| `New-Object System.Net.WebSockets.ClientWebSocket` | Lines 3535–3561 | `ClientWebSocket` — identical, but use `await` instead of `.Wait(3000)` |
| `New-Object System.Net.WebClient` + events | Lines 5578–5623 | `HttpClient` with progress in .NET 8 (WebClient is legacy) |
| `[System.Windows.Forms.Application]::Run()` | Line 9419 | `Application.Run()` — identical, this is the WinForms pump |
| `[System.Windows.Forms.Application]::Exit()` | Lines 8788, 4837 | `Application.Exit()` — identical |
| `WScript.Shell` COM shortcut creation | Lines 3394–3410 | `IShellLink` P/Invoke or `Shell32.ShellLink` COM interop — slightly more code but direct |

---

## Patterns requiring re-architecture

| Pattern | Location | Why non-trivial | Recommended approach |
|---|---|---|---|
| **`Await-WinRT` / `AsTask<T>` via reflection** | Lines 5771–5851 | PS 5.1 can't call `.GetAwaiter()` on `__ComObject`. The entire `Await-WinRT` helper exists only because PowerShell boxes WinRT objects as opaque COM. In C# the WinRT types have proper awaitable implementations. | Use `await manager.RequestAsync()` directly — zero reflection, zero `AsTask` plumbing. Entire `Await-WinRT` function and the `_awaitAsTaskGeneric` / `_awaitAsTaskGenericCts` reflection cache dissolves. |
| **Fire-and-poll async pattern** | Lines 5880–5965 (Get-SMTCManager), 6082–6190 (Get-SMTCMediaPropsCached), 8119–8192 (WMP Deezer), 6234–6350 (thumbnail state machine) | PS 5.1 has no `await`. Every async operation becomes: fire task, store globally, return immediately, check `.IsCompleted` on next tick. Results in ~15 `$global:_*Task` variables and multi-state machines. | `async/await` throughout. The entire cross-tick fire-and-poll infrastructure collapses to `await` calls inside `async Task` methods. |
| **`.GetNewClosure()` event handlers** | Lines 2293–2342, 2627–2722, 4499–4504, 4614–4640, 4969–4979, 5016–5021, etc. | PowerShell closures don't capture outer-scope variables by reference the way C# lambdas do. `.GetNewClosure()` snapshots the current scope into a new script block. Critical caveat: `$script:` writes from inside a closure in a hosted runspace (MastersFM_Tray.exe) are unreliable (v11.2.0 bug). Workarounds use captured hashtables. | C# lambdas and anonymous methods capture variables by reference natively. `() => { DoThing(localVar); }` just works. No analogue to `.GetNewClosure()` needed. |
| **WinForms timer as async substitute** | Lines 2099, 2962, 3002, 4058, 4069, 4157, 4497, 4967, 5014, 5134, 5180, 5455, 5745, 8736 | 15+ `System.Windows.Forms.Timer` instances used for: polling HTTP responses, animations (fade-in/out), one-shot delays, periodic checks. All run on the UI thread. Complex state machines span multiple ticks because PS has no `await`. | Most become `await Task.Delay(ms)` inside `async void` event handlers or `PeriodicTimer` in .NET 6+. Animation loops can use `System.Windows.Forms.Timer` or `async` animation helpers. WinForms Timer itself is fine, but the state machines around it go away. |
| **`$global:` / `$script:` for shared state** | Hundreds of sites | PowerShell scope model (`$script:` = module, `$global:` = runspace root) has no C# equivalent. Variables are explicitly passed or stored in `$global:` because PS functions have their own scope by default. The hosted runspace complicates this further (see `.GetNewClosure()` note). | Convert to `private static` / `private` fields on a class (e.g. `TrayApp : Form` or a `AppState` singleton). All `$global:_*` become instance or static fields. |
| **`ConvertFrom-Json` → PSCustomObject** | Lines 1501, 1544, 2183, 2250, 3560, 4285, 4324, 4366, etc. | `ConvertFrom-Json` in PS 5.1 returns `PSCustomObject` not a typed class. Property access is dynamic: `$j.discord_rpc.enabled`. In C# this becomes either a typed DTO or `JsonElement`. The code uses `.PSObject.Properties.Name -contains 'fieldName'` for existence checks. | Use `System.Text.Json` with typed C# records / classes. `JsonSerializer.Deserialize<ConfigRoot>(json)`. Null-checking replaces `.PSObject.Properties.Name -contains`. |
| **`Add-Type` inline C# compilation** | Lines 26–35, 5220–5226, 4476–4486, 7713–7714 | 5 different `Add-Type -MemberDefinition` blocks compile tiny C# snippets (P/Invoke definitions). These exist because PS can't declare P/Invoke signatures natively. There is a fallback pattern: try to load from tray_native.dll, fall back to inline compile. | All P/Invoke definitions already exist in `tray_native.cs`. In a C# rewrite they are just `[DllImport("...")]` declarations in the same project — no runtime compilation at all. |
| **`[Windows.Media.*,ContentType=WindowsRuntime]` type binding** | Lines 5238, 6258, 7255, 7278, 7279 | PS 5.1 WinRT type binding requires the special `[TypeName,Assembly,ContentType=WindowsRuntime]` cast syntax to load WinRT types into the CLR. This is PS-exclusive. | In C#, WinRT types are projected via `Microsoft.Windows.SDK.Contracts` NuGet or the built-in Windows SDK projections. Types are available directly: `GlobalSystemMediaTransportControlsSessionManager`. No special syntax. |
| **WMI via `[wmiclass]` / `Get-CimInstance`** | Lines 4809–4811, 5109 | Two uses: (1) `[wmiclass]'Win32_Process'.Create(...)` to spawn a detached cmd.exe for the restart kill-chain. (2) `Get-CimInstance Win32_Process` to get parent PID. | (1) Use `ProcessStartInfo` with `UseShellExecute = false` or P/Invoke `CreateProcess` with `dwCreationFlags = DETACHED_PROCESS`. (2) Use `NtQueryInformationProcess` P/Invoke or the managed `ParentProcessUtilities` pattern to get parent PID. |
| **`WScript.Shell` COM for shortcut creation** | Lines 3394–3410 | `New-Object -ComObject WScript.Shell` creates `.lnk` files. PS accesses COM objects dynamically; C# needs explicit COM interop. | P/Invoke `IShellLink` COM interface or a tiny wrapper around shell32/ole32. Alternatively, use `Microsoft.WindowsAPICodePack-Shell` NuGet package which wraps it cleanly. |
| **Pipeline return semantics** | Throughout | In PS, any unassigned expression in a function body is automatically returned. Functions that do file I/O must pipe to `Out-Null` to suppress accidental returns. `$null = ...` is used to suppress. In C# everything is explicit. | Explicit `return` statements and `void` methods. No accidental pipeline pollution. No `| Out-Null`. |
| **`Start-Transcript`** | Lines 113–123 | Captures all PS output to a file. No C# equivalent — it's a PS host feature. | Replace with `Console.SetOut(new StreamWriter(transcriptPath))` or a custom `TextWriter` that tees to both console and file. Or just use structured logging (Serilog / NLog). |
| **`trap { ... continue }`** | Lines 175–180 | PS unhandled-exception trapping across all code in scope. Keeps script alive after any thrown exception. | `AppDomain.CurrentDomain.UnhandledException` + `Application.ThreadException` (both already registered from tray_native + tray_launcher patterns, reuse them). |

---

## WinRT interop — critical details

### The 1-arg vs 2-arg `AsTask` overload — CRITICAL

This is the most historically significant bug in tray.ps1. Documented exhaustively in v9.9.4 patch notes.

**Root cause**: `SystemWindowsRuntimeSystemExtensions` has two overloads:
- `AsTask<T>(IAsyncOperation<T>)` — 1-arg, **NO** cancellation path
- `AsTask<T>(IAsyncOperation<T>, CancellationToken)` — 2-arg, cancels the COM proxy on timeout

When the 1-arg overload was used and `RequestAsync()` timed out (triggered by `soundcloud-rpc` returning `SERVERCALL_RETRYLATER`), each orphaned `IAsyncOperation` kept its COM cross-process proxy alive indefinitely. After 797 timeout events in 10 hours this produced 17,150+ threads and 106,713+ handles, exhausting USER objects and crashing (~1.88 GB RAM).

**Resolution (v9.9.4)**: Two MethodInfo instances are cached at startup:
```powershell
# 1-arg fallback (line 5243)
$global:_awaitAsTaskGeneric    = ... 'AsTask' where params.Count -eq 1
# 2-arg preferred (line 5254)
$global:_awaitAsTaskGenericCts = ... 'AsTask' where params.Count -eq 2 -and params[1] = CancellationToken
```
`Await-WinRT` now uses the 2-arg overload when `TimeoutMs > 0`. The CTS fires at TimeoutMs; the implementation calls `asyncOp.Cancel()`, unregisters the Completed handler, and transitions the Task to Canceled. A `finally` block disposes both CTS and Task, releasing all OS handles.

**In C# .NET 8**: This problem does not exist. `await manager.RequestAsync().AsTask()` or simply `await manager.RequestAsync()` via the Windows SDK projections handles cancellation with `CancellationToken` via standard patterns: `manager.RequestAsync().AsTask(cancellationToken)`. The proxy lifecycle is managed by the CLR's WinRT interop layer. No reflection, no manually cached MethodInfo.

### Other WinRT gotchas in tray.ps1

**`.GetAwaiter().GetResult()` in WebSocket path** (line 3557):
```powershell
$result = $ws.ReceiveAsync($seg, $ct).GetAwaiter().GetResult()
```
This is the one `.GetAwaiter().GetResult()` call in the file, in the OBS WebSocket function `WsRecv`. It works here because `ClientWebSocket` is a .NET socket type, not a WinRT `IAsyncOperation<T>` — `.GetAwaiter()` works on `Task<T>`. In C# this becomes `await ws.ReceiveAsync(...)`.

**WinRT type loading at startup** (lines 5237–5264):
```powershell
$null = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager,Windows.Media.Control,ContentType=WindowsRuntime]
try { Add-Type -AssemblyName System.Runtime.WindowsRuntime } catch {}
```
This PS-specific bootstrap is needed because PS 5.1 doesn't automatically project WinRT namespaces. In C#, adding the `Microsoft.Windows.SDK.Contracts` NuGet package makes `Windows.Media.Control.*` available at compile time. No runtime type loading needed.

**WinRT stream reading for album art** (lines 6258–6350):
The deferred thumbnail extraction state machine (`Invoke-DeferredThumbExtraction`) goes through states: `idle → waiting → opening → loading`. Each state stores a `$global:_smtcArtOpenTask` / `$global:_smtcArtLoadTask` that is polled on the next tick. In C# this collapses to:
```csharp
var stream = await thumbnail.OpenReadAsync();
var reader = new DataReader(stream);
await reader.LoadAsync((uint)stream.Size);
byte[] bytes = new byte[stream.Size];
reader.ReadBytes(bytes);
return "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
```
No state machine, no globals, no cross-tick polling.

**`[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus]::Playing`** (lines 7255, 7278, 7279): Direct enum constant access via WinRT type. In C# this is `GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing` — identical semantics.

---

## Global state catalog

The following `$script:` and `$global:` variables are significant application state. In a C# rewrite these become fields on the main application class or a dedicated state object.

### Lifecycle / init
| Variable | Type | Mutation pattern |
|---|---|---|
| `$script:_initDone` | bool | Set once at line 9413 after scrobble timer starts. Controls whether Log() also writes to startup.log. |
| `$script:APP_VERSION` | string | Set once at line 252. Read-only after that. |
| `$script:PATCH_HISTORY` | array of hashtables | Defined at line 261, never mutated. |
| `$global:_mutex` | `System.Threading.Mutex` | Created at startup, released on exit/restart. |
| `$global:_launcherPid` | int | Set at startup if launched via MastersFM.exe. Checked every 5s. |

### SMTC / WinRT
| Variable | Type | Mutation pattern |
|---|---|---|
| `$global:smtcAvailable` | bool | Set once at startup after type load attempt. |
| `$global:_awaitAsTaskGeneric` | `System.Reflection.MethodInfo` | Set once at startup. The 1-arg AsTask overload MethodInfo. |
| `$global:_awaitAsTaskGenericCts` | `System.Reflection.MethodInfo` | Set once at startup. The 2-arg (CancellationToken) AsTask overload MethodInfo. |
| `$global:_smtcWatcher` | `MasterFM.SMTC.SMTCWatcher` | Created at startup (v12.0.0 event-driven path). Disposed and nulled on stuck-events detection. |
| `$global:_smtcMgrCached` | WinRT manager object | Per-tick TTL cache. Set by Get-SMTCManager fire-and-poll. |
| `$global:_smtcMgrCacheTime` | DateTime | Cache anchor for manager TTL. |
| `$global:_smtcMgrCacheTTL` | int (ms) | Success=600, failure=5000–30000 (exponential backoff). |
| `$global:_smtcMgrTask` | `Task<T>` | In-flight RequestAsync task (fire-and-poll pattern). |
| `$global:_smtcMgrTaskCts` | `CancellationTokenSource` | CTS for the in-flight manager task. |
| `$global:_smtcSessionsCache` | array | Per-tick session list from GetSessions(). |
| `$global:_smtcSessionsTick` | long | Tick count when sessions were last fetched. |
| `$global:_smtcPropsCache` | hashtable | SAUMID → MediaProperties object. Per-tick cache. |
| `$global:_smtcPropsFiredThisTick` | hashtable | SAUMID → bool. Prevents double-firing TryGetMediaPropertiesAsync per tick. |
| `$global:_smtcPlaybackCache` | hashtable | SAUMID → (PlaybackInfo, timestamp). Staleness-guarded (500ms). |
| `$global:_smtcNpCache` / `_smtcNpCacheMs` | object / long | Rate-limit wrapper for Get-SMTCNowPlaying (300ms interval). |
| `$global:_winrtCallsMin` / `_winrtTmoMin` | int | Per-minute counters for CANARY reporting. Reset every 60s. |
| `$global:_gcFlushLastMs` | long | Anchor for 5-minute Gen2 GC flush. |

### Album art state machine
| Variable | Type | Mutation pattern |
|---|---|---|
| `$global:_smtcArtCache` | hashtable | CacheKey → data URI string. LRU-capped at 200 entries. |
| `$global:_smtcArtCacheOrder` | `Queue[string]` | LRU eviction order queue. |
| `$global:_smtcArtState` | string | State machine: `idle / waiting / opening / loading`. |
| `$global:_smtcArtPendingKey` | string | Art cache key for the pending extraction. |
| `$global:_smtcArtPendingProps` | object | MediaProperties RCW for the pending art. |
| `$global:_smtcArtPendingMs` | long | Timestamp when art was queued (400ms cool-off). |
| `$global:_smtcArtOpenTask` / `_smtcArtOpenCts` | Task/CTS | In-flight `OpenReadAsync` task + cancellation. |
| `$global:_smtcArtLoadTask` / `_smtcArtLoadCts` | Task/CTS | In-flight `LoadAsync` task + cancellation. |
| `$global:_smtcArtStreamRef` / `_smtcArtReaderRef` | WinRT objects | Stream + DataReader held across state transitions. |

### Scrobble / playback tracking
| Variable | Type | Mutation pattern |
|---|---|---|
| `$global:_lastNp` | hashtable | Last non-null NowPlaying result. Used for debounce + source-closed detection. |
| `$global:_scrobbleLastKey` | string | `"artist\|\|\|track"` key of last scrobbled track. |
| `$global:_scrobbleLastSendMs` | long | Timestamp of last /webhook send. Heartbeat logic. |
| `$global:_sourceClosedSent` | bool | True after source-closed webhook was sent for the current gap. |
| `$global:_nullTickCount` | int | Consecutive null-NowPlaying ticks. Threshold=12 for source-closed. |
| `$global:_songEpoch` | hashtable | `"artist\|\|\|track"` → epoch ms. Position estimation for sources that don't report position. |
| `$global:_sourceLastPos` | hashtable | Per-key (source + track) position + timestamp for SMTC-frozen detection. |
| `$global:_diagTickCount` | long | Monotonically increasing tick counter. Used for periodic diagnostics modulo. |
| `$global:_tickPhase` | string | Current phase name for SLOW TICK reporting. |
| `$global:_tickPhaseMs` | Hashtable | Per-phase elapsed ms for SLOW TICK breakdown. |
| `$global:_detectorMs` | Hashtable | Per-detector elapsed ms. |
| `$global:_chain` | `List[string]` | Detector chain log for DETECT: log lines. |
| `$global:_detSlowSkip` | hashtable | Detector name → ticks remaining in slow-skip cooldown. |

### HTTP / networking
| Variable | Type | Mutation pattern |
|---|---|---|
| `$global:_httpClient` | `System.Net.Http.HttpClient` | Lazy-initialized once on first Send-WebhookAsync or WMP-Deezer lookup. |
| `$global:_pollTimerTask` | `Task<string>` | In-flight GET for pollTimer `/current` poll. |
| `$global:_wmpDeezerPendingTask` | `Task<string>` | In-flight GET for WMP title resolution via Deezer. |

### Auto-updater
| Variable | Type | Mutation pattern |
|---|---|---|
| `$global:_updateState` | string | State machine: `idle/checking/available/downloading/ready/installing`. |
| `$global:_updateVersion` | string | Latest available version string. |
| `$global:_updateMsiUrl` / `_updateMsiSha256` | string | From manifest JSON. |
| `$global:_updateAutoInstall` | bool | From version.json manifest. |
| `$global:_updateLastCheckMs` | long | Timestamp of last check (1-hour throttle). |
| `$global:_updateMsiPath` | string | Path of downloaded MSI in TEMP. |
| `$global:_updateUserCheck` | bool | Set when user manually clicked Check for Updates. |
| `$global:_updateWebClient` | `System.Net.WebClient` | In-flight download WebClient (null when idle). |
| `$global:_updateCheckTask` | `Task<string>` | In-flight manifest HTTP GET. |
| `$global:_updateWindow` | `System.Windows.Forms.Form` | In-process update progress window (null when closed). |
| `$global:_updateDownloadBytes` / `_updateDownloadTotal` | long | Progress counters. |

### UI / tray menu
| Variable | Type | Mutation pattern |
|---|---|---|
| `$script:_menuForm` | `System.Windows.Forms.Form` | The live custom menu form. Null when menu is closed. |
| `$script:_ObsSrcCacheAt` / `_ObsSrcCacheVal` | DateTime / bool | 30s OBS source scan cache. |
| `$script:_obsAutoAddAttempted` | bool | Set after auto-add fires at startup. |
| `$global:_obsWatchTimer` / `_obsDelayTimer` / `_obsRetryTimer` | `Timer` | OBS exit-watcher timers. |
| `$global:_obsWatcherActive` | bool | Guard against double-starting the OBS exit watcher. |
| `$script:_patchNotesRows` | array | Pre-flattened patch notes row layout for owner-draw render. |
| `$script:_patchNotesPaint` | scriptblock | Cached Paint delegate for patch notes panel. |
| `$script:_audCurKey` | string | Selected audio device key (mirrored for Paint handler only). |
| `$script:_platStates` | hashtable | Platform name → enabled bool. For platforms dialog Paint handlers. |

### Logging
| Variable | Type | Mutation pattern |
|---|---|---|
| `$global:_logRingBuf` | `Queue<string>` | 20-entry ring buffer of recent log messages for SLOW TICK context. |
| `$global:_logRingSize` | int | Constant 20. |

---

## Threading model

### Current (PS 5.1 / WinForms STA)

Everything runs on the **single WinForms STA UI thread**. There is no second thread owned by tray.ps1.

- `[System.Windows.Forms.Application]::Run()` at line 9419 is the message pump.
- `scrobbleTimer` (100ms interval), `pollTimer` (2s interval), `obsTimer` (one-shot 5s), and all other `System.Windows.Forms.Timer` instances fire on the UI thread via the message pump.
- WinRT async operations are started (`Task = asyncOp.AsTask()`), and then their `.IsCompleted` status is polled on the next timer tick — a cross-tick fire-and-poll pattern. No actual thread-pool work occurs in tray.ps1 itself.
- `Send-WebhookAsync` fires `HttpClient.PostAsync` and discards the Task — the .NET thread pool runs the actual network I/O, but the UI thread never waits for it.
- `MasterFM.SMTC.SMTCWatcher` (C# class in tray_native.dll) runs event handlers on thread-pool threads (WinRT dispatches on ThreadPool). Its event handlers update `ConcurrentDictionary` / `ConcurrentQueue` snapshots. PowerShell reads those from the UI thread via `DrainEvents()` / `GetSnapshot()`. This is the only true cross-thread interaction.
- `Start-ThreadJob` is explicitly noted as unavailable in PS 5.1 (line 7222). No background jobs are used.

### What .NET 8 would use

- Single STA UI thread for all WinForms operations (unchanged).
- `async/await` on the UI thread using `SynchronizationContext` (WinForms provides one). WinRT awaits, HTTP calls, and file I/O all become `await` expressions that suspend the method but return control to the message pump.
- `SMTCWatcher` continues unchanged — it already correctly dispatches to thread-pool and exposes thread-safe read APIs.
- `Task.Run(...)` or `PeriodicTimer` for any genuinely background work.
- The entire cross-tick fire-and-poll infrastructure (15+ `$global:_*Task` variables) disappears because `await` replaces it.

---

## Estimated port effort

### Total honest estimate: **800–1,100 engineering-hours** (20–28 person-weeks solo; 10–14 weeks with 2 devs)

Broken down by area:

| Area | Effort | Complexity driver |
|---|---|---|
| SMTC + WinRT interop (event-driven watcher already done) | 40–60h | `Await-WinRT` wrapper dissolves; fire-and-poll cache system dissolves; album art state machine simplifies to ~20 lines with `await`. SMTC watcher C# code can be reused verbatim. |
| Scrobble tick loop + 10 detectors | 80–120h | Logic is straightforward but 10 detectors with complex priority, epoch tracking, position estimation, multi-source conflict resolution. Most translates line-for-line. |
| WinForms UI: tray icon, custom menu, forms | 100–150h | Custom owner-draw menu form (rounded corners, P/Invoke, opacity animation, hover states) is the largest single UI block. ~8 distinct dialogs. |
| Config system + JSON I/O | 20–30h | Straightforward. Typed DTOs replace dynamic `PSCustomObject`. |
| Auto-updater | 40–60h | 6-state machine with WebClient download, progress window, SHA-256 verify, msiexec install helper. Medium complexity. |
| OBS integration (WebSocket + JSON direct) | 40–60h | WebSocket auth/message exchange translates cleanly with `await`. JSON scene editing is straightforward. |
| Process management (server, audio_spectrum, restart chain) | 20–30h | Minor. `ProcessStartInfo` replaces `Start-Process`. WMI detached process spawn becomes `CreateProcess` P/Invoke. |
| Auto-start (WScript.Shell shortcut) | 10–15h | IShellLink COM interop or Windows API Code Pack. |
| WMP COM / UIAutomation detector | 60–80h | WMP COM interface is straightforward C# COM interop. UIAutomation tree walk (`_walkUIA`) is the ugliest single function — heavy use of UIAutomation API, translates but needs care. |
| HTTP client / webhook | 10–15h | Already using `HttpClient` natively; trivial with `await`. |
| Logging + diagnostics | 15–20h | Simple file I/O. Swap `Start-Transcript` with a file-tee TextWriter. |
| Setup, build pipeline, integration | 40–60h | New `.csproj`, NuGet setup, MSI changes for new binary, testing all detectors. |

### Hardest parts (ranked)

1. **SMTC WinRT async in PS 5.1** — ALREADY SOLVED by SMTCWatcher in C# (tray_native.cs). The PS-side complexity dissolves with `await`. The hard work is already done.

2. **`.GetNewClosure()` event handlers with `$script:`/`$global:` scope semantics** — The hosted-runspace scope bugs (v11.2.0 — `$script:` writes from closures are unreliable) forced workarounds with captured hashtables. In C# there are no scope bugs; all closures capture variables correctly.

3. **The cross-tick fire-and-poll async pattern** — 15+ `$global:_*Task` variables and multi-state machines. Each one requires understanding what the equivalent `async/await` flow looks like. Individually simple, but there are many of them.

4. **Custom owner-draw WinForms menu** — The dark-themed popup menu with rounded corners (DwmSetWindowAttribute), CreateRoundRectRgn, fade animation, hover states, opacity, and the patch-notes owner-draw panel are the most time-intensive UI work. No shortcuts here — it's a substantial custom-draw implementation.

5. **WMP UIAutomation tree walk** (`Get-WMPNowPlayingUIA`, lines 7710–8053, ~350 lines) — The recursive `_walkUIA` function with UIAutomation element traversal is complex and fragile. Translates to C# but needs careful testing.

### What the C# rewrite gains

- Eliminate the `tray_launcher.cs` + PS runspace hosting overhead — the hosted PowerShell interpreter adds ~80ms per scrobble tick just in interpreter overhead (documented in v11.2.2 patch notes). C# runs this logic in microseconds.
- True `async/await` — eliminates the ~15 fire-and-poll cross-tick state machines.
- Static typing — catches the kinds of bugs (wrong `$script:` scope, wrong `AsTask` overload, `PSCustomObject` property access) that required multiple patch cycles.
- The `SMTCWatcher` C# class (tray_native.cs) is already fully production-quality and can be copied verbatim.
- `WebClient` (deprecated) replaced with `HttpClient` throughout.
- All `Add-Type` P/Invoke inline compilation goes away — already in tray_native.cs.

---

*Researcher 5 — analysis complete. This document covers all identified PowerShell-specific patterns in tray.ps1 (9,424 lines). The most critical architectural finding is that the two hardest historical problems — SMTC memory leak (AsTask 1-arg vs 2-arg) and SMTC FPS lag (synchronous polling) — are already solved in C# via SMTCWatcher in tray_native.cs. The PS→C# port complexity is concentrated in the UI layer and the async-pattern translation, not in the domain logic.*
