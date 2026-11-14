using System.ComponentModel;

namespace WindowsUltimate.App.Services;

/// <summary>Simple / Advanced mode, bindable from XAML: {Binding IsAdvanced, Source={x:Static svc:UiMode.Instance}}.</summary>
public sealed class UiMode : INotifyPropertyChanged
{
    public static UiMode Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;

    public bool IsAdvanced => AppServices.Settings.AdvancedMode == true;
    public bool IsSimple => !IsAdvanced;

    public void Set(bool advanced)
    {
        if (advanced == IsAdvanced) return;
        AppServices.Settings.AdvancedMode = advanced;
        AppServices.Settings.Save();
        Core.Infrastructure.Log.Info($"Interface mode: {(advanced ? "advanced" : "simple")}");
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdvanced)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSimple)));
        Changed?.Invoke();
    }
}
