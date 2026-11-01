using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Updates;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string InstallerUrl, string InstallerName, long InstallerBytes, string? Sha256Url, DateTime? Published);

/// <summary>
/// Checks this project's own GitHub Releases for a newer version and installs it on request.
/// The only thing ever downloaded is the release's setup .exe, from github.com over HTTPS, and it is
/// refused unless its SHA-256 matches the checksum file published with the same release.
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>"owner/repo", embedded at build time (MSBuild property UpdateRepository). Empty disables updates.</summary>
    public static string Repository { get; } = Metadata("UpdateRepository");

    public static Version CurrentVersion { get; } = ParseVersion(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()) ?? new Version(0, 0, 0);

    public static bool IsConfigured => Repository.Contains('/');

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no release yet
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (ParseVersion(tag) is not { } version || version <= CurrentVersion) return null;

        string? installerUrl = null, installerName = null, shaUrl = null;
        long size = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
            {
                installerUrl = url;
                installerName = name;
                size = asset.GetProperty("size").GetInt64();
            }
            else if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)) shaUrl = url;
        }
        if (installerUrl is null || installerName is null || !IsTrustedUrl(installerUrl)) return null;

        DateTime? published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTime() : null;
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new UpdateInfo(version, tag, notes.Trim(), installerUrl, installerName, size, shaUrl, published);
    }

    /// <summary>Downloads the installer to a fresh temp folder and verifies its SHA-256. Returns the local path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (update.Sha256Url is null || !IsTrustedUrl(update.Sha256Url))
            throw new InvalidOperationException(L.T("This release has no checksum file, so the installer can't be verified. Download it manually from GitHub."));

        var expected = (await Http.GetStringAsync(update.Sha256Url, ct).ConfigureAwait(false))
            .Split([' ', '\t', '\r', '\n', '*'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        if (expected is null || expected.Length != 64) throw new InvalidOperationException(L.T("The release checksum file is malformed."));

        var dir = Path.Combine(Path.GetTempPath(), "WindowsUltimate-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Path.GetFileName(update.InstallerName));

        using (var response = await Http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.InstallerBytes;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = File.Create(path);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }

        string actual;
        await using (var stream = File.OpenRead(path))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        if (actual != expected)
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
            throw new InvalidOperationException(L.T("The downloaded installer does not match the published checksum and was deleted."));
        }
        Log.Audit($"Update {update.Tag} downloaded and verified (sha256 {actual})");
        return path;
    }

    /// <summary>Starts the verified installer. It closes this app, updates in place and relaunches it.</summary>
    public static void StartInstaller(string path)
    {
        Log.Audit($"Starting update installer {path}");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /NORESTART")
        {
            UseShellExecute = true, // the installer requests elevation itself (UAC prompt)
        });
    }

    public static string ReleasesPage => IsConfigured ? $"https://github.com/{Repository}/releases" : "";

    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }

    private static bool IsTrustedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static string Metadata(string key) =>
        Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value ?? "";

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WindowsUltimate", "1.0"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }
}
