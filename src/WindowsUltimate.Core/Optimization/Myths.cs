using WindowsUltimate.Core.Infrastructure;
namespace WindowsUltimate.Core.Optimization;

public sealed record Myth(string Name, string Claim, string Reality, string Verdict, string Source)
{
    public string Name { get => L.T(field); init; } = Name;
    public string Claim { get => L.T(field); init; } = Claim;
    public string Reality { get => L.T(field); init; } = Reality;
    public string Verdict { get => L.T(field); init; } = Verdict;
}

/// <summary>
/// Popular "optimizations" that Windows Ultimate deliberately does NOT apply, and why.
/// Shown to users so they can recognise placebo tweaks in other tools.
/// </summary>
public static class Myths
{
    public static IReadOnlyList<Myth> All { get; } =
    [
        new("NetworkThrottlingIndex / SystemResponsiveness",
            "Setting NetworkThrottlingIndex to FFFFFFFF and SystemResponsiveness to 0 reduces input lag and ping.",
            "Both values tune the Multimedia Class Scheduler Service (MMCSS), which only affects threads that register with it (mostly audio/video playback). Network throttling only applies to non-multimedia traffic while multimedia is playing.",
            "Placebo for games",
            "https://learn.microsoft.com/windows/win32/procthread/multimedia-class-scheduler-service"),
        new("\"Games\" task priority (GPU Priority, Scheduling Category)",
            "Raising the MMCSS 'Games' task priority makes games faster.",
            "These values only apply to threads that call AvSetMmThreadCharacteristics(\"Games\"). Almost no game does.",
            "Placebo",
            "https://learn.microsoft.com/windows/win32/procthread/multimedia-class-scheduler-service"),
        new("Disabling Nagle's algorithm (TcpAckFrequency, TCPNoDelay)",
            "Disabling Nagle lowers ping in online games.",
            "Most real-time games use UDP, which Nagle does not affect. TCP-based games that care already set TCP_NODELAY on their own sockets. A system-wide change only increases small-packet overhead for everything else.",
            "No benefit for most games",
            "https://learn.microsoft.com/windows/win32/winsock/ipproto-tcp-socket-options"),
        new("Disabling HPET / bcdedit useplatformclock / disabledynamictick",
            "Forcing or disabling the HPET timer gives more FPS and less input lag.",
            "Windows selects the best available timer (usually the invariant TSC). Forcing the platform clock makes every timer read slower; disabling dynamic tick increases power use. Measured results are inconsistent and changing boot configuration carries risk.",
            "Not applied — risk without reliable benefit",
            "https://learn.microsoft.com/windows-hardware/drivers/devtest/bcdedit--set"),
        new("Global timer resolution tools",
            "Forcing a 0.5 ms timer resolution reduces input lag.",
            "Since Windows 10 2004 timer resolution is per-process, and Windows 11 ignores requests from minimised or occluded windows. Games already request the resolution they need. A global high resolution mainly increases power consumption.",
            "Not applied",
            "https://learn.microsoft.com/windows/win32/api/timeapi/nf-timeapi-timebeginperiod"),
        new("Win32PrioritySeparation",
            "Changing Win32PrioritySeparation to 0x26/0x28/0x2A improves responsiveness.",
            "The default already boosts foreground applications. Alternatives only change quantum length and boost; measured differences in games fall within run-to-run noise.",
            "Within noise",
            "https://learn.microsoft.com/windows/win32/procthread/scheduling-priorities"),
        new("Disabling SysMain (Superfetch) and Windows Search",
            "Disabling these services frees resources for games.",
            "SysMain preloads frequently used apps into otherwise unused memory and is released immediately when needed. On SSDs, disabling it slows app launches. Disabling Search breaks search in Start, Outlook and File Explorer.",
            "Counter-productive on modern PCs",
            "https://learn.microsoft.com/windows-server/administration/performance-tuning/"),
        new("Disabling the paging file",
            "With enough RAM, the paging file only slows things down.",
            "Without a paging file the commit limit equals physical RAM, so programs fail allocations long before memory is full, and crash dumps cannot be written. Windows Ultimate offers to re-enable it if it was removed.",
            "Harmful — we repair it",
            "https://learn.microsoft.com/troubleshoot/windows-client/performance/introduction-to-the-page-file"),
        new("RAM cleaners / memory optimizers",
            "Freeing RAM periodically makes the PC faster.",
            "Free memory is wasted memory. Purging the standby list throws away cached data that must then be read again from disk, which causes the very stutter these tools claim to fix.",
            "Counter-productive",
            "https://learn.microsoft.com/windows/win32/memory/memory-management"),
        new("Ultimate Performance power plan",
            "Ultimate Performance gives more FPS than High performance.",
            "It is designed for workstations and removes micro-latencies from power-state transitions. In games on a desktop, results are indistinguishable from High performance, and on laptops it wastes battery.",
            "No measurable gaming gain",
            "https://learn.microsoft.com/windows-hardware/design/device-experiences/powercfg-command-line-options"),
        new("Disabling core parking",
            "Core parking causes stutter; disable it.",
            "On AMD Ryzen X3D and Intel hybrid CPUs, parking is how the chipset driver steers games to the right cores. Disabling it can reduce performance. The High performance plan already keeps cores unparked on other CPUs.",
            "Can hurt modern CPUs",
            "https://learn.microsoft.com/windows-hardware/customize/power-settings/configure-processor-power-management-options"),
        new("Custom DNS lowers ping",
            "Switching to a 'gaming DNS' reduces in-game latency.",
            "DNS only translates a name to an address once before connecting. Your in-game ping is the network path to the game server and is not affected. A faster resolver can make the first page load slightly quicker.",
            "Does not affect ping",
            "https://learn.microsoft.com/windows-server/networking/dns/dns-top"),
        new("IRQ priority registry values (IRQ8Priority, etc.)",
            "Raising IRQ priority for the GPU or mouse reduces latency.",
            "These values date back to Windows 9x-era advice and are not read by the modern Windows kernel. Interrupt priority is managed by the hardware and the HAL.",
            "No effect",
            "https://learn.microsoft.com/windows-hardware/drivers/kernel/managing-hardware-priorities"),
        new("Disabling Spectre/Meltdown mitigations",
            "Turning off CPU mitigations restores lost performance.",
            "It can recover some performance in specific workloads but re-opens serious vulnerabilities that can be exploited from a web page. Windows Ultimate never weakens security for marginal gains.",
            "Never applied (security)",
            "https://learn.microsoft.com/troubleshoot/windows-client/windows-security/kb4072698-manage-speculative-execution-side-channel-vulnerabilities"),
        new("Disabling Memory Integrity / VBS",
            "Disabling virtualization-based security increases FPS.",
            "Microsoft acknowledges a performance impact in some games on some hardware. It is also a core protection against kernel malware. Windows Ultimate reports its status and lets you decide in Windows Security; it never changes it.",
            "Reported, never changed",
            "https://learn.microsoft.com/windows/security/hardware-security/enable-virtualization-based-protection-of-code-integrity"),
        new("Disabling Windows Defender for performance",
            "Defender slows down games; disable it.",
            "Real-time protection has a small cost mostly during file-heavy operations. Disabling it leaves the PC unprotected. Windows Ultimate never disables Defender, the firewall or UAC.",
            "Never applied (security)",
            "https://learn.microsoft.com/defender-endpoint/microsoft-defender-antivirus-windows"),
    ];
}
