using System.Windows;
using WindowsUltimate.App.Localization;
using WindowsUltimate.App.Services;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Optimization;

namespace WindowsUltimate.App.Dialogs;

public sealed record ConsentResult(bool CreateRestorePoint);

/// <summary>
/// Shows exactly which settings will change (location, current value → new value), the risk, restart and
/// reversibility of every tweak. Moderate-or-higher risk requires an explicit second confirmation.
/// </summary>
public partial class ConsentDialog : DialogBase
{
    public sealed record Change(string Location, string From, string To);

    public sealed class Row(Tweak t)
    {
        public string Name => t.Name;
        public RiskLevel Risk => t.Risk;
        public string WhatItChanges => t.WhatItChanges;
        public string Disadvantages => "⚠ " + t.Disadvantages;
        public bool ShowDisadvantages => t.Risk >= RiskLevel.LowRisk;
        public string RestartText => t.Restart switch
        {
            RestartRequirement.Restart => Loc.Get("RestartRequired"),
            RestartRequirement.SignOut => Loc.Get("SignOutRequired"),
            _ => Loc.Get("NoRestart"),
        };
        public string ReversibleText => t.IsReversible ? Loc.Get("ReversibleYes") : Loc.Get("ReversibleNo");
        public List<Change> Changes { get; } = t.Actions
            .Where(a => !Safe(a.IsInDesiredState))
            .Select(a => new Change(a.Describe(), Safe(a.DescribeCurrent), a.DescribeDesired()))
            .ToList();

        private static T Safe<T>(Func<T> f)
        {
            try { return f(); } catch { return default!; }
        }
    }

    private readonly bool _needsAck;

    private ConsentDialog(string title, IReadOnlyList<Tweak> tweaks, IReadOnlyList<(Tweak Tweak, string Reason)> blocked)
    {
        InitializeComponent();
        TitleText.Text = title;
        Items.ItemsSource = tweaks.Select(t => new Row(t)).ToList();

        if (blocked.Count > 0)
        {
            BlockedPanel.Visibility = Visibility.Visible;
            BlockedText.Text = string.Join("\n", blocked.Select(b => $"• {b.Tweak.Name} — {b.Reason}"));
        }

        RestorePointBox.IsEnabled = Elevation.IsElevated;
        RestorePointBox.IsChecked = Elevation.IsElevated && AppServices.Settings.CreateRestorePointByDefault && tweaks.Any(t => t.Risk >= RiskLevel.LowRisk || t.RequiresAdmin);
        RestorePointNote.Text = Elevation.IsElevated ? Loc.Get("ConsentRestorePointNote") : Loc.Get("ConsentRestorePointNeedsAdmin");

        _needsAck = tweaks.Any(t => t.Risk >= RiskLevel.Moderate);
        AckBox.Visibility = _needsAck ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.IsEnabled = !_needsAck;
        ApplyButton.Content = string.Format(Loc.Get("ApplyNChanges"), tweaks.Sum(t => Math.Max(1, new Row(t).Changes.Count)));

        var restart = tweaks.Select(t => t.Restart).DefaultIfEmpty().Max();
        SummaryText.Text = restart switch
        {
            RestartRequirement.Restart => Loc.Get("RestartRequired"),
            RestartRequirement.SignOut => Loc.Get("SignOutRequired"),
            _ => Loc.Get("NoRestart"),
        };
    }

    public static ConsentResult? Ask(string title, IReadOnlyList<Tweak> tweaks, IReadOnlyList<(Tweak, string)>? blocked = null)
    {
        var dlg = new ConsentDialog(title, tweaks, blocked ?? []);
        return dlg.ShowDialog() == true ? new ConsentResult(dlg.RestorePointBox.IsChecked == true) : null;
    }

    private void OnAckChanged(object sender, RoutedEventArgs e) => ApplyButton.IsEnabled = !_needsAck || AckBox.IsChecked == true;
    private void OnApply(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
