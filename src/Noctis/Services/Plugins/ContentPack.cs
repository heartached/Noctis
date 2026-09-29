using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.Services.Plugins;

/// <summary>
/// plugin.json's <c>"contents"</c> for <c>"type": "content"</c>: the pack-relative JSON files it
/// provides, per kind. Paths are checked here (relative, no "..", .json only); the files
/// themselves are read by <see cref="ContentPackLoader"/>. See docs/PLUGINS.md, "Content packs".
/// </summary>
public sealed record ContentPackContents(IReadOnlyList<string> Themes, IReadOnlyList<string> LyricsPresets, IReadOnlyList<string> Languages)
{
    internal const int MaxThemes = 50;
    internal const int MaxLyricsPresets = 50;
    internal const int MaxLanguages = 20;

    public int Count => Themes.Count + LyricsPresets.Count + Languages.Count;

    internal static ContentPackContents Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new PluginManifestException("\"contents\" must be an object.");
        var themes = new List<string>();
        var presets = new List<string>();
        var languages = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in e.EnumerateObject())
        {
            (List<string> target, int max) = p.Name.ToLowerInvariant() switch
            {
                "themes" => (themes, MaxThemes),
                "lyricspresets" => (presets, MaxLyricsPresets),
                "languages" => (languages, MaxLanguages),
                _ => throw new PluginManifestException($"Unknown kind \"{p.Name}\" in \"contents\" (known: themes, lyricsPresets, languages)."),
            };
            if (p.Value.ValueKind != JsonValueKind.Array)
                throw new PluginManifestException($"\"contents.{p.Name}\" must be an array of file paths.");
            foreach (var item in p.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw new PluginManifestException($"\"contents.{p.Name}\" must be an array of file paths.");
                var path = ContentPackRules.CheckContentPath(item.GetString()!, "contents." + p.Name);
                if (!seen.Add(path)) throw new PluginManifestException($"\"{path}\" is listed twice in \"contents\".");
                target.Add(path);
            }
            if (target.Count > max) throw new PluginManifestException($"\"contents.{p.Name}\" lists more than {max} files.");
        }
        if (themes.Count + presets.Count + languages.Count == 0)
            throw new PluginManifestException("\"contents\" lists no files.");
        return new ContentPackContents(themes, presets, languages);
    }
}

/// <summary>What may be inside a content pack folder or zip.</summary>
public static class ContentPackRules
{
    public const long MaxFileBytes = 2 * 1024 * 1024;
    public const int MaxFiles = 200;
    public const long MaxTotalBytes = 20L * 1024 * 1024;

    // Data, docs and preview images only. Everything else (DLLs, scripts, executables, AXAML)
    // rejects the whole pack: nothing in a content pack is ever run or instantiated.
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".md", ".txt", ".png", ".jpg", ".jpeg", ".webp",
    };
    private static readonly HashSet<string> AllowedBareNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "LICENSE", "NOTICE", "README", "COPYING", "AUTHORS",
    };

    /// <summary>Why <paramref name="relativePath"/> may not be in a content pack, or null when it may.</summary>
    public static string? CheckFile(string relativePath, long length)
    {
        var name = Path.GetFileName(relativePath.Replace('\\', '/').TrimEnd('/'));
        var ext = Path.GetExtension(name);
        var allowed = ext.Length == 0 ? AllowedBareNames.Contains(name) : AllowedExtensions.Contains(ext);
        if (!allowed)
            return $"\"{relativePath}\" is not allowed in a content pack (only .json data, text and images; no programs, scripts or XAML).";
        if (length > MaxFileBytes) return $"\"{relativePath}\" is larger than 2 MB.";
        return null;
    }

    /// <summary>A listed file path, normalized to forward slashes.</summary>
    /// <exception cref="PluginManifestException">Absolute, escaping, oddly formed, or not .json.</exception>
    internal static string CheckContentPath(string raw, string where)
    {
        var path = raw.Trim().Replace('\\', '/');
        if (path.Length is 0 or > 200) throw new PluginManifestException($"\"{where}\" has an empty or overlong path.");
        if (!PluginInstaller.IsSafeEntryName(path) || path.Split('/').Any(s => s.Length == 0))
            throw new PluginManifestException($"\"{raw}\" in \"{where}\" must be a path inside the pack folder (no leading '/', drive, ':' or '..').");
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new PluginManifestException($"\"{raw}\" in \"{where}\" must be a .json file.");
        return path;
    }
}

/// <summary>A colour scheme from a content pack, applied through the same derivation as custom themes.</summary>
public sealed record PackTheme(string PackId, string PackName, string Id, string Name, bool IsLight, string? Accent,
    IReadOnlyDictionary<string, Color> Colors)
{
    /// <summary>"&lt;pack id&gt;/&lt;theme id&gt;": what Settings stores after "Pack:".</summary>
    public string Key => PackId + "/" + Id;

    /// <summary>Tile preview colours.</summary>
    public string MainHex => PackThemeBuilder.Hex(Colors.TryGetValue("AppMainBackground", out var c) ? c : PackThemeBuilder.DefaultMain(IsLight));
    public string SidebarHex => PackThemeBuilder.Hex(Colors.TryGetValue("AppSidebarBackground", out var c) ? c : PackThemeBuilder.DefaultSidebar(IsLight));
    public string AccentHex => Accent ?? PackThemeBuilder.DefaultAccent;
}

/// <summary>Lyrics-page display settings a preset sets; null = leave as is.</summary>
public sealed record LyricsPresetValues
{
    public int? MinLineOpacity { get; init; }
    public bool? FullScreenFocus { get; init; }
    public bool? JoinSplitWords { get; init; }
    public bool? ShowTranslations { get; init; }
    public bool? ShowRomanization { get; init; }
    public bool? ShowBackgroundVocals { get; init; }
    public bool? TitleMarquee { get; init; }
    public bool? ArtistMarquee { get; init; }
    /// <summary>"Off", or a <see cref="FlowingStyles"/> name.</summary>
    public string? FlowingBackground { get; init; }
    public double? KawarpWarp { get; init; }
    public int? KawarpBlur { get; init; }
    public bool? Visualizer { get; init; }
    /// <summary>A <see cref="VisualizerStyle"/> name.</summary>
    public string? VisualizerStyle { get; init; }
    public bool? VisualizerArtworkColor { get; init; }

    public const string FlowingOff = "Off";
}

public sealed record PackLyricsPreset(string PackId, string PackName, string Id, string Name, LyricsPresetValues Values)
{
    public string Key => PackId + "/" + Id;
}

/// <summary>Strings for one culture, only keys the app knows.</summary>
public sealed record PackLanguage(string PackId, string PackName, string Culture, string? Name,
    IReadOnlyDictionary<string, string> Strings, int IgnoredKeys);

/// <summary>A content pack's parsed files.</summary>
public sealed record ContentPackData(
    IReadOnlyList<PackTheme> Themes,
    IReadOnlyList<PackLyricsPreset> LyricsPresets,
    IReadOnlyList<PackLanguage> Languages,
    string Fingerprint,
    IReadOnlyList<string> Warnings)
{
    /// <summary>"2 themes · 1 lyrics preset · 1 language".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Themes.Count > 0) parts.Add(Themes.Count == 1 ? Loc.T("Plugins.Content.Theme") : Loc.T("Plugins.Content.Themes", Themes.Count));
            if (LyricsPresets.Count > 0) parts.Add(LyricsPresets.Count == 1 ? Loc.T("Plugins.Content.LyricsPreset") : Loc.T("Plugins.Content.LyricsPresets", LyricsPresets.Count));
            if (Languages.Count > 0) parts.Add(Languages.Count == 1 ? Loc.T("Plugins.Content.Language") : Loc.T("Plugins.Content.Languages", Languages.Count));
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Reads a content pack folder: checks every file in it against <see cref="ContentPackRules"/>,
/// then parses the files plugin.json lists, strictly (unknown keys, bad values and anything
/// outside the folder reject the pack with a message for the Plugins tab). Only JSON is read;
/// nothing is loaded as code or markup.
/// </summary>
public static class ContentPackLoader
{
    private static readonly Regex ItemId = new(@"^[a-z0-9][a-z0-9_-]{0,39}$", RegexOptions.CultureInvariant);
    private static readonly Regex Hex6 = new(@"^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);
    private static readonly Regex HexColor = new(@"^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.CultureInvariant);
    private static readonly Regex CultureName = new(@"^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8}){0,3}$", RegexOptions.CultureInvariant);
    private static readonly Regex Placeholder = new(@"\{(\d+)([^{}]*)\}", RegexOptions.CultureInvariant);

    internal const int MaxStrings = 5000;
    internal const int MaxStringLength = 4000;

    /// <exception cref="PluginManifestException">The pack cannot be used; the message says why.</exception>
    public static ContentPackData Load(string directory, PluginManifest manifest, Func<string, string?>? english = null)
    {
        if (manifest.Contents is not { } contents) throw new PluginManifestException("\"contents\" is required for a content pack.");
        english ??= Loc.English;
        var root = Path.GetFullPath(directory);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        CheckFolder(root);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(manifest.Id + "\n" + manifest.Version + "\n"));
        var warnings = new List<string>();

        string Read(string rel)
        {
            var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(rootWithSep, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new PluginManifestException($"\"{rel}\" points outside the pack folder.");
            var info = new FileInfo(full);
            if (!info.Exists) throw new PluginManifestException($"\"{rel}\" is listed in \"contents\" but is not in the pack.");
            if (info.LinkTarget is not null) throw new PluginManifestException($"\"{rel}\" is a link; content packs must be plain files.");
            if (info.Length > ContentPackRules.MaxFileBytes) throw new PluginManifestException($"\"{rel}\" is larger than 2 MB.");
            var bytes = File.ReadAllBytes(full);
            hash.AppendData(Encoding.UTF8.GetBytes(rel + "\n"));
            hash.AppendData(bytes);
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }

        var themes = new List<PackTheme>();
        foreach (var rel in contents.Themes)
        {
            var theme = WithObject(Read(rel), rel, o => ParseTheme(o, rel, manifest));
            if (themes.Any(t => t.Id == theme.Id)) throw new PluginManifestException($"{rel}: theme id \"{theme.Id}\" is used twice in this pack.");
            themes.Add(theme);
        }

        var presets = new List<PackLyricsPreset>();
        foreach (var rel in contents.LyricsPresets)
        {
            var preset = WithObject(Read(rel), rel, o => ParseLyricsPreset(o, rel, manifest));
            if (presets.Any(p => p.Id == preset.Id)) throw new PluginManifestException($"{rel}: preset id \"{preset.Id}\" is used twice in this pack.");
            presets.Add(preset);
        }

        var languages = new List<PackLanguage>();
        foreach (var rel in contents.Languages)
        {
            var language = WithObject(Read(rel), rel, o => ParseLanguage(o, rel, manifest, english));
            if (languages.Any(l => string.Equals(l.Culture, language.Culture, StringComparison.OrdinalIgnoreCase)))
                throw new PluginManifestException($"{rel}: culture \"{language.Culture}\" is provided twice in this pack.");
            if (language.IgnoredKeys > 0)
                warnings.Add($"{rel}: {language.IgnoredKeys} string(s) ignored (unknown to this Noctis, or with placeholders that do not match English).");
            languages.Add(language);
        }

        return new ContentPackData(themes, presets, languages, Convert.ToHexString(hash.GetHashAndReset()), warnings);
    }

    /// <summary>Every file in the folder: plain files only, allowed kinds, within the size and count limits.</summary>
    private static void CheckFolder(string root)
    {
        var files = 0;
        long total = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false, ReturnSpecialDirectories = false };
        foreach (var info in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
        {
            var rel = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
                throw new PluginManifestException($"\"{rel}\" is a link; content packs must be plain files.");
            if (info is not FileInfo file) continue;
            if (++files > ContentPackRules.MaxFiles) throw new PluginManifestException($"The pack has more than {ContentPackRules.MaxFiles} files.");
            total += file.Length;
            if (total > ContentPackRules.MaxTotalBytes) throw new PluginManifestException("The pack is larger than 20 MB.");
            if (rel == PluginManifest.FileName) continue;
            if (ContentPackRules.CheckFile(rel, file.Length) is { } why) throw new PluginManifestException(why);
        }
    }

    private static T WithObject<T>(string text, string rel, Func<JsonElement, T> parse)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 8 });
        }
        catch (JsonException ex) { throw new PluginManifestException($"{rel} is not valid JSON: {ex.Message}"); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new PluginManifestException($"{rel} must be a JSON object.");
            return parse(doc.RootElement);
        }
    }

    // ── Themes ──

    private static PackTheme ParseTheme(JsonElement o, string rel, PluginManifest m)
    {
        OnlyKnown(o, rel, "$schema", "id", "name", "base", "accent", "colors");
        var id = ItemIdOf(o, rel);
        var name = NameOf(o, rel, 40);
        var baseMode = RequiredString(o, "base", rel).Trim().ToLowerInvariant();
        if (baseMode is not ("dark" or "light")) throw new PluginManifestException($"{rel}: \"base\" must be \"dark\" or \"light\".");
        var accent = OptionalString(o, "accent", rel)?.Trim();
        if (string.IsNullOrEmpty(accent)) accent = null;
        else if (!Hex6.IsMatch(accent)) throw new PluginManifestException($"{rel}: \"accent\" must be \"#RRGGBB\".");

        var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        if (TryGet(o, "colors", out var c) && c.ValueKind != JsonValueKind.Null)
        {
            if (c.ValueKind != JsonValueKind.Object) throw new PluginManifestException($"{rel}: \"colors\" must be an object of \"Key\": \"#RRGGBB\".");
            foreach (var p in c.EnumerateObject())
            {
                var key = PackThemeBuilder.CanonicalKey(p.Name)
                    ?? throw new PluginManifestException($"{rel}: \"{p.Name}\" is not a theme colour packs can set (see docs/PLUGINS.md).");
                var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()!.Trim() : "";
                if (!HexColor.IsMatch(value)) throw new PluginManifestException($"{rel}: \"{p.Name}\" must be \"#RRGGBB\" or \"#AARRGGBB\".");
                colors[key] = Color.Parse(value);
            }
        }
        return new PackTheme(m.Id, m.Name, id, name, baseMode == "light", accent?.ToUpperInvariant(), colors);
    }

    // ── Lyrics presets ──

    private static readonly string[] PresetKeys =
    {
        "minLineOpacity", "fullScreenFocus", "joinSplitWords", "showTranslations", "showRomanization",
        "showBackgroundVocals", "titleMarquee", "artistMarquee", "flowingBackground", "kawarpWarp", "kawarpBlur",
        "visualizer", "visualizerStyle", "visualizerArtworkColor",
    };

    private static readonly string[] FlowingNames =
        { LyricsPresetValues.FlowingOff, FlowingStyles.Drift, FlowingStyles.DriftCalm, FlowingStyles.Kawarp, FlowingStyles.KawarpCalm };

    private static PackLyricsPreset ParseLyricsPreset(JsonElement o, string rel, PluginManifest m)
    {
        OnlyKnown(o, rel, "$schema", "id", "name", "settings");
        var id = ItemIdOf(o, rel);
        var name = NameOf(o, rel, 40);
        if (!TryGet(o, "settings", out var s) || s.ValueKind != JsonValueKind.Object)
            throw new PluginManifestException($"{rel}: \"settings\" (an object) is required.");
        OnlyKnown(s, rel + " settings", PresetKeys);
        if (!s.EnumerateObject().Any()) throw new PluginManifestException($"{rel}: \"settings\" sets nothing.");

        string? flowing = null;
        if (OptionalString(s, "flowingBackground", rel) is { } f)
            flowing = FlowingNames.FirstOrDefault(n => string.Equals(n, f.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new PluginManifestException($"{rel}: \"flowingBackground\" must be one of {string.Join(", ", FlowingNames)}.");
        string? vizStyle = null;
        if (OptionalString(s, "visualizerStyle", rel) is { } v)
            vizStyle = Enum.GetNames<VisualizerStyle>().FirstOrDefault(n => string.Equals(n, v.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new PluginManifestException($"{rel}: \"visualizerStyle\" must be one of {string.Join(", ", Enum.GetNames<VisualizerStyle>())}.");

        var values = new LyricsPresetValues
        {
            MinLineOpacity = (int?)Number(s, "minLineOpacity", rel, 0, 60, integer: true),
            FullScreenFocus = Bool(s, "fullScreenFocus", rel),
            JoinSplitWords = Bool(s, "joinSplitWords", rel),
            ShowTranslations = Bool(s, "showTranslations", rel),
            ShowRomanization = Bool(s, "showRomanization", rel),
            ShowBackgroundVocals = Bool(s, "showBackgroundVocals", rel),
            TitleMarquee = Bool(s, "titleMarquee", rel),
            ArtistMarquee = Bool(s, "artistMarquee", rel),
            FlowingBackground = flowing,
            KawarpWarp = Number(s, "kawarpWarp", rel, 0, 3, integer: false),
            KawarpBlur = (int?)Number(s, "kawarpBlur", rel, 1, 16, integer: true),
            Visualizer = Bool(s, "visualizer", rel),
            VisualizerStyle = vizStyle,
            VisualizerArtworkColor = Bool(s, "visualizerArtworkColor", rel),
        };
        return new PackLyricsPreset(m.Id, m.Name, id, name, values);
    }

    // ── Languages ──

    private static PackLanguage ParseLanguage(JsonElement o, string rel, PluginManifest m, Func<string, string?> english)
    {
        OnlyKnown(o, rel, "$schema", "culture", "name", "strings");
        var culture = RequiredString(o, "culture", rel).Trim();
        if (!CultureName.IsMatch(culture)) throw new PluginManifestException($"{rel}: \"culture\" \"{culture}\" is not a culture code like \"es\" or \"pt-BR\".");
        try { culture = CultureInfo.GetCultureInfo(culture).Name; }
        catch (CultureNotFoundException) { throw new PluginManifestException($"{rel}: \"culture\" \"{culture}\" is not a culture this system knows."); }
        if (culture.Length == 0) throw new PluginManifestException($"{rel}: \"culture\" must not be the invariant culture.");
        var name = OptionalString(o, "name", rel)?.Trim();
        if (string.IsNullOrEmpty(name)) name = null;
        else if (name.Length > 60) throw new PluginManifestException($"{rel}: \"name\" is longer than 60 characters.");

        if (!TryGet(o, "strings", out var s) || s.ValueKind != JsonValueKind.Object)
            throw new PluginManifestException($"{rel}: \"strings\" (an object of \"Key\": \"text\") is required.");
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        var ignored = 0;
        var count = 0;
        foreach (var p in s.EnumerateObject())
        {
            if (++count > MaxStrings) throw new PluginManifestException($"{rel}: more than {MaxStrings} strings.");
            if (p.Value.ValueKind != JsonValueKind.String) throw new PluginManifestException($"{rel}: \"{p.Name}\" must be text.");
            var value = p.Value.GetString()!;
            if (value.Length > MaxStringLength) throw new PluginManifestException($"{rel}: \"{p.Name}\" is longer than {MaxStringLength} characters.");
            // Keys the app does not know are ignored (a pack made for a newer Noctis still loads).
            var en = english(p.Name);
            if (en is null || !PlaceholdersFit(en, value)) { ignored++; continue; }
            strings[p.Name] = value;
        }
        return new PackLanguage(m.Id, m.Name, culture, name, strings, ignored);
    }

    /// <summary>A translation must format with the arguments the English text is formatted with,
    /// so Loc.T(key, args) can never throw on a pack string.</summary>
    internal static bool PlaceholdersFit(string english, string value)
    {
        var args = 0;
        var specs = new HashSet<string>(StringComparer.Ordinal) { "" };
        foreach (Match match in Placeholder.Matches(english.Replace("{{", "").Replace("}}", "")))
        {
            if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var i)) args = Math.Max(args, i + 1);
            specs.Add(match.Groups[2].Value);
        }
        if (!Formats(english, args)) return true; // English itself is not a format string: shown as is
        // A width or format the English text does not use is refused before formatting anything:
        // "{0,999999}" pads to a million characters and "{0:D999999999}" asks for a billion digits
        // once Loc.T passes a number (the "x" below ignores the format, so it cannot catch this).
        foreach (Match match in Placeholder.Matches(value.Replace("{{", "").Replace("}}", "")))
            if (!specs.Contains(match.Groups[2].Value)) return false;
        return Formats(value, args);
    }

    private static bool Formats(string text, int args)
    {
        try
        {
            _ = string.Format(CultureInfo.InvariantCulture, text, Enumerable.Repeat((object)"x", args).ToArray());
            return true;
        }
        catch (FormatException) { return false; }
    }

    // ── JSON helpers (property names match case-insensitively, like plugin.json) ──

    private static void OnlyKnown(JsonElement o, string where, params string[] known)
    {
        foreach (var p in o.EnumerateObject())
            if (!known.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                throw new PluginManifestException($"{where}: unknown key \"{p.Name}\".");
    }

    private static bool TryGet(JsonElement o, string name, out JsonElement value)
    {
        foreach (var p in o.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    private static string RequiredString(JsonElement o, string name, string rel)
    {
        var v = OptionalString(o, name, rel);
        if (string.IsNullOrWhiteSpace(v)) throw new PluginManifestException($"{rel}: \"{name}\" is required.");
        return v;
    }

    private static string? OptionalString(JsonElement o, string name, string rel)
    {
        if (!TryGet(o, name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String) throw new PluginManifestException($"{rel}: \"{name}\" must be a string.");
        return v.GetString();
    }

    private static string ItemIdOf(JsonElement o, string rel)
    {
        var id = RequiredString(o, "id", rel).Trim();
        if (!ItemId.IsMatch(id)) throw new PluginManifestException($"{rel}: \"id\" \"{id}\" must be lowercase letters, digits, '-' or '_' (max 40).");
        return id;
    }

    private static string NameOf(JsonElement o, string rel, int max)
    {
        var name = RequiredString(o, "name", rel).Trim();
        if (name.Length > max) throw new PluginManifestException($"{rel}: \"name\" is longer than {max} characters.");
        return name;
    }

    private static bool? Bool(JsonElement o, string name, string rel)
    {
        if (!TryGet(o, name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new PluginManifestException($"{rel}: \"{name}\" must be true or false.");
        return v.GetBoolean();
    }

    private static double? Number(JsonElement o, string name, string rel, double min, double max, bool integer)
    {
        if (!TryGet(o, name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.Number) throw new PluginManifestException($"{rel}: \"{name}\" must be a number.");
        var d = v.GetDouble();
        if (double.IsNaN(d) || d < min || d > max || (integer && d != Math.Floor(d)))
            throw new PluginManifestException($"{rel}: \"{name}\" must be {(integer ? "a whole number" : "a number")} from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}.");
        return d;
    }
}

/// <summary>
/// Turns a <see cref="PackTheme"/> into the resource overlay App.SetTheme merges: the custom-theme
/// derivation (base variant, main/sidebar/accent → every surface, text and island key), then the
/// pack's own colours for the whitelisted keys on top.
/// </summary>
public static class PackThemeBuilder
{
    public const string DefaultAccent = "#E74856";

    /// <summary>Resource keys a pack may set: surfaces, text, cards, the island, sidebar and queue.
    /// Accent-driven keys are not here (now-playing row, island slider fill and accent icon,
    /// toggles): App.SetAccent owns them, and the accent comes from "accent" and the user's picker.</summary>
    public static readonly IReadOnlyList<string> AllowedKeys = new[]
    {
        // Surfaces
        "AppWindowBackgroundBrush", "AppMainBackground", "AppSidebarBackground",
        "TrackListStripeBrush", "TrackListHoverBrush", "TrackListMultiSelectBrush",
        // Text
        "PrimaryTextBrush", "SecondaryTextBrush", "TertiaryTextBrush",
        // Cards, chips, pills
        "HomeCardBackground", "HomeCardHoverBackground", "ChipBackground", "ChipHoverBackground",
        "GlassPillBackground", "GlassPillHoverBackground", "GlassPillPressedBackground", "GlassPillBorder",
        "InputOutlineBrush", "ArtworkPlaceholderBackground", "RankGoldBrush", "RankSilverBrush", "RankBronzeBrush",
        "ToggleTrackOffBrush", "ToggleKnobOffBrush",
        // Playback island
        "IslandBackground", "IslandBorder", "IslandForeground", "IslandForegroundSecondary", "IslandForegroundTertiary",
        "IslandIconFill", "IslandSliderUnfilled", "IslandAlbumArtPlaceholder", "IslandExplicitBadge",
        "IslandTrackBoxSliderUnfilled",
        // Sidebar + queue drawer
        "SidebarSelectedBrush", "SidebarSelectedHoverBrush", "SidebarHoverBrush", "QueueDrawerBackground", "QueueDrawerForeground",
    };

    private static readonly Dictionary<string, string> Canonical =
        AllowedKeys.ToDictionary(k => k, k => k, StringComparer.OrdinalIgnoreCase);

    /// <summary>The key as the app spells it, or null when packs may not set it.</summary>
    public static string? CanonicalKey(string name) => Canonical.TryGetValue(name, out var k) ? k : null;

    internal static Color DefaultMain(bool light) => Color.Parse(light ? "#F5F5F5" : "#121212");
    internal static Color DefaultSidebar(bool light) => Color.Parse(light ? "#EAEAEA" : "#1C1C1C");

    internal static string Hex(Color c) => c.A == 0xFF ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Key → Color/brush, plus the "__BaseVariant" sentinel ("Dark"/"Light").</summary>
    public static IDictionary<string, object> Build(PackTheme theme)
    {
        var def = new CustomThemeDefinition
        {
            Id = theme.Key,
            Name = theme.Name,
            BaseMode = theme.IsLight ? "Light" : "Dark",
            MainBackgroundHex = theme.MainHex,
            SidebarBackgroundHex = theme.SidebarHex,
            AccentHex = theme.AccentHex,
        };
        var dict = ThemeDerivation.Derive(def);
        foreach (var (key, color) in theme.Colors)
            dict[key] = new SolidColorBrush(color);

        // Keys that travel together: the Color twins of brushes, and Fluent's own foreground
        // brushes, which otherwise keep the base palette's text colours (see Ink.axaml).
        if (theme.Colors.TryGetValue("AppMainBackground", out var main)) dict["AppMainBackgroundColor"] = main;
        if (theme.Colors.TryGetValue("IslandBackground", out var island)) dict["IslandBackgroundColor"] = Color.FromRgb(island.R, island.G, island.B);
        if (theme.Colors.TryGetValue("PrimaryTextBrush", out var primary))
        {
            dict["SystemControlForegroundBaseHighBrush"] = new SolidColorBrush(primary);
            dict["SystemBaseHighColor"] = primary;
            dict["SystemBaseMediumHighColor"] = primary;
        }
        if (theme.Colors.TryGetValue("SecondaryTextBrush", out var secondary))
        {
            dict["SystemControlForegroundBaseMediumBrush"] = new SolidColorBrush(secondary);
            dict["SystemBaseMediumColor"] = secondary;
        }
        if (theme.Colors.TryGetValue("TertiaryTextBrush", out var tertiary))
        {
            dict["SystemControlForegroundBaseMediumLowBrush"] = new SolidColorBrush(tertiary);
            dict["SystemBaseMediumLowColor"] = tertiary;
        }
        return dict;
    }
}

/// <summary>
/// What the enabled content packs provide right now, merged in pack order. Settings lists the
/// themes and lyrics presets; languages are layered over the app's strings through
/// <see cref="Loc.SetOverlays"/>. <see cref="Changed"/> fires only when the content really
/// changed (a reload of the same packs is silent, so the active theme is not re-applied).
/// </summary>
public sealed class ContentCatalog
{
    private string _signature = "";
    private bool _hadLanguages;

    public IReadOnlyList<PackTheme> Themes { get; private set; } = Array.Empty<PackTheme>();
    public IReadOnlyList<PackLyricsPreset> LyricsPresets { get; private set; } = Array.Empty<PackLyricsPreset>();
    public IReadOnlyList<PackLanguage> Languages { get; private set; } = Array.Empty<PackLanguage>();

    public event EventHandler? Changed;

    public PackTheme? FindTheme(string key) => Themes.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
    public PackLyricsPreset? FindLyricsPreset(string key) => LyricsPresets.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
    public PackLanguage? FindLanguage(string culture) => Languages.FirstOrDefault(l => string.Equals(l.Culture, culture, StringComparison.OrdinalIgnoreCase));

    /// <summary>Replaces the content with <paramref name="packs"/> (enabled packs, in order). True when it changed.</summary>
    internal bool Update(IReadOnlyList<ContentPackData> packs)
    {
        var signature = string.Join("|", packs.Select(p => p.Fingerprint));
        if (signature == _signature) return false;
        _signature = signature;

        // Ids are unique per pack; across packs the first one wins (the key includes the pack id,
        // so a clash only happens when the same pack is installed twice, which the host refuses).
        Themes = packs.SelectMany(p => p.Themes).GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        LyricsPresets = packs.SelectMany(p => p.LyricsPresets).GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        Languages = packs.SelectMany(p => p.Languages).ToList();

        // Loc is process-wide: only touch it when some pack has (or had) strings.
        if (Languages.Count > 0 || _hadLanguages)
        {
            var overlays = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var lang in Languages)
            {
                if (!overlays.TryGetValue(lang.Culture, out var merged)) overlays[lang.Culture] = merged = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (k, v) in lang.Strings) merged.TryAdd(k, v);
            }
            Loc.Instance.SetOverlays(overlays.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)kv.Value, StringComparer.OrdinalIgnoreCase));
            _hadLanguages = Languages.Count > 0;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
