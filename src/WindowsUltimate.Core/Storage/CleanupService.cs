using System.Diagnostics;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Storage;

public enum CleanupSafety { Safe, Caution }

/// <summary>A category of regenerable files. Only cache/temp locations are ever listed — never personal folders.</summary>
public sealed class CleanupCategory
{
    public required string Id { get; init; }
    public required string Name { get => L.T(field); init; }
    public required string Description { get => L.T(field); init; }
    public CleanupSafety Safety { get; init; } = CleanupSafety.Safe;
    public bool RequiresAdmin { get; init; }
    public string? Note { get => L.TOrNull(field); set; }
    public bool SelectedByDefault { get; init; } = true;

    /// <summary>Folders scanned recursively. Junctions/symlinks are never followed.</summary>
    public IReadOnlyList<string> Roots { get; init; } = [];
    public string Pattern { get; init; } = "*";
    public TimeSpan MinAge { get; init; } = TimeSpan.Zero;
    public bool IsRecycleBin { get; init; }

    public long Bytes { get; set; }
    public int FileCount { get; set; }
    public List<string> Files { get; } = [];
    public bool Available => RequiresAdmin ? Elevation.IsElevated : true;
}

public sealed record CleanupResult(string CategoryId, string Name, long FreedBytes, int Deleted, int Skipped);

public sealed class CleanupHistoryEntry
{
    public DateTime Time { get; set; }
    public long FreedBytes { get; set; }
    public List<string> Categories { get; set; } = [];
    public int Skipped { get; set; }
}

/// <summary>
/// Finds and removes regenerable files. Scan and clean are separate steps: the UI always shows exactly what will be
/// removed and how much space it frees before anything is deleted. Files that are in use are skipped.
/// </summary>
public static class CleanupService
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public static List<CleanupCategory> Categories()
    {
        var list = new List<CleanupCategory>
        {
            new()
            {
                Id = "temp.user", Name = "Temporary files (your account)",
                Description = "Files programs left in your temp folder. Only files older than 24 hours are removed, so running installers are not affected.",
                Roots = [Path.GetTempPath()], MinAge = TimeSpan.FromHours(24),
            },
            new()
            {
                Id = "temp.windows", Name = "Windows temporary files",
                Description = "Temporary files in C:\\Windows\\Temp older than 24 hours.",
                Roots = [Path.Combine(Windows, "Temp")], MinAge = TimeSpan.FromHours(24), RequiresAdmin = true,
            },
            new()
            {
                Id = "thumbnails", Name = "Thumbnail cache",
                Description = "Cached previews of pictures and videos. Explorer rebuilds them as you browse (files in use are skipped).",
                Roots = [Path.Combine(Local, @"Microsoft\Windows\Explorer")], Pattern = "thumbcache_*.db",
            },
            new()
            {
                Id = "crashdumps", Name = "Crash dumps and error reports",
                Description = "Memory dumps and Windows Error Reporting archives from past crashes. Keep them if you are currently troubleshooting a crash with support.",
                Roots = [Path.Combine(Local, "CrashDumps"), Path.Combine(Local, @"Microsoft\Windows\WER"), Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue")],
            },
            new()
            {
                Id = "minidump", Name = "System minidumps",
                Description = "Blue-screen minidumps in C:\\Windows\\Minidump. Useful only while diagnosing BSODs.",
                Roots = [Path.Combine(Windows, "Minidump")], RequiresAdmin = true, Safety = CleanupSafety.Caution, SelectedByDefault = false,
            },
            new()
            {
                Id = "wucache", Name = "Windows Update download cache",
                Description = "Update packages already installed. Only files older than 10 days are removed; files in use by an ongoing update are skipped.",
                Roots = [Path.Combine(Windows, @"SoftwareDistribution\Download")], MinAge = TimeSpan.FromDays(10), RequiresAdmin = true,
            },
            new()
            {
                Id = "shadercache", Name = "DirectX shader cache",
                Description = "Compiled shaders. Windows and games rebuild them automatically.",
                Note = L.T("After cleaning, games may stutter briefly the first time they recompile shaders. Clean it only if it is large or you suspect corruption."),
                Roots = [Path.Combine(Local, "D3DSCache")], Safety = CleanupSafety.Caution, SelectedByDefault = false,
            },
            new()
            {
                Id = "nvcache", Name = "NVIDIA / AMD shader caches",
                Description = "GPU driver shader caches.",
                Note = L.T("Same trade-off as the DirectX cache: first launch of each game recompiles shaders."),
                Roots = [Path.Combine(Local, @"NVIDIA\DXCache"), Path.Combine(Local, @"NVIDIA\GLCache"), Path.Combine(Local, @"AMD\DxCache"), Path.Combine(Local, @"AMD\DxcCache"), Path.Combine(Local, @"AMD\VkCache")],
                Safety = CleanupSafety.Caution, SelectedByDefault = false,
            },
            new()
            {
                Id = "recyclebin", Name = "Recycle Bin",
                Description = "Files you already deleted. Emptying the Recycle Bin is permanent.",
                Safety = CleanupSafety.Caution, IsRecycleBin = true, SelectedByDefault = false,
            },
        };
        list.AddRange(BrowserCaches());
        return list;
    }

    /// <summary>Only the HTTP/code/GPU caches — never cookies, history, passwords or extensions.</summary>
    private static IEnumerable<CleanupCategory> BrowserCaches()
    {
        var chromium = new (string Name, string Dir, string Process)[]
        {
            ("Google Chrome", @"Google\Chrome\User Data", "chrome"),
            ("Microsoft Edge", @"Microsoft\Edge\User Data", "msedge"),
            ("Brave", @"BraveSoftware\Brave-Browser\User Data", "brave"),
            ("Vivaldi", @"Vivaldi\User Data", "vivaldi"),
        };
        foreach (var (name, dir, process) in chromium)
        {
            var root = Path.GetFullPath(Path.Combine(Local, dir));
            if (!Directory.Exists(root)) continue;
            var profiles = new[] { root }.Concat(SafeDirs(root).Where(d => Path.GetFileName(d) is "Default" || Path.GetFileName(d).StartsWith("Profile ", StringComparison.Ordinal)));
            var roots = profiles.SelectMany(p => new[] { Path.Combine(p, @"Cache\Cache_Data"), Path.Combine(p, "Code Cache"), Path.Combine(p, "GPUCache") })
                .Concat([Path.Combine(root, "ShaderCache"), Path.Combine(root, "GrShaderCache")])
                .Where(Directory.Exists).ToList();
            if (roots.Count == 0) continue;
            yield return new CleanupCategory
            {
                Id = "browser." + process, Name = L.F("{0} cache", name),
                Description = "Cached web content. Cookies, history, saved passwords and sessions are not touched.",
                Note = Process.GetProcessesByName(process).Length > 0 ? L.F("{0} is running: files in use will be skipped. Close it for a complete clean.", name) : null,
                Roots = roots,
            };
        }

        var ff = Path.Combine(Local, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(ff))
        {
            var roots = SafeDirs(ff).Select(p => Path.Combine(p, "cache2")).Where(Directory.Exists).ToList();
            if (roots.Count > 0)
                yield return new CleanupCategory
                {
                    Id = "browser.firefox", Name = "Mozilla Firefox cache",
                    Description = "Cached web content. Cookies, history and logins are stored elsewhere and are not touched.",
                    Note = Process.GetProcessesByName("firefox").Length > 0 ? L.T("Firefox is running: files in use will be skipped.") : null,
                    Roots = roots,
                };
        }
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); }
        catch { return []; }
    }

    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        ReturnSpecialDirectories = false,
    };

    public static Task ScanAsync(IEnumerable<CleanupCategory> categories, IProgress<string>? progress = null, CancellationToken ct = default) => Task.Run(() =>
    {
        foreach (var c in categories)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(L.F("Scanning {0}…", c.Name));
            c.Files.Clear();
            c.Bytes = 0;
            c.FileCount = 0;
            if (!c.Available) continue;

            if (c.IsRecycleBin)
            {
                var info = new Native.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.SHQUERYRBINFO>() };
                if (Native.SHQueryRecycleBin(null, ref info) == 0)
                {
                    c.Bytes = info.i64Size;
                    c.FileCount = (int)info.i64NumItems;
                }
                continue;
            }

            var cutoff = DateTime.UtcNow - c.MinAge;
            foreach (var root in c.Roots.Where(Directory.Exists))
            {
                if (IsReparsePoint(root)) continue;
                try
                {
                    foreach (var file in new DirectoryInfo(root).EnumerateFiles(c.Pattern, Enumeration))
                    {
                        if (c.MinAge > TimeSpan.Zero && file.LastWriteTimeUtc > cutoff) continue;
                        c.Files.Add(file.FullName);
                        c.Bytes += file.Length;
                        c.FileCount++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Scan of {root} failed: {ex.Message}");
                }
            }
        }
    }, ct);

    private static bool IsReparsePoint(string path)
    {
        try { return new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch { return true; }
    }

    /// <summary>Deletes the files found by the last scan of each category. Locked files are skipped, never forced.</summary>
    public static Task<List<CleanupResult>> CleanAsync(IEnumerable<CleanupCategory> categories, IProgress<string>? progress = null, CancellationToken ct = default) => Task.Run(() =>
    {
        var results = new List<CleanupResult>();
        foreach (var c in categories.Where(c => c.Available))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(L.F("Cleaning {0}…", c.Name));
            if (c.IsRecycleBin)
            {
                var hr = Native.SHEmptyRecycleBin(0, null, Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);
                results.Add(new CleanupResult(c.Id, c.Name, hr == 0 ? c.Bytes : 0, hr == 0 ? c.FileCount : 0, hr == 0 ? 0 : c.FileCount));
                continue;
            }

            long freed = 0;
            int deleted = 0, skipped = 0;
            foreach (var file in c.Files)
            {
                // Defence in depth: only delete files located under the category's own roots.
                if (!c.Roots.Any(r => file.StartsWith(Path.TrimEndingDirectorySeparator(r) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) { skipped++; continue; }
                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists) continue;
                    var size = info.Length;
                    if (info.IsReadOnly) info.IsReadOnly = false;
                    info.Delete();
                    freed += size;
                    deleted++;
                }
                catch
                {
                    skipped++; // in use or access denied
                }
            }
            foreach (var root in c.Roots.Where(Directory.Exists)) RemoveEmptyDirectories(root);
            results.Add(new CleanupResult(c.Id, c.Name, freed, deleted, skipped));
            Log.Audit($"Cleanup {c.Id}: deleted {deleted} files ({Format.Bytes(freed)}), skipped {skipped}");
        }

        var history = JsonStore.Load<List<CleanupHistoryEntry>>(AppPaths.CleanupHistoryFile) ?? [];
        history.Insert(0, new CleanupHistoryEntry
        {
            Time = DateTime.Now,
            FreedBytes = results.Sum(r => r.FreedBytes),
            Categories = results.Select(r => $"{r.Name}: {Format.Bytes(r.FreedBytes)}").ToList(),
            Skipped = results.Sum(r => r.Skipped),
        });
        JsonStore.Save(AppPaths.CleanupHistoryFile, history.Take(200).ToList());
        return results;
    }, ct);

    private static void RemoveEmptyDirectories(string root)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", Enumeration).OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                }
                catch { /* in use */ }
            }
        }
        catch { /* root vanished */ }
    }

    public static List<CleanupHistoryEntry> History() => JsonStore.Load<List<CleanupHistoryEntry>>(AppPaths.CleanupHistoryFile) ?? [];
}
