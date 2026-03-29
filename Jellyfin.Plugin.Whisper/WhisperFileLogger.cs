using System;
using System.IO;

namespace Jellyfin.Plugin.Whisper;

/// <summary>
/// Simple file logger that appends whisper-related log lines to a dedicated log file
/// next to the plugin DLL, so they can be shared without copying the entire Jellyfin log.
/// </summary>
public static class WhisperFileLogger
{
    private static readonly object Lock = new();
    private static string? _logPath;

    /// <summary>
    /// Gets the path to the whisper log file.
    /// </summary>
    public static string LogPath
    {
        get
        {
            if (_logPath == null)
            {
                var dllDir = Path.GetDirectoryName(typeof(WhisperFileLogger).Assembly.Location)
                    ?? Environment.CurrentDirectory;
                _logPath = Path.Combine(dllDir, "whisper.log");
            }

            return _logPath;
        }
    }

    /// <summary>
    /// Appends a timestamped log line to the whisper log file.
    /// </summary>
    public static void Log(string level, string message)
    {
        try
        {
            var line = $"[{DateTime.UtcNow:O}] [{level}] {message}{Environment.NewLine}";
            lock (Lock)
            {
                System.IO.File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Don't let logging failures crash the plugin.
        }
    }

    /// <summary>
    /// Logs an informational message.
    /// </summary>
    public static void Info(string message) => Log("INF", message);

    /// <summary>
    /// Logs an error message.
    /// </summary>
    public static void Error(string message) => Log("ERR", message);
}
