using System;
using System.IO;

namespace VmixGtPlus.Services;

/// <summary>simple logger, writes timestamped lines to Console and a .log file at once</summary>
public static class Logger
{
    private static StreamWriter? _file;

    public static void Init()
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "vmix-gt-plus.log");
        try
        {
            _file = new StreamWriter(logPath, append: false) { AutoFlush = true };
            Write("INFO", $"Log started - {logPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Could not open log file: {ex.Message}");
        }
    }

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg, Exception? ex = null)
    {
        Write("ERROR", ex is null ? msg : $"{msg}\n        {ex.GetType().Name}: {ex.Message}");
        if (ex?.InnerException is { } inner)
            Write("ERROR", $"  Inner: {inner.GetType().Name}: {inner.Message}");
    }

    public static void Debug(string msg) => Write("DEBUG", msg);

    private static void Write(string level, string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {msg}";
        Console.WriteLine(line);
        try { _file?.WriteLine(line); }
        catch { /* swallow, don't let logging crash the app */ }
    }
}
