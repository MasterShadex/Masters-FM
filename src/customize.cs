// customize.cs — Master's FM Customize Overlay window
// Standalone WinForms app that embeds Microsoft Edge WebView2 to render
// customize.html as a native window — no browser chrome, no extra tabs.
// Launched from tray.ps1 "Customize Overlay..." menu item.
//
// Dependencies (ship alongside this exe):
//   Microsoft.Web.WebView2.Core.dll       (managed, net45)
//   Microsoft.Web.WebView2.WinForms.dll   (managed, net45)
//   WebView2Loader.dll                    (native, win-x64)
// Runtime: Microsoft Edge WebView2 Runtime (preinstalled on Windows 11,
// auto-installed via Windows Update on recent Windows 10).
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: System.Reflection.AssemblyTitle("Master's FM Customize")]
[assembly: System.Reflection.AssemblyProduct("Master's FM")]
[assembly: System.Reflection.AssemblyCompany("MasterShadex")]
[assembly: System.Reflection.AssemblyDescription("Master's FM customize window")]
[assembly: System.Reflection.AssemblyVersion("5.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("5.0.0.0")]

namespace MastersFMCustomize
{
    static class Program
    {
        internal static string LogDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MastersFM");
        }

        internal static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(LogDir());
                File.AppendAllText(
                    Path.Combine(LogDir(), "customize.log"),
                    string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}{2}",
                        DateTime.Now, msg, Environment.NewLine));
            }
            catch { }
        }

        // AUMID — same as MastersFM.exe + MastersFM_Tray.exe so all Master's
        // FM processes share one Shell identity. Task Manager groups them.
        [DllImport("shell32.dll", PreserveSig = false)]
        static extern void SetCurrentProcessExplicitAppUserModelID(
            [MarshalAs(UnmanagedType.LPWStr)] string AppID);

        [STAThread]
        static void Main(string[] args)
        {
            try { SetCurrentProcessExplicitAppUserModelID("MastersFM.App"); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Log("=== Customize launcher starting ===");
            // Unhandled exception nets so the user sees a clear error instead of a
            // silent crash disappearing into the Windows event log.
            Application.ThreadException += (s, e) =>
            {
                Log("UI thread exception: " + e.Exception);
                MessageBox.Show("Unexpected error:\n\n" + e.Exception.Message,
                    "Master's FM — Customize", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log("Unhandled exception: " + e.ExceptionObject);
            };

            try
            {
                Application.Run(new CustomizeForm());
                Log("=== Customize launcher exited cleanly ===");
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex);
                MessageBox.Show("Failed to open Customize Overlay:\n\n" + ex.Message,
                    "Master's FM", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    public class CustomizeForm : Form
    {
        private WebView2 webView;
        // DWM window-attribute: DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        public CustomizeForm()
        {
            SuspendLayout();
            Text = "Customize Overlay  —  Master's FM";
            Width = 1180;
            Height = 820;
            MinimumSize = new Size(720, 560);
            BackColor = Color.FromArgb(13, 9, 28);  // #0D091C — matches tray menu bg
            ForeColor = Color.FromArgb(238, 228, 255);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            // Pick the Master's FM icon from install dir if present
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath) ?? "";
            string iconPath = Path.Combine(exeDir, "MastersFM.ico");
            if (File.Exists(iconPath))
            {
                try { Icon = new Icon(iconPath); }
                catch (Exception iconEx) { Program.Log("failed to load icon from " + iconPath + ": " + iconEx.Message); }
            }

            webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(13, 9, 28)
            };
            Controls.Add(webView);

            Load += (s, e) => { InitWebViewAsync(); };
            ResumeLayout();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Rounded window corners on Windows 11 — no-op on Win10 (returns non-zero)
            try
            {
                int round = 2;
                DwmSetWindowAttribute(Handle, 33, ref round, 4);
            }
            catch (Exception dwmEx) { Program.Log("DwmSetWindowAttribute(round-corners) failed: " + dwmEx.Message); }
        }

        // Fire-and-forget async starter. Errors surface via MessageBox in the
        // async method itself so we don't silently hang on WebView2 init failures.
        private async void InitWebViewAsync()
        {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MastersFM", "WebView2Data");
            try { Directory.CreateDirectory(userDataFolder); }
            catch (Exception mkdEx) { Program.Log("WebView2Data mkdir failed: " + mkdEx.Message); }

            Program.Log("InitWebView: userData=" + userDataFolder);
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(env);

                // Strip app-inappropriate browser behaviors. Keep DevTools for debug.
                var s = webView.CoreWebView2.Settings;
                s.AreBrowserAcceleratorKeysEnabled = false;  // no Ctrl+T / Ctrl+N
                s.IsStatusBarEnabled               = false;
                s.IsZoomControlEnabled             = true;
                s.AreDefaultContextMenusEnabled    = true;
                s.AreDevToolsEnabled               = true;

                // Block navigation to external sites — this is an app window, not
                // a browser. Only localhost:4242 is allowed. Reject everything else.
                webView.CoreWebView2.NavigationStarting += (sender, ev) =>
                {
                    try
                    {
                        var uri = new Uri(ev.Uri);
                        bool ok = uri.Host == "localhost" || uri.Host == "127.0.0.1";
                        if (!ok && uri.Scheme != "data") ev.Cancel = true;
                    }
                    catch { }
                };
                // Open external links in the default browser instead of this window.
                webView.CoreWebView2.NewWindowRequested += (sender, ev) =>
                {
                    ev.Handled = true;
                    try { System.Diagnostics.Process.Start(ev.Uri); }
                    catch (Exception procEx) { Program.Log("NewWindowRequested Process.Start failed for '" + ev.Uri + "': " + procEx.Message); }
                };

                webView.CoreWebView2.Navigate("http://localhost:4242/customize");
                Program.Log("InitWebView: Navigate dispatched");
            }
            catch (Exception ex)
            {
                Program.Log("InitWebView FAILED: " + ex);
                MessageBox.Show(
                    "WebView2 initialization failed:\n\n" + ex.Message +
                    "\n\nMake sure Microsoft Edge WebView2 Runtime is installed.\n" +
                    "Download: https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                    "Master's FM — Customize",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }
    }
}
