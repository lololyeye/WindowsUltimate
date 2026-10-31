using System.Text;

namespace WindowsUltimate.Core.Infrastructure;

/// <summary>Minimal append-only file log. Every state-changing operation is written here as an AUDIT line.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    /// <summary>Audit entries describe a change to the system configuration.</summary>
    public static void Audit(string message) => Write("AUDIT", message);

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {(Elevation.IsElevated ? "(admin) " : "")}{message}{Environment.NewLine}";
            var file = Path.Combine(AppPaths.Logs, $"app-{DateTime.Now:yyyyMMdd}.log");
            lock (Gate) File.AppendAllText(file, line, Encoding.UTF8);
        }
        catch
        {
            // Logging must never take the application down.
        }
    }
}
