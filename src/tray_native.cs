// tray_native.cs — Pre-compiled native/P-Invoke types for tray.ps1
//
// Replaces 5 inline Add-Type blocks that previously ran csc.exe at every
// app launch (cost: 10-25 s per startup). Build step in _full_rebuild.ps1
// compiles this to tray_native.dll once; tray.ps1 loads the DLL in ~50 ms.
//
// Types included:
//   MFM_Shell          — shell32: SetCurrentProcessExplicitAppUserModelID
//   MFM_MenuNative     — dwmapi/gdi32/user32: rounded corners, foreground focus
//   NativeMethods.GuiRes   — user32: GetGuiResources (GDI/User handle monitoring)
//   MasterFM.Win32Windows  — EnumWindows helpers (WMP window enumeration)
//   MasterFM.AudioPeak     — Core Audio peak-value detection (soundcloud-rpc pause)

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

// ── MFM_Shell ────────────────────────────────────────────────────────────────
// Sets the AppUserModelID so Windows groups the tray under the correct taskbar
// entry and Task Manager row.
public static class MFM_Shell {
    [DllImport("shell32.dll", PreserveSig = false)]
    public static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string AppID);
}

// ── MFM_MenuNative ───────────────────────────────────────────────────────────
// Rounded corners + foreground focus for the custom tray menu form.
public static class MFM_MenuNative {
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);
    [DllImport("user32.dll")]
    public static extern bool SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);
}

// ── NativeMethods.GuiRes ─────────────────────────────────────────────────────
// GDI/User object count monitoring (slow-leak canary).
namespace NativeMethods {
    public static class GuiRes {
        [DllImport("user32.dll")]
        public static extern int GetGuiResources(IntPtr hProcess, uint uiFlags);
    }
}

// ── MasterFM.Win32Windows + MasterFM.AudioPeak ──────────────────────────────
namespace MasterFM {

    // EnumWindows helpers — finds every top-level HWND for wmplayer.exe and
    // for the soundcloud-rpc pause-override scanner.
    public static class Win32Windows {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern int  GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        public static List<IntPtr> GetProcessWindows(uint pid) {
            var list = new List<IntPtr>();
            EnumWindows((h, l) => {
                uint wpid; GetWindowThreadProcessId(h, out wpid);
                if (wpid == pid) list.Add(h);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static List<string> GetAllVisibleTitles() {
            var list = new List<string>();
            EnumWindows((h, l) => {
                if (!IsWindowVisible(h)) return true;
                var sb = new StringBuilder(512);
                GetWindowTextW(h, sb, 512);
                var t = sb.ToString();
                if (!string.IsNullOrEmpty(t)) list.Add(t);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static string GetTitle(IntPtr h) {
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, 512);
            return sb.ToString();
        }

        public static string GetClass(IntPtr h) {
            var sb = new StringBuilder(256);
            GetClassNameW(h, sb, 256);
            return sb.ToString();
        }
    }

    // Core Audio peak-value detection — used to detect soundcloud-rpc play/pause
    // state since it never updates SMTC PlaybackStatus.
    public static class AudioPeak {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        public class MMDeviceEnumeratorComObject { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint dwStateMask, out IMMDeviceCollection ppDevices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
        }

        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceCollection {
            [PreserveSig] int GetCount(out uint pcDevices);
            [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice {
            [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                                       [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionManager2 {
            int NotImpl_GetAudioSessionControl();
            int NotImpl_GetSimpleAudioVolume();
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
        }

        [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionEnumerator {
            [PreserveSig] int GetCount(out int SessionCount);
            [PreserveSig] int GetSession(int SessionCount, out IAudioSessionControl Session);
        }

        [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionControl {
            int NotImpl_GetState();
            int NotImpl_GetDisplayName();
            int NotImpl_SetDisplayName();
            int NotImpl_GetIconPath();
            int NotImpl_SetIconPath();
            int NotImpl_GetGroupingParam();
            int NotImpl_SetGroupingParam();
            int NotImpl_RegisterAudioSessionNotification();
            int NotImpl_UnregisterAudioSessionNotification();
        }

        [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionControl2 {
            int NotImpl_0(); int NotImpl_1(); int NotImpl_2();
            int NotImpl_3(); int NotImpl_4(); int NotImpl_5();
            int NotImpl_6(); int NotImpl_7(); int NotImpl_8();
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig] int GetProcessId(out uint pRetVal);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference(bool optOut);
        }

        [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioMeterInformation {
            [PreserveSig] int GetPeakValue(out float pfPeak);
        }

        public static float GetPeakForProcessName(string nameContains) {
            try {
                var enumer = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
                IMMDeviceCollection col;
                if (enumer.EnumAudioEndpoints(0, 1, out col) != 0 || col == null) return -1f;
                uint ndev; col.GetCount(out ndev);
                Guid iidMgr = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
                float best = -1f;
                for (uint d = 0; d < ndev; d++) {
                    IMMDevice dev;
                    if (col.Item(d, out dev) != 0 || dev == null) continue;
                    object mgrObj;
                    if (dev.Activate(ref iidMgr, 1, IntPtr.Zero, out mgrObj) != 0 || mgrObj == null) continue;
                    var mgr = (IAudioSessionManager2)mgrObj;
                    IAudioSessionEnumerator sessions;
                    if (mgr.GetSessionEnumerator(out sessions) != 0 || sessions == null) continue;
                    int count; sessions.GetCount(out count);
                    for (int i = 0; i < count; i++) {
                        IAudioSessionControl ctl;
                        if (sessions.GetSession(i, out ctl) != 0 || ctl == null) continue;
                        var ctl2 = ctl as IAudioSessionControl2;
                        if (ctl2 == null) continue;
                        uint sessPid;
                        if (ctl2.GetProcessId(out sessPid) != 0) continue;
                        if (sessPid == 0) continue;
                        string pname = "";
                        try {
                            var p = System.Diagnostics.Process.GetProcessById((int)sessPid);
                            pname = p.ProcessName;
                        } catch { continue; }
                        if (pname.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var meter = ctl as IAudioMeterInformation;
                        if (meter == null) continue;
                        float peak;
                        if (meter.GetPeakValue(out peak) != 0) continue;
                        if (peak > best) best = peak;
                    }
                }
                return best;
            } catch { return -1f; }
        }
    }
}
