using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Optimization;

namespace WindowsUltimate.Core.Security;

/// <summary>
/// Moves a file into Windows Ultimate's quarantine folder under a random name without extension, so it can no longer
/// be launched from where it was. Nothing is deleted: undoing the change in History moves the file back exactly.
/// </summary>
public sealed class QuarantineFileAction(string path, string? quarantinePath = null) : IChangeAction
{
    public static string Folder => Directory.CreateDirectory(Path.Combine(AppPaths.Root, "quarantine")).FullName;

    private readonly string _qpath = quarantinePath ?? Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".quarantined");

    public string OriginalPath => path;
    public string Key => "quarantine:" + path.ToLowerInvariant();
    public bool RequiresAdmin => !FileInspector.IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                                 && !FileInspector.IsUnder(path, Path.GetFullPath(Path.GetTempPath()));
    public bool IsReversible => true;
    public string Describe() => L.F("File {0}", path);
    public string DescribeDesired() => L.T("In quarantine");
    public string DescribeCurrent() => File.Exists(path) ? L.T("In place") : File.Exists(_qpath) ? L.T("In quarantine") : L.T("Missing");
    public bool IsInDesiredState() => !File.Exists(path) && File.Exists(_qpath);

    public ActionSnapshot Capture() => new()
    {
        ActionKey = Key,
        Kind = "quarantine",
        Data =
        {
            ["path"] = path,
            ["qpath"] = _qpath,
            ["sha256"] = FileInspector.Sha256(path) ?? "",
            ["attributes"] = File.Exists(path) ? ((int)File.GetAttributes(path)).ToString() : "",
        },
    };

    public void Apply()
    {
        if (FileInspector.InWindowsFolder(path))
            throw new InvalidOperationException(L.T("Files inside the Windows folder are never quarantined by Windows Ultimate. Use Microsoft Defender."));
        if (!File.Exists(path)) throw new FileNotFoundException(L.T("The file no longer exists."), path);
        File.SetAttributes(path, FileAttributes.Normal);
        File.Move(path, _qpath);
        Log.Audit($"Quarantined {path} -> {_qpath}");
    }

    public void Restore(ActionSnapshot s)
    {
        var original = s.Data.GetValueOrDefault("path") ?? path;
        var stored = s.Data.GetValueOrDefault("qpath") ?? _qpath;
        if (!File.Exists(stored)) throw new FileNotFoundException(L.T("The quarantined copy is missing."), stored);
        if (File.Exists(original)) throw new IOException(L.T("A file already exists at the original location; it was not overwritten."));
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.Move(stored, original);
        if (int.TryParse(s.Data.GetValueOrDefault("attributes"), out var attr)) File.SetAttributes(original, (FileAttributes)attr);
        Log.Audit($"Restored from quarantine {original}");
    }

    public static Tweak CreateTweak(string file, string reason) => new()
    {
        Id = "quarantine:" + file.ToLowerInvariant(),
        Name = L.F("Quarantine {0}", Path.GetFileName(file)),
        Category = TweakCategory.Security,
        Description = reason,
        WhatItChanges = L.F("Moves {0} into the Windows Ultimate quarantine folder under a random name, so it can no longer run from its location.", file),
        WhyItHelps = L.T("Neutralises a suspicious file while keeping it intact, in case it turns out to be legitimate."),
        ExpectedImpact = L.T("A program that depends on this file stops working until it is restored."),
        Disadvantages = L.T("If the file belongs to software you use, that software will report an error. Restore it from History in one click."),
        TechnicalDetails = $"File.Move → {Folder}",
        Source = L.T("Windows Ultimate quarantine (reversible move, nothing is deleted)"),
        Risk = RiskLevel.Moderate,
        Benefit = BenefitLevel.Medium,
        Actions = [new QuarantineFileAction(file)],
    };
}
