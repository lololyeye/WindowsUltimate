using System.Globalization;

namespace WindowsUltimate.Core.Infrastructure;

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        double v = bytes;
        var i = 0;
        while (Math.Abs(v) >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00", CultureInfo.CurrentCulture) + " " + Units[i];
    }

    public static string Rate(double bytesPerSecond) => Bytes((long)bytesPerSecond) + "/s";

    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" :
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" :
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.TotalSeconds:0.0}s";
}
