using WindowsUltimate.Core.Network;
using WindowsUltimate.Core.Optimization;
using WindowsUltimate.Core.Security;
using WindowsUltimate.Core.SystemServices;

namespace WindowsUltimate.Tests;

/// <summary>The non-negotiable rules of the network, services and security features.</summary>
public sealed class SafetyRulesTests
{
    [Theory]
    [InlineData("WinDefend")]
    [InlineData("mpssvc")]
    [InlineData("wuauserv")]
    [InlineData("SysMain")]
    public void ProtectedServices_AreNeverOfferedForChange(string name)
    {
        var k = ServiceCatalog.Find(name);
        Assert.NotNull(k);
        Assert.NotEqual(ServiceClass.Optional, k!.Class);
        var info = new ServiceInfo { Name = name, StartMode = "Auto", Class = k.Class, Knowledge = k };
        Assert.Null(info.OfferedStart);
        Assert.Null(ServiceManager.CreateChange(info));
    }

    [Fact]
    public void OptionalServices_AreChangeableOnlyToTheirDocumentedValue()
    {
        foreach (var k in ServiceCatalog.All.Where(k => k.SuggestedStart is not null))
        {
            Assert.Equal(ServiceClass.Optional, k.Class);
            Assert.False(string.IsNullOrWhiteSpace(k.Benefit), k.Name);
            Assert.False(string.IsNullOrWhiteSpace(k.Drawback), k.Name);
        }
    }

    [Fact]
    public void Advisor_WithoutGateway_ReportsOnlyTheMissingConnection()
    {
        var advice = NetworkAdvisor.Advise(new NetworkSnapshot { Adapters = [new AdapterInfo { Name = "Ethernet", Type = "Ethernet" }] });
        Assert.Single(advice);
        Assert.Equal("bad", advice[0].Severity);
        Assert.Null(advice[0].ActionId);
    }

    [Fact]
    public void Advisor_NeverPromisesLowerPing()
    {
        var adapter = new AdapterInfo { Name = "Wi-Fi", Type = "Wireless", IsWireless = true, Gateways = ["192.168.1.1"], DnsServers = ["192.168.1.1"], Mtu = 1500, SpeedBps = 300_000_000 };
        var speed = new NetworkMeasurement { DownloadMbps = 300, UploadMbps = 20, PingMs = 12, LoadedPingMs = 120 };
        var advice = NetworkAdvisor.Advise(new NetworkSnapshot { Adapters = [adapter], Speed = speed, Tcp = new TcpGlobalInfo("restricted", "CUBIC", true, true), PathMtu = 1492 });

        Assert.Contains(advice, a => a.ActionId == "autotuning");
        Assert.Contains(advice, a => a.Severity == "bad" && a.ActionId is null); // bufferbloat: router-side, no Windows "fix" offered
        foreach (var a in advice)
            Assert.DoesNotMatch(@"(?i)(lower|reduce)s? (your )?ping|guarantee", a.Title + " " + a.Detail);
    }

    [Fact]
    public void Quarantine_RefusesFilesInsideWindows()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        var action = new QuarantineFileAction(file);
        Assert.Throws<InvalidOperationException>(action.Apply);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Quarantine_IsAReversibleMove()
    {
        var dir = Directory.CreateTempSubdirectory("wu-test-");
        var file = Path.Combine(dir.FullName, "sample.bin");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        try
        {
            var action = new QuarantineFileAction(file);
            var snapshot = action.Capture();
            action.Apply();
            Assert.False(File.Exists(file));
            Assert.True(action.IsInDesiredState());
            action.Restore(snapshot);
            Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(file));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void UnsignedFile_IsNotAFindingOnItsOwn()
    {
        var dir = Directory.CreateTempSubdirectory("wu-test-");
        var file = Path.Combine(dir.FullName, "tool.txt");
        File.WriteAllText(file, "hello");
        try
        {
            var report = FileInspector.Inspect(file);
            Assert.True(report.Score < 3);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void DnsTweaks_ShareOneConflictGroupPerAdapter()
    {
        var a = new AdapterInfo { Id = "{11111111-2222-3333-4444-555555555555}", Name = "Ethernet", Index = 7 };
        var tweaks = DnsProviders.All.Select(p => NetworkConfig.DnsTweak(a, p)).ToList();
        Assert.Single(tweaks.Select(t => t.ConflictGroup).Distinct());
        Assert.Equal(tweaks.Count, tweaks.Select(t => t.Id).Distinct().Count());
        Assert.All(tweaks, t => Assert.True(t.IsReversible));
    }
}
