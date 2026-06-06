// launcher.cs — Master's FM process host
// Spawns server.exe and tray.ps1 as direct children so all three PIDs appear
// grouped under "Master's FM" in Task Manager.  A Windows Job Object with
// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE ensures server.exe is killed if
// MastersFM.exe exits for any reason (crash, Task Manager kill, etc.).
// tray.ps1 receives -skipServerLaunch so it does NOT spawn a second server.
//
// Root-cause note: SMTC slowness (10s per RequestAsync call) is caused by a
// broken Windows Store SoundCloud SMTC session returning SERVERCALL_RETRYLATER.
// It is NOT caused by this launcher architecture.  tray.ps1 uses Get-SMTCManager
// (tick-level cache + 1.5s timeout) to handle that independently.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;   // v5: hidden HWND so Task Manager treats this
                              // process as the "app root" and groups server.exe,
                              // MastersFM_Tray.exe, audio_spectrum.exe underneath
                              // it like Discord does.

[assembly: System.Reflection.AssemblyTitle("Master's FM")]
[assembly: System.Reflection.AssemblyDescription("Now Playing Overlay for OBS")]
[assembly: System.Reflection.AssemblyProduct("Master's FM")]
[assembly: System.Reflection.AssemblyCompany("MasterShadex")]
[assembly: System.Reflection.AssemblyVersion("5.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("5.0.0.0")]

class MastersFM
{
    // ── diagnostic log (Part 4 instrumentation) ──────────────────────
    // Until this pass, MastersFM.exe ran completely silently — if server.exe
    // or audio_spectrum.exe failed to spawn, the only evidence was "the
    // process doesn't exist" which is hard to diagnose from logs. Now every
    // significant action writes to %LOCALAPPDATA%\MastersFM\launcher.log.
    // Truncated each boot so disk usage stays bounded.
    static readonly string s_logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MastersFM", "launcher.log");
    static void InitLog()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(s_logPath));
            File.WriteAllText(s_logPath,
                string.Format("=== MastersFM launcher boot {0:yyyy-MM-dd HH:mm:ss} ==={1}",
                    DateTime.Now, Environment.NewLine));
        }
        catch { }   // if we can't log we can't do much; app still runs
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

    // ── Job Object P/Invoke ──────────────────────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr hJob,
        int JobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        int cbJobObjectInfoLength);

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE   = 0x00002000;
    const uint JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x00001000;

    // AUMID: every Master's FM process sets this so the Windows shell sees
    // them as one logical app (taskbar grouping, toasts, Jump Lists, etc.).
    [DllImport("shell32.dll", PreserveSig = false)]
    static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string AppID);

    // SHChangeNotify broadcast — forces Explorer / Task Manager to drop any
    // cached file icons they're holding.  Called once on launch after the
    // MSI install so the shell notices server.exe's new purple icon right
    // away instead of serving pkg's stale green Node icon from cache.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    const uint SHCNE_ASSOCCHANGED = 0x08000000;
    const uint SHCNF_IDLIST       = 0x0000;

    // SHGetPropertyStoreForWindow — lets us attach the AUMID to our hidden
    // window's property store so the Shell's grouping engine sees it as
    // part of the Master's FM app.  Without this, Explicit Process AUMID
    // alone was not enough for Task Manager's app-tree view.
    [DllImport("shell32.dll", PreserveSig = false)]
    static extern void SHGetPropertyStoreForWindow(
        IntPtr hwnd,
        [In] ref Guid iid,
        [Out, MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr p;
        public int p2;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        void Commit();
    }

    // PROPVARIANT string (VT_LPWSTR) helper. Caller is responsible for
    // freeing the CoTaskMem pointer after the SetValue call.
    static PROPVARIANT MakeStringVariant(string s)
    {
        PROPVARIANT v = new PROPVARIANT();
        v.vt = 31; // VT_LPWSTR
        v.p  = Marshal.StringToCoTaskMemUni(s);
        return v;
    }

    // PKEY_AppUserModel_ID — the property the Shell grouping engine reads
    // to decide which app a window belongs to.  Set this on our hidden
    // window and the Shell correctly groups all child processes (tray,
    // server, audio_spectrum) under a single "Master's FM" app row like
    // Discord has.
    static readonly PROPERTYKEY PKEY_AppUserModel_ID = new PROPERTYKEY {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid   = 5
    };
    static readonly Guid IID_IPropertyStore = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    static void SetWindowAumid(IntPtr hwnd, string aumid)
    {
        try
        {
            IPropertyStore ps;
            // CS0199 fix: can't pass `static readonly` field by `ref`
            // (C# only allows that inside a static constructor).
            // Copy to a local Guid first and pass the local. SHGetPropertyStoreForWindow
            // treats the IID as input-only, so round-tripping through a
            // local is semantically identical.
            Guid iid = IID_IPropertyStore;
            SHGetPropertyStoreForWindow(hwnd, ref iid, out ps);
            if (ps == null) return;
            PROPERTYKEY k = PKEY_AppUserModel_ID;
            PROPVARIANT v = MakeStringVariant(aumid);
            try
            {
                ps.SetValue(ref k, ref v);
                ps.Commit();
            }
            finally
            {
                if (v.p != IntPtr.Zero) Marshal.FreeCoTaskMem(v.p);
                Marshal.ReleaseComObject(ps);
            }
        }
        catch (Exception ex) { Log("SetWindowAumid best-effort path failed: " + ex.Message); /* grouping degrades to flat but nothing else breaks */ }
    }

    // ── Port-clearing (mirrors tray.ps1 lines 5075-5086) ────────────────────
    // Kills the first process found listening on port 4242, then waits 400 ms
    // for the port to release.  Mirrors tray.ps1 exactly:
    //   netstat -ano | Select-String ":4242 " | split[-1] | Select -First 1
    //   Stop-Process -Id $existing -Force; Start-Sleep -Milliseconds 400
    // Called inside the $skipServerLaunch path (launcher path) before server.exe
    // spawn.  Without this, Kestrel silently fails to bind if the legacy Node
    // server.exe is still in LISTENING/TIME_WAIT on 4242 (Hypothesis C, 4.1).
    static void ClearPort4242()
    {
        try
        {
            var psi = new ProcessStartInfo("netstat.exe", "-ano")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            string output;
            using (var netstat = Process.Start(psi))
            {
                if (netstat == null) return;
                output = netstat.StandardOutput.ReadToEnd();
                netstat.WaitForExit();
            }
            foreach (var line in output.Split('\n'))
            {
                // Mirror tray.ps1: Select-String ":4242 " (note trailing space)
                if (!line.Contains(":4242 ")) continue;

                // Split on whitespace, take last element = PID (mirrors ($_ -split "\s+")[-1])
                var parts = line.Trim().Split(new char[]{' '}, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                if (!int.TryParse(parts[parts.Length - 1], out int pid) || pid <= 4) continue;

                // Kill only the first match (mirrors Select-Object -First 1)
                try
                {
                    Process.GetProcessById(pid).Kill();
                    Log("ClearPort4242: killed PID=" + pid + " on :4242 (mirrors tray.ps1:5083)");
                    Thread.Sleep(400);   // mirrors Start-Sleep -Milliseconds 400
                }
                catch (Exception ex) { Log("ClearPort4242: Kill PID=" + pid + " failed: " + ex.Message); }
                break;
            }
        }
        catch (Exception ex) { Log("ClearPort4242: netstat check failed: " + ex.Message); }
    }

    // ── Entry point ──────────────────────────────────────────────────────────
    [STAThread]
    static void Main()
    {
        InitLog();
        Log("=== launcher Main() entered ===");
        try { SetCurrentProcessExplicitAppUserModelID("MastersFM.App"); Log("AUMID set OK"); }
        catch (Exception ex) { Log("SetCurrentProcessExplicitAppUserModelID failed: " + ex.Message); }
        // v5: shake the Shell's icon cache so server.exe's purple icon shows
        // up in Task Manager / Explorer immediately instead of pkg's stale
        // green Node icon that Windows has been caching since the first
        // install. Best-effort — a no-op if Explorer isn't listening.
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception ex) { Log("SHChangeNotify failed: " + ex.Message); }

        bool owned;
        var mutex = new Mutex(true, @"Global\MastersFM_Launcher", out owned);
        if (!owned)
        {
            Log("mutex Global\\MastersFM_Launcher already owned - another launcher is running. Exiting.");
            mutex.Close();
            return;
        }

        string dir    = AppDomain.CurrentDomain.BaseDirectory;
        string tray   = Path.Combine(dir, "tray.ps1");
        string server = Path.Combine(dir, "server.exe");
        Log("dir    = " + dir);
        Log("tray   = " + tray);
        Log("server = " + server);

        if (!File.Exists(tray))
        {
            Log("FATAL: tray.ps1 not found at " + tray + " - aborting");
            mutex.ReleaseMutex();
            mutex.Close();
            return;
        }

        // Strip trailing separator to avoid \" quoting bug.
        // AppDomain.BaseDirectory returns "C:\path\" — the trailing backslash
        // inside the argument string becomes '\"' which escapes the closing
        // quote, absorbing any subsequent switch into the -scriptDir value.
        string dirQ = dir.TrimEnd(Path.DirectorySeparatorChar,
                                  Path.AltDirectorySeparatorChar);

        // ── Create Job Object so server.exe dies with us ─────────────────────
        IntPtr hJob = IntPtr.Zero;
        try
        {
            hJob = CreateJobObject(IntPtr.Zero, null);
            if (hJob != IntPtr.Zero)
            {
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK;
                SetInformationJobObject(hJob, JobObjectExtendedLimitInformation,
                    ref info, Marshal.SizeOf(info));
                Log("Job Object created + configured (KILL_ON_JOB_CLOSE | SILENT_BREAKAWAY_OK)");
            }
            else
            {
                Log("CreateJobObject returned IntPtr.Zero - child procs won't die with us");
            }
        }
        catch (Exception ex) { Log("Job Object setup failed: " + ex.Message); hJob = IntPtr.Zero; }

        // ── Spawn server.exe ─────────────────────────────────────────────────
        Process srv = null;
        if (File.Exists(server))
        {
            // Clear port 4242 before spawning (Hypothesis C fix, sub-stage 4.11).
            // Mirrors tray.ps1:5075-5086: kill anything on :4242, wait 400 ms.
            ClearPort4242();

            var srvPsi = new ProcessStartInfo
            {
                FileName        = server,
                WorkingDirectory = dir,
                UseShellExecute  = false,
                CreateNoWindow   = true
            };
            try
            {
                srv = Process.Start(srvPsi);
                if (srv != null)
                {
                    Log("server.exe spawned, PID=" + srv.Id);
                    if (hJob != IntPtr.Zero)
                    {
                        bool ok = AssignProcessToJobObject(hJob, srv.Handle);
                        if (!ok) Log("AssignProcessToJobObject(server) returned false - GetLastError=" + Marshal.GetLastWin32Error());
                    }
                    // v9.6.0 — lower process priority to BelowNormal so background apps
                    // (other browser tabs, OBS preview threads, Discord background, etc.)
                    // win CPU contests. server.exe is async I/O bound (HTTP + SSE) and
                    // tolerates being preempted; lowering priority just means the system
                    // schedules it AFTER Normal-priority background work, not that it
                    // gets less CPU when there's work to do. User explicitly authorized
                    // this in the v9.6 procedure: "If we can set the program to lower
                    // priority that might also work, but that freeze bug has to get out."
                    try { srv.PriorityClass = ProcessPriorityClass.BelowNormal; Log("server.exe priority=BelowNormal (v9.6.0)"); }
                    catch (Exception exP) { Log("server.exe priority set failed: " + exP.Message); }
                }
                else
                {
                    Log("Process.Start(server.exe) returned null");
                }
            }
            catch (Exception ex) { Log("Process.Start(server.exe) threw: " + ex.Message); srv = null; }
        }
        else
        {
            Log("server.exe not found at " + server + " - skipping spawn");
        }

        // ── Spawn audio_spectrum.exe (v2.1.0 WASAPI loopback provider) ───────
        // Runs a WASAPI loopback capture on the default render endpoint, does
        // FFT, and serves 60 log-spaced frequency bands over SSE on
        // http://127.0.0.1:4243/spectrum.  The overlay consumes that stream
        // to drive the spectrum visualizer with REAL system audio (Spotify,
        // SoundCloud, YouTube — anything going through the user's speakers)
        // instead of depending on OBS routing an audio-input-capture source
        // into the browser. Optional — tray menu toggle can kill it; overlay
        // falls back to the simulator animation when the stream is offline.
        Process audio = null;
        string audioExe = Path.Combine(dir, "audio_spectrum.exe");
        if (File.Exists(audioExe))
        {
            var audioPsi = new ProcessStartInfo
            {
                FileName         = audioExe,
                WorkingDirectory = dir,
                UseShellExecute  = false,
                CreateNoWindow   = true
            };
            try
            {
                audio = Process.Start(audioPsi);
                if (audio != null)
                {
                    Log("audio_spectrum.exe spawned, PID=" + audio.Id);
                    if (hJob != IntPtr.Zero)
                    {
                        bool ok = AssignProcessToJobObject(hJob, audio.Handle);
                        if (!ok) Log("AssignProcessToJobObject(audio) returned false - GetLastError=" + Marshal.GetLastWin32Error());
                    }
                    // v9.8.0 — raise audio_spectrum.exe to AboveNormal. At 140 fps the
                    // SSE serve loop does ~140 context-switches/sec; during heavy gaming
                    // (where games elevate via MMCSS to High/Realtime) Normal-priority
                    // threads can miss their WaitOne wake-up by 5-10 ms extra, producing
                    // bursty SSE delivery and visible spectrum lag. AboveNormal lets the
                    // SSE thread win CPU contests vs Normal-priority game threads.
                    // timeBeginPeriod(1) (added v7.0.0) already ensures 1 ms WaitOne
                    // resolution; process priority ensures the thread actually gets a
                    // core when it wakes. The v9.1.0 capture-thread ThreadPriority.AboveNormal
                    // boost (set inside audio_spectrum.cs) is preserved — effective level
                    // rises from 9 → 11 ("Highest normal"), still below MMCSS Realtime.
                    // WASAPI hard-real-time deadlines are unaffected: AboveNormal ≠ Realtime
                    // and WASAPI itself runs its internal pump at THREAD_PRIORITY_TIME_CRITICAL (15).
                    try { audio.PriorityClass = ProcessPriorityClass.AboveNormal; Log("audio_spectrum.exe priority=AboveNormal (v9.8.0 — spectrum lag fix)"); }
                    catch (Exception exP) { Log("audio_spectrum.exe priority set failed: " + exP.Message); }
                }
                else
                {
                    Log("Process.Start(audio_spectrum.exe) returned null");
                }
            }
            catch (Exception ex) { Log("Process.Start(audio_spectrum.exe) threw: " + ex.Message); audio = null; }
        }
        else
        {
            Log("audio_spectrum.exe not found at " + audioExe + " - visualizer will fall back to simulator");
        }

        // ── Spawn tray.ps1 via MastersFM_Tray.exe (v2.0.0 in-process host) ─
        // MastersFM_Tray.exe compiles tray_launcher.cs — a C# exe that hosts
        // PowerShell in-process via System.Management.Automation. Advantages:
        //   (a) Task Manager shows ONE "Master's FM" app row — no separate
        //       "Windows PowerShell" entry.
        //   (b) ProductName in VersionInfo = "Master's FM" so the shell
        //       groups it with MastersFM.exe + server.exe.
        //
        // The v1.9.9 version of this same host broke because it used
        // AddCommand(tray.ps1) which creates a child pipeline scope — menu
        // click handlers lost access to script functions once the pipeline
        // marked the top-level script "complete". v2.0.0's tray_launcher.cs
        // uses AddScript(..., useLocalScope:false) instead, running the
        // script in the runspace's GLOBAL scope so closures stay valid.
        //
        // Fallback: if MastersFM_Tray.exe is missing (e.g. SMA DLL not
        // available at build), spawn powershell.exe directly. This lets the
        // launcher run on stripped-down Windows SKUs.
        string hostedTray = Path.Combine(dir, "MastersFM_Tray.exe");
        ProcessStartInfo psPsi;
        if (File.Exists(hostedTray))
        {
            psPsi = new ProcessStartInfo
            {
                FileName  = hostedTray,
                Arguments = string.Format(
                    "-scriptDir \"{0}\" -skipServerLaunch",
                    dirQ),
                WorkingDirectory = dir,
                UseShellExecute  = false,
                CreateNoWindow   = true
            };
        }
        else
        {
            psPsi = new ProcessStartInfo
            {
                FileName  = "powershell.exe",
                Arguments = string.Format(
                    "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -NonInteractive " +
                    "-File \"{0}\" -scriptDir \"{1}\" -skipServerLaunch",
                    tray, dirQ),
                UseShellExecute = false,
                CreateNoWindow  = true
            };
        }

        Process ps = null;
        try
        {
            ps = Process.Start(psPsi);
            if (ps != null)
            {
                Log("tray host (" + Path.GetFileName(psPsi.FileName) + ") spawned, PID=" + ps.Id);
                // Add tray to the job so killing MastersFM.exe kills powershell too.
                if (hJob != IntPtr.Zero)
                {
                    bool ok = AssignProcessToJobObject(hJob, ps.Handle);
                    if (!ok) Log("AssignProcessToJobObject(tray) returned false - GetLastError=" + Marshal.GetLastWin32Error());
                }
                // v9.6.0 — lower tray host process priority to BelowNormal so background
                // apps (other browser tabs, OBS preview threads, etc.) win CPU contests.
                // The tray host runs the PowerShell scrobble timer + balloon UI + menu
                // refresh loops — none of which are real-time. v9.5.0 already drove the
                // per-tick cost down to ~2 ms; lowering priority just means when CPU is
                // contested the tray yields to whatever app the user is actively using.
                try { ps.PriorityClass = ProcessPriorityClass.BelowNormal; Log("tray host priority=BelowNormal (v9.6.0)"); }
                catch (Exception exP) { Log("tray host priority set failed: " + exP.Message); }
            }
            else
            {
                Log("Process.Start(tray host) returned null");
            }
        }
        catch (Exception ex) { Log("Process.Start(tray host) threw: " + ex.Message); }

        // ── v5: hidden-but-registered-with-Shell window.
        // The window is 0×0, fully transparent, positioned off-screen so it
        // never reaches the user's eyes, not in Alt+Tab, not in the taskbar.
        // But it IS visible-to-the-Shell (no Hide() call) and its HWND's
        // PropertyStore has PKEY_AppUserModel_ID set to "MastersFM.App" —
        // the same AUMID the children declare. That combination is what
        // makes Task Manager see this process as the "app root" of Master's
        // FM and nest server.exe / MastersFM_Tray.exe / audio_spectrum.exe
        // underneath it in an expandable tree like Discord's.
        //
        // Background thread watches the tray process; when it exits, we
        // post a Close to the hidden form via BeginInvoke and Application.Run
        // unwinds cleanly.
        var hiddenForm                = new Form();
        hiddenForm.Text               = "Master's FM";
        hiddenForm.FormBorderStyle    = FormBorderStyle.None;
        hiddenForm.ShowInTaskbar      = false;
        hiddenForm.Opacity            = 0;
        hiddenForm.Size               = new System.Drawing.Size(1, 1);
        hiddenForm.StartPosition      = FormStartPosition.Manual;
        hiddenForm.Location           = new System.Drawing.Point(-32000, -32000);
        // Load own icon from the exe's resource so the Shell sees a proper
        // app icon attached to the window (important for app-grouping visual
        // output in Task Manager).
        // v14: Environment.ProcessPath returns the real apphost .exe path under BOTH
        // single-file and framework-dependent publish (Assembly.GetEntryAssembly().Location
        // is EMPTY under single-file, which would make ExtractAssociatedIcon throw).
        try { hiddenForm.Icon = System.Drawing.Icon.ExtractAssociatedIcon(
            Environment.ProcessPath); } catch { }
        // Force the HWND to exist NOW so we can attach AUMID before
        // Application.Run even starts.
        IntPtr hwnd = hiddenForm.Handle;
        SetWindowAumid(hwnd, "MastersFM.App");
        Log("hidden HWND created + AUMID attached, entering Application.Run");
        // NOTE: no Hide() in Load — the form MUST stay "visible" (to the
        // Shell's grouping engine) for app-grouping to engage. Because
        // Opacity=0 + off-screen + ShowInTaskbar=false, the user never
        // sees the window anywhere.

        var trayWatcher = new Thread(() => {
            try
            {
                if (ps != null) ps.WaitForExit();
            }
            catch { }
            // Marshal the Close back onto the UI thread — Application.Run
            // unwinds from here.
            try { hiddenForm.BeginInvoke(new Action(() => {
                try { hiddenForm.Close(); } catch { }
            })); } catch { }
        });
        trayWatcher.IsBackground = true;
        trayWatcher.Start();

        Application.Run(hiddenForm);
        Log("Application.Run returned - beginning teardown");

        // Kill server + audio if still running (job object also handles this).
        if (srv   != null) { try { srv.Kill();   Log("killed server.exe"); }   catch (Exception ex) { Log("srv.Kill threw: " + ex.Message); } }
        if (audio != null) { try { audio.Kill(); Log("killed audio_spectrum.exe"); } catch (Exception ex) { Log("audio.Kill threw: " + ex.Message); } }

        mutex.ReleaseMutex();
        mutex.Close();
        Log("=== launcher exiting cleanly ===");
    }
}
