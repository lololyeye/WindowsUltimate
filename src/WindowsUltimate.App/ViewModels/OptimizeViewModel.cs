using System.Windows.Input;
using WindowsUltimate.App.Localization;
using WindowsUltimate.App.Mvvm;
using WindowsUltimate.App.Services;
using WindowsUltimate.Core.Optimization;

namespace WindowsUltimate.App.ViewModels;

/// <summary>Every documented optimization in one searchable, filterable list, plus the one-click safe set.</summary>
public sealed class OptimizeViewModel : PageViewModel
{
    private List<TweakEvaluation> _safe = [];
    public override string Key => "optimize";

    public OptimizeViewModel()
    {
        ApplySafeCommand = new AsyncCommand(ApplySafeAsync, () => _safe.Count > 0 && !IsBusy);
        OpenHistoryCommand = new RelayCommand(() => Navigator.Go("history"));
        AppServices.ConfigurationChanged += () => OnUi(async () => await RefreshSafeAsync());
    }

    public TweakCollection Tweaks { get; } = new(t => t.Category is not TweakCategory.Startup, showFilters: true);
    public ICommand ApplySafeCommand { get; }
    public ICommand OpenHistoryCommand { get; }
    public int SafeCount => _safe.Count;
    public bool HasSafe => _safe.Count > 0;
    public string SafeSummary => _safe.Count == 0 ? Loc.Get("NoSafeRecommendations") : string.Join(" · ", _safe.Take(5).Select(s => s.Tweak.Name)) + (_safe.Count > 5 ? $" +{_safe.Count - 5}" : "");

    protected override Task LoadAsync() => Busy(Loc.Get("Analyzing"), async () =>
    {
        await Tweaks.RefreshAsync();
        await RefreshSafeAsync();
    });

    private async Task RefreshSafeAsync()
    {
        _safe = await TweakApplier.SafeRecommendationsAsync();
        OnPropertiesChanged(nameof(SafeCount), nameof(HasSafe), nameof(SafeSummary));
    }

    private Task ApplySafeAsync() => TweakApplier.ApplyAsync(_safe.Select(s => s.Tweak).ToList(), Loc.Get("ApplySafeTitle"), "optimize:safe");
}
