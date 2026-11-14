using System.Collections.ObjectModel;
using System.Windows.Input;
using WindowsUltimate.App.Dialogs;
using WindowsUltimate.App.Localization;
using WindowsUltimate.App.Mvvm;
using WindowsUltimate.App.Services;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Network;
using WindowsUltimate.Core.Optimization;

namespace WindowsUltimate.App.ViewModels;

/// <summary>A public DNS provider offered for the active adapter.</summary>
public sealed class DnsProviderViewModel : ObservableObject
{
    public DnsProviderViewModel(DnsProvider p, AdapterInfo adapter, NetworkViewModel owner)
    {
        Provider = p;
        Adapter = adapter;
        ApplyCommand = new AsyncCommand(() => owner.ApplyDnsAsync(this), () => !IsApplied);
        UndoCommand = new AsyncCommand(() => owner.UndoDnsAsync(this), () => IsApplied);
        PolicyCommand = new RelayCommand(() => ProcessRunner.ShellOpen(p.PolicyUrl));
    }

    public DnsProvider Provider { get; }
    public AdapterInfo Adapter { get; }
    public string Name => Provider.Name;
    public string Addresses => Provider.AddressesText;
    public string Strengths => Provider.Strengths;
    public string Drawbacks => Provider.Drawbacks;
    public string TweakId => $"dns:{Adapter.Id}:{Provider.Id}".ToLowerInvariant();
    public bool IsApplied => AppServices.Journal.HasActiveChanges(TweakId);
    public bool IsCurrent => DnsProviders.Match(Adapter.DnsServers)?.Id == Provider.Id;
    public double? MeasuredMs { get; set; }
    public string MeasuredText => MeasuredMs is { } ms ? string.Format(Loc.Get("MeasuredHere"), ms) : "";

    public ICommand ApplyCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand PolicyCommand { get; }

    public void Refresh() => OnPropertyChanged(string.Empty);
}

public sealed class AdviceViewModel(NetworkAdvice advice, Func<string, Task> act) : ObservableObject
{
    public string Title => advice.Title;
    public string Detail => advice.Detail;
    public string Severity => advice.Severity;
    public bool HasAction => advice.ActionId is not null;
    public string ActionLabel => advice.ActionLabel ?? "";
    public ICommand ActionCommand { get; } = new AsyncCommand(() => advice.ActionId is { } id ? act(id) : Task.CompletedTask);
}

public sealed class MeasurementRow(NetworkMeasurement m)
{
    public NetworkMeasurement M => m;
    public string TimeText => m.Time.ToString("g");
    public string KindText => Loc.Get(m.Kind == "speed" ? "SpeedTest" : "PingTest");
    public string Down => m.DownloadMbps is { } d ? $"{d:0.#} Mbps" : "—";
    public string Up => m.UploadMbps is { } u ? $"{u:0.#} Mbps" : "—";
    public string Ping => m.PingMs is { } p ? $"{p:0.#} ms" : "—";
    public string Jitter => m.JitterMs is { } j ? $"{j:0.#} ms" : "—";
    public string Loss => m.LossPercent is { } l ? $"{l:0.#} %" : "—";
    public string Bloat => m.Bufferbloat is { } b ? $"+{b:0} ms" : "—";
    public string Adapter => m.Adapter;
}

/// <summary>
/// Network Optimization: measure first (speed, latency under load, DNS, MTU), explain what the numbers mean, and offer
/// only the changes a measurement supports. Every change is journaled and undoable; nothing promises lower ping.
/// </summary>
public sealed class NetworkViewModel : PageViewModel
{
    private PingStats? _gateway, _internet;
    private string _diagnosis = "";
    private List<double> _pingSeries = [];
    private CancellationTokenSource? _cts;
    private NetworkMeasurement? _lastSpeed;
    private TcpGlobalInfo? _tcp;
    private HashSet<string> _ipv6Unbound = [];
    private int? _pathMtu;
    private bool _speedRunning;
    private string _speedPhase = "";
    private double _speedMbps, _speedFraction;
    private AdapterInfo? _active;
    public override string Key => "network";

    public NetworkViewModel()
    {
        PingCommand = new AsyncCommand(RunPingAsync, () => _cts is null);
        SpeedCommand = new AsyncCommand(RunSpeedAsync, () => _cts is null && _active is not null);
        DnsCommand = new AsyncCommand(CompareDnsAsync, () => _cts is null);
        MtuCommand = new AsyncCommand(ProbeMtuAsync, () => _cts is null);
        ClearCachesCommand = new AsyncCommand(ClearCachesAsync);
        ResetCommand = new AsyncCommand(ResetStackAsync);
        RefreshCommand = new AsyncCommand(() => Busy(Loc.Get("Loading"), LoadAllAsync));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _cts is not null);
        ConnectionsCommand = new AsyncCommand(LoadConnectionsAsync);
        ClearHistoryCommand = new RelayCommand(ClearHistory, () => History.Count > 0);
        OpenDnsSettingsCommand = new RelayCommand(() => ProcessRunner.ShellOpen("ms-settings:network-status"));
        OpenTroubleshootCommand = new RelayCommand(() => ProcessRunner.ShellOpen("ms-settings:troubleshoot"));
        AppServices.ConfigurationChanged += () => OnUi(RefreshProviders);
    }

    public ObservableCollection<AdapterInfo> Adapters { get; } = [];
    public ObservableCollection<CheckItem> WifiAdvice { get; } = [];
    public ObservableCollection<DnsResult> Dns { get; } = [];
    public ObservableCollection<DnsProviderViewModel> Providers { get; } = [];
    public ObservableCollection<AdviceViewModel> Advice { get; } = [];
    public ObservableCollection<MeasurementRow> History { get; } = [];
    public ObservableCollection<ProcessConnections> Connections { get; } = [];
    private List<WifiStatus> _wifi = [];

    public PingStats? Gateway { get => _gateway; private set => Set(ref _gateway, value); }
    public PingStats? Internet { get => _internet; private set => Set(ref _internet, value); }
    public string Diagnosis { get => _diagnosis; private set => Set(ref _diagnosis, value); }
    public IReadOnlyList<double> PingSeries => _pingSeries.ToArray();
    public string DnsNote => Loc.Get("DnsNote");

    public bool SpeedRunning { get => _speedRunning; private set => Set(ref _speedRunning, value); }
    public string SpeedPhase { get => _speedPhase; private set => Set(ref _speedPhase, value); }
    public double SpeedMbps { get => _speedMbps; private set => Set(ref _speedMbps, value); }
    public double SpeedFraction { get => _speedFraction; private set => Set(ref _speedFraction, value); }
    public string SpeedNote => string.Format(Loc.Get("SpeedTestNote"), SpeedTest.Host, (SpeedTest.MaxDownloadBytes + SpeedTest.MaxUploadBytes) / 1_000_000);
    public NetworkMeasurement? LastSpeed { get => _lastSpeed; private set { Set(ref _lastSpeed, value); OnPropertiesChanged(nameof(HasSpeed), nameof(DownText), nameof(UpText), nameof(IdlePingText), nameof(LoadedPingText), nameof(BloatText), nameof(BloatSeverity), nameof(SpeedMeta), nameof(CompareText)); } }
    public bool HasSpeed => _lastSpeed is not null;
    public string DownText => _lastSpeed?.DownloadMbps is { } d ? $"{d:0.#}" : "—";
    public string UpText => _lastSpeed?.UploadMbps is { } u ? $"{u:0.#}" : "—";
    public string IdlePingText => _lastSpeed?.PingMs is { } p ? $"{p:0.#} ms" + (_lastSpeed.JitterMs is { } j ? $" · ±{j:0.#}" : "") : "—";
    public string LoadedPingText => _lastSpeed?.LoadedPingMs is { } p ? $"{p:0.#} ms" : "—";
    public string BloatText => _lastSpeed?.Bufferbloat is { } b ? $"+{b:0} ms" : "—";
    public string BloatSeverity => _lastSpeed?.Bufferbloat is { } b ? b < 25 ? "good" : b < 60 ? "warn" : "bad" : "";
    public string SpeedMeta => _lastSpeed is null ? "" : string.Format(Loc.Get("SpeedMeta"), _lastSpeed.Server, Format.Bytes(_lastSpeed.BytesUsed), _lastSpeed.Time.ToString("t"));

    /// <summary>Before/after: the latest speed test against the previous one on the same adapter.</summary>
    public string CompareText
    {
        get
        {
            if (_lastSpeed is null) return "";
            var prev = History.Select(h => h.M).Where(m => m.Kind == "speed" && m != _lastSpeed && m.Adapter == _lastSpeed.Adapter && m.Time < _lastSpeed.Time)
                .OrderByDescending(m => m.Time).FirstOrDefault();
            if (prev is null) return Loc.Get("CompareFirst");
            static string Pct(double? now, double? before) => now is { } n && before is { } b && b > 0 ? $"{(n - b) / b * 100:+0;-0} %" : "—";
            static string Ms(double? now, double? before) => now is { } n && before is { } b ? $"{n - b:+0.#;-0.#} ms" : "—";
            return string.Format(Loc.Get("CompareWith"), prev.Time.ToString("g"), Pct(_lastSpeed.DownloadMbps, prev.DownloadMbps), Pct(_lastSpeed.UploadMbps, prev.UploadMbps),
                Ms(_lastSpeed.PingMs, prev.PingMs), Ms(_lastSpeed.Bufferbloat, prev.Bufferbloat));
        }
    }

    public string ActiveAdapterText => _active is null ? Loc.Get("NoActiveAdapter")
        : $"{_active.Name} · {(_active.IsWireless ? "Wi-Fi" : _active.Type)} · {_active.SpeedText}";
    public string AutoTuningText => _tcp?.AutoTuning ?? "—";
    public string CongestionText => _tcp?.Congestion ?? "—";
    public string RssText => _tcp?.ReceiveSideScaling switch { true => Loc.Get("On"), false => Loc.Get("Off"), _ => "—" };
    public string Ipv6Text => _active is null ? "—"
        : _ipv6Unbound.Contains(_active.Id) ? Loc.Get("Ipv6Disabled")
        : _active.HasGlobalIPv6 ? Loc.Get("Ipv6Working") : Loc.Get("Ipv6LocalOnly");
    public string MtuText => _active is null ? "—" : $"{_active.Mtu}" + (_pathMtu is { } p ? " · " + string.Format(Loc.Get("PathMtuIs"), p) : "");
    public bool HasConnections => Connections.Count > 0;

    public ICommand PingCommand { get; }
    public ICommand SpeedCommand { get; }
    public ICommand DnsCommand { get; }
    public ICommand MtuCommand { get; }
    public ICommand ClearCachesCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ConnectionsCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand OpenDnsSettingsCommand { get; }
    public ICommand OpenTroubleshootCommand { get; }

    protected override Task LoadAsync() => Busy(Loc.Get("Loading"), LoadAllAsync);

    private async Task LoadAllAsync()
    {
        var (adapters, wifi, tcp, unbound) = await Task.Run(() => (NetworkDiagnostics.Adapters(), WifiInfo.Query(), NetworkConfig.Tcp(), NetworkConfig.Ipv6Unbound()));
        Adapters.Clear();
        foreach (var a in adapters) Adapters.Add(a);
        _active = adapters.FirstOrDefault(a => a.Gateways.Count > 0);
        _wifi = wifi;
        _tcp = tcp;
        _ipv6Unbound = unbound;
        WifiAdvice.Clear();
        foreach (var w in wifi)
            WifiAdvice.Add(new CheckItem($"{w.Ssid} · {w.Band} · {Loc.Get("Channel")} {w.Channel}", $"{w.SignalQuality} %{(w.Rssi is { } r ? $" ({r} dBm)" : "")}",
                w.SignalQuality >= 70 ? "good" : w.SignalQuality >= 45 ? "warn" : "bad", WifiInfo.Advice(w)));

        History.Clear();
        foreach (var m in NetworkHistory.Load().OrderByDescending(m => m.Time)) History.Add(new MeasurementRow(m));
        LastSpeed = History.Select(h => h.M).FirstOrDefault(m => m.Kind == "speed");

        BuildProviders();
        await UpdateAdviceAsync();
        OnPropertiesChanged(nameof(ActiveAdapterText), nameof(AutoTuningText), nameof(CongestionText), nameof(RssText), nameof(Ipv6Text), nameof(MtuText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void BuildProviders()
    {
        Providers.Clear();
        if (_active is null) return;
        foreach (var p in DnsProviders.All)
        {
            var vm = new DnsProviderViewModel(p, _active, this);
            vm.MeasuredMs = Dns.FirstOrDefault(d => p.V4.Contains(d.Server))?.MedianMs;
            Providers.Add(vm);
        }
    }

    private void RefreshProviders()
    {
        foreach (var p in Providers) p.Refresh();
    }

    private async Task UpdateAdviceAsync()
    {
        var deliveryLanOnly = AppServices.Engine.Find("network.deliveryopt") is { } t && t.Actions.All(a => a.IsInDesiredState());
        var power = _active is null ? null : await Task.Run(() => NetworkConfig.AdapterPowerTweak(_active));
        var snapshot = new NetworkSnapshot
        {
            Adapters = Adapters.ToList(),
            Wifi = _wifi,
            Tcp = _tcp,
            Ipv6Unbound = _ipv6Unbound,
            Gateway = Gateway,
            Internet = Internet,
            Dns = Dns.ToList(),
            Speed = LastSpeed,
            PathMtu = _pathMtu,
            DeliveryOptimizationLanOnly = deliveryLanOnly,
            HasPowerSavingTweak = power is not null && !power.Actions.All(a => a.IsInDesiredState()),
        };
        Advice.Clear();
        foreach (var a in NetworkAdvisor.Advise(snapshot)) Advice.Add(new AdviceViewModel(a, ActAsync));
    }

    private async Task ActAsync(string id)
    {
        if (id == "drivers") { Navigator.Go("drivers"); return; }
        if (id.StartsWith("dns:") && Providers.FirstOrDefault(p => p.Provider.Id == id[4..]) is { } provider)
        {
            await ApplyDnsAsync(provider);
            return;
        }
        Tweak? tweak = id switch
        {
            "autotuning" => AppServices.Engine.Find("network.autotuning"),
            "deliveryopt" => AppServices.Engine.Find("network.deliveryopt"),
            "adapterpower" when _active is not null => NetworkConfig.AdapterPowerTweak(_active),
            _ => null,
        };
        if (tweak is null) return;
        if (id == "adapterpower") AppServices.Engine.Register(tweak);
        await TweakApplier.ApplyAsync([tweak], tweak.Name, "network");
        await LoadAllAsync();
    }

    // ───────── Measurements ─────────

    private CancellationTokenSource Begin()
    {
        _cts = new CancellationTokenSource();
        CommandManager.InvalidateRequerySuggested();
        return _cts;
    }

    private void End()
    {
        _cts = null;
        CommandManager.InvalidateRequerySuggested();
    }

    private void Record(NetworkMeasurement m)
    {
        NetworkHistory.Add(m);
        History.Insert(0, new MeasurementRow(m));
        while (History.Count > 60) History.RemoveAt(History.Count - 1);
    }

    private async Task RunSpeedAsync()
    {
        if (_active is null) return;
        var cts = Begin();
        SpeedRunning = true;
        SpeedMbps = 0;
        SpeedFraction = 0;
        try
        {
            var progress = new Progress<SpeedTestProgress>(p =>
            {
                SpeedPhase = Loc.Get("SpeedPhase_" + p.Phase);
                SpeedMbps = p.Mbps;
                SpeedFraction = p.Fraction;
            });
            Log.Info($"Speed test started on {_active.Name}");
            var m = await SpeedTest.RunAsync(_active.Name, progress, cts.Token);
            Record(m);
            LastSpeed = m;
            await UpdateAdviceAsync();
        }
        catch (OperationCanceledException) { Notifier.Info(Loc.Get("TestCancelled")); }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TimeoutException)
        {
            Notifier.Error(string.Format(Loc.Get("SpeedTestFailed"), ex.Message));
        }
        finally
        {
            SpeedRunning = false;
            SpeedPhase = "";
            End();
        }
    }

    private async Task RunPingAsync()
    {
        var cts = Begin();
        var adapter = _active;
        _pingSeries = [];
        try
        {
            await Busy(Loc.Get("PingRunning"), async () =>
            {
                if (adapter?.Gateways.FirstOrDefault() is { } gw)
                    Gateway = await NetworkDiagnostics.PingAsync(gw, Loc.Get("Router"), 30, TimeSpan.FromMilliseconds(200), null, cts.Token);
                var progress = new Progress<PingStats>(s =>
                {
                    _pingSeries = s.Samples.Select(x => x ?? 0).ToList();
                    OnPropertyChanged(nameof(PingSeries));
                    BusyText = $"{Loc.Get("PingRunning")} {s.Sent}/60";
                });
                Internet = await NetworkDiagnostics.PingAsync("1.1.1.1", "Internet (1.1.1.1)", 60, TimeSpan.FromMilliseconds(250), progress, cts.Token);
                Diagnosis = NetworkDiagnostics.Diagnose(Gateway, Internet, adapter?.IsWireless ?? false);
                Record(new NetworkMeasurement
                {
                    Kind = "ping", Adapter = adapter?.Name ?? "", Server = "1.1.1.1",
                    PingMs = Internet.Average, JitterMs = Internet.Jitter, LossPercent = Internet.LossPercent,
                });
                await UpdateAdviceAsync();
            });
        }
        finally { End(); }
    }

    private async Task CompareDnsAsync()
    {
        var cts = Begin();
        try
        {
            await Busy(Loc.Get("DnsRunning"), async () =>
            {
                var results = await NetworkDiagnostics.CompareDnsAsync(BusyProgress, cts.Token);
                Dns.Clear();
                foreach (var r in results.OrderBy(r => r.MedianMs ?? double.MaxValue)) Dns.Add(r);
                BuildProviders();
                await UpdateAdviceAsync();
            });
        }
        finally { End(); }
    }

    private async Task ProbeMtuAsync()
    {
        var cts = Begin();
        try
        {
            await Busy(Loc.Get("MtuRunning"), async () =>
            {
                _pathMtu = await NetworkConfig.ProbePathMtuAsync("1.1.1.1", cts.Token);
                if (_pathMtu is null) Notifier.Warning(Loc.Get("MtuFailed"));
                OnPropertyChanged(nameof(MtuText));
                await UpdateAdviceAsync();
            });
        }
        finally { End(); }
    }

    private async Task LoadConnectionsAsync()
    {
        var list = await Task.Run(NetworkConfig.Connections);
        Connections.Clear();
        foreach (var c in list.OrderByDescending(c => c.Established).Take(40)) Connections.Add(c);
        OnPropertyChanged(nameof(HasConnections));
    }

    private void ClearHistory()
    {
        if (!ConfirmDialog.Show(Loc.Get("ClearHistoryTitle"), Loc.Get("ClearMeasurementsMessage"), confirmText: Loc.Get("Clear")))
            return;
        NetworkHistory.Clear();
        History.Clear();
        OnPropertyChanged(nameof(CompareText));
    }

    // ───────── Changes ─────────

    internal async Task ApplyDnsAsync(DnsProviderViewModel item)
    {
        // Every provider of this adapter shares a conflict group, so registering them all lets the engine replace one
        // with another while keeping the original (DHCP or static) servers as the undo target.
        foreach (var p in Providers) AppServices.Engine.Register(NetworkConfig.DnsTweak(p.Adapter, p.Provider));
        var tweak = NetworkConfig.DnsTweak(item.Adapter, item.Provider);
        AppServices.Engine.Register(tweak);
        await TweakApplier.ApplyAsync([tweak], tweak.Name, "network:dns");
        await LoadAllAsync();
    }

    internal async Task UndoDnsAsync(DnsProviderViewModel item)
    {
        await TweakApplier.UndoAsync(item.TweakId, item.Name);
        await LoadAllAsync();
    }

    private async Task ClearCachesAsync()
    {
        var steps = await NetworkConfig.ClearCachesAsync();
        var failed = steps.Where(s => !s.Ok).Select(s => s.Step).ToList();
        if (failed.Count == 0) Notifier.Success(Loc.Get("CachesCleared"));
        else Notifier.Warning(string.Format(Loc.Get("CachesPartlyCleared"), string.Join(", ", failed)));
        if (!Elevation.IsElevated) Notifier.Info(Loc.Get("CachesAdminNote"));
    }

    private async Task ResetStackAsync()
    {
        if (!Elevation.IsElevated) { Notifier.Warning(Loc.Get("NeedsAdmin")); return; }
        if (!ConfirmDialog.Show(Loc.Get("ResetNetworkTitle"), Loc.Get("ResetNetworkMessage"), [Loc.Get("ResetNetworkDetail1"), Loc.Get("ResetNetworkDetail2"), Loc.Get("ResetNetworkDetail3")],
                Loc.Get("Continue"), danger: true))
            return;
        if (!ConfirmDialog.Show(Loc.Get("ResetNetworkTitle"), Loc.Get("ResetNetworkConfirm2"), confirmText: Loc.Get("ResetNetwork"), danger: true,
                acknowledge: Loc.Get("ResetNetworkAck")))
            return;
        await Busy(Loc.Get("ResettingNetwork"), async () =>
        {
            var (ok, output) = await NetworkDiagnostics.ResetStackAsync();
            AppServices.Journal.RecordIrreversible(Loc.Get("ResetNetworkTitle"), "network",
                [("Winsock and TCP/IP reset", "netsh winsock reset; netsh int ip reset", "Reconfigure static IP/DNS/VPN settings manually if you had any.")]);
            if (ok) Notifier.Success(Loc.Get("NetworkResetDone"));
            else Notifier.Error(output);
        });
    }
}
