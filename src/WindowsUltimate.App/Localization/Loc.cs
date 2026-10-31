using System.ComponentModel;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Markup;

namespace WindowsUltimate.App.Localization;

/// <summary>
/// String table with live language switching. XAML uses {l:T Key}; code uses Loc.Get("Key").
/// English is the default and the fallback for any missing translation.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();
    public static readonly string[] Languages = ["en", "fr"];

    private readonly Dictionary<string, string> _fallback;
    private Dictionary<string, string> _strings;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Language { get; private set; } = "en";

    private Loc()
    {
        _fallback = Load("en");
        _strings = _fallback;
    }

    public string this[string key] =>
        _strings.TryGetValue(key, out var v) ? v : _fallback.TryGetValue(key, out var f) ? f : key;

    public static string Get(string key) => Instance[key];

    public void SetLanguage(string language)
    {
        if (!Languages.Contains(language)) language = "en";
        Language = language;
        _strings = language == "en" ? _fallback : Load(language);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    private static Dictionary<string, string> Load(string language)
    {
        var asm = typeof(Loc).Assembly;
        using var stream = asm.GetManifestResourceStream($"WindowsUltimate.App.Localization.Strings.{language}.json");
        if (stream is null) return new();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
    }
}

[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

/// <summary>Maps a page key ("dashboard") to its localized title.</summary>
public sealed class PageTitleConverter : IValueConverter
{
    public static PageTitleConverter Instance { get; } = new();
    public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => Loc.Get($"Page_{value}");
    public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
}
