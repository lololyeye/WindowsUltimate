using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Optimization;

namespace WindowsUltimate.Core.Startup;

public enum StartupSource { UserRun, MachineRun, MachineRun32, UserStartupFolder, CommonStartupFolder }

public sealed class StartupEntry
{
    public StartupSource Source { get; init; }
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string? ExecutablePath { get; init; }
    public bool FileExists { get; init; }
    public string Publisher { get; init; } = "";
    public string Signer { get; init; } = "";
    public string Description { get; init; } = "";
    public bool Enabled { get; set; }
    public long RunningMemoryBytes { get; set; }
    public bool IsRunning { get; set; }

    public bool RequiresAdmin => Source is StartupSource.MachineRun or StartupSource.MachineRun32 or StartupSource.CommonStartupFolder;
    public bool IsMicrosoft => Publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) || Signer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

    public string SourceLabel => Source switch
    {
        StartupSource.UserRun => "Registry (current user)",
        StartupSource.MachineRun => "Registry (all users)",
        StartupSource.MachineRun32 => "Registry (all users, 32-bit)",
        StartupSource.UserStartupFolder => "Startup folder (current user)",
        _ => "Startup folder (all users)",
    };

    /// <summary>
    /// Windows does not expose Task Manager's boot-trace impact to applications, so this is an estimate from the
    /// memory the program uses right now. It is labelled as an estimate in the UI.
    /// </summary>
    public string EstimatedImpact => !IsRunning ? "Not running" : RunningMemoryBytes switch
    {
        < 50L << 20 => "Low",
        < 200L << 20 => "Medium",
        _ => "High",
    };

    internal (RegistryHive Hive, string Path) ApprovedKey => Source switch
    {
        StartupSource.UserRun => (RegistryHive.CurrentUser, StartupManager.ApprovedBase + "Run"),
        StartupSource.MachineRun => (RegistryHive.LocalMachine, StartupManager.ApprovedBase + "Run"),
        StartupSource.MachineRun32 => (RegistryHive.LocalMachine, StartupManager.ApprovedBase + "Run32"),
        StartupSource.UserStartupFolder => (RegistryHive.CurrentUser, StartupManager.ApprovedBase + "StartupFolder"),
        _ => (RegistryHive.LocalMachine, StartupManager.ApprovedBase + "StartupFolder"),
    };
}

/// <summary>
/// Startup entries from the Run keys and Startup folders. Enabling/disabling uses the same
/// StartupApproved mechanism as Task Manager, so the entry itself is never deleted and the change is reversible.
/// </summary>
public static class StartupManager
{
    internal const string ApprovedBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKey32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

    public static List<StartupEntry> List(IReadOnlyList<Monitoring.ProcessSample>? running = null)
    {
        var list = new List<StartupEntry>();
        ReadRun(list, RegistryHive.CurrentUser, RunKey, StartupSource.UserRun);
        ReadRun(list, RegistryHive.LocalMachine, RunKey, StartupSource.MachineRun);
        ReadRun(list, RegistryHive.LocalMachine, RunKey32, StartupSource.MachineRun32);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.UserStartupFolder);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.CommonStartupFolder);

        if (running is not null)
        {
            foreach (var e in list.Where(e => e.ExecutablePath is not null))
            {
                var matches = running.Where(p => string.Equals(p.Path, e.ExecutablePath, StringComparison.OrdinalIgnoreCase)).ToList();
                e.IsRunning = matches.Count > 0;
                e.RunningMemoryBytes = matches.Sum(p => p.PrivateBytes);
            }
        }
        return list;
    }

    public static int EnabledCount() => List().Count(e => e.Enabled);

    private static void ReadRun(List<StartupEntry> list, RegistryHive hive, string path, StartupSource source)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var k = root.OpenSubKey(path);
            if (k is null) return;
            foreach (var name in k.GetValueNames().Where(n => n.Length > 0))
            {
                var cmd = k.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
                list.Add(Build(source, name, cmd, ExtractExecutable(cmd)));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading {hive}\\{path} failed: {ex.Message}");
        }
    }

    private static void ReadFolder(List<StartupEntry> list, string folder, StartupSource source)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder).Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
        {
            var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(file) : file;
            list.Add(Build(source, Path.GetFileName(file), target ?? file, target));
        }
    }

    private static StartupEntry Build(StartupSource source, string name, string command, string? exe)
    {
        var exists = exe is not null && File.Exists(exe);
        string publisher = "", description = "", signer = "";
        if (exists)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exe!);
                publisher = info.CompanyName ?? "";
                description = info.FileDescription ?? "";
            }
            catch { /* unreadable version resource */ }
            signer = SignerOf(exe!);
        }
        var entry = new StartupEntry
        {
            Source = source, Name = name, Command = command, ExecutablePath = exe, FileExists = exists,
            Publisher = publisher, Description = description, Signer = signer,
        };
        entry.Enabled = IsApproved(entry);
        return entry;
    }

    /// <summary>Subject CN of the Authenticode certificate. Not a trust verification — shown as "signed by" information only.</summary>
    public static string SignerOf(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            var subject = cert.Subject;
            var cn = subject.Split(',').Select(s => s.Trim()).FirstOrDefault(s => s.StartsWith("CN=", StringComparison.Ordinal));
            return cn is null ? subject : cn[3..].Trim('"');
        }
        catch
        {
            return "";
        }
    }

    public static string? ExtractExecutable(string command)
    {
        var cmd = Environment.ExpandEnvironmentVariables(command.Trim());
        if (cmd.Length == 0) return null;
        if (cmd[0] == '"')
        {
            var end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : null;
        }
        var idx = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) return cmd[..(idx + 4)];
        var first = cmd.Split(' ')[0];
        return first.Contains('\\') ? first : Path.Combine(Environment.SystemDirectory, first);
    }

    private static string? ResolveShortcut(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return null;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic sc = shell.CreateShortcut(lnk);
            string target = sc.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsApproved(StartupEntry e)
    {
        var (hive, path) = e.ApprovedKey;
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var k = root.OpenSubKey(path);
        return k?.GetValue(e.Name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
    }

    /// <summary>A reversible engine change that enables or disables the entry (journaled like any tweak).</summary>
    public static Tweak CreateToggle(StartupEntry e, bool enable)
    {
        var (hive, path) = e.ApprovedKey;
        return new Tweak
        {
            Id = $"startup:{e.Source}:{e.Name}",
            Name = $"{(enable ? "Enable" : "Disable")} startup entry \"{e.Name}\"",
            Category = TweakCategory.Startup,
            Description = $"{(enable ? "Allows" : "Prevents")} {e.Name} from starting when you sign in.",
            WhatItChanges = "Marks the entry as enabled/disabled in StartupApproved (the same mechanism as Task Manager). The program itself is not modified or removed.",
            WhyItHelps = "Fewer programs at sign-in means a faster, lighter startup.",
            ExpectedImpact = "Depends on the program.",
            Disadvantages = enable ? "The program will start at sign-in again." : "The program will not start automatically; some apps (sync, drivers' control panels) may stop working until started manually.",
            TechnicalDetails = $@"{(hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM")}\{path} › {e.Name}",
            Source = "Task Manager › Startup apps (StartupApproved registry mechanism)",
            Risk = RiskLevel.Safe,
            Benefit = BenefitLevel.Low,
            Actions = [new StartupApprovedAction(hive, path, e.Name, enable)],
        };
    }
}

/// <summary>Writes the 12-byte StartupApproved record: first byte even = enabled, odd = disabled (+ FILETIME of disabling).</summary>
public sealed class StartupApprovedAction(RegistryHive hive, string path, string name, bool enable) : IChangeAction
{
    private readonly RegistryValueAction _inner = new(hive, path, name, Array.Empty<byte>(), RegistryValueKind.Binary);

    public string Key => _inner.Key;
    public bool RequiresAdmin => hive == RegistryHive.LocalMachine;
    public bool IsReversible => true;
    public string Describe() => _inner.Describe();
    public string DescribeDesired() => enable ? "Enabled" : "Disabled";
    public string DescribeCurrent() => CurrentEnabled() ? "Enabled" : "Disabled";

    private bool CurrentEnabled()
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var k = root.OpenSubKey(path);
        return k?.GetValue(name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
    }

    public bool IsInDesiredState() => CurrentEnabled() == enable;
    public ActionSnapshot Capture() => _inner.Capture();
    public void Restore(ActionSnapshot snapshot) => _inner.Restore(snapshot);

    public void Apply()
    {
        var data = new byte[12];
        data[0] = enable ? (byte)0x02 : (byte)0x03;
        if (!enable) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var k = root.CreateSubKey(path, true);
        k.SetValue(name, data, RegistryValueKind.Binary);
    }
}
