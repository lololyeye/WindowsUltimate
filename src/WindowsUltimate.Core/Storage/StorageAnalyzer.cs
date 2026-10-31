using System.Security.Cryptography;
using Microsoft.Win32;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Storage;

public sealed record DriveUsage(string Name, string Label, string Format, long Total, long Free)
{
    public long Used => Total - Free;
    public double UsedPercent => Total == 0 ? 0 : 100.0 * Used / Total;
}

public sealed record FolderSize(string Path, string Name, long Bytes);

public sealed record LargeFile(string Path, long Bytes, DateTime Modified);

public sealed class DuplicateGroup
{
    public long FileSize { get; init; }
    public string Hash { get; init; } = "";
    public List<LargeFile> Files { get; init; } = [];
    public long Wasted => FileSize * (Files.Count - 1);
}

public sealed record InstalledApp(string Name, string Publisher, string Version, long SizeBytes, DateTime? InstallDate, string Scope);

/// <summary>Read-only analysis of disk usage. Nothing in this class deletes anything.</summary>
public static class StorageAnalyzer
{
    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static List<DriveUsage> Drives() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new DriveUsage(d.Name, d.VolumeLabel, d.DriveFormat, d.TotalSize, d.AvailableFreeSpace))
            .ToList();

    public static Task<List<FolderSize>> TopFoldersAsync(string root, IProgress<string>? progress = null, CancellationToken ct = default) => Task.Run(() =>
    {
        var result = new List<FolderSize>();
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(root).ToList(); }
        catch { return result; }
        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                progress?.Report(dir);
                long size = 0;
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Enumeration)) size += f.Length;
                result.Add(new FolderSize(dir, Path.GetFileName(dir), size));
            }
            catch { /* inaccessible */ }
        }
        return result.OrderByDescending(f => f.Bytes).ToList();
    }, ct);

    public static Task<List<LargeFile>> LargeFilesAsync(string root, long minBytes, int max = 200, IProgress<int>? progress = null, CancellationToken ct = default) => Task.Run(() =>
    {
        var heap = new PriorityQueue<LargeFile, long>();
        var scanned = 0;
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", Enumeration))
        {
            ct.ThrowIfCancellationRequested();
            if (++scanned % 5000 == 0) progress?.Report(scanned);
            if (f.Length < minBytes) continue;
            heap.Enqueue(new LargeFile(f.FullName, f.Length, f.LastWriteTime), f.Length);
            if (heap.Count > max) heap.Dequeue();
        }
        var list = new List<LargeFile>();
        while (heap.Count > 0) list.Add(heap.Dequeue());
        list.Reverse();
        return list;
    }, ct);

    /// <summary>Groups identical files: same size, then same hash of the first 64 KB, then same full SHA-256.</summary>
    public static Task<List<DuplicateGroup>> DuplicatesAsync(string root, long minBytes, IProgress<string>? progress = null, CancellationToken ct = default) => Task.Run(() =>
    {
        var bySize = new Dictionary<long, List<FileInfo>>();
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", Enumeration))
        {
            ct.ThrowIfCancellationRequested();
            if (f.Length < minBytes) continue;
            if (!bySize.TryGetValue(f.Length, out var l)) bySize[f.Length] = l = [];
            l.Add(f);
        }

        var groups = new List<DuplicateGroup>();
        foreach (var (size, files) in bySize.Where(kv => kv.Value.Count > 1))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var partial in files.GroupBy(f => Hash(f.FullName, 64 * 1024)).Where(g => g.Key is not null && g.Count() > 1))
            {
                foreach (var full in partial.GroupBy(f => Hash(f.FullName, long.MaxValue)).Where(g => g.Key is not null && g.Count() > 1))
                {
                    progress?.Report(full.First().Name);
                    groups.Add(new DuplicateGroup
                    {
                        FileSize = size,
                        Hash = full.Key!,
                        Files = full.Select(f => new LargeFile(f.FullName, f.Length, f.LastWriteTime)).ToList(),
                    });
                }
            }
        }
        return groups.OrderByDescending(g => g.Wasted).ToList();
    }, ct);

    private static string? Hash(string path, long maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            if (maxBytes == long.MaxValue) return Convert.ToHexString(SHA256.HashData(stream));
            var buffer = new byte[Math.Min(maxBytes, stream.Length)];
            stream.ReadExactly(buffer);
            return Convert.ToHexString(SHA256.HashData(buffer));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Sends a file to the Recycle Bin (recoverable) after the user confirmed the selection.</summary>
    public static void Recycle(string path)
    {
        var op = new Native.SHFILEOPSTRUCT
        {
            wFunc = Native.FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_SILENT | Native.FOF_NOERRORUI,
        };
        var rc = Native.SHFileOperation(ref op);
        if (rc != 0 || op.fAnyOperationsAborted) throw new IOException($"Could not move {path} to the Recycle Bin (code {rc}).");
        Log.Audit($"Sent to Recycle Bin: {path}");
    }

    public static List<InstalledApp> InstalledApps()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        void Read(RegistryHive hive, RegistryView view, string scope)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var k = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (k is null) return;
                foreach (var sub in k.GetSubKeyNames())
                {
                    using var a = k.OpenSubKey(sub);
                    if (a?.GetValue("DisplayName") is not string name || name.Length == 0) continue;
                    if (a.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (a.GetValue("ParentKeyName") is string) continue;
                    var size = a.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0;
                    DateTime? date = DateTime.TryParseExact(a.GetValue("InstallDate") as string, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
                    apps.TryAdd(name, new InstalledApp(name, a.GetValue("Publisher") as string ?? "", a.GetValue("DisplayVersion") as string ?? "", size, date, scope));
                }
            }
            catch { /* inaccessible hive */ }
        }
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, "All users");
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, "All users (32-bit)");
        Read(RegistryHive.CurrentUser, RegistryView.Registry64, "Current user");
        return apps.Values.OrderByDescending(a => a.SizeBytes).ThenBy(a => a.Name).ToList();
    }
}
