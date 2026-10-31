using WindowsUltimate.Core.Debloat;
using WindowsUltimate.Core.Diagnostics;
using WindowsUltimate.Core.Hardware;
using WindowsUltimate.Core.Network;
using WindowsUltimate.Core.Optimization;
using WindowsUltimate.Core.Startup;

namespace WindowsUltimate.Tests;

public sealed class CatalogAndHelperTests
{
    private static readonly IReadOnlyList<Tweak> Catalog = TweakCatalog.Create();

    [Fact]
    public void Catalog_IdsAreUnique() =>
        Assert.Equal(Catalog.Count, Catalog.Select(t => t.Id).Distinct().Count());

    [Fact]
    public void Catalog_EveryTweakIsDocumentedReversibleAndNotDangerous()
    {
        foreach (var t in Catalog)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Source), t.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.Disadvantages), t.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.ExpectedImpact), t.Id);
            Assert.NotEmpty(t.Actions);
            Assert.True(t.IsReversible, t.Id);
            Assert.NotEqual(RiskLevel.Dangerous, t.Risk);
        }
    }

    [Fact]
    public void Catalog_NeverTouchesSecurityCriticalSettings()
    {
        string[] forbidden = ["WinDefend", "DisableAntiSpyware", "EnableLUA", "MpsSvc", "DisableRealtimeMonitoring", "FeatureSettingsOverride", "HypervisorEnforcedCodeIntegrity"];
        foreach (var t in Catalog)
            foreach (var a in t.Actions)
                Assert.DoesNotContain(forbidden, f => a.Describe().Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Telemetry_IsIncompatibleWithInsiderBuilds()
    {
        var t = Catalog.Single(t => t.Id == "privacy.telemetry");
        var insider = new SystemProfile { Os = new OsInfo { IsInsider = true, Build = 26100 } };
        Assert.False(t.CheckCompatibility(insider).IsCompatible);
    }

    [Fact]
    public void Debloat_ProtectsStoreAndFrameworks()
    {
        Assert.Equal(DebloatCategory.DoNotRemove, DebloatCatalog.Match("Microsoft.WindowsStore")!.Category);
        Assert.Equal(DebloatCategory.DoNotRemove, DebloatCatalog.Match("Microsoft.VCLibs.140.00.UWPDesktop")!.Category);
        Assert.Equal(DebloatCategory.DoNotRemove, DebloatCatalog.Match("Microsoft.XboxIdentityProvider")!.Category);
        Assert.Equal(DebloatCategory.SafeToRemove, DebloatCatalog.Match("Microsoft.BingNews")!.Category);
        Assert.Null(DebloatCatalog.Match("Some.UnknownApp"));
        var framework = new AppxPackageInfo { Name = "Some.Framework", IsFramework = true };
        Assert.True(framework.Protected);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --minimized", "C:\\Program Files\\App\\app.exe")]
    [InlineData("C:\\Tools\\tool.exe /background", "C:\\Tools\\tool.exe")]
    public void Startup_ExtractsExecutable(string command, string expected) =>
        Assert.Equal(expected, StartupManager.ExtractExecutable(command));

    [Fact]
    public void Dns_QueryIsWellFormed()
    {
        var q = DnsQuery.Build(0x1234, "example.com");
        Assert.Equal(0x12, q[0]);
        Assert.Equal(0x34, q[1]);
        Assert.Equal(7, q[12]); // "example"
        Assert.Equal(new byte[] { 0, 0, 1, 0, 1 }, q[^5..]);
        var response = new byte[12];
        response[0] = 0x12; response[1] = 0x34; response[2] = 0x81; response[3] = 0x80;
        Assert.True(DnsQuery.IsValidResponse(response, 0x1234));
        Assert.False(DnsQuery.IsValidResponse(response, 0x1235));
    }

    [Fact]
    public void Ping_JitterAndLoss()
    {
        var s = new PingStats();
        s.Samples.AddRange([10, 20, null, 10]);
        Assert.Equal(25, s.LossPercent);
        Assert.Equal(10, s.Jitter);
    }

    [Fact]
    public void HealthScore_IsBoundedAndExplained()
    {
        var r = new ScanReport
        {
            Findings =
            [
                new Finding { Id = "a", Severity = Severity.Critical, Area = "x", Title = "a", Detail = "", ScorePenalty = 80 },
                new Finding { Id = "b", Severity = Severity.Warning, Area = "x", Title = "b", Detail = "", ScorePenalty = 40 },
                new Finding { Id = "c", Severity = Severity.Info, Area = "x", Title = "c", Detail = "" },
            ],
        };
        Assert.Equal(0, r.Score);
        Assert.Equal(2, r.Penalties.Count());
    }

    [Fact]
    public void NvidiaDriverVersion_IsShownInVendorFormat()
    {
        var g = new GpuInfo { Vendor = HardwareVendor.Nvidia, DriverVersion = "32.0.15.6094" };
        Assert.Equal("560.94", g.FriendlyDriverVersion);
    }
}
