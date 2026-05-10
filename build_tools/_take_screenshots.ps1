# Stage 7.7B FINAL STEP 6: screenshot capture script
# Run this script AFTER manually opening each dialog to capture AFTER screenshots.
# The script captures the active window via BitBlt and saves to screenshots_after/.

param(
    [string]$Name = "screenshot",
    [string]$OutDir = "G:\Project Folder\Master FM\_BACKUPS_2026-05-10_S7_7B_FINAL_PRE\screenshots_after"
)

Add-Type @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public class ScreenCapture {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, ref RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    public static void CaptureWindow(IntPtr hwnd, string path) {
        var r = new RECT();
        GetWindowRect(hwnd, ref r);
        int w = r.R - r.L, h = r.B - r.T;
        if (w <= 0 || h <= 0) throw new Exception("Invalid window dimensions");
        using (var bmp = new Bitmap(w, h))
        using (var g = Graphics.FromImage(bmp)) {
            g.CopyFromScreen(r.L, r.T, 0, 0, new Size(w, h));
            bmp.Save(path, ImageFormat.Png);
        }
    }

    public static void CaptureScreen(string path) {
        var screen = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        using (var bmp = new Bitmap(screen.Width, screen.Height))
        using (var g = Graphics.FromImage(bmp)) {
            g.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
            bmp.Save(path, ImageFormat.Png);
        }
    }
}
"@

Add-Type -AssemblyName System.Windows.Forms

$outFile = Join-Path $OutDir "$Name.png"
$hwnd = [ScreenCapture]::GetForegroundWindow()
try {
    [ScreenCapture]::CaptureWindow($hwnd, $outFile)
    Write-Host "Captured active window -> $outFile"
} catch {
    [ScreenCapture]::CaptureScreen($outFile)
    Write-Host "Captured full screen -> $outFile"
}
