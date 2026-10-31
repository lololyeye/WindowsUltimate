using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Optimization;

/// <summary>Serializable record of a setting's state before we touched it.</summary>
public sealed class ActionSnapshot
{
    public string ActionKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public Dictionary<string, string?> Data { get; set; } = new();
}

/// <summary>
/// A single, atomic, reversible change. The engine always calls <see cref="Capture"/> and persists the
/// snapshot before <see cref="Apply"/>, then <see cref="IsInDesiredState"/> to verify.
/// </summary>
public interface IChangeAction
{
    /// <summary>Identity of the underlying setting (used to detect conflicts between tweaks).</summary>
    string Key { get; }
    string Describe();
    string DescribeDesired();
    string DescribeCurrent();
    bool RequiresAdmin { get; }
    bool IsReversible { get; }
    bool IsInDesiredState();
    ActionSnapshot Capture();
    void Apply();
    void Restore(ActionSnapshot snapshot);
}

/// <summary>Sets one registry value. Restores the exact previous value/kind, or deletes it if it did not exist.</summary>
public sealed class RegistryValueAction : IChangeAction
{
    public RegistryHive Hive { get; }
    public string Path { get; }
    public string Name { get; }
    public object Value { get; }
    public RegistryValueKind Kind { get; }
    private readonly Action? _afterChange;

    public RegistryValueAction(RegistryHive hive, string path, string name, object value, RegistryValueKind kind = RegistryValueKind.DWord, Action? afterChange = null)
    {
        Hive = hive; Path = path; Name = name; Value = value; Kind = kind; _afterChange = afterChange;
    }

    private string HiveName => Hive switch { RegistryHive.CurrentUser => "HKCU", RegistryHive.LocalMachine => "HKLM", _ => Hive.ToString() };
    public string Key => $"reg:{HiveName}\\{Path}\\{Name}".ToLowerInvariant();
    public bool RequiresAdmin => Hive == RegistryHive.LocalMachine;
    public bool IsReversible => true;

    public string Describe() => $@"{HiveName}\{Path} → {Name}";
    public string DescribeDesired() => FormatValue(Value, Kind);

    public string DescribeCurrent()
    {
        using var k = Open(false);
        var v = k?.GetValue(Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return v is null ? "(not set — Windows default)" : FormatValue(v, k!.GetValueKind(Name));
    }

    private RegistryKey? Open(bool writable)
    {
        using var root = RegistryKey.OpenBaseKey(Hive, RegistryView.Registry64);
        return writable ? root.CreateSubKey(Path, true) : root.OpenSubKey(Path, false);
    }

    public bool IsInDesiredState()
    {
        using var k = Open(false);
        var v = k?.GetValue(Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return v is not null && ValuesEqual(v, Value);
    }

    public ActionSnapshot Capture()
    {
        using var root = RegistryKey.OpenBaseKey(Hive, RegistryView.Registry64);
        using var k = root.OpenSubKey(Path, false);
        var snap = new ActionSnapshot { ActionKey = Key, Kind = "registry" };
        // Location is stored so the entry stays restorable even if the tweak that created it is gone.
        snap.Data["hive"] = Hive.ToString();
        snap.Data["path"] = Path;
        snap.Data["name"] = Name;
        snap.Data["keyExisted"] = (k is not null).ToString();
        var v = k?.GetValue(Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        snap.Data["exists"] = (v is not null).ToString();
        if (v is not null)
        {
            var kind = k!.GetValueKind(Name);
            snap.Data["kind"] = kind.ToString();
            snap.Data["value"] = Serialize(v, kind);
        }
        return snap;
    }

    public void Apply()
    {
        using var k = Open(true) ?? throw new InvalidOperationException($"Cannot open {Describe()}");
        k.SetValue(Name, Value, Kind);
        _afterChange?.Invoke();
    }

    public void Restore(ActionSnapshot s)
    {
        using (var root = RegistryKey.OpenBaseKey(Hive, RegistryView.Registry64))
        {
            if (s.Data.GetValueOrDefault("exists") == bool.TrueString)
            {
                var kind = Enum.Parse<RegistryValueKind>(s.Data["kind"]!);
                using var k = root.CreateSubKey(Path, true);
                k.SetValue(Name, Deserialize(s.Data["value"] ?? "", kind), kind);
            }
            else
            {
                using (var k = root.OpenSubKey(Path, true))
                    k?.DeleteValue(Name, throwOnMissingValue: false);

                // Remove the key only if we created it and it is now empty.
                if (s.Data.GetValueOrDefault("keyExisted") == bool.FalseString)
                {
                    using var check = root.OpenSubKey(Path, false);
                    if (check is not null && check.ValueCount == 0 && check.SubKeyCount == 0)
                    {
                        check.Close();
                        try { root.DeleteSubKey(Path, false); } catch { /* not critical */ }
                    }
                }
            }
        }
        _afterChange?.Invoke();
    }

    internal static bool ValuesEqual(object current, object desired) => current switch
    {
        int i when desired is int d => i == d,
        long l when desired is long d => l == d,
        string s when desired is string d => string.Equals(s, d, StringComparison.Ordinal),
        byte[] b when desired is byte[] d => b.AsSpan().SequenceEqual(d),
        string[] a when desired is string[] d => a.SequenceEqual(d),
        _ => Equals(current, desired),
    };

    internal static string FormatValue(object v, RegistryValueKind kind) => v switch
    {
        int i => $"{i} (DWORD)",
        long l => $"{l} (QWORD)",
        byte[] b => $"{Convert.ToHexString(b.Length > 16 ? b[..16] : b)}{(b.Length > 16 ? "…" : "")} (binary)",
        string[] a => string.Join("; ", a) + " (multi-string)",
        _ => $"\"{v}\"" + (kind == RegistryValueKind.ExpandString ? " (expandable)" : ""),
    };

    internal static string Serialize(object v, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => ((int)v).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => ((long)v).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.ToBase64String((byte[])v),
        RegistryValueKind.MultiString => string.Join("\u001f", (string[])v),
        _ => v.ToString() ?? "",
    };

    internal static object Deserialize(string s, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => int.Parse(s, CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => long.Parse(s, CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.FromBase64String(s),
        RegistryValueKind.MultiString => s.Length == 0 ? Array.Empty<string>() : s.Split('\u001f'),
        _ => s,
    };
}

/// <summary>
/// Sets one "Key=Value;" token inside a semicolon-separated registry string without disturbing the other tokens
/// (used by HKCU\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings).
/// </summary>
public sealed class RegistryTokenAction(RegistryHive hive, string path, string name, string token, string tokenValue) : IChangeAction
{
    public string Key => $"regtoken:{hive}\\{path}\\{name}\\{token}".ToLowerInvariant();
    public bool RequiresAdmin => hive == RegistryHive.LocalMachine;
    public bool IsReversible => true;
    public string Describe() => $@"{(hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM")}\{path} → {name} [{token}]";
    public string DescribeDesired() => $"{token}={tokenValue}";

    public string DescribeCurrent()
    {
        var current = Tokens(Read()).GetValueOrDefault(token);
        return current is null ? "(not set — Windows default)" : $"{token}={current}";
    }

    private string? Read()
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var k = root.OpenSubKey(path, false);
        return k?.GetValue(name) as string;
    }

    internal static Dictionary<string, string> Tokens(string? s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) d[part[..eq]] = part[(eq + 1)..];
        }
        return d;
    }

    internal static string Join(Dictionary<string, string> tokens) => string.Concat(tokens.Select(t => $"{t.Key}={t.Value};"));

    public bool IsInDesiredState() => Tokens(Read()).GetValueOrDefault(token) == tokenValue;

    public ActionSnapshot Capture()
    {
        var s = Read();
        return new ActionSnapshot
        {
            ActionKey = Key,
            Kind = "registry-token",
            Data = { ["exists"] = (s is not null).ToString(), ["tokenValue"] = Tokens(s).GetValueOrDefault(token) },
        };
    }

    public void Apply()
    {
        var tokens = Tokens(Read());
        tokens[token] = tokenValue;
        Write(Join(tokens));
    }

    public void Restore(ActionSnapshot snapshot)
    {
        // Only our token is restored, so changes the user made to other tokens in the meantime survive.
        var tokens = Tokens(Read());
        var original = snapshot.Data.GetValueOrDefault("tokenValue");
        if (original is null) tokens.Remove(token); else tokens[token] = original;
        if (tokens.Count == 0 && snapshot.Data.GetValueOrDefault("exists") == bool.FalseString)
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var k = root.OpenSubKey(path, true);
            k?.DeleteValue(name, false);
        }
        else Write(Join(tokens));
    }

    private void Write(string value)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var k = root.CreateSubKey(path, true);
        k.SetValue(name, value, RegistryValueKind.String);
    }
}

/// <summary>Activates a power plan with powercfg. Restores the previously active plan.</summary>
public sealed partial class PowerPlanAction(Guid scheme, string schemeName) : IChangeAction
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public Guid Scheme => scheme;
    public string Key => "powerplan:active";
    public bool RequiresAdmin => false;
    public bool IsReversible => true;
    public string Describe() => "Active power plan";
    public string DescribeDesired() => schemeName;
    public string DescribeCurrent() => ActivePlan() is { } a ? $"{a.Name}" : "Unknown";

    [GeneratedRegex(@"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\s+\((.*?)\)(\s*\*)?", RegexOptions.IgnoreCase)]
    private static partial Regex SchemeLine();

    /// <summary>Lists installed plans. Parses only GUIDs and the parenthesised name, so it is language-independent.</summary>
    public static List<(Guid Id, string Name, bool Active)> ListPlans()
    {
        var r = ProcessRunner.Run(ProcessRunner.System32("powercfg.exe"), "/list");
        return SchemeLine().Matches(r.StdOut)
            .Select(m => (Guid.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim(), m.Groups[3].Success))
            .Select(p => (p.Item1, FriendlyName(p.Item1) ?? p.Item2, p.Item3))
            .ToList();
    }

    public static (Guid Id, string Name)? ActivePlan()
    {
        var r = ProcessRunner.Run(ProcessRunner.System32("powercfg.exe"), "/getactivescheme");
        var m = SchemeLine().Match(r.StdOut);
        if (!m.Success) return null;
        var id = Guid.Parse(m.Groups[1].Value);
        return (id, FriendlyName(id) ?? m.Groups[2].Value.Trim());
    }

    /// <summary>Reads the plan name as Unicode; powercfg's console output loses characters such as ™ in the OEM code page.</summary>
    private static string? FriendlyName(Guid scheme)
    {
        try
        {
            uint size = 0;
            if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size == 0) return null;
            var buffer = new byte[size];
            if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return null;
            var name = System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
            return name.Length > 0 ? name : null;
        }
        catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid, byte[]? buffer, ref uint bufferSize);

    public static bool IsInstalled(Guid id) => ListPlans().Any(p => p.Id == id);

    public bool IsInDesiredState() => ActivePlan()?.Id == scheme;

    public ActionSnapshot Capture() => new()
    {
        ActionKey = Key,
        Kind = "powerplan",
        Data = { ["scheme"] = ActivePlan()?.Id.ToString() },
    };

    public void Apply() => SetActive(scheme);

    public void Restore(ActionSnapshot snapshot)
    {
        if (Guid.TryParse(snapshot.Data.GetValueOrDefault("scheme"), out var g)) SetActive(g);
    }

    private static void SetActive(Guid id)
    {
        var r = ProcessRunner.Run(ProcessRunner.System32("powercfg.exe"), $"/setactive {id}");
        if (!r.Success) throw new InvalidOperationException($"powercfg /setactive failed ({r.ExitCode}): {r.StdOut.Trim()} {r.StdErr.Trim()}");
    }
}

/// <summary>Enables or disables a Task Scheduler task through the Task Scheduler COM API.</summary>
public sealed class ScheduledTaskEnabledAction(string taskPath, bool enabled) : IChangeAction
{
    public string Key => $"task:{taskPath}".ToLowerInvariant();
    public bool RequiresAdmin => false; // depends on the task's ACL; failures are reported as access denied
    public bool IsReversible => true;
    public string Describe() => $"Scheduled task {taskPath}";
    public string DescribeDesired() => enabled ? "Enabled" : "Disabled";
    public string DescribeCurrent() => Get() ? "Enabled" : "Disabled";

    private static dynamic Service()
    {
        var t = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is unavailable");
        dynamic svc = Activator.CreateInstance(t)!;
        svc.Connect();
        return svc;
    }

    private dynamic TaskObject()
    {
        dynamic svc = Service();
        var slash = taskPath.LastIndexOf('\\');
        var folder = slash <= 0 ? "\\" : taskPath[..slash];
        return svc.GetFolder(folder).GetTask(taskPath[(slash + 1)..]);
    }

    private bool Get() => (bool)TaskObject().Enabled;

    private void Set(bool value)
    {
        dynamic task = TaskObject();
        task.Enabled = value;
    }

    public bool IsInDesiredState() => Get() == enabled;
    public ActionSnapshot Capture() => new() { ActionKey = Key, Kind = "task", Data = { ["enabled"] = Get().ToString(), ["path"] = taskPath } };
    public void Apply() => Set(enabled);
    public void Restore(ActionSnapshot snapshot) => Set(snapshot.Data.GetValueOrDefault("enabled") == bool.TrueString);
}

public static class Reg
{
    public static RegistryValueAction User(string path, string name, int value) => new(RegistryHive.CurrentUser, path, name, value);
    public static RegistryValueAction User(string path, string name, string value) => new(RegistryHive.CurrentUser, path, name, value, RegistryValueKind.String);
    public static RegistryValueAction Machine(string path, string name, int value) => new(RegistryHive.LocalMachine, path, name, value);

    /// <summary>Service start type via its documented registry value (2 = Automatic, 3 = Manual, 4 = Disabled).</summary>
    public static RegistryValueAction ServiceStart(string service, int start) =>
        new(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{service}", "Start", start);

    public static int? ReadInt(RegistryHive hive, string path, string name)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var k = root.OpenSubKey(path, false);
        return k?.GetValue(name) is int i ? i : null;
    }

    public static string? ReadString(RegistryHive hive, string path, string name)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var k = root.OpenSubKey(path, false);
        return k?.GetValue(name) as string;
    }
}
