// Stage 7.1 skeleton -- Logger.
// Writes to %LOCALAPPDATA%\MastersFM\overlay.log (SAME path as PS tray, per
// V14_S7_P1_TRAY_INVENTORY.md S1). UTF-8 without BOM (per Stage 4.5 lesson; mandatory
// per absolute rule 3 of this brief). Thread-safe; multiple writers OK during dev
// parallel period (PS tray and C# tray may both write to overlay.log).
//
// Format: "[yyyy-MM-dd HH:mm:ss.fff] [LEVEL] [TRAY-CS] message"
// The [TRAY-CS] prefix distinguishes C# tray entries from PS tray entries (which
// use no source-tag prefix).
//
// Append-only behavior (per pre-execution Q2 default): the C# skeleton does NOT
// truncate overlay.log on startup. PS tray's truncation behavior (tray.ps1:196)
// would conflict if both processes tried to truncate simultaneously during the
// dev parallel period. Stage 7.3 (logging proper) will revisit this once the
// PS tray no longer co-writes.
//
// Ring buffer (Queue<string>, capacity 20) is allocated here as a placeholder for
// SLOW TICK forensics in 7.3. It is populated but NOT consumed in 7.1.

using System.Collections.Generic;
using System.Text;

namespace MastersFM.Tray;

internal static class Logger
{
    private static readonly object _lock = new();
    private static readonly Encoding _utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly string _logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MastersFM");
    private static readonly string _logPath = Path.Combine(_logDir, "overlay.log");
    private static readonly Queue<string> _ringBuf = new();
    private const int RingCap = 20;

    static Logger()
    {
        try
        {
            Directory.CreateDirectory(_logDir);
        }
        catch
        {
            // Best-effort. If the directory cannot be created the file writes will
            // fail silently below; the tray itself still runs.
        }
    }

    public static void EarlyLog(string msg) => Write("EARLY", msg);
    public static void Log(string msg) => Write("INFO ", msg);

    public static void LogErr(string context, Exception err)
    {
        var sb = new StringBuilder();
        sb.Append("!! ERROR [").Append(context).Append("]: ");
        sb.Append(err.GetType().FullName).Append(": ").Append(err.Message);
        if (!string.IsNullOrEmpty(err.StackTrace))
        {
            sb.AppendLine();
            sb.Append("   stack: ").Append(err.StackTrace);
        }
        if (err.InnerException != null)
        {
            sb.AppendLine();
            sb.Append("   inner: ").Append(err.InnerException.ToString());
        }
        Write("ERROR", sb.ToString());
    }

    private static void Write(string level, string msg)
    {
        var line = string.Format(
            "[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] [TRAY-CS] {2}",
            DateTime.Now,
            level,
            msg);
        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logPath, line + Environment.NewLine, _utf8NoBom);
            }
            catch
            {
                // Best-effort logging; do not crash on log failure.
            }

            try
            {
                if (_ringBuf.Count >= RingCap) _ringBuf.Dequeue();
                _ringBuf.Enqueue(msg);
            }
            catch { }

            try { Console.WriteLine(line); } catch { }
        }
    }

    /// <summary>
    /// Returns a snapshot of the in-memory ring buffer (last RingCap log messages).
    /// Stage 7.1 leaves this method unused; Stage 7.3 (logging proper) will consume it
    /// for SLOW TICK context dumps.
    /// </summary>
    public static IReadOnlyList<string> RingSnapshot()
    {
        lock (_lock)
        {
            return _ringBuf.ToArray();
        }
    }
}
