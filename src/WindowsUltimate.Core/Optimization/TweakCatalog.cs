using Microsoft.Win32;
using WindowsUltimate.Core.Hardware;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Optimization;

/// <summary>
/// Every optimization the application can apply. Rules for adding an entry:
/// it must change a documented setting, have a source, be reversible through the journal,
/// state its realistic impact, and never weaken Windows security.
/// </summary>
public static class TweakCatalog
{
    private const string CDM = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string DxPrefs = @"Software\Microsoft\DirectX\UserGpuPreferences";

    public static IReadOnlyList<Tweak> Create() =>
    [
        // ───────────── Gaming ─────────────
        new Tweak
        {
            Id = "gaming.gamemode",
            Name = "Turn on Game Mode",
            Category = TweakCategory.Gaming,
            Description = "Lets Windows prioritise the game you are playing.",
            WhatItChanges = "Enables Settings › Gaming › Game Mode for the current user.",
            WhyItHelps = "While a game runs, Windows Update stops installing drivers and showing restart notifications, and the game gets scheduling priority over background work.",
            ExpectedImpact = "Mostly improves consistency (fewer interruptions) rather than average FPS. Gains are usually within a few percent and most visible on CPUs with few cores.",
            Disadvantages = "None known on current Windows 11 builds. It is on by default.",
            TechnicalDetails = "HKCU\\Software\\Microsoft\\GameBar › AutoGameModeEnabled = 1",
            Source = "Windows Settings › Gaming › Game Mode (documented user setting)",
            Risk = RiskLevel.Safe,
            Benefit = BenefitLevel.Low,
            Actions = [Reg.User(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1)],
            RecommendationCheck = _ => Recommendation.Yes("Windows default and beneficial for every gaming PC."),
        },
        new Tweak
        {
            Id = "gaming.windowed",
            Name = "Optimizations for windowed games",
            Category = TweakCategory.Gaming,
            Description = "Use the modern flip presentation model for DirectX 10/11 games in windowed or borderless mode.",
            WhatItChanges = "Enables Settings › Display › Graphics › Optimizations for windowed games.",
            WhyItHelps = "Legacy 'blt' presentation adds a copy through the desktop compositor. The flip model removes it, lowering latency and enabling Auto HDR and variable refresh rate in windowed games.",
            ExpectedImpact = "Lower input latency in borderless/windowed DX10/DX11 games. No effect on DX12/Vulkan or exclusive fullscreen games.",
            Disadvantages = "A small number of older games may show presentation glitches; disable it per game in Graphics settings if that happens.",
            TechnicalDetails = $"HKCU\\{DxPrefs} › DirectXUserGlobalSettings token SwapEffectUpgradeEnable=1 (other tokens are preserved)",
            Source = "https://devblogs.microsoft.com/directx/optimizations-for-windowed-games-in-windows-11/",
            Risk = RiskLevel.Safe,
            Benefit = BenefitLevel.Medium,
            Actions = [new RegistryTokenAction(RegistryHive.CurrentUser, DxPrefs, "DirectXUserGlobalSettings", "SwapEffectUpgradeEnable", "1")],
            CompatibilityCheck = p => p.Os.IsWindows11 ? Compatibility.Yes : Compatibility.No("Requires Windows 11."),
            RecommendationCheck = _ => Recommendation.Yes("Microsoft-supported latency improvement for windowed and borderless games."),
        },
        new Tweak
        {
            Id = "gaming.vrr",
            Name = "Variable refresh rate for all DirectX 11 games",
            Category = TweakCategory.Gaming,
            Description = "Extends VRR (G-SYNC Compatible / FreeSync) to DX11 games that do not support it natively.",
            WhatItChanges = "Enables Settings › Display › Graphics › Variable refresh rate.",
            WhyItHelps = "On a VRR monitor, the display waits for each frame instead of the frame waiting for the display, removing tearing and reducing stutter when FPS fluctuates.",
            ExpectedImpact = "Smoother frame pacing on VRR monitors. No FPS change. No effect without a VRR-capable display.",
            Disadvantages = "Some monitors flicker at low frame rates with VRR. Turn it off again if you notice brightness flicker.",
            TechnicalDetails = $"HKCU\\{DxPrefs} › DirectXUserGlobalSettings token VRROptimizeEnable=1",
            Source = "https://devblogs.microsoft.com/directx/os-variable-refresh-rate/",
            Risk = RiskLevel.LowRisk,
            Benefit = BenefitLevel.Low,
            Actions = [new RegistryTokenAction(RegistryHive.CurrentUser, DxPrefs, "DirectXUserGlobalSettings", "VRROptimizeEnable", "1")],
            RecommendationCheck = p => p.Displays.Any(d => d.MaxHzAtCurrentResolution > 75)
                ? Recommendation.No("Useful only if your high-refresh monitor supports VRR (G-SYNC/FreeSync) — check its specifications.")
                : Recommendation.No("Your display does not appear to be a high refresh rate VRR display."),
        },
        new Tweak
        {
            Id = "gaming.hags",
            Name = "Hardware-accelerated GPU scheduling",
            Category = TweakCategory.Gaming,
            Description = "Lets the GPU manage its own video memory scheduling.",
            WhatItChanges = "Enables Settings › Display › Graphics › Hardware-accelerated GPU scheduling.",
            WhyItHelps = "Moves part of the GPU work scheduling from the CPU to a dedicated scheduling processor on the GPU, which can reduce latency and CPU overhead. Required for DLSS 3 Frame Generation.",
            ExpectedImpact = "Varies by GPU, driver and game: typically within ±2% FPS. Required on RTX 40/50-series for Frame Generation.",
            Disadvantages = "Some older drivers and capture/overlay software have shown stutter with it enabled. Measure with the benchmark and revert if worse.",
            TechnicalDetails = "HKLM\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers › HwSchMode = 2 (1 = off). Requires a WDDM 2.7+ driver that supports it.",
            Source = "https://devblogs.microsoft.com/directx/hardware-accelerated-gpu-scheduling/",
            Risk = RiskLevel.LowRisk,
            Benefit = BenefitLevel.Low,
            Restart = RestartRequirement.Restart,
            Actions = [Reg.Machine(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2)],
            CompatibilityCheck = p => p.HasDiscreteGpu
                ? Compatibility.Yes
                : Compatibility.No("Needs a discrete GPU whose driver supports GPU scheduling (NVIDIA GTX 10-series+, AMD RX 5000+, Intel Arc)."),
            RecommendationCheck = p => p.PrimaryGpu is { Vendor: HardwareVendor.Nvidia } g && System.Text.RegularExpressions.Regex.IsMatch(g.Name, @"RTX\s*(40|50)\d\d")
                ? Recommendation.Yes("Your RTX 40/50-series GPU needs it for DLSS Frame Generation.")
                : Recommendation.No("Benefit is game-dependent on your GPU. Try it, then compare with the Before/After benchmark and in-game FPS."),
        },
        new Tweak
        {
            Id = "gaming.dvr.background",
            Name = "Turn off background recording",
            Category = TweakCategory.Gaming,
            Description = "Stops Xbox Game Bar from continuously recording the last minutes of gameplay.",
            WhatItChanges = "Disables Settings › Gaming › Captures › Record what happened.",
            WhyItHelps = "Background recording keeps the GPU video encoder and disk busy for the whole session, even if you never save a clip.",
            ExpectedImpact = "Measurable when the feature was on: a few percent of GPU time and constant disk writes are freed. No effect if it was already off.",
            Disadvantages = "You can no longer save a clip of something that just happened (Win+Alt+G). Manual recording still works.",
            TechnicalDetails = "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR › HistoricalCaptureEnabled = 0",
            Source = "Windows Settings › Gaming › Captures (documented Xbox Game Bar setting)",
            Risk = RiskLevel.Safe,
            Benefit = BenefitLevel.Medium,
            Actions = [Reg.User(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", 0)],
            RecommendationCheck = _ => Reg.ReadInt(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled") == 1
                ? Recommendation.Yes("Background recording is currently ON and continuously uses your GPU encoder.")
                : Recommendation.No("Background recording is already off."),
        },
        new Tweak
        {
            Id = "gaming.dvr.off",
            Name = "Disable Game Bar captures",
            Category = TweakCategory.Gaming,
            Description = "Turns off Xbox Game Bar screenshot and recording hooks in games.",
            WhatItChanges = "Disables game capture for the current user (Game Bar itself stays installed).",
            WhyItHelps = "Without capture enabled, the Game DVR hooks are not loaded into games.",
            ExpectedImpact = "Small. Mostly removes a possible source of overlay conflicts.",
            Disadvantages = "Win+Alt+R recording and Game Bar screenshots stop working until you undo this.",
            TechnicalDetails = "HKCU\\System\\GameConfigStore › GameDVR_Enabled = 0; HKCU\\…\\GameDVR › AppCaptureEnabled = 0",
            Source = "https://learn.microsoft.com/windows/client-management/mdm/policy-csp-applicationmanagement#allowgamedvr",
            Risk = RiskLevel.Safe,
            Benefit = BenefitLevel.Low,
            Actions =
            [
                Reg.User(@"System\GameConfigStore", "GameDVR_Enabled", 0),
                Reg.User(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
            ],
        },
        new Tweak
        {
            Id = "input.mouseaccel",
            Name = "Turn off mouse acceleration",
            Category = TweakCategory.Input,
            Description = "Disables 'Enhance pointer precision' so cursor movement depends only on physical distance.",
            WhatItChanges = "Settings › Bluetooth & devices › Mouse › Additional mouse settings › Enhance pointer precision = off.",
            WhyItHelps = "With acceleration, moving the mouse fast travels further than moving it slowly over the same distance, which makes aim inconsistent. Most games using raw input ignore it, but menus and some games do not.",
            ExpectedImpact = "Consistent, predictable cursor movement. No FPS change.",
            Disadvantages = "Covering a large desktop with a low-DPI mouse may need more hand movement; raise the pointer speed if needed.",
            TechnicalDetails = "HKCU\\Control Panel\\Mouse › MouseSpeed = \"0\", MouseThreshold1 = \"0\", MouseThreshold2 = \"0\" (defaults 1/6/10), applied live with SystemParametersInfo(SPI_SETMOUSE).",
            Source = "https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-systemparametersinfoa (SPI_SETMOUSE)",
            Risk = RiskLevel.Safe,
            Benefit = BenefitLevel.Low,
            Actions =
            [
                new RegistryValueAction(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseSpeed", "0", RegistryValueKind.String, ApplyMouseSettings),
                new RegistryValueAction(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1", "0", RegistryValueKind.String, ApplyMouseSettings),
                new RegistryValueAction(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold2", "0", RegistryValueKind.String, ApplyMouseSettings),
            ],
        },

        // ───────────── Power ─────────────
        new Tweak
        {
            Id = "power.high",
            Name = "Use the High performance power plan",
            Category = TweakCategory.Power,
            Description = "Keeps the processor ready at higher performance states.",
            WhatItChanges = "Switches the active power plan to Windows' built-in High performance plan (powercfg /setactive).",
            WhyItHelps = "Reduces the time the CPU needs to ramp up clocks and keeps cores unparked, which can smooth frame times in CPU-bound games.",
            ExpectedImpact = "Small (typically 0–3%) and mostly on frame-time consistency. Modern CPUs ramp quickly on Balanced.",
            Disadvantages = "Higher idle power draw and temperature. Not recommended on laptops running on battery. On AMD Ryzen X3D and Intel hybrid CPUs, Balanced is preferred because the chipset driver relies on core parking for correct scheduling.",
            TechnicalDetails = "powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c. The previously active plan GUID is journaled and restored on undo.",
            Source = "https://learn.microsoft.com/windows-hardware/design/device-experiences/powercfg-command-line-options",
            Risk = RiskLevel.LowRisk,
            Benefit = BenefitLevel.Low,
            ConflictGroup = "powerplan",
            Actions = [new PowerPlanAction(PowerPlanAction.HighPerformance, "High performance")],
            CompatibilityCheck = _ => PowerPlanAction.IsInstalled(PowerPlanAction.HighPerformance)
                ? Compatibility.Yes
                : Compatibility.No("The High performance plan is not available on this PC (common on Modern Standby laptops)."),
            RecommendationCheck = p =>
                p.IsLaptop ? Recommendation.No("Laptop detected: higher power use and heat outweigh the small gain.")
                : p.Cpu.Name.Contains("X3D", StringComparison.OrdinalIgnoreCase) ? Recommendation.No("Ryzen X3D CPUs rely on core parking under Balanced for correct game scheduling.")
                : System.Text.RegularExpressions.Regex.IsMatch(p.Cpu.Name, @"i\d-1[2-5]\d{3}|Core\(TM\) Ultra", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? Recommendation.No("Intel hybrid (P/E-core) CPUs schedule best with Balanced.")
                : Recommendation.Yes("Desktop PC: small frame-time benefit at the cost of some idle power."),
        },
        new Tweak
        {
            Id = "power.balanced",
            Name = "Use the Balanced power plan",
            Category = TweakCategory.Power,
            Description = "Windows' default plan: performance on demand, low power at idle.",
            WhatItChanges = "Switches the active power plan to Balanced (powercfg /setactive).",
            WhyItHelps = "Restores normal behaviour after Power saver was left on, which caps CPU performance and hurts games noticeably.",
            ExpectedImpact = "Large if Power saver was active; none otherwise.",
            Disadvantages = "None — this is the Windows default.",
            TechnicalDetails = "powercfg /setactive 381b4222-f694-41f0-9685-ff5bb260df2e",
            Source = "https://learn.microsoft.com/windows-hardware/design/device-experiences/powercfg-command-line-options",
            Risk = RiskLevel.Safe,
            Benefit = BenefitLevel.Low,
            ConflictGroup = "powerplan",
            Actions = [new PowerPlanAction(PowerPlanAction.Balanced, "Balanced")],
            RecommendationCheck = _ => PowerPlanAction.ActivePlan()?.Id == PowerPlanAction.PowerSaver
                ? Recommendation.Yes("Power saver is active and limits CPU performance.")
                : Recommendation.No("Your current plan is fine."),
        },

        // ───────────── Responsiveness / visual ─────────────
        new Tweak
        {
            Id = "visual.transparency",
            Name = "Turn off transparency effects",
            Category = TweakCategory.Visual,
            Description = "Disables blur/translucency on the taskbar, Start and windows.",
            WhatItChanges = "Settings › Personalization › Colors › Transparency effects = off.",
            WhyItHelps = "Blur effects cost GPU time in the desktop compositor. Only relevant on integrated or very weak GPUs.",
            ExpectedImpact = "Negligible on a discrete GPU. Slightly lighter desktop on low-end integrated graphics.",
            Disadvantages = "Less polished look.",
            TechnicalDetails = "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize › EnableTransparency = 0",
            Source = "Windows Settings › Personalization › Colors (documented user setting)",
            Risk = RiskLevel.Safe,
            Benefit = BenefitLevel.Low,
            Actions = [Reg.User(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0)],
            RecommendationCheck = p => !p.HasDiscreteGpu && p.Memory.TotalBytes <= 8L << 30
                ? Recommendation.Yes("Low-end integrated graphics detected.")
                : Recommendation.No("Your GPU handles these effects without measurable cost."),
        },
        new Tweak
        {
            Id = "visual.animations",
            Name = "Reduce window and taskbar animations",
            Category = TweakCategory.Visual,
            Description = "Windows open, close and minimise instantly.",
            WhatItChanges = "Turns off minimise/maximise and taskbar animations.",
            WhyItHelps = "Animations add a fixed delay of roughly 100–250 ms to window operations. Removing them makes the desktop feel more immediate.",
            ExpectedImpact = "Perceived snappiness only. Does not change performance in games or apps.",
            Disadvantages = "Less smooth visual transitions. Takes effect after signing out.",
            TechnicalDetails = "HKCU\\Control Panel\\Desktop\\WindowMetrics › MinAnimate = \"0\"; HKCU\\…\\Explorer\\Advanced › TaskbarAnimations = 0",
            Source = "https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-animationinfo",
            Risk = RiskLevel.Safe,
            Benefit = BenefitLevel.Low,
            Restart = RestartRequirement.SignOut,
            Actions =
            [
                new RegistryValueAction(RegistryHive.CurrentUser, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", RegistryValueKind.String),
                Reg.User(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 0),
            ],
            RecommendationCheck = p => p.IsLowEnd ? Recommendation.Yes("Makes a modest PC feel more responsive.") : Recommendation.No("Purely a preference on your hardware."),
        },
        new Tweak
        {
            Id = "responsiveness.backgroundapps",
            Name = "Block background activity of Store apps",
            Category = TweakCategory.Responsiveness,
            Description = "Prevents Microsoft Store apps from running in the background.",
            WhatItChanges = "Sets the 'Let Windows apps run in the background' policy to Force Deny for all users.",
            WhyItHelps = "Store apps (News, Weather, Phone Link, Mail…) can wake up periodically to sync. Blocking them saves a little CPU, memory and battery.",
            ExpectedImpact = "Small. Desktop (Win32) programs such as Steam, Discord or browsers are not affected.",
            Disadvantages = "Store apps stop receiving background notifications and live updates: Phone Link, Mail/Outlook (new), Teams, Spotify (Store version), alarms in Clock. Settings will show 'managed by your organisation'.",
            TechnicalDetails = "HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy › LetAppsRunInBackground = 2",
            Source = "https://learn.microsoft.com/windows/client-management/mdm/policy-csp-privacy#letappsruninbackground",
            Risk = RiskLevel.Moderate,
            Benefit = BenefitLevel.Low,
            Actions = [Reg.Machine(@"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", 2)],
            RecommendationCheck = _ => Recommendation.No("Only worth it if you do not rely on notifications from Store apps."),
        },

        // ───────────── Privacy ─────────────
        PrivacyTweak("privacy.adid", "Turn off advertising ID",
            "Apps can no longer use your advertising ID to personalise ads across apps.",
            "Settings › Privacy & security › General › Let apps show me personalised ads = off.",
            "Ads in apps become generic instead of profiled.",
            "None.",
            [Reg.User(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0)],
            "https://learn.microsoft.com/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#181-general",
            recommended: true),
        PrivacyTweak("privacy.tailored", "Turn off tailored experiences",
            "Microsoft stops using your diagnostic data to personalise tips, ads and recommendations.",
            "Settings › Privacy & security › Diagnostics & feedback › Tailored experiences = off.",
            "Diagnostic data is no longer used for personalisation.",
            "None.",
            [Reg.User(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0)],
            "https://learn.microsoft.com/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services",
            recommended: true),
        PrivacyTweak("privacy.language", "Don't share your language list with websites",
            "Websites can no longer read your installed language list.",
            "Settings › Privacy & security › General › Let websites show me locally relevant content = off.",
            "Reduces browser fingerprinting surface.",
            "Some websites may not automatically pick your language.",
            [Reg.User(@"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", 1)],
            "https://learn.microsoft.com/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#181-general",
            recommended: true),
        PrivacyTweak("privacy.launchtracking", "Don't track app launches",
            "Windows stops recording which apps you launch.",
            "Settings › Privacy & security › General › Let Windows improve Start and search results by tracking app launches = off.",
            "App-usage history is no longer kept.",
            "The 'Most used' list in Start becomes empty.",
            [Reg.User(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackProgs", 0)],
            "Windows Settings › Privacy & security › General (documented user setting)"),
        PrivacyTweak("privacy.settingssuggestions", "Hide suggested content in Settings",
            "Removes Microsoft suggestions and promotions inside the Settings app.",
            "Settings › Privacy & security › General › Show me suggested content in the Settings app = off.",
            "Fewer promotions; Settings shows only settings.",
            "None.",
            [
                Reg.User(CDM, "SubscribedContent-338393Enabled", 0),
                Reg.User(CDM, "SubscribedContent-353694Enabled", 0),
                Reg.User(CDM, "SubscribedContent-353696Enabled", 0),
            ],
            "https://learn.microsoft.com/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services",
            recommended: true),
        PrivacyTweak("privacy.feedback", "Stop feedback requests",
            "Windows no longer asks you for feedback.",
            "Settings › Privacy & security › Diagnostics & feedback › Feedback frequency = Never.",
            "No more feedback pop-ups.",
            "Windows Insiders should keep feedback enabled to help their channel.",
            [Reg.User(@"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0)],
            "https://learn.microsoft.com/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#1816-feedback--diagnostics",
            recommended: true,
            recommend: p => p.Os.IsInsider ? Recommendation.No("You are a Windows Insider: feedback is part of the programme.") : Recommendation.Yes("Removes interruptions with no downside.")),
        PrivacyTweak("privacy.searchweb", "Remove web results from Start search",
            "Start search shows only local apps, files and settings — no Bing results.",
            "Enables the user policy 'Turn off display of recent search entries in the File Explorer search box', which Windows 11 also uses to disable web suggestions in Start search.",
            "Searches typed in Start are no longer sent to Bing; local results appear faster.",
            "No web answers in Start. Takes effect after signing out or restarting Explorer.",
            [Reg.User(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1)],
            "https://learn.microsoft.com/windows/client-management/mdm/policy-csp-admx-windowsexplorer#disablesearchboxsuggestions",
            risk: RiskLevel.LowRisk, restart: RestartRequirement.SignOut),
        PrivacyTweak("privacy.activity", "Turn off activity history",
            "Windows stops collecting and uploading your activity history.",
            "Enables the 'Allow publishing of User Activities' = disabled and 'Allow upload of User Activities' = disabled policies.",
            "Activity history is no longer stored or uploaded.",
            "Features that resume activities across devices stop working. Settings shows 'managed by your organisation'.",
            [
                Reg.Machine(@"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0),
                Reg.Machine(@"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0),
            ],
            "https://learn.microsoft.com/windows/client-management/mdm/policy-csp-privacy#enableactivityfeed",
            risk: RiskLevel.LowRisk),
        PrivacyTweak("privacy.telemetry", "Send only required diagnostic data",
            "Limits Windows diagnostic data to the required level.",
            "Sets the 'Allow Diagnostic Data' policy to 1 (Required). Windows Update, Defender and the Store keep working normally.",
            "Optional diagnostic data (browsing, app usage, inking/typing) is no longer sent.",
            "Settings shows 'managed by your organisation'. Incompatible with the Windows Insider Program, which requires optional diagnostic data.",
            [Reg.Machine(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1)],
            "https://learn.microsoft.com/windows/privacy/configure-windows-diagnostic-data-in-your-organization",
            risk: RiskLevel.LowRisk,
            compat: p => p.Os.IsInsider ? Compatibility.No("Windows Insider builds require optional diagnostic data; this would stop new builds.") : Compatibility.Yes),
        PrivacyTweak("privacy.recall", "Turn off Recall snapshots",
            "Prevents Windows from saving screen snapshots for Recall.",
            "Enables the 'Turn off saving snapshots for Windows' user policy.",
            "No snapshots of your screen are stored.",
            "Recall stops working. Only relevant on Copilot+ PCs.",
            [Reg.User(@"Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1)],
            "https://learn.microsoft.com/windows/client-management/manage-recall",
            risk: RiskLevel.LowRisk,
            compat: p => p.Os.Build >= 26100 ? Compatibility.Yes : Compatibility.No("Recall exists only on Windows 11 24H2 and later.")),

        // ───────────── Consumer features (debloat) ─────────────
        ConsumerTweak("consumer.silentinstall", "Stop automatic installation of suggested apps",
            "Windows stops silently installing promoted apps and games (e.g. after updates).",
            [
                Reg.User(CDM, "SilentInstalledAppsEnabled", 0),
                Reg.User(CDM, "PreInstalledAppsEnabled", 0),
                Reg.User(CDM, "OemPreInstalledAppsEnabled", 0),
            ], recommended: true),
        ConsumerTweak("consumer.startsuggestions", "Hide suggestions in Start",
            "Removes promoted apps and recommendations for apps you don't have.",
            [
                Reg.User(CDM, "SubscribedContent-338388Enabled", 0),
                Reg.User(CDM, "SystemPaneSuggestionsEnabled", 0),
            ], recommended: true),
        ConsumerTweak("consumer.tips", "Turn off tips and suggestions notifications",
            "Stops 'Get tips and suggestions when using Windows' notifications.",
            [
                Reg.User(CDM, "SubscribedContent-338389Enabled", 0),
                Reg.User(CDM, "SoftLandingEnabled", 0),
            ], recommended: true),
        ConsumerTweak("consumer.lockscreen", "Remove fun facts and tips on the lock screen",
            "Lock screen shows your picture without Microsoft tips and promotions.",
            [
                Reg.User(CDM, "RotatingLockScreenOverlayEnabled", 0),
                Reg.User(CDM, "SubscribedContent-338387Enabled", 0),
            ]),
        new Tweak
        {
            Id = "consumer.widgets",
            Name = "Disable the Widgets board",
            Category = TweakCategory.ConsumerFeatures,
            Description = "Removes the Widgets board and its news feed.",
            WhatItChanges = "Enables the 'Allow widgets' = disabled policy (Widgets button and board removed).",
            WhyItHelps = "The Widgets host and its WebView2 processes keep 100–300 MB of RAM in use and refresh the news feed in the background.",
            ExpectedImpact = "Frees memory on PCs with 8 GB of RAM or less. Negligible on 16 GB+.",
            Disadvantages = "No weather/news/widgets board. Settings shows 'managed by your organisation'.",
            TechnicalDetails = "HKLM\\SOFTWARE\\Policies\\Microsoft\\Dsh › AllowNewsAndInterests = 0",
            Source = "https://learn.microsoft.com/windows/client-management/mdm/policy-csp-newsandinterests",
            Risk = RiskLevel.LowRisk,
            Benefit = BenefitLevel.Low,
            Restart = RestartRequirement.SignOut,
            Actions = [Reg.Machine(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0)],
            RecommendationCheck = p => p.Memory.TotalBytes <= 8L << 30
                ? Recommendation.Yes("8 GB of RAM or less: the memory used by Widgets matters.")
                : Recommendation.No("Optional — mostly a preference with your amount of RAM."),
        },

        // ───────────── Network ─────────────
        new Tweak
        {
            Id = "network.deliveryopt",
            Name = "Don't upload Windows updates to internet peers",
            Category = TweakCategory.Network,
            Description = "Delivery Optimization will only share updates with PCs on your local network.",
            WhatItChanges = "Sets the Delivery Optimization 'Download mode' policy to LAN (1).",
            WhyItHelps = "By default Windows may upload update pieces to other PCs on the internet. Upload saturation on asymmetric connections increases latency for everything, including games.",
            ExpectedImpact = "Prevents occasional upload-induced lag spikes on slow-upload connections. Updates still download normally from Microsoft.",
            Disadvantages = "Other PCs on the internet won't receive pieces from you. Settings shows 'managed by your organisation'.",
            TechnicalDetails = "HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\DeliveryOptimization › DODownloadMode = 1",
            Source = "https://learn.microsoft.com/windows/deployment/do/waas-delivery-optimization-reference#download-mode",
            Risk = RiskLevel.LowRisk,
            Benefit = BenefitLevel.Low,
            Actions = [Reg.Machine(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 1)],
            RecommendationCheck = _ => Recommendation.Yes("Avoids background uploads competing with your games and calls."),
        },

        // ───────────── Services / repairs of bad configurations ─────────────
        new Tweak
        {
            Id = "services.spooler",
            Name = "Disable the Print Spooler (no printer installed)",
            Category = TweakCategory.Services,
            Description = "Stops the printing service on PCs that never print.",
            WhatItChanges = "Sets the Print Spooler service startup type to Disabled.",
            WhyItHelps = "Reduces attack surface (the spooler has had several critical vulnerabilities, e.g. PrintNightmare) and saves a few MB of memory.",
            ExpectedImpact = "Security hardening. No measurable performance change.",
            Disadvantages = "You cannot print or use 'Microsoft Print to PDF' until you undo this. Adding a printer later requires re-enabling the service.",
            TechnicalDetails = "HKLM\\SYSTEM\\CurrentControlSet\\Services\\Spooler › Start = 4 (default 2 = Automatic). Takes effect after restart.",
            Source = "https://learn.microsoft.com/windows-server/security/windows-services/security-guidelines-for-disabling-system-services-in-windows-server",
            Risk = RiskLevel.Moderate,
            Benefit = BenefitLevel.Low,
            Restart = RestartRequirement.Restart,
            Actions = [Reg.ServiceStart("Spooler", 4)],
            CompatibilityCheck = _ => HasPhysicalPrinter() ? Compatibility.No("A physical or network printer is installed.") : Compatibility.Yes,
            RecommendationCheck = _ => Recommendation.No("Optional hardening — only if you are sure you never print."),
        },
        new Tweak
        {
            Id = "fix.wuauserv",
            Name = "Re-enable the Windows Update service",
            Category = TweakCategory.Services,
            Description = "Repairs a disabled Windows Update service (often left by other 'optimizers').",
            WhatItChanges = "Restores the Windows Update service startup type to Manual (the Windows default; it starts on demand).",
            WhyItHelps = "A disabled update service leaves the PC without security patches, breaks the Microsoft Store and Defender definition updates.",
            ExpectedImpact = "Restores security updates.",
            Disadvantages = "None — this is the Windows default.",
            TechnicalDetails = "HKLM\\SYSTEM\\CurrentControlSet\\Services\\wuauserv › Start = 3",
            Source = "https://learn.microsoft.com/windows/deployment/update/windows-update-troubleshooting",
            Risk = RiskLevel.Safe,
            Benefit = BenefitLevel.High,
            Restart = RestartRequirement.Restart,
            Actions = [Reg.ServiceStart("wuauserv", 3)],
            CompatibilityCheck = _ => Reg.ReadInt(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\wuauserv", "Start") == 4
                ? Compatibility.Yes : Compatibility.No("Windows Update is not disabled — nothing to repair."),
            RecommendationCheck = _ => Recommendation.Yes("Windows Update is disabled: your PC is not receiving security fixes."),
        },
        new Tweak
        {
            Id = "fix.pagefile",
            Name = "Re-enable the paging file",
            Category = TweakCategory.Services,
            Description = "Restores a system-managed paging file after it was removed.",
            WhatItChanges = "Restores the system-managed paging file on the system drive (Windows default).",
            WhyItHelps = "Without a paging file the commit limit equals physical RAM: games and browsers crash with 'out of memory' long before RAM is actually full, and crash dumps cannot be written.",
            ExpectedImpact = "Eliminates out-of-memory crashes caused by a missing paging file.",
            Disadvantages = "Uses some disk space (managed automatically).",
            TechnicalDetails = "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Memory Management › PagingFiles = \"?:\\pagefile.sys\"",
            Source = "https://learn.microsoft.com/troubleshoot/windows-client/performance/introduction-to-the-page-file",
            Risk = RiskLevel.Safe,
            Benefit = BenefitLevel.High,
            Restart = RestartRequirement.Restart,
            Actions = [new RegistryValueAction(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "PagingFiles", new[] { @"?:\pagefile.sys" }, RegistryValueKind.MultiString)],
            CompatibilityCheck = _ => PagingFileDisabled() ? Compatibility.Yes : Compatibility.No("A paging file is configured — nothing to repair."),
            RecommendationCheck = _ => Recommendation.Yes("No paging file is configured; this causes avoidable crashes."),
        },
    ];

    private static Tweak PrivacyTweak(string id, string name, string description, string what, string impact, string disadvantages,
        IReadOnlyList<IChangeAction> actions, string source, bool recommended = false, RiskLevel risk = RiskLevel.Safe,
        RestartRequirement restart = RestartRequirement.None, Func<SystemProfile, Compatibility>? compat = null,
        Func<SystemProfile, Recommendation>? recommend = null) => new()
        {
            Id = id,
            Name = name,
            Category = TweakCategory.Privacy,
            Description = description,
            WhatItChanges = what,
            WhyItHelps = "Reduces data shared with Microsoft or apps.",
            ExpectedImpact = impact + " No performance effect.",
            Disadvantages = disadvantages,
            TechnicalDetails = string.Join("; ", actions.Select(a => $"{a.Describe()} = {a.DescribeDesired()}")),
            Source = source,
            Risk = risk,
            Benefit = BenefitLevel.Low,
            Restart = restart,
            Actions = actions,
            CompatibilityCheck = compat,
            RecommendationCheck = recommend ?? (recommended
                ? _ => Recommendation.Yes("Privacy improvement with no functional downside.")
                : _ => Recommendation.No("Optional — depends on your preferences.")),
        };

    private static Tweak ConsumerTweak(string id, string name, string description, IReadOnlyList<IChangeAction> actions, bool recommended = false) => new()
    {
        Id = id,
        Name = name,
        Category = TweakCategory.ConsumerFeatures,
        Description = description,
        WhatItChanges = "Turns off the corresponding Content Delivery Manager feature for the current user.",
        WhyItHelps = "Removes promotional content and prevents apps you didn't ask for from appearing.",
        ExpectedImpact = "Cleaner Windows experience; prevents future bloat. No direct performance effect.",
        Disadvantages = "You will no longer see Microsoft suggestions in these places.",
        TechnicalDetails = string.Join("; ", actions.Select(a => $"{a.Describe()} = {a.DescribeDesired()}")),
        Source = "https://learn.microsoft.com/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services",
        Risk = RiskLevel.Safe,
        Benefit = BenefitLevel.Low,
        Actions = actions,
        RecommendationCheck = recommended ? _ => Recommendation.Yes("Removes promotions with no downside.") : _ => Recommendation.No("Preference."),
    };

    private static void ApplyMouseSettings()
    {
        static int Read(string name) =>
            int.TryParse(Reg.ReadString(RegistryHive.CurrentUser, @"Control Panel\Mouse", name), out var v) ? v : 0;
        int[] values = [Read("MouseThreshold1"), Read("MouseThreshold2"), Read("MouseSpeed")];
        Native.SystemParametersInfo(Native.SPI_SETMOUSE, 0, values, Native.SPIF_SENDCHANGE);
    }

    internal static bool HasPhysicalPrinter()
    {
        var virtualPorts = new[] { "PORTPROMPT:", "nul:", "SHRFAX:", "XPSPort:", "OneNote", "FILE:" };
        return Wmi.TryQuery("SELECT Name, PortName, Network FROM Win32_Printer")
            .Any(r => r.Bool("Network") == true || !virtualPorts.Any(v => r.Str("PortName").StartsWith(v, StringComparison.OrdinalIgnoreCase)));
    }

    internal static bool PagingFileDisabled()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
        return k?.GetValue("PagingFiles") is string[] files && files.All(string.IsNullOrWhiteSpace);
    }
}
