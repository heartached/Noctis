using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Noctis.Localization;

/// <summary>
/// <c>{loc:T Nav.Home}</c> — binds a XAML property to a UI string from <see cref="Loc"/>.
/// It is a live binding, not a one-shot value, so switching the language in Settings
/// re-labels every open view without a restart.
/// </summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) => Key = key;

    /// <summary>Resource key in Strings.resx, e.g. "Nav.Home".</summary>
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
}
