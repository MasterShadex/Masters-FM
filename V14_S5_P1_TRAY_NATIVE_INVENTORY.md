# V14 Stage 5 Phase 1 -- tray_native Inventory

## 1. Source files

| File | Language | Size | Last modified |
|------|----------|------|---------------|
| src/tray_native.cs | C# (pure managed) | 43,747 bytes | 2026-05-04 07:58 |
| tray_native.dll (output) | .NET assembly | 33,304 bytes | 2026-05-07 00:09 |
| v13_source_backup/tray_native.cs | C# (backup) | 42,924 bytes | 2026-05-04 07:26 |

### Critical finding: NOT C++/CLI

CLAUDE_CODE_INSTRUCTIONS.md assumed tray_native.dll was a C++/CLI mixed-mode binary.
This is incorrect. tray_native.cs is pure C# targeting .NET Framework 4.x. There is
no MSVC dependency, no C++/CLI compilation step, no vcxproj, no Windows SDK
(build-time). A separate .csproj does NOT exist for tray_native -- it is compiled
directly by csc.exe via _full_rebuild.ps1.

### No separate .csproj

There is no tray_native.csproj or tray_native.vcxproj. The build command is embedded
in _full_rebuild.ps1 as a direct csc.exe invocation.

---

## 2. Public API surface

### 2.1 MFM_Shell (public static class)

Purpose: Sets AppUserModelID for taskbar grouping.

```csharp
public static void SetCurrentProcessExplicitAppUserModelID(string AppID)
// P/Invoke: shell32.dll, PreserveSig=false
```

tray.ps1 call: `[MFM_Shell]::SetCurrentProcessExplicitAppUserModelID("MastersFM.App")`
Location: tray.ps1 line ~35

---

### 2.2 MFM_MenuNative (public static class)

Purpose: Rounded-corner styling and foreground focus for the custom tray menu form.

```csharp
public static int  DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size)
public static IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy)
public static bool SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw)
public static bool SetForegroundWindow(IntPtr hwnd)
// P/Invoke: dwmapi.dll, gdi32.dll, user32.dll
```

tray.ps1 call: Used by custom WinForms menu rendering code.

---

### 2.3 NativeMethods.GuiRes (public static class in namespace NativeMethods)

Purpose: GDI/User object count monitoring -- canary for slow handle leaks.

```csharp
public static int GetGuiResources(IntPtr hProcess, uint uiFlags)
// P/Invoke: user32.dll
```

tray.ps1 call: Periodic GDI/User handle count logging.

---

### 2.4 MasterFM.Win32Windows (public static class)

Purpose: Enumerate top-level windows by PID or title -- used to detect active
streaming service from browser window titles.

```csharp
// Delegates
public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam)

// P/Invoke
public static bool   EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam)
public static int    GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId)
public static bool   IsWindowVisible(IntPtr hWnd)
public static int    GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount)
public static int    GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount)

// Managed helpers
public static List<IntPtr>  GetProcessWindows(uint pid)
public static List<string>  GetAllVisibleTitles()
public static string        GetTitle(IntPtr h)
public static string        GetClass(IntPtr h)
```

tray.ps1 call: `[MasterFM.Win32Windows]::GetAllVisibleTitles()` (tray.ps1 ~line 6380)
Used in Get-BrowserPlatformFromWindows() to detect streaming service.

---

### 2.5 MasterFM.AudioPeak (public static class)

Purpose: Core Audio peak-value detection -- used to detect soundcloud-rpc play/pause
since it never updates SMTC PlaybackStatus.

```csharp
public static float GetPeakForProcessName(string nameContains)
```

COM interfaces defined manually via P/Invoke (no Windows SDK reference):
- IMMDeviceEnumerator (GUID A95664D2...)
- IMMDeviceCollection (GUID 0BD7A1BE...)
- IMMDevice (GUID D666063F...)
- IAudioSessionManager2 (GUID 77AA99A0...)
- IAudioSessionEnumerator (GUID E2F5BB11...)
- IAudioSessionControl (GUID F4B1A599...)
- IAudioSessionControl2 (GUID BFB7FF88...)
- IAudioMeterInformation (GUID C02216F6...)
- MMDeviceEnumeratorComObject (GUID BCDE0395...)

All COM interfaces defined inline -- no dependency on Windows.h or audioclient.h.

---

### 2.6 MasterFM.SMTC.SMTCWatcher (public sealed class, IDisposable)

This is the largest and most complex type. Event-driven SMTC watcher introduced
in v12.0.0 to fix a system-wide FPS lag spike (3-5s of 0 FPS) on track changes.

#### Constructor

```csharp
public SMTCWatcher()  // default
```

#### Initialization (must call once after construction)

```csharp
public void Initialize(object manager)
// manager: WinRT GlobalSystemMediaTransportControlsSessionManager
// Passed in from tray.ps1 -- the watcher does not activate WinRT itself
```

#### Read API (called from PowerShell tick handler)

```csharp
public SMTCSessionSnapshot GetSnapshot(string saumid)
public string[]            GetSaumids()
public object[]            GetSessions()
public SMTCChangeRecord[]  DrainEvents()
```

#### Properties

```csharp
public long     EventsReceivedTotal  // Interlocked counter, all events ever received
public DateTime LastEventUtc         // Timestamp of most recent event
public string   CurrentSaumid        // SAUMID of GetCurrentSession() result
public int      SessionCount         // Count of tracked sessions
```

#### Disposal

```csharp
public void Dispose()
// Detaches all WinRT event subscriptions via EventRegistrationToken
```

#### tray.ps1 call sites (lines)

| Line | Call |
|------|------|
| 5308 | `[MasterFM.SMTC.SMTCWatcher]::new()` |
| 5309 | `.Initialize($_v12Mgr)` |
| 5310 | `.SessionCount`, `.EventsReceivedTotal` |
| 6036 | `.GetSessions()` |
| 6080 | `.GetSnapshot($key)` |
| 6119 | `.GetSnapshot($key)` |
| 6711 | `.GetSnapshot($_tlKey)` |
| 8791 | `.DrainEvents()` |
| 9389-9394 | `.EventsReceivedTotal`, `.LastEventUtc`, `.SessionCount`, `.Dispose()` |

---

### 2.7 MasterFM.SMTC.SMTCSessionSnapshot (public class)

Data transfer object. All fields are public (not properties) for zero-overhead
PS reflection access.

```csharp
public string Saumid
public string PlaybackStatusName      // "Playing", "Paused", "Stopped", etc.
public int    PlaybackStatusValue     // enum int value
public long   PositionMs
public long   DurationMs
public long   LastUpdatedTimeUtcTicks
public string Title
public string Artist
public string AlbumTitle
public string AlbumArtist
public object MediaPropertiesRcw      // Raw WinRT RCW (for Thumbnail access)
public object PlaybackInfoRcw
public object TimelinePropertiesRcw
public object SessionRef              // Raw session RCW
public bool   IsPauseEnabledRaw
public bool   IsPlayEnabledRaw
public bool   HasMediaProps
public bool   HasPlaybackInfo
public bool   HasTimeline
public DateTime SnapshotUtc
public long   LastPlaybackReadTicks   // Rate-limit anchor (internal use)
public long   LastTimelineReadTicks
public long   LastMediaPropsReadTicks
```

---

### 2.8 MasterFM.SMTC.SMTCChangeRecord (public class)

```csharp
public SMTCEventKind Kind
public string        Saumid
public DateTime      UtcTime
```

---

### 2.9 MasterFM.SMTC.SMTCEventKind (public enum)

```csharp
SessionAdded              = 1
SessionRemoved            = 2
PlaybackInfoChanged       = 3
MediaPropertiesChanged    = 4
TimelinePropertiesChanged = 5
CurrentSessionChanged     = 6
InitialEnumeration        = 7
```

---

## 3. SMTC APIs used (via reflection inside SMTCWatcher)

tray_native does NOT reference Windows.winmd or any WinRT projection at compile
time. All WinRT access uses System.Reflection at runtime. The following WinRT
APIs are accessed by name:

### Manager-level (GlobalSystemMediaTransportControlsSessionManager)

| API | Access method |
|-----|---------------|
| `GetSessions()` | MethodInfo.Invoke |
| `GetCurrentSession()` | MethodInfo.Invoke |
| Event: `SessionsChanged` | EventInfo.GetAddMethod().Invoke (token captured) |
| Event: `CurrentSessionChanged` | EventInfo.GetAddMethod().Invoke (token captured) |

### Per-session (GlobalSystemMediaTransportControlsSession)

| API | Access method |
|-----|---------------|
| Property: `SourceAppUserModelId` | PropertyInfo.GetValue |
| `GetPlaybackInfo()` | MethodInfo.Invoke |
| `GetTimelineProperties()` | MethodInfo.Invoke |
| `TryGetMediaPropertiesAsync()` | MethodInfo.Invoke (polled sync wait) |
| Event: `MediaPropertiesChanged` | EventInfo.GetAddMethod().Invoke |
| Event: `PlaybackInfoChanged` | EventInfo.GetAddMethod().Invoke |
| Event: `TimelinePropertiesChanged` | EventInfo.GetAddMethod().Invoke |

### PlaybackInfo (GlobalSystemMediaTransportControlsSessionPlaybackInfo)

| Property | Notes |
|----------|-------|
| `PlaybackStatus` | Enum, toString() used |
| `Controls.IsPauseEnabled` | bool |
| `Controls.IsPlayEnabled` | bool |

### TimelineProperties

| Property | Notes |
|----------|-------|
| `Position` | TimeSpan |
| `EndTime` | TimeSpan |
| `StartTime` | TimeSpan |
| `LastUpdatedTime` | DateTimeOffset |

### MediaProperties (TryGetMediaPropertiesAsync result)

| Property | Notes |
|----------|-------|
| `Title` | string |
| `Artist` | string |
| `AlbumTitle` | string |
| `AlbumArtist` | string |
| `Thumbnail` | IRandomAccessStreamReference -- NOT extracted in tray_native |

---

## 4. Thumbnail extraction details

Thumbnail extraction is in tray.ps1 (NOT in tray_native.dll).
tray_native only captures `MediaPropertiesRcw` -- the raw RCW object -- in
SMTCSessionSnapshot. tray.ps1 reads `.Thumbnail` from that object directly.

### Async state machine (tray.ps1)

State machine avoids blocking the WinForms tick thread:

1. **idle**: on MediaPropertiesChanged event, extract `snap.MediaPropertiesRcw.Thumbnail`
             (IRandomAccessStreamReference). Call `thumbRef.OpenReadAsync()` wrapped in
             AsTask with 500ms CancellationToken. Advance to 'opening'.

2. **opening**: poll task.IsCompleted each tick. On completion, get
               `IRandomAccessStreamWithContentType`. Create `DataReader`. Call
               `reader.LoadAsync(stream.Size)` with 500ms CTS. Advance to 'loading'.

3. **loading**: poll task.IsCompleted. On completion, call `reader.ReadBytes(bytes)`.
               Determine MIME: use `stream.ContentType`, override to 'image/jpeg' if
               magic bytes are 0xFF 0xD8 0xFF. Encode as base64 data URI.
               Cache via Write-SMTCArtCacheEntry. Return URI.

### Format

- No resize, no recompress -- raw bytes from SMTC stream
- MIME auto-detected: PNG assumed, JPEG if magic bytes match
- Delivered as: `data:image/png;base64,...` or `data:image/jpeg;base64,...`
- Max timeout per async step: 500ms (CancellationTokenSource)

---

## 5. Threading and event delivery model

### Event delivery

WinRT fires SMTC events on background COM/WinRT threads. SMTCWatcher handles them
on those threads.

### Rate limiting (inside SMTCWatcher)

Multiple WinRT events arrive in bursts on track changes (8+ PlaybackInfoChanged in
<100ms is normal during soundcloud-rpc session recreation).

Two suppression mechanisms:
- **Rate limit (250ms)**: per-metric, per-session. If the same metric (playback,
  timeline, media props) was read <250ms ago, skip the ALPC read.
- **Burst window (800ms)**: set on SessionsChanged and CurrentSessionChanged.
  During burst, ALL ALPC reads are skipped. After burst settles, coalesced
  enumerate re-syncs everything.

### Coalesced enumeration (SessionsChanged)

Multiple SessionsChanged within 750ms collapse to one re-enumeration:
- Extends deadline ticks, launches one Task.Run worker if not already running
- Worker waits until deadline stops moving, then runs EnumerateAndSubscribeSessions

### Thread-safe data structures

```
ConcurrentDictionary<string, SMTCSessionSnapshot>  _snaps   (SAUMID -> snapshot)
ConcurrentDictionary<string, SessionSubs>           _subs    (SAUMID -> subscriptions)
ConcurrentQueue<SMTCChangeRecord>                   _events  (change log)
```

### PowerShell tick reads

tray.ps1's tick handler calls DrainEvents() and GetSnapshot() synchronously.
These never make WinRT calls -- they only read ConcurrentDictionary/Queue.
Zero ALPC traffic in steady state. This is the core v12.0.0 performance fix.

---

## 6. Build tooling currently required

| Requirement | Details |
|-------------|---------|
| Compiler | csc.exe from C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe |
| Fallback | C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe (x86) |
| References | System.dll, System.Core.dll (both in GAC) |
| Windows SDK | Not required (WinRT accessed via reflection at runtime) |
| MSVC | Not required (no C++/CLI) |
| dotnet SDK | Not required for tray_native (used for server_dotnet, launcher, etc.) |
| Signing | build_tools\signing\_sign_msi.ps1 signs tray_native.dll after compile |

Build command in _full_rebuild.ps1:
```
csc /nologo /target:library /out:tray_native.dll
    /reference:System.dll /reference:System.Core.dll
    src\tray_native.cs
```

---

## 7. Output binary characteristics

| Property | Value |
|----------|-------|
| File size | 33,304 bytes |
| Target framework | .NET Framework 4.x (csc.exe output) |
| Architecture | AnyCPU |
| Assembly name | tray_native |
| Load mechanism | Add-Type -Path tray_native.dll (in PowerShell 5.1) |
| Runtime host | Windows PowerShell 5.1 (.NET Framework 4.x) |
| WinRT dependency | Runtime only (reflection) -- zero compile-time WinRT refs |
| MSVC CRT | None |
| PInvoke targets | shell32.dll, dwmapi.dll, gdi32.dll, user32.dll |
| COM dependencies | MMDeviceEnumerator (AudioPeak), EventRegistrationToken (SMTCWatcher) |

Note: dumpbin.exe not available in this environment. Size confirmed via filesystem.
ildasm not run (READ-ONLY brief, no build tools invoked).

---

## Key Stage 5 reframe

The original Stage 5 description assumed C++/CLI. The actual situation changes the
scope significantly:

CURRENT STATE:
- Pure C# source, no C++/CLI, no MSVC
- Build: csc.exe (.NET Framework 4.x)
- WinRT: runtime reflection (no compile-time projection)
- Host: PowerShell 5.1 (.NET Framework 4.x)
- Loads: Add-Type -Path (PS5.1 loads .NET Framework assembly)

STAGE 5 ORIGINAL GOAL: "Replace C++/CLI with CsWinRT" -- goal is already achieved
for the C++/CLI part (it was never C++/CLI). The remaining migration question is:

  Should tray_native.dll move from csc.exe + .NET Framework to dotnet build + .NET 8?

BLOCKER: PowerShell 5.1 CANNOT load .NET 8 assemblies. Moving to dotnet build
requires also migrating tray.ps1's host from PS5.1 to PS7 (or finding another
load mechanism). This is a larger architectural decision than just recompiling.

See V14_S5_P1_RISKS.md and V14_S5_P1_QUESTIONS.md for resolution paths.
