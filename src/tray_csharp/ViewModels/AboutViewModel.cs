// Stage 7.7: AboutViewModel. Backs the About tab embedded inside
// WelcomeWindow per Q-MOCK-10a=A. NOT a separate dialog.

using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MastersFM.Tray.Services;

namespace MastersFM.Tray.ViewModels;

public sealed partial class AboutViewModel : ObservableObject
{
    private const string Component = "About";

    private readonly ILogger _logger;

    [ObservableProperty]
    private string brandName = "Master's FM";

    [ObservableProperty]
    private string version = "v14.0.0-rc.1";

    [ObservableProperty]
    private string buildDate = DateTime.Now.ToString("yyyy.MM.dd");

    [ObservableProperty]
    private string osCompat = "for Windows 10 / 11";

    [ObservableProperty]
    private string author = "Created by Orken (orken.ae)";

    [ObservableProperty]
    private string runtimeStack = "";

    [ObservableProperty]
    private string discordClientId = "1495411843836018819";

    [ObservableProperty]
    private string authenticodeCn = "(unknown)";

    [ObservableProperty]
    private bool showTechnicalDetails = true;

    public AboutViewModel(ILogger logger)
    {
        _logger = logger;

        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var asmVer = asm.GetName().Version?.ToString() ?? "unknown";
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            Version = info ?? asmVer;
        }
        catch { }

        try
        {
            RuntimeStack = "Made with tray_native.dll, .NET " + Environment.Version + ", WPF.";
        }
        catch { RuntimeStack = "Made with .NET 8 + WPF."; }

        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                var cert = X509Certificate.CreateFromSignedFile(exePath);
                AuthenticodeCn = cert.Subject; // typical: "CN=MasterShadex,..."
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarn("authenticode read failed: " + ex.Message, Component);
            AuthenticodeCn = "(unsigned or read failed)";
        }
    }

    [RelayCommand]
    private void OpenGitHub()
    {
        TryOpenUrl("https://github.com/MasterShadex/Masters-FM");
    }

    [RelayCommand]
    private void ReportBug()
    {
        TryOpenUrl("https://github.com/MasterShadex/Masters-FM/issues/new");
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MastersFM");
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            _logger.LogErr("open data folder", ex, Component);
        }
    }

    private void TryOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogErr("open URL " + url, ex, Component);
        }
    }
}
