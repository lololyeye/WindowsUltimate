using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WindowsUltimate.Core.Diagnostics;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Optimization;

namespace WindowsUltimate.App.Converters;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var b = value switch
        {
            bool x => x,
            int i => i > 0,
            string s => !string.IsNullOrWhiteSpace(s),
            System.Collections.ICollection col => col.Count > 0,
            null => false,
            _ => true,
        };
        return b ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBool : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is not true;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is not true;
}

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        long l => Format.Bytes(l),
        int i => Format.Bytes(i),
        double d => Format.Bytes((long)d),
        _ => "",
    };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

internal static class Palette
{
    public static Brush Get(string key) => (Brush)Application.Current.Resources[key];
}

public sealed class SeverityToBrush : IValueConverter
{
    public bool Soft { get; set; }

    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var key = value switch
        {
            Severity.Critical => "Danger",
            Severity.Warning => "Warning",
            Severity.Suggestion => "Info",
            Severity.Good => "Success",
            "good" => "Success",
            "warn" => "Warning",
            "bad" => "Danger",
            "info" => "Info",
            _ => Soft ? "Neutral" : "TextTertiary",
        };
        return Palette.Get(Soft ? $"B.{key}Soft" : $"B.{key}");
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class SeverityToGlyph : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        Severity.Critical or "bad" => "",  // ErrorBadge
        Severity.Warning or "warn" => "",  // Warning
        Severity.Suggestion => "",         // Lightbulb
        Severity.Good or "good" => "",     // Completed
        _ => "",                           // Info
    };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class RiskToBrush : IValueConverter
{
    public bool Soft { get; set; }

    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var key = value switch
        {
            RiskLevel.Safe => "Success",
            RiskLevel.LowRisk => "Info",
            RiskLevel.Moderate => "Warning",
            _ => "Danger",
        };
        return Palette.Get(Soft ? $"B.{key}Soft" : $"B.{key}");
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class RiskToLabel : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        RiskLevel.Safe => Localization.Loc.Get("RiskSafe"),
        RiskLevel.LowRisk => Localization.Loc.Get("RiskLow"),
        RiskLevel.Moderate => Localization.Loc.Get("RiskModerate"),
        RiskLevel.Advanced => Localization.Loc.Get("RiskAdvanced"),
        _ => Localization.Loc.Get("RiskDangerous"),
    };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class ScoreToBrush : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is int s
        ? Palette.Get(s >= 85 ? "B.Success" : s >= 60 ? "B.Warning" : "B.Danger")
        : Palette.Get("B.Accent");

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class PercentToBrush : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var v = value is double d ? d : 0;
        return Palette.Get(v >= 90 ? "B.Danger" : v >= 75 ? "B.Warning" : "B.Accent");
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
