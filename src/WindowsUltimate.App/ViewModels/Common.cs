using System.Collections.ObjectModel;
using System.Windows.Input;
using WindowsUltimate.App.Localization;
using WindowsUltimate.App.Mvvm;
using WindowsUltimate.App.Services;
using WindowsUltimate.Core.Diagnostics;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Optimization;

namespace WindowsUltimate.App.ViewModels;

/// <summary>One tweak row: state, compatibility, recommendation and the full documentation shown when expanded.</summary>
public sealed class TweakItemViewModel : ObservableObject
{
    private TweakEvaluation _eval;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _hasActiveChanges;
    private readonly Action? _selectionChanged;

    public TweakItemViewModel(TweakEvaluation eval, Action? selectionChanged = null)
    {
        _eval = eval;
        _selectionChanged = selectionChanged;
        _hasActiveChanges = AppServices.Journal.HasActiveChanges(eval.Tweak.Id);
        ApplyCommand = new AsyncCommand(() => TweakApplier.ApplyAsync([Tweak], Tweak.Name, "manual:" + Tweak.Id), () => CanApply);
        UndoCommand = new AsyncCommand(() => TweakApplier.UndoAsync(Tweak), () => CanUndo);
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        OpenSourceCommand = new RelayCommand(() => ProcessRunner.ShellOpen(Tweak.Source), () => SourceIsLink);
    }

    public Tweak Tweak => _eval.Tweak;
    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public RiskLevel Risk => Tweak.Risk;
    public string WhatItChanges => Tweak.WhatItChanges;
    public string WhyItHelps => Tweak.WhyItHelps;
    public string ExpectedImpact => Tweak.ExpectedImpact;
    public string Disadvantages => Tweak.Disadvantages;
    public string TechnicalDetails => Tweak.TechnicalDetails;
    public string Source => Tweak.Source;
    public bool SourceIsLink => Tweak.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    public string UndoInfo => Tweak.UndoDescription;
    public string BenefitText => Loc.Get("Benefit" + Tweak.Benefit);
    public string RestartText => Tweak.Restart switch
    {
        RestartRequirement.Restart => Loc.Get("RestartRequired"),
        RestartRequirement.SignOut => Loc.Get("SignOutRequired"),
        _ => Loc.Get("NoRestart"),
    };

    public TweakState State => _eval.State;
    public bool IsApplied => _eval.State == TweakState.Applied;
    public string StateText => _eval.State switch
    {
        TweakState.Applied => Loc.Get("StateApplied"),
        TweakState.Partial => Loc.Get("StatePartial"),
        TweakState.NotApplied => Loc.Get("StateNotApplied"),
        _ => Loc.Get("StateUnknown"),
    };
    public string StateSeverity => _eval.State switch { TweakState.Applied => "good", TweakState.Partial => "warn", _ => "" };

    public bool IsCompatible => _eval.Compatibility.IsCompatible;
    public string? IncompatibleReason => _eval.Compatibility.Reason;
    public bool IsRecommended => _eval.Recommendation.IsRecommended && IsCompatible && !IsApplied;
    public string RecommendationReason => _eval.Recommendation.Reason;
    public bool RequiresAdmin => Tweak.RequiresAdmin;
    public bool AdminBlocked => Tweak.RequiresAdmin && !Elevation.IsElevated;

    public bool CanApply => IsCompatible && !IsApplied && !AdminBlocked;
    public bool CanUndo => _hasActiveChanges && (!AdminBlocked);
    public bool HasActiveChanges => _hasActiveChanges;

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value && CanApply)) _selectionChanged?.Invoke();
        }
    }

    public ICommand ApplyCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand ToggleCommand { get; }
    public ICommand OpenSourceCommand { get; }

    public void Update(TweakEvaluation eval)
    {
        _eval = eval;
        _hasActiveChanges = AppServices.Journal.HasActiveChanges(eval.Tweak.Id);
        if (!CanApply) _isSelected = false;
        OnPropertyChanged(string.Empty); // refresh every binding
    }
}

/// <summary>A filtered, evaluated set of catalog tweaks with multi-selection and batch apply.</summary>
public sealed class TweakCollection : ObservableObject
{
    private readonly Func<Tweak, bool> _filter;
    private int _selectedCount;

    public TweakCollection(Func<Tweak, bool> filter)
    {
        _filter = filter;
        ApplySelectedCommand = new AsyncCommand(ApplySelectedAsync, () => SelectedCount > 0);
        SelectRecommendedCommand = new RelayCommand(() =>
        {
            foreach (var i in Items) i.IsSelected = i.IsRecommended;
        });
        AppServices.ConfigurationChanged += () => OnUi(async () => await RefreshAsync());
    }

    public ObservableCollection<TweakItemViewModel> Items { get; } = [];
    public int SelectedCount { get => _selectedCount; private set => Set(ref _selectedCount, value); }
    public int RecommendedCount => Items.Count(i => i.IsRecommended);
    public int AppliedCount => Items.Count(i => i.IsApplied);
    public ICommand ApplySelectedCommand { get; }
    public ICommand SelectRecommendedCommand { get; }

    public async Task RefreshAsync()
    {
        var profile = await AppServices.ProfileTask;
        var evals = await Task.Run(() => AppServices.Engine.EvaluateAll(profile, _filter));
        if (Items.Count == 0)
        {
            foreach (var e in evals.OrderByDescending(e => e.Recommendation.IsRecommended && e.State != TweakState.Applied).ThenBy(e => e.Tweak.Risk))
                Items.Add(new TweakItemViewModel(e, () => SelectedCount = Items.Count(i => i.IsSelected)));
        }
        else
        {
            foreach (var item in Items)
            {
                var e = evals.FirstOrDefault(x => x.Tweak.Id == item.Tweak.Id);
                if (e is not null) item.Update(e);
            }
        }
        SelectedCount = Items.Count(i => i.IsSelected);
        OnPropertiesChanged(nameof(RecommendedCount), nameof(AppliedCount));
    }

    private async Task ApplySelectedAsync()
    {
        var tweaks = Items.Where(i => i.IsSelected).Select(i => i.Tweak).ToList();
        await TweakApplier.ApplyAsync(tweaks, string.Format(Loc.Get("ApplySelectedTitle"), tweaks.Count), "manual:batch");
    }
}

public sealed class FindingViewModel(Finding finding) : ObservableObject
{
    public Finding Finding => finding;
    public Severity Severity => finding.Severity;
    public string Title => finding.Title;
    public string Detail => finding.Detail;
    public string? Evidence => finding.Evidence;
    public string Area => finding.Area;
    public string PenaltyText => finding.ScorePenalty > 0 ? $"−{finding.ScorePenalty}" : "";
    public bool HasAction => finding.TweakId is not null || finding.NavigateTo is not null || finding.OpenUri is not null;
    public string ActionLabel => finding.ActionLabel ?? Loc.Get("Fix");

    public ICommand ActCommand { get; } = new AsyncCommand(async () =>
    {
        if (finding.TweakId is { } id && AppServices.Engine.Find(id) is { } tweak)
            await TweakApplier.ApplyAsync([tweak], tweak.Name, "finding:" + finding.Id);
        else if (finding.NavigateTo is { } page)
            Navigator.Go(page);
        else if (finding.OpenUri is { } uri)
            ProcessRunner.ShellOpen(uri);
    });
}

/// <summary>Simple status line used by analysis panels (label, value, status colour, explanation).</summary>
public sealed record CheckItem(string Name, string Value, string Status, string Detail = "")
{
    public string Glyph => Status switch { "good" => "", "warn" => "", "bad" => "", _ => "" };
}
