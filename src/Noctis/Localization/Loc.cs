using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace Noctis.Localization;

/// <summary>
/// UI string lookup with live language switching. Strings live in <c>Localization/Strings.resx</c>
/// (English, the fallback) and one <c>Strings.{culture}.resx</c> per translation; the SDK
/// compiles them into satellite assemblies, so adding a language is adding a file — no code.
///
/// XAML binds through <see cref="TExtension"/> (<c>{loc:T Key}</c>) to the indexer here;
/// changing the culture raises one PropertyChanged for the indexer and every bound string
/// re-reads. Code uses <see cref="T"/>. Missing keys return the key itself so a typo is
/// visible in the UI instead of an empty label.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    /// <summary>Setting value meaning "follow the OS language".</summary>
    public const string SystemLanguage = "";

    /// <summary>Cultures that ship a translation, English first. Update when adding a resx.</summary>
    public static readonly IReadOnlyList<string> Supported = new[] { "en", "es" };

    private readonly ResourceManager _resources = new("Noctis.Localization.Strings", typeof(Loc).Assembly);
    private CultureInfo _culture = CultureInfo.CurrentUICulture;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the culture changes, for code that caches strings (sidebar labels).</summary>
    public event EventHandler? CultureChanged;

    /// <summary>The culture strings are currently served in.</summary>
    public CultureInfo Culture => _culture;

    /// <summary>Indexer for XAML bindings: <c>Instance["Nav.Home"]</c>.</summary>
    public string this[string key] => Get(key);

    /// <summary>The string for <paramref name="key"/> in the current culture (English fallback, key on miss).</summary>
    public static string T(string key) => Instance.Get(key);

    /// <summary><see cref="T"/> with <see cref="string.Format(IFormatProvider, string, object[])"/> in the current culture.</summary>
    public static string T(string key, params object[] args)
        => string.Format(Instance._culture, Instance.Get(key), args);

    private string Get(string key)
    {
        try { return _resources.GetString(key, _culture) ?? key; }
        catch (MissingManifestResourceException) { return key; }
    }

    /// <summary>
    /// Switches the UI language. <paramref name="languageCode"/> is a culture name ("es",
    /// "pt-BR") or <see cref="SystemLanguage"/> to follow the OS. Unknown names fall back to
    /// the OS culture. Every <c>{loc:T}</c> binding re-reads; <see cref="CultureChanged"/> fires.
    /// </summary>
    public void SetCulture(string? languageCode)
    {
        CultureInfo culture;
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            culture = CultureInfo.InstalledUICulture;
        }
        else
        {
            try { culture = CultureInfo.GetCultureInfo(languageCode.Trim()); }
            catch (CultureNotFoundException) { culture = CultureInfo.InstalledUICulture; }
        }

        if (culture.Name == _culture.Name) return;
        _culture = culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The language a stored setting resolves to: the setting itself when it names a shipped
    /// translation, else the OS language's parent if that ships, else English. Used by the
    /// Settings picker to show which entry is active.
    /// </summary>
    public static string Resolve(string? setting)
    {
        var name = string.IsNullOrWhiteSpace(setting) ? CultureInfo.InstalledUICulture.Name : setting.Trim();
        return Supported.FirstOrDefault(s => name.Equals(s, StringComparison.OrdinalIgnoreCase))
            ?? Supported.FirstOrDefault(s => name.StartsWith(s + "-", StringComparison.OrdinalIgnoreCase))
            ?? Supported[0];
    }
}
