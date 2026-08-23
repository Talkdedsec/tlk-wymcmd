using System.Collections.Concurrent;
using Wymcmd.Core.Store;

namespace Wymcmd.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogLine(DateTime When, LogLevel Level, string Message);

/// <summary>
/// Own diagnostics only - never user-facing text, so these strings stay untranslated.
/// Keeps the last few hundred lines in memory for the UI and appends to a capped file.
/// </summary>
public static class Log
{
    private const int MemoryLines = 500;
    private const long MaxFileBytes = 2 * 1024 * 1024;

    private static readonly ConcurrentQueue<LogLine> Recent = new();
    private static readonly Lock FileSync = new();

    public static LogLevel Minimum { get; set; } = LogLevel.Info;
    public static event Action<LogLine>? Written;

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Error(string message, Exception ex) => Write(LogLevel.Error, $"{message}: {ex.GetType().Name} {ex.Message}");

    public static IReadOnlyList<LogLine> Tail(int count = 100)
        => Recent.Reverse().Take(count).Reverse().ToList();

    private static void Write(LogLevel level, string message)
    {
        if (level < Minimum) return;

        var line = new LogLine(DateTime.Now, level, message);
        Recent.Enqueue(line);
        while (Recent.Count > MemoryLines) Recent.TryDequeue(out _);

        Written?.Invoke(line);
        Append(line);
    }

    private static void Append(LogLine line)
    {
        try
        {
            lock (FileSync)
            {
                var path = AppPaths.LogFile;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    var rolled = path + ".1";
                    File.Delete(rolled);
                    File.Move(path, rolled);
                }

                File.AppendAllText(path,
                    $"{line.When:yyyy-MM-dd HH:mm:ss.fff} {line.Level.ToString().ToUpperInvariant(),-5} {line.Message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
