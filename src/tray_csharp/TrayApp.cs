// Stage 7.1 skeleton -- TrayApp ApplicationContext.
// Minimal NotifyIcon with one menu item: Quit. No detection logic, no other menu items,
// no dialogs, no update-check. Those land in later sub-stages (7.2 update, 7.5 detection,
// 7.6 full menu, 7.7 dialogs, etc.) per V14_S7_P1_SUBSTAGE_BREAKDOWN.md.

using System.Drawing;
using System.Windows.Forms;

namespace MastersFM.Tray;

internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private bool _disposed;

    public TrayApp()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Master's FM v14 dev (skeleton)",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        Logger.Log("TrayApp constructed; NotifyIcon visible with Quit-only menu");
    }

    private static Icon LoadIcon()
    {
        // The csproj <ApplicationIcon> sets the Win32 resource icon on the exe.
        // ExtractAssociatedIcon retrieves it at runtime. If extraction fails for any
        // reason, fall back to a system icon so the tray remains visible.
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                var icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon != null)
                {
                    return icon;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogErr("LoadIcon ExtractAssociatedIcon", ex);
        }

        Logger.Log("LoadIcon: falling back to SystemIcons.Information");
        return SystemIcons.Information;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var quit = new ToolStripMenuItem("Quit");
        quit.Click += (_, _) =>
        {
            Logger.Log("Quit clicked; ExitThread()");
            ExitThread();
        };
        menu.Items.Add(quit);
        return menu;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                try
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    Logger.Log("TrayApp.Dispose: NotifyIcon hidden + disposed");
                }
                catch (Exception ex)
                {
                    Logger.LogErr("NotifyIcon disposal", ex);
                }
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
