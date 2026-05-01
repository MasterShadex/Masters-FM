// install_bootstrapper.cs - Master's FM one-click friend installer
//
// Compiles to "Install Master's FM.exe" — a single signed bootstrapper
// that embeds the real MSI and the MasterShadex code-signing certificate
// as resources. Friends double-click this ONE file, approve the UAC
// elevation prompt, and it:
//   1. Extracts the embedded MSI + .cer to %TEMP%
//   2. Imports the MasterShadex cert into CurrentUser\TrustedPublisher,
//      CurrentUser\Root, LocalMachine\TrustedPublisher, LocalMachine\Root
//      (the last two require elevation, which is why we run elevated)
//   3. Runs msiexec /i /passive on the extracted MSI
//   4. Shows a simple console message with the result
//
// The bootstrapper itself is Authenticode-signed with MasterShadex cert,
// so the UAC prompt shows "MasterShadex" as publisher instead of "Unknown".
// On non-SAC machines that is all that's needed. On SAC-enabled machines
// the friend still needs SAC off (Microsoft policy — only a real CA cert
// can clear SAC, which we don't have).
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

[assembly: AssemblyTitle("Master's FM Installer")]
[assembly: AssemblyDescription("Master's FM one-click installer")]
[assembly: AssemblyProduct("Master's FM")]
[assembly: AssemblyCompany("MasterShadex")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

class Bootstrapper
{
    [DllImport("shell32.dll")]
    static extern IntPtr ShellExecute(IntPtr hwnd, string lpOperation, string lpFile,
        string lpParameters, string lpDirectory, int nShowCmd);

    static bool IsElevated()
    {
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // Self-elevate: re-launch this same exe with "runas" verb so Windows
    // raises the UAC prompt. If accepted, the new process runs elevated
    // and we exit from the unelevated one.
    static void SelfElevate(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName  = Assembly.GetEntryAssembly().Location,
                Arguments = string.Join(" ", args),
                Verb      = "runas",
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("Admin permission is required to install Master's FM.");
            Console.Error.WriteLine("Right-click the installer and choose 'Run as administrator'.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Press any key to close...");
            Console.ReadKey(true);
        }
    }

    // Extract an embedded resource to a temp file path. Resources are
    // named `<default-namespace>.<filename>` by csc /resource:file,name.
    static string ExtractResource(string resourceName, string outFileName)
    {
        string outPath = Path.Combine(Path.GetTempPath(), outFileName);
        using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
        {
            if (src == null)
                throw new Exception("Embedded resource not found: " + resourceName);
            using (var dst = File.Create(outPath))
            {
                src.CopyTo(dst);
            }
        }
        return outPath;
    }

    static void ImportCert(string cerPath)
    {
        var cert = new X509Certificate2(cerPath);
        // v5.2.2 — TrustedPublisher only. Importing into the Root store
        // triggers Windows' "install a certificate from a CA" security
        // warning dialog — a big scary prompt that friends hesitate on.
        // TrustedPublisher is silent and sufficient: msiexec running from
        // an already-elevated context inherits admin and installs the
        // self-signed MSI without further prompts. When we eventually
        // sign with a real CA cert (option D) we won't need this dance
        // at all — the cert will chain to a trusted root automatically.
        StoreLocation[] locations = new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser };
        foreach (var loc in locations)
        {
            try
            {
                using (var store = new X509Store(StoreName.TrustedPublisher, loc))
                {
                    store.Open(OpenFlags.ReadWrite);
                    store.Add(cert);
                    store.Close();
                }
                Console.WriteLine("  Cert -> " + loc + "\\TrustedPublisher");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Skipped " + loc + "\\TrustedPublisher: " + ex.Message);
            }
        }
    }

    // Countdown with early-exit on any key. Prints "Closing in Ns..."
    // and ticks down in-place. Pressing any key skips the remaining wait.
    static void WaitOrKeyPress(int seconds)
    {
        Console.WriteLine("(this window closes in " + seconds + "s, or press any key)");
        int leftTenths = seconds * 10;
        while (leftTenths > 0)
        {
            if (Console.KeyAvailable)
            {
                try { Console.ReadKey(true); } catch { }
                return;
            }
            Thread.Sleep(100);
            leftTenths--;
        }
    }

    static int Main(string[] args)
    {
        Console.Title = "Master's FM - Installer";
        Console.WriteLine();
        Console.WriteLine("====================================");
        Console.WriteLine("  Master's FM - One-Click Installer");
        Console.WriteLine("====================================");
        Console.WriteLine();

        if (!IsElevated())
        {
            Console.WriteLine("Requesting admin permission (UAC prompt)...");
            SelfElevate(args);
            return 0;
        }

        string tempMsi = null;
        string tempCer = null;
        try
        {
            Console.WriteLine("[1/3] Extracting installer files...");
            tempMsi = ExtractResource("payload.msi",    "Masters_FM_Setup.msi");
            tempCer = ExtractResource("publisher.cer",  "MastersFM_publisher.cer");
            Console.WriteLine("      MSI: " + tempMsi);
            Console.WriteLine("      CER: " + tempCer);
            Console.WriteLine();

            Console.WriteLine("[2/3] Installing MasterShadex publisher certificate...");
            ImportCert(tempCer);
            Console.WriteLine();

            Console.WriteLine("[3/3] Running MSI installer...");
            var psi = new ProcessStartInfo
            {
                FileName  = "msiexec.exe",
                Arguments = "/i \"" + tempMsi + "\" /passive /norestart",
                UseShellExecute = false
            };
            var proc = Process.Start(psi);
            proc.WaitForExit();
            int exit = proc.ExitCode;
            Console.WriteLine();
            if (exit == 0)
            {
                Console.WriteLine("====================================");
                Console.WriteLine("  INSTALL COMPLETE");
                Console.WriteLine("====================================");
                Console.WriteLine();
                // Launch Master's FM via explorer.exe — that drops our
                // elevated token so MastersFM.exe runs as the normal user
                // (security best-practice: a tray app shouldn't linger with
                // admin rights just because its installer needed them).
                //  explorer.exe is the standard Windows "launch-as-user"
                // trampoline that signed installers use for exactly this.
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string exePath  = Path.Combine(localApp, "MastersFM", "MastersFM.exe");
                if (File.Exists(exePath))
                {
                    Console.WriteLine("  Starting Master's FM...");
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName  = "explorer.exe",
                            Arguments = "\"" + exePath + "\"",
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex) { Console.WriteLine("  (launch fallback: " + ex.Message + ")"); }
                }
                Console.WriteLine("  Look for the purple music-note in");
                Console.WriteLine("  your system tray.");
                Console.WriteLine();
                WaitOrKeyPress(10);
            }
            else
            {
                Console.WriteLine("====================================");
                Console.WriteLine("  INSTALL FAILED (exit " + exit + ")");
                Console.WriteLine("====================================");
                WaitOrKeyPress(10);
            }
            return exit;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("FATAL: " + ex.Message);
            Console.Error.WriteLine();
            WaitOrKeyPress(10);
            return 1;
        }
        finally
        {
            try { if (tempMsi != null && File.Exists(tempMsi)) File.Delete(tempMsi); } catch { }
            try { if (tempCer != null && File.Exists(tempCer)) File.Delete(tempCer); } catch { }
        }
    }
}
