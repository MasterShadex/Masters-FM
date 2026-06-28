// Stage 7.12 Batch B DIAG-10 debug: dead-simple thread-safe file logger.
// Append-only writes to %LOCALAPPDATA%\MastersFM\server.log.
// Kept minimal on purpose — no rotation, no buffering, no category filters.
// Single global writer guarded by a lock so concurrent ILogger callers don't
// interleave bytes inside a single line.

using System;
using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;

namespace MastersFM.Server;

internal sealed class SimpleFileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _lock = new();

    public SimpleFileLoggerProvider(string path)
    {
        _path = path;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // v14: bounded log on startup. The original "no rotation" design let
            // ASP.NET request logging grow server.log to 16 GB (silent disk fill).
            // Now: if the existing log is pathologically large (> 200 MB) just
            // delete it to reclaim the disk immediately; if it is merely large
            // (> 10 MB) rotate it to server.log.old (replacing any prior .old) so
            // one session of history is kept. Disk use stays bounded at ~2x cap.
            // Rotation at startup is sufficient because the AddFilter in Program.cs
            // reduces in-session growth to a trickle.
            const long NukeBytes   = 200L * 1024 * 1024; // 200 MB -> too big to keep
            const long RotateBytes = 10L  * 1024 * 1024; // 10 MB  -> rotate to .old
            var fi = new FileInfo(path);
            if (fi.Exists)
            {
                if (fi.Length > NukeBytes)
                {
                    try { File.Delete(path); } catch { /* best-effort */ }
                }
                else if (fi.Length > RotateBytes)
                {
                    var old = path + ".old";
                    try { if (File.Exists(old)) File.Delete(old); } catch { }
                    try { File.Move(path, old); } catch { /* best-effort */ }
                }
            }
        }
        catch { /* best-effort */ }
    }

    public ILogger CreateLogger(string categoryName)
        => new SimpleFileLogger(categoryName, _path, _lock);

    public void Dispose() { }
}

internal sealed class SimpleFileLogger : ILogger
{
    private readonly string _category;
    private readonly string _path;
    private readonly object _lock;

    public SimpleFileLogger(string category, string path, object lockObj)
    {
        _category = category;
        _path     = path;
        _lock     = lockObj;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        string message;
        try { message = formatter(state, exception); }
        catch { return; }

        var line = string.Format(
            CultureInfo.InvariantCulture,
            "[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1,-5}] [{2}] {3}{4}",
            DateTime.Now,
            logLevel switch
            {
                LogLevel.Trace       => "TRACE",
                LogLevel.Debug       => "DEBUG",
                LogLevel.Information => "INFO",
                LogLevel.Warning     => "WARN",
                LogLevel.Error       => "ERROR",
                LogLevel.Critical    => "CRIT",
                _                    => "?"
            },
            ShortCategory(_category),
            message,
            exception != null ? Environment.NewLine + exception : string.Empty);

        try
        {
            lock (_lock)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch { /* swallow — never let logging crash the server */ }
    }

    // Trim category to just the class name (last segment after the final dot)
    // so each line stays readable.
    private static string ShortCategory(string cat)
    {
        var dot = cat.LastIndexOf('.');
        return dot >= 0 && dot < cat.Length - 1 ? cat[(dot + 1)..] : cat;
    }
}
