using System.Collections.ObjectModel;
using System.Windows.Data;
using System.Windows.Input;
using WindowsUltimate.App.Localization;
using WindowsUltimate.App.Mvvm;
using WindowsUltimate.App.Services;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.SystemServices;

namespace WindowsUltimate.App.ViewModels;

public sealed class ServiceItemViewModel : ObservableObject
{
    private bool _expanded;

    public ServiceItemViewModel(ServiceInfo info, Func<ServiceItemViewModel, Task> change, Func<ServiceItemViewModel, Task> undo)
    {
        Info = info;
        ChangeCommand = new AsyncCommand(() => change(this), () => CanChange);
        UndoCommand = new AsyncCommand(() => undo(this), () => HasActiveChange && Elevation.IsElevated);
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public ServiceInfo Info { get; }
    public string Name => Info.Name;
    public string DisplayName => Info.DisplayName;
    public string Description => Info.Description;
    public string PathName => Info.PathName;
    public string Company => Info.Company.Length > 0 ? Info.Company : Loc.Get("UnknownPublisher");
    public string TweakId => $"service:{Info.Name}".ToLowerInvariant();
    public bool HasActiveChange => AppServices.Journal.HasActiveChanges(TweakId);

    public string StateText => Info.State switch
    {
        "Running" => Loc.Get("SvcRunning"), "Stopped" => Loc.Get("SvcStopped"), "" => "—", _ => Info.State,
    };
    public string StateSeverity => Info.IsRunning ? "good" : "";
    public string StartText => ServiceManager.StartLabel(Info.StartValue) + (Info.DelayedStart && Info.StartValue == 2 ? " " + Loc.Get("SvcDelayed") : "");

    public string ClassText => Loc.Get("SvcClass_" + Info.Class);
    public string ClassSeverity => Info.Class switch
    {
        ServiceClass.Essential or ServiceClass.Security => "bad",
        ServiceClass.KeepDefault => "info",
        ServiceClass.Optional => "good",
        _ => "",
    };

    public string Why => Info.Knowledge?.Why ?? Loc.Get("SvcWhy_" + Info.Class);
    public string Verdict => Info.OfferedStart is { } s
        ? string.Format(Loc.Get("SvcVerdictChange"), ServiceManager.StartLabel(s))
        : Info.Class switch
        {
            ServiceClass.Essential or ServiceClass.Security => Loc.Get("SvcVerdictNever"),
            ServiceClass.ThirdParty => Loc.Get("SvcVerdictThirdParty"),
            ServiceClass.Unclassified => Loc.Get("SvcVerdictUnknown"),
            _ => Loc.Get("SvcVerdictKeep"),
        };
    public string Benefit => Info.Knowledge?.Benefit ?? "";
    public string Drawback => Info.Knowledge?.Drawback ?? "";
    public bool HasTradeoffs => Info.OfferedStart is not null;

    public string MemoryText => Info.ProcessId <= 0 ? "—"
        : Info.SharedWith > 1 ? string.Format(Loc.Get("SvcMemoryShared"), Format.Bytes(Info.MemoryBytes), Info.SharedWith)
        : Format.Bytes(Info.MemoryBytes);

    public bool CanChange => Info.OfferedStart is not null && Elevation.IsElevated && !HasActiveChange;
    public bool ShowChange => Info.OfferedStart is not null && !HasActiveChange;
    public string ChangeLabel => Info.OfferedStart is { } s ? string.Format(Loc.Get("SvcSetTo"), ServiceManager.StartLabel(s)) : "";

    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
    public ICommand ChangeCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand ToggleCommand { get; }

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// Windows services with plain explanations. Changes are offered only for cataloged optional services, are journaled,
/// apply after restart and are undone from the page or from History. Essential, security and unknown services are read-only.
/// </summary>
public sealed class ServicesViewModel : PageViewModel
{
    private string _search = "";
    private string _filter = "all";
    private List<ServiceInfo> _all = [];
    public override string Key => "services";

    public ServicesViewModel()
    {
        RefreshCommand = new AsyncCommand(() => Busy(Loc.Get("Loading"), LoadServicesAsync));
        OpenServicesConsoleCommand = new RelayCommand(() => ProcessRunner.ShellOpen("services.msc"));
        CollectionViewSource.GetDefaultView(Items).Filter = o => o is ServiceItemViewModel s && Matches(s);
    }

    public ObservableCollection<ServiceItemViewModel> Items { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand OpenServicesConsoleCommand { get; }
    public bool IsElevated => Elevation.IsElevated;

    public string Search { get => _search; set { if (Set(ref _search, value ?? "")) Refilter(); } }
    public string Filter { get => _filter; set { if (Set(ref _filter, value ?? "all")) Refilter(); } }

    public int Total => _all.Count;
    public int RunningCount => _all.Count(s => s.IsRunning);
    public int ChangeableCount => _all.Count(s => s.OfferedStart is not null);
    public int ThirdPartyCount => _all.Count(s => s.Class == ServiceClass.ThirdParty);
    public int VisibleCount => Items.Count(Matches);

    protected override Task LoadAsync() => Busy(Loc.Get("Loading"), LoadServicesAsync);

    private async Task LoadServicesAsync()
    {
        _all = await Task.Run(ServiceManager.List);
        Items.Clear();
        foreach (var s in _all.OrderByDescending(s => s.OfferedStart is not null).ThenBy(s => s.Class == ServiceClass.ThirdParty ? 0 : 1).ThenBy(s => s.DisplayName))
            Items.Add(new ServiceItemViewModel(s, ChangeAsync, UndoAsync));
        OnPropertiesChanged(nameof(Total), nameof(RunningCount), nameof(ChangeableCount), nameof(ThirdPartyCount), nameof(VisibleCount));
    }

    private bool Matches(ServiceItemViewModel s)
    {
        var ok = Filter switch
        {
            "running" => s.Info.IsRunning,
            "changeable" => s.Info.OfferedStart is not null || s.HasActiveChange,
            "thirdparty" => s.Info.Class == ServiceClass.ThirdParty,
            "essential" => s.Info.Class is ServiceClass.Essential or ServiceClass.Security,
            _ => true,
        };
        var q = Search.Trim();
        return ok && (q.Length == 0 || s.DisplayName.Contains(q, StringComparison.CurrentCultureIgnoreCase) || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                      || s.Description.Contains(q, StringComparison.CurrentCultureIgnoreCase) || s.Company.Contains(q, StringComparison.CurrentCultureIgnoreCase));
    }

    private void Refilter()
    {
        CollectionViewSource.GetDefaultView(Items).Refresh();
        OnPropertyChanged(nameof(VisibleCount));
    }

    private async Task ChangeAsync(ServiceItemViewModel item)
    {
        if (ServiceManager.CreateChange(item.Info) is not { } tweak) return;
        AppServices.Engine.Register(tweak);
        await TweakApplier.ApplyAsync([tweak], tweak.Name, "services");
        item.Refresh();
    }

    private async Task UndoAsync(ServiceItemViewModel item)
    {
        await TweakApplier.UndoAsync(item.TweakId, item.DisplayName);
        item.Refresh();
    }
}
