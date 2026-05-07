# V14 Stage 5 Phase 1 -- CsWinRT Research

## 1. CsWinRT overview

CsWinRT (C#/WinRT) is the modern .NET projection for Windows Runtime (WinRT) APIs.
It generates C# bindings at build time from WinRT metadata (.winmd files), providing:
- Compile-time type checking of WinRT APIs
- Strong typing in IDE (IntelliSense, refactoring)
- No runtime reflection overhead for property/method dispatch
- Standard .NET async/await (Task<T>) instead of IAsyncOperation<T> workarounds

CsWinRT replaces two older approaches:
- The .NET Framework 4.x built-in WinRT interop (the [ContentType=WindowsRuntime]
  Add-Type trick used in tray.ps1)
- The Microsoft.Windows.SDK.Contracts package (NuGet-based .NET Framework/Core path)

GitHub: https://github.com/microsoft/CsWinRT
License: MIT

---

## 2. Package / TFM selection

### Option A: net8.0-windows10.0.19041.0 (RECOMMENDED for new code)

```xml
<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
```

- The Windows SDK NuGet reference is implicit -- no separate package needed
- All Windows.Media.Control, Windows.Storage.Streams APIs available at compile time
- Requires .NET 8 runtime -- NOT loadable by PowerShell 5.1 (.NET Framework 4.x)
- Used by server_dotnet in this project (for SmtcSource.cs, ArtSources)

This is the correct TFM for a greenfield .NET 8 CsWinRT library.

### Option B: net8.0-windows (no version suffix)

```xml
<TargetFramework>net8.0-windows</TargetFramework>
```

- Adds basic Windows API surface but NOT full WinRT projection
- Missing Windows.Media.Control, Windows.Storage.Streams etc.
- Not suitable for SMTC work

### Option C: netstandard2.0 (cross-runtime compatibility)

```xml
<TargetFramework>netstandard2.0</TargetFramework>
```

- Can be loaded by BOTH .NET Framework 4.6.1+ (PowerShell 5.1) AND .NET 5+
- Does NOT include CsWinRT Windows API projections
- WinRT APIs would still require runtime reflection
- Benefit: moves off csc.exe without breaking PS5.1 loading

### Option D: Multi-target net462 + net8.0-windows10.0.19041.0

```xml
<TargetFrameworks>net462;net8.0-windows10.0.19041.0</TargetFrameworks>
```

- Produces two DLLs in different lib/ folders
- PS5.1 would load net462 variant (reflection-based, same as today)
- PS7 or future host would load net8.0 variant (CsWinRT projections)
- Complex build output; Add-Type -Path needs the right DLL path

### Recommendation for Stage 5

Given that tray.ps1 currently runs in PowerShell 5.1 (NET Framework 4.x):

SHORT TERM: Option C (netstandard2.0) -- moves tray_native off csc.exe,
maintains PS5.1 compatibility, keeps reflection-based WinRT.

LONG TERM: Option A (net8.0-windows10.0.19041.0) -- requires PS7 migration for
tray.ps1 (Stage 7 prerequisite question, see QUESTIONS.md).

The existing .NET 8 projects (server_dotnet, audio_spectrum, launcher) already
use net8.0-windows, so dotnet SDK infrastructure exists.

---

## 3. SMTC API mapping: current reflection vs CsWinRT

The following table maps each reflection call in SMTCWatcher to its CsWinRT equivalent.

### Manager-level

| Current (reflection) | CsWinRT equivalent |
|---------------------|-------------------|
| `mgrT.GetMethod("GetSessions").Invoke(mgr, null)` | `mgr.GetSessions()` |
| `mgrT.GetMethod("GetCurrentSession").Invoke(mgr, null)` | `mgr.GetCurrentSession()` |
| `AttachEvent(..., "SessionsChanged", handler)` | `mgr.SessionsChanged += handler` |
| `AttachEvent(..., "CurrentSessionChanged", handler)` | `mgr.CurrentSessionChanged += handler` |

Namespace: `Windows.Media.Control`
Type: `GlobalSystemMediaTransportControlsSessionManager`

### Per-session

| Current (reflection) | CsWinRT equivalent |
|---------------------|-------------------|
| `session.GetType().GetProperty("SourceAppUserModelId").GetValue(session)` | `session.SourceAppUserModelId` |
| `sessT.GetMethod("GetPlaybackInfo").Invoke(session, null)` | `session.GetPlaybackInfo()` |
| `sessT.GetMethod("GetTimelineProperties").Invoke(session, null)` | `session.GetTimelineProperties()` |
| `sessT.GetMethod("TryGetMediaPropertiesAsync").Invoke(session, null)` | `await session.TryGetMediaPropertiesAsync()` |
| `AttachEvent(..., "MediaPropertiesChanged", h)` | `session.MediaPropertiesChanged += h` |
| `AttachEvent(..., "PlaybackInfoChanged", h)` | `session.PlaybackInfoChanged += h` |
| `AttachEvent(..., "TimelinePropertiesChanged", h)` | `session.TimelinePropertiesChanged += h` |

Type: `GlobalSystemMediaTransportControlsSession`

### PlaybackInfo

| Current (reflection) | CsWinRT equivalent |
|---------------------|-------------------|
| `infoT.GetProperty("PlaybackStatus").GetValue(info)` | `info.PlaybackStatus` |
| `cT.GetProperty("IsPauseEnabled").GetValue(controls)` | `controls.IsPauseEnabled` |
| `cT.GetProperty("IsPlayEnabled").GetValue(controls)` | `controls.IsPlayEnabled` |

Type: `GlobalSystemMediaTransportControlsSessionPlaybackInfo`

### TimelineProperties

| Current (reflection) | CsWinRT equivalent |
|---------------------|-------------------|
| `tlT.GetProperty("Position").GetValue(tl)` | `tl.Position` |
| `tlT.GetProperty("EndTime").GetValue(tl)` | `tl.EndTime` |
| `tlT.GetProperty("StartTime").GetValue(tl)` | `tl.StartTime` |
| `tlT.GetProperty("LastUpdatedTime").GetValue(tl)` | `tl.LastUpdatedTime` |

Type: `GlobalSystemMediaTransportControlsSessionTimelineProperties`

### MediaProperties

| Current (reflection) | CsWinRT equivalent |
|---------------------|-------------------|
| `pT.GetProperty("Title").GetValue(props)` | `props.Title` |
| `pT.GetProperty("Artist").GetValue(props)` | `props.Artist` |
| `pT.GetProperty("AlbumTitle").GetValue(props)` | `props.AlbumTitle` |
| `pT.GetProperty("AlbumArtist").GetValue(props)` | `props.AlbumArtist` |
| `pT.GetProperty("Thumbnail").GetValue(props)` | `props.Thumbnail` |

Type: `GlobalSystemMediaTransportControlsSessionMediaProperties`

### Event handler signatures (CsWinRT)

```csharp
// TypedEventHandler<TSender, TResult>
mgr.SessionsChanged += (GlobalSystemMediaTransportControlsSessionManager sender,
                        SessionsChangedEventArgs args) => { ... };

session.MediaPropertiesChanged += (GlobalSystemMediaTransportControlsSession sender,
                                   MediaPropertiesChangedEventArgs args) => { ... };
// etc.
```

The current AttachEvent() machinery in SMTCWatcher (Expression.Lambda, token capture)
is entirely replaced by standard C# event syntax. Disposal via `event -= handler`.

---

## 4. Thumbnail extraction code pattern (CsWinRT)

The current thumbnail extraction lives in tray.ps1 as a PowerShell async state machine.
In a .NET 8 CsWinRT library, this becomes synchronous C# with async/await:

```csharp
// Requires: Windows.Storage.Streams namespace
// using Windows.Media.Control;
// using Windows.Storage.Streams;

public static async Task<(byte[] bytes, string mimeType)> ExtractThumbnailAsync(
    GlobalSystemMediaTransportControlsSessionMediaProperties props,
    CancellationToken ct = default)
{
    var thumbRef = props.Thumbnail;
    if (thumbRef == null) return (null, null);

    using var stream = await thumbRef.OpenReadAsync().AsTask(ct);
    if (stream == null || stream.Size == 0) return (null, null);

    string mime = stream.ContentType ?? "image/png";

    using var reader = new DataReader(stream);
    var bytesLoaded = await reader.LoadAsync((uint)stream.Size).AsTask(ct);
    var bytes = new byte[bytesLoaded];
    reader.ReadBytes(bytes);

    // Magic-byte override (JPEG: FF D8 FF)
    if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        mime = "image/jpeg";

    return (bytes, mime);
}

// Encode to data URI:
// string uri = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
```

Notes:
- `AsTask()` is from `System.WindowsRuntimeSystemExtensions` -- available in .NET 8
- The state machine in tray.ps1 exists only because PS5.1 cannot await naturally
- In .NET 8 C#, this collapses to straightforward async/await
- No resize, no recompress -- matches current behavior

---

## 5. Build tooling changes

### Current (csc.exe)

```
csc /nologo /target:library /out:tray_native.dll
    /reference:System.dll /reference:System.Core.dll
    src\tray_native.cs
```

Requires: csc.exe at C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe

### Future (dotnet build with tray_native.csproj)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>  <!-- or netstandard2.0 -->
    <AssemblyName>tray_native</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="tray_native.cs" />
  </ItemGroup>
</Project>
```

Build command: `dotnet build src\tray_native\tray_native.csproj -c Release`

Changes to _full_rebuild.ps1:
- Remove csc.exe block for tray_native (lines ~177-194)
- Add dotnet publish step (same pattern as launcher, customize)
- Remove csc.exe availability check dependency for tray_native

### Dependency removals

| Removed | Reason |
|---------|--------|
| csc.exe requirement | dotnet SDK used instead |
| System.dll (GAC ref) | Implicit in SDK |
| System.Core.dll (GAC ref) | Implicit in SDK |

No new runtime dependencies for netstandard2.0 option.
For net8.0-windows10.0.19041.0 option: requires .NET 8 runtime (already installed).

---

## 6. .NET 8 alignment notes

### Existing .NET 8 projects in this codebase

| Project | TFM | Notes |
|---------|-----|-------|
| server_dotnet | net8.0 | No windows TFM suffix -- uses SmtcSource via reflection |
| audio_spectrum | net8.0-windows | WASAPI/MME audio capture |
| launcher | net8.0-windows | Process launcher, C# port of launcher.cs |
| customize | net8.0-windows | WinForms customizer dialog |

server_dotnet uses `SmtcSource.cs` for SMTC art -- this is a SEPARATE SMTC
client that runs server-side, distinct from tray_native's SMTCWatcher.

### WinRT in server_dotnet

server_dotnet.csproj uses `net8.0` without a windows TFM suffix. It accesses
SMTC via reflection in SmtcSource.cs (same pattern as tray_native). This is
intentional -- server runs headless without a UI thread, and WinRT SMTC access
from a non-windowed .NET 8 process has its own threading constraints.

### PowerShell host compatibility

| Host | .NET version | Can load .NET Framework DLL | Can load .NET 8 DLL |
|------|-------------|----------------------------|---------------------|
| Windows PowerShell 5.1 | .NET Framework 4.x | YES | NO |
| PowerShell 7.x | .NET 8 | NO (in most cases) | YES |

This incompatibility is the central constraint for Stage 5 execution.
If tray.ps1 stays in PS5.1, tray_native.dll must stay .NET Framework (csc.exe)
or use netstandard2.0 (still no CsWinRT Windows APIs).

### WinRT activation in PS5.1 vs PS7

PS5.1: `[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager,
        Windows.Media.Control,ContentType=WindowsRuntime]`
-- This is a PS5.1-specific syntax. PS7 uses different WinRT activation.

PS7: WinRT types are available directly when targeting net8.0-windows10.0.x.
     No Add-Type hack required in C# code.

---

## 7. Gotchas and known issues

### G1: EventRegistrationToken requirement

WinRT events use EventRegistrationToken for unsubscription. The CLR's standard
EventInfo.RemoveEventHandler() THROWS on WinRT events:
"Adding or removing event handlers dynamically is not supported on WinRT events"

Current SMTCWatcher handles this correctly by capturing the token from add_X()
and calling remove_X(token) at Dispose.

In CsWinRT (net8.0-windows10.0.x), standard C# event -= syntax works correctly
because CsWinRT handles the token internally. The AttachEvent/DetachBinding
infrastructure in SMTCWatcher becomes unnecessary.

### G2: WinRT async in .NET 8

`IAsyncOperation<T>.AsTask()` is available in .NET 8 via
`System.WindowsRuntimeSystemExtensions` (in Microsoft.Windows.SDK.NET.Ref).
The current FetchMediaPropsSync polling loop in SMTCWatcher can be replaced
with a straightforward `await session.TryGetMediaPropertiesAsync()`.

### G3: Thread apartment requirements

Some WinRT APIs (specifically SMTC) require STA or are called from the WinRT
thread pool. SMTCWatcher's current pattern (fire events on WinRT threads, read
snapshots from PS tick) is correct and will remain correct under CsWinRT.

### G4: ps.exe Add-Type conflict

If tray.ps1 does `Add-Type -Path tray_native.dll` and the DLL is .NET Framework,
this works in PS5.1. If tray.ps1 ever loads BOTH the .NET Framework version AND
a .NET 8 version (via some future multi-load), types would conflict.
Stage 5 must ensure only one DLL variant is loaded.

### G5: Soundcloud-rpc session recycling

SMTCWatcher already handles the soundcloud-rpc pattern (session object replaced
on every track change, same SAUMID). This is a critical behavioral requirement
that must be validated in any CsWinRT port.
