using System;
using System.IO;
using System.Text;

namespace FATXTools.Utilities;

public static class AppLogger
{
    private static readonly object SyncObject = new();
    private static string _logFilePath = "log.txt";
    private static bool _enabled;

    public static event Action<string>? LineWritten;

    public static void Configure(string logFilePath, bool enabled)
    {
        lock (SyncObject)
        {
            _logFilePath = string.IsNullOrWhiteSpace(logFilePath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt")
                : ResolvePath(logFilePath);
            _enabled = enabled;
        }
    }

    public static void WriteLine(string line)
    {
        LineWritten?.Invoke(line);
        if (!_enabled)
        {
            return;
        }

        string path;
        lock (SyncObject)
        {
            path = _logFilePath;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // Logging should never break recovery workflows.
        }
    }

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
    }
}
