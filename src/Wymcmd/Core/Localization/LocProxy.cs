using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace Wymcmd.Core.Localization;

/// <summary>
/// Indexer the XAML binds to. Switching language raises a change for every key at once,
/// so the whole window retranslates without being reloaded.
/// </summary>
public sealed class LocProxy : INotifyPropertyChanged
{
    public static LocProxy Instance { get; } = new();

    private LocProxy() => Loc.Changed += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));

    public string this[string key] => Loc.T(key);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Usage in XAML: Text="{loc:T app.tagline}"</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public TExtension() : this("") { }

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]")
        {
            Source = LocProxy.Instance,
            Mode = BindingMode.OneWay
        }.ProvideValue(serviceProvider);
}
