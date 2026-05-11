# Diagnosis: Issue 7 -- View Log opens folder instead of log file

## Reproduction (operator-confirmed)
Clicking "View log" in tray menu opens Windows Explorer to %LOCALAPPDATA%\MastersFM folder instead of opening the log file directly.

## Source-of-truth analysis

**TrayMenuViewModel.cs lines 451-462** -- OpenLog command:
```csharp
[RelayCommand]
private void OpenLog()
{
    _logger.Log("TrayMenu: View log", "Tray");
    var logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MastersFM");
    try
    {
        if (Directory.Exists(logDir))
            Process.Start("explorer.exe", logDir);
    }
    catch (Exception ex) { _logger.LogErr("OpenLog explorer", ex, "Tray"); }
}
```

This opens `%LOCALAPPDATA%\MastersFM` (the directory) in Windows Explorer. It does not target the log file.

**Logger.cs lines 23-26** -- log file path definition:
```csharp
private static readonly string _logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "MastersFM");
private static readonly string _logPath = Path.Combine(_logDir, "overlay.log");
```

Log file: `%LOCALAPPDATA%\MastersFM\overlay.log`. This is the same `_logDir` referenced in OpenLog, but `OpenLog` uses the directory not the file.

## Root cause
TrayMenuViewModel.cs line 460: `Process.Start("explorer.exe", logDir)` passes the directory path to Explorer, opening it as a folder. The intent is to open the log file (`overlay.log`) directly. The fix is to target the file path rather than the directory.

## Why prior work missed it
The "View log" command was implemented in Stage 7.6. The directory open was likely a placeholder or oversight -- it technically shows the user where the log lives, but does not open the file for reading. Multi-monitor or display issues may have made this less apparent during testing.

## Fix complexity
Trivial -- 2-3 line change. No new imports needed.

## Recommended fix shape (NOT implemented)
Replace the OpenLog implementation to open the file directly using the shell (default handler, which is typically Notepad or the user's associated text editor):

```csharp
[RelayCommand]
private void OpenLog()
{
    _logger.Log("TrayMenu: View log", "Tray");
    var logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MastersFM", "overlay.log");
    try
    {
        if (File.Exists(logPath))
            Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
        else
        {
            // Log file does not exist yet; fall back to opening the folder
            var logDir = Path.GetDirectoryName(logPath)!;
            if (Directory.Exists(logDir))
                Process.Start("explorer.exe", logDir);
        }
    }
    catch (Exception ex) { _logger.LogErr("OpenLog", ex, "Tray"); }
}
```

UseShellExecute=true opens the file with its associated default application (typically Notepad for .log files). If the log file does not yet exist, the fallback opens the folder so the user can see why it is absent.

Alternative: use `explorer /select,"path\overlay.log"` to open Explorer with the file selected -- this lets the user see the file in context. Either approach is acceptable; the direct-open approach above is closer to user expectation for "View log".

## Verification after fix
1. Ensure tray has been running long enough to create overlay.log.
2. Click "View log".
3. Confirm overlay.log opens in Notepad (or default .log handler).
4. Confirm the file content is the actual log output.
5. Edge case: if overlay.log does not exist, confirm the fallback opens the MastersFM folder.