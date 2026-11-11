using System.IO;

namespace TapeRescue.Services;

/// <summary>Plain-text log at %LOCALAPPDATA%\TapeTimeMachine\TapeTimeMachine.log for troubleshooting camcorder quirks.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(AppSettings.DataFolder, "TapeTimeMachine.log");

    static Log()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > 5_000_000) info.Delete();
        }
        catch { /* logging must never break the app */ }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
