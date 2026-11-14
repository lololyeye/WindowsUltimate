using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Security;

public enum SignatureState { Valid, Unsigned, Untrusted, Tampered, Unknown }

public sealed record Indicator(string Text, int Weight, ThreatCategory Category);

public sealed class FileReport
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public DateTime Created { get; init; }
    public DateTime Modified { get; init; }
    public string Sha256 { get; set; } = "";
    public SignatureState Signature { get; init; }
    public string Signer { get; init; } = "";
    public bool FromInternet { get; init; }
    public string Company { get; init; } = "";
    public List<Indicator> Indicators { get; } = [];
    public int Score => Indicators.Sum(i => i.Weight);
    public bool IsSigned => Signature == SignatureState.Valid;

    public ThreatSeverity Severity => Score switch { >= 7 => ThreatSeverity.High, >= 4 => ThreatSeverity.Medium, >= 2 => ThreatSeverity.Low, _ => ThreatSeverity.Info };
    public ThreatCategory MainCategory => Indicators.OrderByDescending(i => i.Weight).FirstOrDefault()?.Category ?? ThreatCategory.SuspiciousExecutable;

    public string SignatureText => Signature switch
    {
        SignatureState.Valid => L.F("Signed by {0}", Signer.Length > 0 ? Signer : "?"),
        SignatureState.Unsigned => L.T("Not signed"),
        SignatureState.Untrusted => L.F("Signature not trusted ({0})", Signer),
        SignatureState.Tampered => L.T("Signature broken: the file was modified after signing"),
        _ => L.T("Signature could not be checked"),
    };
}

/// <summary>
/// Static, read-only inspection of one file: Authenticode signature (WinVerifyTrust, offline), SHA-256, origin
/// (Mark of the Web), location and — for scripts — known malicious patterns. Files are never executed or opened
/// for writing. The result is a set of indicators, not a verdict: Microsoft Defender gives the verdict.
/// </summary>
public static class FileInspector
{
    public static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".scr", ".com", ".pif", ".cpl", ".msi", ".sys" };

    public static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".bat", ".cmd" };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".jpg", ".jpeg", ".png", ".gif", ".txt", ".mp3", ".mp4", ".zip", ".rar" };

    /// <summary>Windows binaries that only ever live in the Windows folder; the same name elsewhere is a classic disguise.</summary>
    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost.exe", "lsass.exe", "csrss.exe", "winlogon.exe", "services.exe", "smss.exe", "wininit.exe", "spoolsv.exe",
        "dllhost.exe", "conhost.exe", "taskhostw.exe", "rundll32.exe", "lsm.exe", "dwm.exe", "ctfmon.exe", "sihost.exe", "explorer.exe",
    };

    private static readonly (Regex Pattern, string Text, int Weight, ThreatCategory Category)[] ScriptPatterns =
    [
        (new(@"-e(nc(odedcommand)?)?\s+[A-Za-z0-9+/=]{60,}", RegexOptions.IgnoreCase), "Runs an encoded (hidden) PowerShell command", 3, ThreatCategory.SuspiciousScript),
        (new(@"FromBase64String|\[Convert\]::FromBase64", RegexOptions.IgnoreCase), "Decodes Base64 data at run time", 1, ThreatCategory.SuspiciousScript),
        (new(@"\bIEX\b|Invoke-Expression", RegexOptions.IgnoreCase), "Executes code built at run time (Invoke-Expression)", 1, ThreatCategory.SuspiciousScript),
        (new(@"DownloadString|DownloadFile|DownloadData|Invoke-WebRequest|\biwr\b|Net\.WebClient|Start-BitsTransfer|bitsadmin\s+/transfer|certutil[^\n]*-urlcache|MSXML2\.XMLHTTP|WinHttp\.WinHttpRequest", RegexOptions.IgnoreCase), "Downloads content from the internet", 1, ThreatCategory.SuspiciousScript),
        (new(@"-w(indowstyle)?\s+h(idden)?\b", RegexOptions.IgnoreCase), "Starts a hidden window", 1, ThreatCategory.SuspiciousScript),
        (new(@"(Set|Add)-MpPreference[^\n]*(-Disable|-Exclusion)", RegexOptions.IgnoreCase), "Tries to switch off Microsoft Defender or add exclusions", 5, ThreatCategory.SuspiciousScript),
        (new(@"Login Data|Web Data|\\Cookies\b|Local State|logins\.json|key4\.db|cookies\.sqlite|wallet\.dat|\\Exodus\\|\\Electrum\\|Local Storage\\leveldb|Telegram Desktop\\tdata", RegexOptions.IgnoreCase), "References browser password/cookie stores, crypto wallets or session files", 3, ThreatCategory.InfoStealer),
        (new(@"discord(app)?\.com/api/webhooks|api\.telegram\.org/bot|UploadFile|UploadString|Invoke-RestMethod[^\n]*-Method\s+Post", RegexOptions.IgnoreCase), "Sends data to a webhook, a Telegram bot or another server", 2, ThreatCategory.InfoStealer),
        (new(@"GetAsyncKeyState|SetWindowsHookEx|GetKeyboardState", RegexOptions.IgnoreCase), "Reads keystrokes (keylogging API)", 4, ThreatCategory.Keylogger),
        (new(@"Net\.Sockets\.TCPClient[\s\S]{0,400}GetStream|\bnc(\.exe)?\s+-e\b", RegexOptions.IgnoreCase), "Opens a remote shell connection", 5, ThreatCategory.Backdoor),
        (new(@"CurrentVersion\\Run|schtasks(\.exe)?\s+/create|Register-ScheduledTask|New-ScheduledTask|\\Start Menu\\Programs\\Startup", RegexOptions.IgnoreCase), "Installs itself to start automatically", 1, ThreatCategory.Persistence),
        (new(@"mshta(\.exe)?\s+[""']?(https?|javascript|vbscript):|regsvr32[^\n]*/i:https?|rundll32[^\n]*javascript:", RegexOptions.IgnoreCase), "Abuses a Windows tool to run remote code", 4, ThreatCategory.SuspiciousScript),
        (new(@"vssadmin[^\n]*delete\s+shadows|wbadmin[^\n]*delete|bcdedit[^\n]*recoveryenabled\s+no", RegexOptions.IgnoreCase), "Deletes backups or shadow copies (ransomware behaviour)", 5, ThreatCategory.SuspiciousScript),
    ];

    public static string? UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string Temp = Path.GetFullPath(Path.GetTempPath());
    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static readonly string Public = Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public";

    public static bool IsUnder(string path, string root) =>
        root.Length > 0 && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public static bool InWindowsFolder(string path) => IsUnder(path, WindowsDir);

    /// <summary>Locations any program can write to without administrator rights — where malware usually drops files.</summary>
    public static bool InUserWritableLocation(string path) =>
        IsUnder(path, Temp) || IsUnder(path, Path.Combine(WindowsDir, "Temp")) || IsUnder(path, AppData) || IsUnder(path, LocalAppData) || IsUnder(path, Public)
        || IsUnder(path, Path.Combine(UserProfile ?? "", "Downloads")) || DirectlyIn(path, ProgramData);

    /// <summary>File placed directly in a root such as %APPDATA% (legitimate programs use a vendor sub-folder).</summary>
    public static bool DirectlyIn(string path, string root) =>
        string.Equals(Path.GetDirectoryName(path)?.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static FileReport Inspect(string path, bool hash = true)
    {
        var info = new FileInfo(path);
        var ext = info.Extension;
        var isExe = ExecutableExtensions.Contains(ext);
        var (sig, signer) = isExe || ext is ".ps1" or ".psm1" ? Verify(path) : (SignatureState.Unknown, "");
        string company = "";
        if (isExe)
        {
            try { company = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).CompanyName ?? ""; } catch { }
        }
        var report = new FileReport
        {
            Path = path,
            Size = info.Exists ? info.Length : 0,
            Created = info.Exists ? info.CreationTime : default,
            Modified = info.Exists ? info.LastWriteTime : default,
            Signature = sig,
            Signer = signer,
            FromInternet = ZoneId(path) >= 3,
            Company = company.Trim(),
        };
        if (hash) report.Sha256 = Sha256(path) ?? "";

        var name = info.Name;
        var inWindows = InWindowsFolder(path);

        if (isExe && !inWindows)
        {
            if (SystemNames.Contains(name))
                report.Indicators.Add(new(L.F("Uses the name of a Windows system file ({0}) outside the Windows folder", name), 6, ThreatCategory.Masquerading));
            if (sig == SignatureState.Tampered)
                report.Indicators.Add(new(L.T("Digital signature is broken (file modified after signing)"), 4, ThreatCategory.SuspiciousExecutable));
            else if (sig == SignatureState.Unsigned)
                report.Indicators.Add(new(L.T("Not digitally signed (common for small or open-source tools — not suspicious on its own)"), 1, ThreatCategory.SuspiciousExecutable));
            if (IsUnder(path, Temp) || IsUnder(path, Path.Combine(WindowsDir, "Temp")))
                report.Indicators.Add(new(L.T("Program stored in a temporary folder"), 2, ThreatCategory.SuspiciousExecutable));
            else if (DirectlyIn(path, AppData) || DirectlyIn(path, LocalAppData) || DirectlyIn(path, ProgramData) || IsUnder(path, Public))
                report.Indicators.Add(new(L.T("Program placed directly in a shared or application-data root folder instead of an application folder"), 2, ThreatCategory.SuspiciousExecutable));
            if (report.FromInternet && !report.IsSigned && (IsUnder(path, AppData) || IsUnder(path, LocalAppData)))
                report.Indicators.Add(new(L.T("Downloaded from the internet and unsigned"), 1, ThreatCategory.SuspiciousExecutable));
        }

        // invoice.pdf.exe, photo.jpg.scr …
        var inner = Path.GetExtension(Path.GetFileNameWithoutExtension(name));
        if ((isExe || ScriptExtensions.Contains(ext)) && DocumentExtensions.Contains(inner))
            report.Indicators.Add(new(L.F("Double extension ({0}{1}) — made to look like a document", inner, ext), 4, ThreatCategory.Masquerading));
        if (!isExe && DocumentExtensions.Contains(ext) && HasExecutableHeader(path))
            report.Indicators.Add(new(L.T("Contains a Windows program although its extension says document or image"), 4, ThreatCategory.Masquerading));
        if (isExe && !inWindows && (info.Attributes & FileAttributes.Hidden) != 0)
            report.Indicators.Add(new(L.T("Program file is hidden"), 1, ThreatCategory.SuspiciousExecutable));

        if (ScriptExtensions.Contains(ext) && info.Exists && info.Length < 4_000_000)
            InspectScript(path, report);

        return report;
    }

    private static void InspectScript(string path, FileReport report)
    {
        string text;
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[Math.Min(s.Length, 512 * 1024)];
            var n = s.Read(buffer, 0, buffer.Length);
            text = Encoding.UTF8.GetString(buffer, 0, n);
            if (text.Count(c => c == '\0') > n / 4) text = Encoding.Unicode.GetString(buffer, 0, n); // UTF-16 scripts
        }
        catch { return; }

        var downloads = false;
        foreach (var (pattern, label, weight, category) in ScriptPatterns)
        {
            if (!pattern.IsMatch(text)) continue;
            report.Indicators.Add(new(L.T(label), weight, category));
            downloads |= label.StartsWith("Downloads", StringComparison.Ordinal);
        }
        // Download + execute in the same script is the dropper pattern.
        if (downloads && report.Indicators.Any(i => i.Text == L.T("Executes code built at run time (Invoke-Expression)")))
            report.Indicators.Add(new(L.T("Downloads code and runs it immediately"), 2, ThreatCategory.SuspiciousScript));
    }

    private static bool HasExecutableHeader(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return s.ReadByte() == 'M' && s.ReadByte() == 'Z';
        }
        catch { return false; }
    }

    public static string? Sha256(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(s));
        }
        catch { return null; }
    }

    /// <summary>Zone.Identifier alternate stream ("Mark of the Web"): 3 = Internet, 4 = Restricted.</summary>
    public static int ZoneId(string path)
    {
        try
        {
            var ads = path + ":Zone.Identifier";
            if (!File.Exists(ads)) return 0;
            var line = File.ReadLines(ads).FirstOrDefault(l => l.StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase));
            return line is not null && int.TryParse(line[7..].Trim(), out var z) ? z : 0;
        }
        catch { return 0; }
    }

    // ── Authenticode (offline: no revocation or URL retrieval) ──

    public static (SignatureState State, string Signer) Verify(string path)
    {
        if (!File.Exists(path)) return (SignatureState.Unknown, "");
        var signer = Startup.StartupManager.SignerOf(path);
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        var marshalled = false;
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            marshalled = true;
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2, // WTD_UI_NONE
                fdwRevocationChecks = 0,
                dwUnionChoice = 1, // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,
                dwProvFlags = 0x1000 | 0x40, // WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            var hr = WinVerifyTrust(-1, ref action, ref data);
            var state = (uint)hr switch
            {
                0 => SignatureState.Valid,
                0x800B0100 or 0x800B0003 or 0x800B0001 => SignatureState.Unsigned, // no signature / unknown subject form / unknown provider
                0x80096010 => SignatureState.Tampered, // TRUST_E_BAD_DIGEST
                _ => signer.Length > 0 ? SignatureState.Untrusted : SignatureState.Unsigned,
            };
            return (state, signer);
        }
        catch (Exception ex)
        {
            Log.Warn($"WinVerifyTrust failed for {path}: {ex.Message}");
            return (SignatureState.Unknown, signer);
        }
        finally
        {
            if (marshalled) Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public nint pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public nint pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(nint hwnd, ref Guid action, ref WINTRUST_DATA data);
}
