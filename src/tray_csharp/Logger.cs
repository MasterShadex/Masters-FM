// Stage 7.3: Logger refactored to instance + ILogger interface (registered
// as singleton in DI). Static EarlyLog method preserved for pre-DI bootstrap
// (App.xaml.cs OnStartup before container build). Behavior preservation
// (per Stage 7.3 brief absolute rule 10): same overlay.log path
// (%LOCALAPPDATA%\MastersFM\overlay.log), same `[TRAY-CS]` prefix, same
// UTF-8 no BOM, same append-only-with-thread-safe-lock, same line format
// (with optional `[component]` segment after the prefix per 7.3 addition).
//
// Format:
//   [yyyy-MM-dd HH:mm:ss.fff] [LEVEL] [TRAY-CS] [component] message
// (the [component] segment is omitted when component is empty/null.)

using System.Collections.Generic;
using System.Text;
using MastersFM.Tray.Services;

namespace MastersFM.Tray;

public sealed class Logger : ILogger
{
    private static readonly object _staticLock = new();
    private static readonly Encoding _utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly string _logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MastersFM");
    private static readonly string _logPath = Path.Combine(_logDir, "overlay.log");

    private readonly object _instanceLock = new();
    private readonly Queue<string> _ringBuffer = new();
    private const int RingCap = 20;

    static Logger()
    {
        try
        {
            Directory.CreateDirectory(_logDir);
        }
        catch
        {
            // Best-effort directory creation; file writes will fail silently if
            // the directory cannot be created. The tray still runs.
        }
    }

    public Logger()
    {
        // Instance constructor exists for DI registration. All static fields
        // are shared (log path, encoding, directory) so multiple instances
        // would write to the same file (DI registers as singleton, so only
        // one instance is constructed per process anyway).
    }

    /// <summary>
    /// Static fallback for pre-DI bootstrap callsites. Routes through the
    /// same write path with [EARLY] level. Used by App.xaml.cs OnStartup
    /// before the container is built.
    /// </summary>
    public static void EarlyLog(string message)
    {
        WriteStatic("EARLY", message, component: null);
    }

    /// <summary>
    /// Static fallback for ERROR-level pre-DI bootstrap. Used by
    /// App.xaml.cs OnStartup catch blocks before the container exists.
    /// </summary>
    public static void EarlyLogErr(string context, Exception ex)
    {
        var sb = new StringBuilder();
        sb.Append("!! ERROR [").Append(context).Append("]: ");
        sb.Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
        if (!string.IsNullOrEmpty(ex.StackTrace))
        {
            sb.AppendLine();
            sb.Append("   stack: ").Append(ex.StackTrace);
        }
        WriteStatic("ERROR", sb.ToString(), component: null);
    }

    public void Log(string message, string component = "")
    {
        WriteInstance("INFO ", message, component);
    }

    public void LogWarn(string message, string component = "")
    {
        WriteInstance("WARN ", message, component);
    }

    public void LogErr(string context, Exception? ex = null, string component = "")
    {
        var sb = new StringBuilder();
        sb.Append("!! ERROR [").Append(context).Append("]");
        if (ex != null)
        {
            sb.Append(": ");
            sb.Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                sb.AppendLine();
                sb.Append("   stack: ").Append(ex.StackTrace);
            }
            if (ex.InnerException != null)
            {
                sb.AppendLine();
                sb.Append("   inner: ").Append(ex.InnerException.ToString());
            }
        }
        WriteInstance("ERROR", sb.ToString(), component);
    }

    public IReadOnlyList<string> SnapshotRingBuffer()
    {
        lock (_instanceLock)
        {
            return _ringBuffer.ToArray();
        }
    }

    private void WriteInstance(string level, string message, string component)
    {
        var line = FormatLine(level, message, component);
        lock (_instanceLock)
        {
            try
            {
                File.AppendAllText(_logPath, line + Environment.NewLine, _utf8NoBom);
            }
            catch
            {
                // Best-effort; do not crash on log failure.
            }

            try
            {
                if (_ringBuffer.Count >= RingCap) _ringBuffer.Dequeue();
                _ringBuffer.Enqueue(message);
            }
            catch
            {
                // best-effort
            }

            try { Console.WriteLine(line); } catch { }
        }
    }

    private static void WriteStatic(string level, string message, string? component)
    {
        var line = FormatLine(level, message, component);
        lock (_staticLock)
        {
            try
            {
                File.AppendAllText(_logPath, line + Environment.NewLine, _utf8NoBom);
            }
            catch
            {
                // best-effort
            }

            try { Console.WriteLine(line); } catch { }
        }
    }

    private static string FormatLine(string level, string message, string? component)
    {
        if (string.IsNullOrEmpty(component))
        {
            return string.Format(
                "[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] [TRAY-CS] {2}",
                DateTime.Now,
                level,
                message);
        }
        return string.Format(
            "[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] [TRAY-CS] [{2}] {3}",
            DateTime.Now,
            level,
            component,
            message);
    }
}
