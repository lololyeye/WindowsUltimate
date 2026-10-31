using System.Management;

namespace WindowsUltimate.Core.Infrastructure;

/// <summary>Thin, defensive wrapper over WMI/CIM queries. Returns plain dictionaries so callers never hold COM objects.</summary>
public static class Wmi
{
    public static List<Dictionary<string, object?>> Query(string wql, string scope = @"root\cimv2", TimeSpan? timeout = null)
    {
        var rows = new List<Dictionary<string, object?>>();
        var options = new System.Management.EnumerationOptions { Timeout = timeout ?? TimeSpan.FromSeconds(20), ReturnImmediately = true, Rewindable = false };
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql), options);
        using var results = searcher.Get();
        foreach (var obj in results)
        {
            using (obj)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in obj.Properties) row[p.Name] = p.Value;
                rows.Add(row);
            }
        }
        return rows;
    }

    /// <summary>Same as <see cref="Query"/> but returns an empty list (and logs) on failure.</summary>
    public static List<Dictionary<string, object?>> TryQuery(string wql, string scope = @"root\cimv2", TimeSpan? timeout = null)
    {
        try { return Query(wql, scope, timeout); }
        catch (Exception ex)
        {
            Log.Warn($"WMI query failed ({scope}: {wql}): {ex.Message}");
            return [];
        }
    }

    public static string Str(this Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) && v is not null ? (v.ToString() ?? string.Empty).Trim() : string.Empty;

    public static long Long(this Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null) return 0;
        try { return Convert.ToInt64(v); } catch { return 0; }
    }

    public static bool? Bool(this Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) && v is bool b ? b : null;

    public static DateTime? Date(this Dictionary<string, object?> row, string key)
    {
        var s = row.Str(key);
        if (s.Length < 8) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); }
        catch { return null; }
    }
}
