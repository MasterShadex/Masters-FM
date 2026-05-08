// Stage 7.3: ILogger interface. Logger.cs implements this; DI registers as
// singleton. EarlyLog stays as a static method on the Logger class for
// pre-DI bootstrap (App.xaml.cs OnStartup before container build).

namespace MastersFM.Tray.Services;

public interface ILogger
{
    /// <summary>Default INFO level. Optional component string is bracketed before the message.</summary>
    void Log(string message, string component = "");

    /// <summary>WARN level. Optional component string is bracketed before the message.</summary>
    void LogWarn(string message, string component = "");

    /// <summary>ERROR level with optional exception. Stack trace + inner exception are appended.</summary>
    void LogErr(string context, Exception? ex = null, string component = "");

    /// <summary>Snapshot of the last 20 log lines (read-only copy; safe for cross-thread reading).</summary>
    IReadOnlyList<string> SnapshotRingBuffer();
}
