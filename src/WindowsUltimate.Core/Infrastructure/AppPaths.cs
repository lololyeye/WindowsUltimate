namespace WindowsUltimate.Core.Infrastructure;

/// <summary>Locations of everything the application persists. All data stays in the user's local profile.</summary>
public static class AppPaths
{
    private static string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsUltimate");

    public static string Root => Ensure(_root);
    public static string Journal => Ensure(Path.Combine(_root, "journal"));
    public static string Logs => Ensure(Path.Combine(_root, "logs"));
    public static string Exports => Ensure(Path.Combine(_root, "exports"));
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string ProfilesFile => Path.Combine(Root, "profiles.json");
    public static string ProfileStateFile => Path.Combine(Root, "profile-state.json");
    public static string BenchmarksFile => Path.Combine(Root, "benchmarks.json");
    public static string CleanupHistoryFile => Path.Combine(Root, "cleanup-history.json");

    /// <summary>Used by tests to isolate state.</summary>
    public static void OverrideRoot(string path) => _root = path;

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
