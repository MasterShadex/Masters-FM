// tray_launcher.cs — Master's FM tray host (v2.0.0)
// Compiles to MastersFM_Tray.exe. Hosts PowerShell in-process via
// System.Management.Automation so tray.ps1 runs INSIDE this exe rather
// than spawning a separate powershell.exe child.  That way Task Manager
// sees a single "Master's FM" process (ProductName from VersionInfo below)
// instead of a "Windows PowerShell" row that can't be grouped with
// MastersFM.exe + server.exe.
//
// The v2.0.0 fix vs the earlier failed attempt:
//   v1.9.9 used ps.AddCommand(tray.ps1). That runs the script in a
//   pipeline-child scope. Functions defined inside tray.ps1 land in
//   that child scope, and scriptblocks registered as WinForms click
//   handlers — which PowerShell binds to the script's SessionState —
//   could not resolve functions like Show-OverlayCustomizer once the
//   pipeline was "done" with the top-level code. Menu clicks did
//   nothing.
//
//   v2.0.0 runs tray.ps1 via AddScript(scriptText, useLocalScope: false).
//   That dot-sources the .ps1, so every function / variable lands in
//   the runspace's GLOBAL scope. Closures stay valid forever, regardless
//   of pipeline state, because the script's own global SessionState
//   lives as long as the runspace itself. The runspace is kept alive
//   for the entire app lifetime (Application.Run blocks this thread).
//
// Also sets AppUserModelID so Windows shell groups taskbar/notification
// entries across all Master's FM processes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

[assembly: AssemblyTitle("Master's FM")]
[assembly: AssemblyDescription("Master's FM tray host")]
[assembly: AssemblyProduct("Master's FM")]
[assembly: AssemblyCompany("MasterShadex")]
[assembly: AssemblyVersion("5.0.0.0")]
[assembly: AssemblyFileVersion("5.0.0.0")]

class MastersFMTray
{
    // AUMID so all Master's FM processes share one Windows-shell identity.
    [DllImport("shell32.dll", PreserveSig = false)]
    static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string AppID);

    // Diagnostic log lives alongside server.log, overlay.log, menu.log
    // in %LOCALAPPDATA%\MastersFM\ (per user feedback — keep logs out of %TEMP%).
    // Each launch truncates the log to avoid unbounded growth across boots;
    // if the last run's errors were interesting, the user has already seen
    // them in the screen-preserving transcript.log or menu.log.
    static string s_logPath = Path.Combine(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MastersFM"),
        "host.log");
    static void InitLog()
    {
        try
        {
            File.WriteAllText(s_logPath,
                string.Format("=== MastersFM_Tray host boot {0:yyyy-MM-dd HH:mm:ss} ==={1}",
                    DateTime.Now, Environment.NewLine));
        }
        catch { }
    }
    static void Log(string msg)
    {
        try
        {
            File.AppendAllText(s_logPath,
                string.Format("[{0:HH:mm:ss.fff}] {1}{2}",
                    DateTime.Now, msg, Environment.NewLine));
        }
        catch { }
    }

    [STAThread]
    static int Main(string[] args)
    {
        // SetCurrentProcessExplicitAppUserModelID can only be called once
        // per process; if this throws the failure is recorded in host.log
        // via InitLog+Log right below (Log's append is safe even if the
        // file doesn't exist yet).
        string aumidErr = null;
        try { SetCurrentProcessExplicitAppUserModelID("MastersFM.App"); }
        catch (Exception ex) { aumidErr = ex.Message; }
        InitLog();
        if (aumidErr != null) Log("SetCurrentProcessExplicitAppUserModelID failed: " + aumidErr);

        string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string tray = Path.Combine(dir, "tray.ps1");

        string argDump = (args == null)
            ? "[]"
            : "[" + string.Join(", ", args) + "]";
        Log("=== MastersFM_Tray host starting (v2.0.0) ===");
        Log("exe   = " + Assembly.GetExecutingAssembly().Location);
        Log("dir   = " + dir);
        Log("tray  = " + tray);
        Log("args  = " + argDump);

        if (!File.Exists(tray))
        {
            Log("FATAL: tray.ps1 not found - exiting");
            return 2;
        }

        // Build a dot-source command that forwards our host args verbatim.
        // Dot-sourcing (.  'path.ps1') runs the script in the CALLER's scope,
        // and because we're using AddScript(_, useLocalScope:false), the
        // caller's scope IS the runspace's global scope.  That gives
        // .GetNewClosure() handlers permanent access to every function and
        // variable tray.ps1 defines.
        string trayEscaped = tray.Replace("'", "''");
        var sb = new System.Text.StringBuilder();
        sb.Append(". '");
        sb.Append(trayEscaped);
        sb.Append("'");
        if (args != null)
        {
            foreach (var a in args)
            {
                sb.Append(' ');
                // Preserve already-quoted arguments; wrap unquoted ones in
                // single quotes to survive PowerShell parsing. Switches like
                // -skipServerLaunch pass through unquoted.
                if (a.StartsWith("-") && !a.Contains(" "))
                {
                    sb.Append(a);
                }
                else
                {
                    sb.Append('\'');
                    sb.Append(a.Replace("'", "''"));
                    sb.Append('\'');
                }
            }
        }
        string invocation = sb.ToString();
        Log("invocation = " + invocation);

        try
        {
            // CreateDefault loads the full set of built-in cmdlets — we need
            // Add-Type, Get-Content, Invoke-WebRequest, etc.  CreateDefault2
            // would be lighter but strip too much.
            var iss = InitialSessionState.CreateDefault();
            iss.ApartmentState = ApartmentState.STA;
            iss.ThreadOptions  = PSThreadOptions.UseCurrentThread;
            // Bypass execution policy inside the runspace (no effect on the
            // rest of the machine). tray.ps1 itself isn't reading from disk
            // via Invoke-Command so this is belt-and-braces.
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

            using (var rs = RunspaceFactory.CreateRunspace(iss))
            {
                rs.Open();
                Log("runspace opened");

                // Make this the default runspace on the current thread so
                // any scriptblock invoked later (e.g. WinForms Add_Click
                // events that PowerShell fires back through its engine)
                // can find the runspace implicitly.
                Runspace.DefaultRunspace = rs;

                using (var ps = PowerShell.Create())
                {
                    ps.Runspace = rs;

                    // useLocalScope: false — run in GLOBAL scope.  This is
                    // the core v2.0.0 fix.
                    ps.AddScript(invocation, false);

                    Log("invoking tray script (global scope)...");
                    var results = ps.Invoke();
                    Log("tray script returned - results="
                        + (results == null ? 0 : results.Count));

                    if (ps.Streams.Error != null && ps.Streams.Error.Count > 0)
                    {
                        foreach (var err in ps.Streams.Error)
                        {
                            Log("PS ERROR: " + err.ToString());
                            if (err.Exception != null)
                                Log("  stack: " + err.Exception.ToString());
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log("FATAL: host exception - " + ex.ToString());
            return 1;
        }

        Log("=== host exited cleanly ===");
        return 0;
    }
}
