using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using WhisperBatch.Core;

namespace WhisperBatch.App.Infrastructure;

/// <summary>XAML から表示文字列を引くための元。表示言語が変わるとすべての文字列を読み直させる。</summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Localizer.Shared.String(key);

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}

/// <summary>XAML で <c>{l:L キー}</c> と書くと表示文字列になる（表示言語の切り替えにも追従する）</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LExtension : MarkupExtension
{
    public LExtension() { }

    public LExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = LocSource.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
