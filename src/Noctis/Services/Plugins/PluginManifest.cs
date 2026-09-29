using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Noctis.Plugins;

namespace Noctis.Services.Plugins;

/// <summary>Kind of a declared plugin setting; Noctis draws the control, the plugin reads the value.</summary>
public enum PluginSettingType { Bool, String, Number, Choice }

/// <summary>One entry of plugin.json's "settings" array.</summary>
public sealed record PluginSettingDefinition(
    string Key,
    string Label,
    PluginSettingType Type,
    string Default,
    IReadOnlyList<string> Choices,
    string? Description,
    double? Min,
    double? Max);

/// <summary>plugin.json could not be used; the message is shown in Settings → Plugins as is.</summary>
public sealed class PluginManifestException : Exception
{
    public PluginManifestException(string message) : base(message) { }
}

/// <summary>
/// plugin.json, read without loading any plugin code. The manifest is the source of truth
/// for a plugin's identity (id, name, version), what it may do through the API
/// (permissions) and which host it needs (apiVersion, minAppVersion, platforms).
/// See docs/PLUGINS.md for the format.
/// </summary>
public sealed class PluginManifest
{
    public const string FileName = "plugin.json";
    public const string TypeDotnet = "dotnet";
    /// <summary>Data-only packs (themes, lyrics presets, languages): no code, see <see cref="ContentPackContents"/>.</summary>
    public const string TypeContent = "content";

    /// <summary>Every top-level field a manifest may have. A content pack must not use any other.</summary>
    private static readonly HashSet<string> KnownFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "$schema", "id", "name", "version", "author", "description", "type", "entry", "entryType", "apiVersion",
        "minAppVersion", "platforms", "permissions", "settings", "homepage", "contents",
    };

    /// <summary>Largest plugin.json we read; anything bigger is not a manifest.</summary>
    internal const int MaxBytes = 256 * 1024;

    private static readonly Regex IdPattern = new(@"^[a-z0-9]+(?:[-_][a-z0-9]+)*(?:\.[a-z0-9]+(?:[-_][a-z0-9]+)*)+$", RegexOptions.CultureInvariant);
    private static readonly Regex SettingKeyPattern = new(@"^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly string[] KnownPlatforms = { "windows", "macos", "linux" };

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string Type { get; init; } = TypeDotnet;
    /// <summary>File name of the plugin DLL inside the plugin folder (dotnet plugins).</summary>
    public string Entry { get; init; } = "";
    /// <summary>Optional full type name of the INoctisPlugin to create when the DLL holds several.</summary>
    public string? EntryType { get; init; }
    /// <summary>"major.minor" of Noctis.Plugins.Abstractions the plugin was built against.</summary>
    public string ApiVersion { get; init; } = "";
    public string? MinAppVersion { get; init; }
    /// <summary>Empty = every desktop platform.</summary>
    public IReadOnlyList<string> Platforms { get; init; } = Array.Empty<string>();
    /// <summary>Declared permissions, known names only, without the implicit "playback.read".</summary>
    public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PluginSettingDefinition> Settings { get; init; } = Array.Empty<PluginSettingDefinition>();
    public string? Homepage { get; init; }
    /// <summary>Things that did not stop the plugin but the author should fix (unknown permission names, …).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>What a content pack provides (null for dotnet plugins).</summary>
    public ContentPackContents? Contents { get; init; }

    public bool IsDotnet => Type == TypeDotnet;
    public bool IsContent => Type == TypeContent;

    /// <summary>Reads <c>&lt;dir&gt;/plugin.json</c>. Null when the folder has none (a legacy plugin).</summary>
    /// <exception cref="PluginManifestException">The file exists but is not a valid manifest.</exception>
    public static PluginManifest? TryLoad(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return null;
        string json;
        try
        {
            if (new FileInfo(path).Length > MaxBytes) throw new PluginManifestException("plugin.json is larger than 256 KB.");
            json = File.ReadAllText(path);
        }
        catch (PluginManifestException) { throw; }
        catch (Exception ex) { throw new PluginManifestException("plugin.json could not be read: " + ex.Message); }
        return Parse(json);
    }

    /// <summary>Parses and validates manifest text.</summary>
    /// <exception cref="PluginManifestException">With the first problem found, phrased for the user.</exception>
    public static PluginManifest Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex) { throw new PluginManifestException("plugin.json is not valid JSON: " + ex.Message); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new PluginManifestException("plugin.json must be a JSON object.");
            var warnings = new List<string>();

            var id = RequiredString(root, "id");
            if (id.Length is < 3 or > 100 || !IdPattern.IsMatch(id))
                throw new PluginManifestException($"\"id\" \"{id}\" must be lowercase reverse-DNS style, e.g. \"dev.example.myplugin\" (letters, digits, '-', '_', at least one '.').");

            var name = RequiredString(root, "name").Trim();
            if (name.Length > 60) throw new PluginManifestException("\"name\" is longer than 60 characters.");

            var version = RequiredString(root, "version").Trim();
            if (!PluginVersion.TryParse(version, out _))
                throw new PluginManifestException($"\"version\" \"{version}\" is not a semantic version (e.g. \"1.0.0\").");

            var type = (OptionalString(root, "type") ?? TypeDotnet).Trim().ToLowerInvariant();
            if (type is not (TypeDotnet or TypeContent))
                throw new PluginManifestException($"\"type\" must be \"{TypeDotnet}\" or \"{TypeContent}\", not \"{type}\".");

            var entry = OptionalString(root, "entry")?.Trim() ?? "";
            var apiVersion = OptionalString(root, "apiVersion")?.Trim() ?? "";
            if (type == TypeDotnet)
            {
                if (entry.Length == 0) throw new PluginManifestException("\"entry\" (the plugin DLL's file name) is required.");
                if (entry.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || entry.Contains("..", StringComparison.Ordinal)
                    || !entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    throw new PluginManifestException($"\"entry\" \"{entry}\" must be a DLL file name in the plugin folder, without a path.");
                if (apiVersion.Length == 0) throw new PluginManifestException("\"apiVersion\" is required (e.g. \"" + PluginApi.Version + "\").");
            }
            if (apiVersion.Length > 0 && !TryParseApiVersion(apiVersion, out _, out _))
                throw new PluginManifestException($"\"apiVersion\" \"{apiVersion}\" must look like \"{PluginApi.Version}\".");

            ContentPackContents? contents = null;
            if (type == TypeContent)
            {
                // Content packs are validated strictly: they are data, so anything unexpected is
                // a mistake (or an attempt to smuggle code), never a newer optional feature.
                foreach (var p in root.EnumerateObject())
                    if (!KnownFields.Contains(p.Name))
                        throw new PluginManifestException($"Unknown field \"{p.Name}\" in a content pack's plugin.json.");
                foreach (var codeField in new[] { "entry", "entryType" })
                    if (TryGet(root, codeField, out var cv) && cv.ValueKind != JsonValueKind.Null)
                        throw new PluginManifestException($"A content pack cannot have \"{codeField}\": content packs carry no code.");
                if (StringArray(root, "permissions").Count > 0)
                    throw new PluginManifestException("A content pack has no permissions; remove \"permissions\".");
                if (TryGet(root, "settings", out var sv) && sv.ValueKind == JsonValueKind.Array && sv.GetArrayLength() > 0)
                    throw new PluginManifestException("A content pack cannot declare \"settings\".");
                if (!TryGet(root, "contents", out var cs) || cs.ValueKind == JsonValueKind.Null)
                    throw new PluginManifestException("\"contents\" is required for a content pack (the files it provides).");
                contents = ContentPackContents.Parse(cs);
            }
            else if (TryGet(root, "contents", out _))
                warnings.Add("\"contents\" only applies to \"type\": \"content\" and was ignored.");

            var minApp = OptionalString(root, "minAppVersion")?.Trim();
            if (string.IsNullOrEmpty(minApp)) minApp = null;
            else if (!PluginVersion.TryParseLoose(minApp, out _))
                throw new PluginManifestException($"\"minAppVersion\" \"{minApp}\" is not a version (e.g. \"1.5.2\").");

            var platforms = new List<string>();
            foreach (var p in StringArray(root, "platforms"))
            {
                var norm = p.Trim().ToLowerInvariant();
                if (KnownPlatforms.Contains(norm)) { if (!platforms.Contains(norm)) platforms.Add(norm); }
                else warnings.Add($"Unknown platform \"{p}\" ignored.");
            }

            var permissions = new List<string>();
            foreach (var p in StringArray(root, "permissions"))
            {
                var norm = p.Trim().ToLowerInvariant();
                if (norm == PluginPermissions.PlaybackRead) continue; // implicit
                if (PluginPermissions.All.Contains(norm)) { if (!permissions.Contains(norm)) permissions.Add(norm); }
                else warnings.Add($"Unknown permission \"{p}\" ignored.");
            }

            var homepage = OptionalString(root, "homepage")?.Trim();
            if (!string.IsNullOrEmpty(homepage)
                && !(Uri.TryCreate(homepage, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"))
            {
                warnings.Add("\"homepage\" is not an http(s) link and was ignored.");
                homepage = null;
            }

            return new PluginManifest
            {
                Id = id,
                Name = name,
                Version = version,
                Author = OptionalString(root, "author")?.Trim() ?? "",
                Description = OptionalString(root, "description")?.Trim() ?? "",
                Type = type,
                Entry = entry,
                EntryType = OptionalString(root, "entryType")?.Trim() is { Length: > 0 } et ? et : null,
                ApiVersion = apiVersion,
                MinAppVersion = minApp,
                Platforms = platforms,
                Permissions = permissions,
                Settings = ParseSettings(root),
                Homepage = string.IsNullOrEmpty(homepage) ? null : homepage,
                Contents = contents,
                Warnings = warnings,
            };
        }
    }

    /// <summary>
    /// Why this host cannot run the plugin, or null when it can: a different API major
    /// version, a newer required app version, or another platform.
    /// </summary>
    public string? CheckCompatibility(string appVersion)
    {
        if (IsDotnet && TryParseApiVersion(ApiVersion, out var major, out var minor))
        {
            if (major != PluginApi.Major)
                return $"Built for plugin API {major}.{minor}; this Noctis has API {PluginApi.Version}. Get a version of the plugin made for API {PluginApi.Major}.x.";
        }
        if (MinAppVersion is not null && PluginVersion.TryParseLoose(appVersion, out var app)
            && PluginVersion.TryParseLoose(MinAppVersion, out var min) && app < min)
            return $"Needs Noctis {MinAppVersion} or newer (this is {app.ToString(3)}). Update Noctis to use it.";
        if (Platforms.Count > 0 && !Platforms.Contains(CurrentPlatform))
            return $"Made for {string.Join(", ", Platforms)}; not available on {CurrentPlatform}.";
        return null;
    }

    /// <summary>"windows", "macos" or "linux".</summary>
    public static string CurrentPlatform =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>"1.1" → (1, 1). "1.1.0" is accepted too.</summary>
    public static bool TryParseApiVersion(string? value, out int major, out int minor)
    {
        major = minor = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Trim().Split('.');
        if (parts.Length is < 2 or > 3) return false;
        return int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor)
            && (parts.Length == 2 || int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    private static IReadOnlyList<PluginSettingDefinition> ParseSettings(JsonElement root)
    {
        if (!TryGet(root, "settings", out var arr) || arr.ValueKind == JsonValueKind.Null) return Array.Empty<PluginSettingDefinition>();
        if (arr.ValueKind != JsonValueKind.Array) throw new PluginManifestException("\"settings\" must be an array.");
        var list = new List<PluginSettingDefinition>();
        foreach (var s in arr.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) throw new PluginManifestException("Each entry of \"settings\" must be an object.");
            var key = RequiredString(s, "key", "settings[].key");
            if (!SettingKeyPattern.IsMatch(key)) throw new PluginManifestException($"Setting key \"{key}\" may only use letters, digits, '.', '-' and '_' (max 64).");
            if (list.Any(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase)))
                throw new PluginManifestException($"Setting key \"{key}\" is declared twice.");
            var label = OptionalString(s, "label")?.Trim();
            if (string.IsNullOrEmpty(label)) label = key;
            var typeText = (OptionalString(s, "type") ?? "").Trim().ToLowerInvariant();
            PluginSettingType type = typeText switch
            {
                "bool" or "boolean" => PluginSettingType.Bool,
                "string" or "text" => PluginSettingType.String,
                "number" => PluginSettingType.Number,
                "choice" => PluginSettingType.Choice,
                _ => throw new PluginManifestException($"Setting \"{key}\": type must be bool, string, number or choice."),
            };

            var choices = StringArray(s, "choices").Select(c => c.Trim()).Where(c => c.Length > 0).Distinct().ToList();
            if (type == PluginSettingType.Choice && choices.Count == 0)
                throw new PluginManifestException($"Setting \"{key}\": a choice setting needs a non-empty \"choices\" array.");

            double? min = TryGet(s, "min", out var mn) && mn.ValueKind == JsonValueKind.Number ? mn.GetDouble() : null;
            double? max = TryGet(s, "max", out var mx) && mx.ValueKind == JsonValueKind.Number ? mx.GetDouble() : null;

            string def;
            TryGet(s, "default", out var d);
            switch (type)
            {
                case PluginSettingType.Bool:
                    if (d.ValueKind is JsonValueKind.True or JsonValueKind.False) def = d.GetBoolean() ? "true" : "false";
                    else if (d.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) def = "false";
                    else throw new PluginManifestException($"Setting \"{key}\": default must be true or false.");
                    break;
                case PluginSettingType.Number:
                    if (d.ValueKind == JsonValueKind.Number) def = d.GetDouble().ToString("R", CultureInfo.InvariantCulture);
                    else if (d.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) def = (min ?? 0).ToString("R", CultureInfo.InvariantCulture);
                    else throw new PluginManifestException($"Setting \"{key}\": default must be a number.");
                    break;
                case PluginSettingType.Choice:
                    def = d.ValueKind == JsonValueKind.String ? d.GetString()!.Trim() : choices[0];
                    if (!choices.Contains(def)) throw new PluginManifestException($"Setting \"{key}\": default \"{def}\" is not one of its choices.");
                    break;
                default:
                    if (d.ValueKind == JsonValueKind.String) def = d.GetString()!;
                    else if (d.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) def = "";
                    else throw new PluginManifestException($"Setting \"{key}\": default must be a string.");
                    break;
            }
            list.Add(new PluginSettingDefinition(key, label, type, def, choices, OptionalString(s, "description")?.Trim(), min, max));
        }
        return list;
    }

    // Property names are matched case-insensitively: "apiversion" and "apiVersion" both work.
    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    private static string RequiredString(JsonElement obj, string name, string? display = null)
    {
        var v = OptionalString(obj, name, display);
        if (string.IsNullOrWhiteSpace(v)) throw new PluginManifestException($"\"{display ?? name}\" is required.");
        return v;
    }

    private static string? OptionalString(JsonElement obj, string name, string? display = null)
    {
        if (!TryGet(obj, name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String) throw new PluginManifestException($"\"{display ?? name}\" must be a string.");
        return v.GetString();
    }

    private static List<string> StringArray(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var v) || v.ValueKind == JsonValueKind.Null) return new List<string>();
        if (v.ValueKind != JsonValueKind.Array) throw new PluginManifestException($"\"{name}\" must be an array of strings.");
        var list = new List<string>();
        foreach (var e in v.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String) throw new PluginManifestException($"\"{name}\" must be an array of strings.");
            list.Add(e.GetString()!);
        }
        return list;
    }
}

/// <summary>Semantic versions ("1.2.3", "1.2.3-beta.1") for plugin versions and app gating.</summary>
public static class PluginVersion
{
    private static readonly Regex SemVer = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant);

    /// <summary>Strict x.y.z[-pre][+build].</summary>
    public static bool TryParse(string? value, out (Version Core, string Pre) version)
    {
        version = default;
        if (value is null) return false;
        var m = SemVer.Match(value.Trim());
        if (!m.Success) return false;
        version = (new Version(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                               int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                               int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)),
                   m.Groups[4].Success ? m.Groups[4].Value : "");
        return true;
    }

    /// <summary>Finds "x.y[.z]" anywhere ("Version 1.5.2" → 1.5.2); missing parts are 0.</summary>
    public static bool TryParseLoose(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value)) return false;
        var m = Regex.Match(value, @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return false;
        version = new Version(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                              int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                              m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0);
        return true;
    }

    /// <summary>Semver precedence: &lt;0 when a is older. Unparseable versions sort first.</summary>
    public static int Compare(string? a, string? b)
    {
        var okA = TryParse(a, out var va);
        var okB = TryParse(b, out var vb);
        if (!okA || !okB)
        {
            if (!okA && !okB) return 0;
            return okA ? 1 : -1;
        }
        var c = va.Core.CompareTo(vb.Core);
        if (c != 0) return c;
        // A release outranks its pre-releases: 1.0.0 > 1.0.0-beta.
        if (va.Pre.Length == 0 && vb.Pre.Length == 0) return 0;
        if (va.Pre.Length == 0) return 1;
        if (vb.Pre.Length == 0) return -1;
        var pa = va.Pre.Split('.');
        var pb = vb.Pre.Split('.');
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var na = int.TryParse(pa[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ia);
            var nb = int.TryParse(pb[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ib);
            int r = na && nb ? ia.CompareTo(ib) : na ? -1 : nb ? 1 : string.CompareOrdinal(pa[i], pb[i]);
            if (r != 0) return r;
        }
        return pa.Length.CompareTo(pb.Length);
    }
}
