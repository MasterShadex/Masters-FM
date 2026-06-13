using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MastersFM.Tray.Services;

/// <summary>
/// Forces a WPF window onto the PRIMARY monitor (centred) and marks it always-on-top.
///
/// Multi-monitor + mixed-DPI safe: the old approach set Window.Left/Top from
/// SystemParameters.WorkArea (DIPs) BEFORE the window was shown -- with no real HWND
/// or measured size, and DIP math that breaks across monitors at different scales, a
/// window could land off every screen on a friend's 4-5 display rig. This positions
/// via Win32 in PHYSICAL pixels AFTER the HWND + final size exist, so it always lands
/// centred on the primary monitor regardless of layout or per-monitor DPI.
///
/// Topmost keeps every Master's FM window above other apps (Task-Manager style). The
/// user can still minimise -- Topmost does not block minimise, and a restored window
/// simply returns to the top.
/// </summary>
internal static class WindowPlacement
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    /// <summary>
    /// Mark the window always-on-top and centre it on the primary monitor. Call once,
    /// before Show()/ShowDialog(); the centring runs at Loaded and ContentRendered so it
    /// works for fixed-size AND SizeToContent windows.
    /// </summary>
    public static void ApplyPrimaryAndTopmost(Window w)
    {
        if (w == null) return;
        w.Topmost = true; // above other apps; minimise still works
        // Manual + a safe on-primary seed position so WPF's own CenterScreen never gets
        // a chance to guess onto an off-screen monitor before our authoritative centring.
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        try { var wa = SystemParameters.WorkArea; w.Left = wa.Left + 60; w.Top = wa.Top + 60; } catch { /* best effort */ }
        w.Loaded += (_, _) => CenterOnPrimary(w);
        w.ContentRendered += (_, _) => CenterOnPrimary(w);
    }

    /// <summary>Centre the window on the primary monitor's work area, in physical pixels.</summary>
    public static void CenterOnPrimary(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            var hMon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMon, ref mi)) return;
            if (!GetWindowRect(hwnd, out var wr)) return;
            int winW = wr.Right - wr.Left;
            int winH = wr.Bottom - wr.Top;
            int workW = mi.rcWork.Right - mi.rcWork.Left;
            int workH = mi.rcWork.Bottom - mi.rcWork.Top;
            int x = mi.rcWork.Left + Math.Max(0, (workW - winW) / 2);
            int y = mi.rcWork.Top + Math.Max(0, (workH - winH) / 2);
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        catch { /* never crash a dialog over placement */ }
    }
}
