using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
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

    /// <summary>
    /// The string names a Ctrl shortcut ("Search (Ctrl+F)"). On macOS the key is ⌘, so the
    /// translated text is shown with "Ctrl+" written as ⌘ — every language keeps its wording.
    /// </summary>
    public bool MacKeys { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
            Converter = MacKeys && OperatingSystem.IsMacOS() ? MacShortcutText.Instance : null,
        };

    private sealed class MacShortcutText : IValueConverter
    {
        public static readonly MacShortcutText Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is string s ? s.Replace("Ctrl+Enter", "⌘↩").Replace("Ctrl+", "⌘") : value;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
