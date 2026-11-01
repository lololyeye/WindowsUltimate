using WindowsUltimate.Core.Infrastructure;
namespace WindowsUltimate.Core.Debloat;

public sealed record CatalogEntry(
    string Pattern,
    string FriendlyName,
    DebloatCategory Category,
    string WhatItDoes,
    string Consequences)
{
    public string FriendlyName { get => L.T(field); init; } = FriendlyName;
    public string WhatItDoes { get => L.T(field); init; } = WhatItDoes;
    public string Consequences { get => L.T(field); init; } = Consequences;
}

/// <summary>
/// Classification of preinstalled Windows 11 packages. Anything not listed here is shown as "Unclassified"
/// with no recommendation; frameworks and system packages are always protected.
/// </summary>
public static class DebloatCatalog
{
    private const string Restore = "Reinstall it from the Microsoft Store at any time.";

    public static readonly IReadOnlyList<CatalogEntry> Entries =
    [
        // SAFE TO REMOVE — promotional or redundant, no dependants
        new("Microsoft.BingNews", "Microsoft News", DebloatCategory.SafeToRemove, "News reader app.", "None. " + Restore),
        new("Microsoft.BingWeather", "MSN Weather", DebloatCategory.SafeToRemove, "Weather app.", "The Weather app disappears (the Widgets board can still show weather). " + Restore),
        new("Microsoft.BingSearch", "Bing Search", DebloatCategory.SafeToRemove, "Bing web search integration app.", "None for local search. " + Restore),
        new("Microsoft.Getstarted", "Tips", DebloatCategory.SafeToRemove, "Windows tips and introduction app.", "None. " + Restore),
        new("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", DebloatCategory.SafeToRemove, "Card games with ads.", "None. " + Restore),
        new("Microsoft.People", "People", DebloatCategory.SafeToRemove, "Legacy contacts app.", "None on Windows 11. " + Restore),
        new("Microsoft.WindowsMaps", "Maps", DebloatCategory.SafeToRemove, "Offline maps app (retired by Microsoft).", "None. " + Restore),
        new("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office) hub", DebloatCategory.SafeToRemove, "Launcher that promotes Microsoft 365.", "Installed Office apps keep working. " + Restore),
        new("Microsoft.549981C3F5F10", "Cortana", DebloatCategory.SafeToRemove, "Retired voice assistant.", "None. " + Restore),
        new("Clipchamp.Clipchamp", "Clipchamp", DebloatCategory.SafeToRemove, "Online video editor.", "None unless you edit videos with it. " + Restore),
        new("Microsoft.PowerAutomateDesktop", "Power Automate", DebloatCategory.SafeToRemove, "Desktop automation tool.", "Your flows stop working if you created any. " + Restore),
        new("Microsoft.Todos", "Microsoft To Do", DebloatCategory.SafeToRemove, "Task list app.", "Tasks stay in your Microsoft account. " + Restore),
        new("microsoft.windowscommunicationsapps", "Mail and Calendar (legacy)", DebloatCategory.SafeToRemove, "Retired Mail and Calendar apps, replaced by the new Outlook.", "None — Microsoft has ended support. " + Restore),
        new("Microsoft.Windows.DevHome", "Dev Home", DebloatCategory.SafeToRemove, "Developer dashboard (retired).", "None. " + Restore),
        new("Microsoft.MixedReality.Portal", "Mixed Reality Portal", DebloatCategory.SafeToRemove, "Windows Mixed Reality headset app (deprecated).", "WMR headsets won't work. " + Restore),
        new("Microsoft.Microsoft3DViewer", "3D Viewer", DebloatCategory.SafeToRemove, "3D model viewer.", "None. " + Restore),
        new("Microsoft.SkypeApp", "Skype", DebloatCategory.SafeToRemove, "Retired calling app.", "None. " + Restore),
        new("SpotifyAB.SpotifyMusic", "Spotify (pre-installed)", DebloatCategory.SafeToRemove, "Promoted third-party app.", "None unless you use it. " + Restore),
        new("Disney.", "Disney+ (pre-installed)", DebloatCategory.SafeToRemove, "Promoted third-party app.", "None unless you use it. " + Restore),
        new("BytedancePte.Ltd.TikTok", "TikTok (pre-installed)", DebloatCategory.SafeToRemove, "Promoted third-party app.", "None unless you use it. " + Restore),
        new("Facebook.", "Facebook/Instagram (pre-installed)", DebloatCategory.SafeToRemove, "Promoted third-party app.", "None unless you use it. " + Restore),
        new("king.com.", "King games (Candy Crush…)", DebloatCategory.SafeToRemove, "Promoted games.", "None unless you play them. " + Restore),
        new("AmazonVideo.PrimeVideo", "Prime Video (pre-installed)", DebloatCategory.SafeToRemove, "Promoted third-party app.", "None unless you use it. " + Restore),

        // OPTIONAL — useful to some people
        new("Microsoft.ZuneMusic", "Media Player", DebloatCategory.Optional, "Windows' music and video player.", "Audio/video files open in another player you choose. " + Restore),
        new("Microsoft.ZuneVideo", "Films & TV", DebloatCategory.Optional, "Video player and Microsoft movie store.", "Purchased movies need the app to play. " + Restore),
        new("Microsoft.GetHelp", "Get Help", DebloatCategory.Optional, "Microsoft support and troubleshooter app.", "Built-in troubleshooters that open in Get Help won't start. " + Restore),
        new("Microsoft.WindowsFeedbackHub", "Feedback Hub", DebloatCategory.Optional, "Sends feedback to Microsoft.", "Windows Insiders need it to report issues. " + Restore),
        new("Microsoft.YourPhone", "Phone Link", DebloatCategory.Optional, "Connects your Android phone/iPhone.", "Phone notifications, texts and calls on the PC stop. " + Restore),
        new("MicrosoftTeams", "Microsoft Teams (personal)", DebloatCategory.Optional, "Chat and calls.", "None unless you use it. " + Restore),
        new("MSTeams", "Microsoft Teams", DebloatCategory.Optional, "Chat and calls.", "None unless you use it. " + Restore),
        new("Microsoft.OutlookForWindows", "Outlook (new)", DebloatCategory.Optional, "Mail and calendar app.", "None unless you use it for mail. " + Restore),
        new("Microsoft.MicrosoftStickyNotes", "Sticky Notes", DebloatCategory.Optional, "Desktop notes.", "Notes synced to your account remain online. " + Restore),
        new("Microsoft.WindowsSoundRecorder", "Sound Recorder", DebloatCategory.Optional, "Simple audio recorder.", Restore),
        new("Microsoft.WindowsAlarms", "Clock", DebloatCategory.Optional, "Alarms, timers and Focus sessions.", "Alarms and Focus sessions stop working. " + Restore),
        new("Microsoft.Copilot", "Copilot", DebloatCategory.Optional, "AI assistant app.", "The Copilot key/app opens nothing. " + Restore),
        new("MicrosoftCorporationII.QuickAssist", "Quick Assist", DebloatCategory.Optional, "Remote help tool.", "You cannot receive remote help through Quick Assist. " + Restore),
        new("Microsoft.WindowsCamera", "Camera", DebloatCategory.Optional, "Webcam app.", "Webcam keeps working in other apps. " + Restore),
        new("Microsoft.Windows.Photos", "Photos", DebloatCategory.Optional, "Default image viewer.", "Pick another image viewer. " + Restore),
        new("Microsoft.MicrosoftJournal", "Journal", DebloatCategory.Optional, "Pen note-taking app.", Restore),
        new("Microsoft.XboxSpeechToTextOverlay", "Xbox speech-to-text overlay", DebloatCategory.Optional, "Accessibility captions for Xbox party chat.", Restore),
        new("Microsoft.GamingApp", "Xbox app", DebloatCategory.Optional, "Xbox / PC Game Pass launcher.", "PC Game Pass games cannot be installed or updated. " + Restore),

        // ADVANCED — removal affects gaming or other features
        new("Microsoft.XboxGamingOverlay", "Xbox Game Bar", DebloatCategory.Advanced, "Win+G overlay: captures, performance widget, Xbox social.", "Win+G, game clips and Game Bar widgets stop working. Some games show an error when pressing the capture key. " + Restore),
        new("Microsoft.Xbox.TCUI", "Xbox TCUI", DebloatCategory.Advanced, "Xbox Live UI component used by games.", "Some Xbox/Game Pass games fail to show sign-in or social dialogs. " + Restore),
        new("Microsoft.XboxGameOverlay", "Xbox Game Overlay", DebloatCategory.Advanced, "Legacy overlay component used by some games.", "Some Xbox games may show errors. " + Restore),
        new("Microsoft.WindowsTerminal", "Windows Terminal", DebloatCategory.Advanced, "Default terminal host.", "Command-line tools fall back to the classic console. " + Restore),
        new("Microsoft.ScreenSketch", "Snipping Tool", DebloatCategory.Advanced, "Screenshots (Win+Shift+S).", "Win+Shift+S stops working. " + Restore),
        new("Microsoft.Paint", "Paint", DebloatCategory.Advanced, "Image editor.", Restore),
        new("Microsoft.WindowsNotepad", "Notepad", DebloatCategory.Advanced, "Text editor.", "Text files need another editor. " + Restore),
        new("Microsoft.WindowsCalculator", "Calculator", DebloatCategory.Advanced, "Calculator.", Restore),

        // DO NOT REMOVE — dependencies of the Store, security, updates or other apps
        new("Microsoft.WindowsStore", "Microsoft Store", DebloatCategory.DoNotRemove, "App store and update channel for Store apps.", "Apps can no longer be installed or updated; very hard to restore."),
        new("Microsoft.StorePurchaseApp", "Store Purchase App", DebloatCategory.DoNotRemove, "Store purchase flow.", "Store purchases break."),
        new("Microsoft.DesktopAppInstaller", "App Installer (winget)", DebloatCategory.DoNotRemove, "Installs MSIX packages and provides winget.", "MSIX installs and winget stop working."),
        new("Microsoft.SecHealthUI", "Windows Security", DebloatCategory.DoNotRemove, "Windows Security dashboard.", "You lose the security dashboard; protection status cannot be checked."),
        new("Microsoft.XboxIdentityProvider", "Xbox Identity Provider", DebloatCategory.DoNotRemove, "Xbox account sign-in used by many PC games.", "Games that use Xbox sign-in (incl. many Steam titles) fail to sign in."),
        new("Microsoft.Services.Store.Engagement", "Store Engagement", DebloatCategory.DoNotRemove, "Store framework used by apps.", "Dependent apps can break."),
        new("Microsoft.VCLibs", "Visual C++ runtime (UWP)", DebloatCategory.DoNotRemove, "Runtime framework.", "Many apps stop launching."),
        new("Microsoft.UI.Xaml", "WinUI framework", DebloatCategory.DoNotRemove, "UI framework.", "Many apps stop launching."),
        new("Microsoft.NET.Native", ".NET Native runtime", DebloatCategory.DoNotRemove, "Runtime framework.", "Many apps stop launching."),
        new("Microsoft.WindowsAppRuntime", "Windows App SDK runtime", DebloatCategory.DoNotRemove, "Runtime framework.", "Apps built on it stop launching."),
        new("Microsoft.WebMediaExtensions", "Web Media Extensions", DebloatCategory.DoNotRemove, "Codecs (OGG, Vorbis, Theora).", "Media playback in apps breaks."),
        new("Microsoft.HEIFImageExtension", "HEIF Image Extension", DebloatCategory.DoNotRemove, "Codec.", "HEIC photos cannot be opened."),
        new("Microsoft.HEVCVideoExtension", "HEVC Video Extension", DebloatCategory.DoNotRemove, "Codec.", "HEVC videos cannot be played."),
        new("Microsoft.VP9VideoExtensions", "VP9 Video Extension", DebloatCategory.DoNotRemove, "Codec.", "VP9 videos cannot be played."),
        new("Microsoft.WebpImageExtension", "WebP Image Extension", DebloatCategory.DoNotRemove, "Codec.", "WebP images cannot be opened."),
        new("Microsoft.AV1VideoExtension", "AV1 Video Extension", DebloatCategory.DoNotRemove, "Codec.", "AV1 videos cannot be played."),
        new("Microsoft.RawImageExtension", "Raw Image Extension", DebloatCategory.DoNotRemove, "Codec.", "Camera RAW files cannot be previewed."),
        new("Microsoft.MicrosoftEdge", "Microsoft Edge", DebloatCategory.DoNotRemove, "Browser and WebView host.", "Windows components and apps that rely on Edge/WebView2 break."),
        new("Microsoft.Win32WebViewHost", "WebView host", DebloatCategory.DoNotRemove, "Web content host for apps.", "Apps embedding web content break."),
        new("Microsoft.ApplicationCompatibilityEnhancements", "App compatibility enhancements", DebloatCategory.DoNotRemove, "Compatibility layer.", "Some apps may break."),
        new("Microsoft.Winget.Source", "winget source", DebloatCategory.DoNotRemove, "Package catalog for winget.", "winget stops working."),
        new("Microsoft.WidgetsPlatformRuntime", "Widgets runtime", DebloatCategory.DoNotRemove, "Runtime for widgets (use the Widgets policy instead).", "Widgets break in an unsupported way."),
        new("MicrosoftWindows.Client.WebExperience", "Windows Web Experience Pack", DebloatCategory.DoNotRemove, "Provides Widgets and other shell web content. Use the 'Disable the Widgets board' option instead.", "Shell features may break."),
    ];

    public static CatalogEntry? Match(string packageName) =>
        Entries.FirstOrDefault(e => e.Pattern.EndsWith('.')
            ? packageName.StartsWith(e.Pattern, StringComparison.OrdinalIgnoreCase)
            : packageName.Equals(e.Pattern, StringComparison.OrdinalIgnoreCase)
              || packageName.StartsWith(e.Pattern + ".", StringComparison.OrdinalIgnoreCase) && e.Category == DebloatCategory.DoNotRemove);
}
