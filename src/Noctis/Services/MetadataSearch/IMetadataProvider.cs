using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services.MetadataSearch;

/// <summary>
/// One metadata source behind <see cref="MetadataSearchService"/>. A provider returns raw
/// candidates (the engine scores, merges and sorts them) and throws on failure — the engine turns
/// exceptions into a <see cref="ProviderStatus"/>, so "offline" never masquerades as "no match".
/// Partial failures inside a provider (a details lookup after a good search) should degrade to
/// less-complete candidates instead of throwing.
/// </summary>
public interface IMetadataProvider
{
    /// <summary>Display name; also the key in <see cref="MetadataQuery.Providers"/>.</summary>
    string Name { get; }

    /// <summary>Whole-search budget for this provider (pacing included).</summary>
    TimeSpan Timeout { get; }

    bool IsEnabled(AppSettings settings);

    Task<IReadOnlyList<MetadataCandidate>> SearchAsync(MetadataQuery query, CancellationToken ct);
}

/// <summary>Provider display names (also the merge precedence keys).</summary>
public static class ProviderNames
{
    public const string AppleMusic = "Apple Music";
    public const string Deezer = "Deezer";
    public const string MusicBrainz = "MusicBrainz";
}

/// <summary>Tolerant JsonElement readers: a missing or mistyped field reads as "unknown".</summary>
internal static class Json
{
    public static string Str(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? string.Empty).Trim() : string.Empty;

    public static int? Int(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    public static long? Long(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt64(out var l) ? l : null;

    public static double? Dbl(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;

    public static bool? Bool(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    public static JsonElement Obj(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Object
            ? v : default;

    public static IEnumerable<JsonElement> Arr(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    public static Uri? Url(string s)
        => Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? u : null;

    public static TimeSpan? Seconds(int? s) => s is > 0 ? TimeSpan.FromSeconds(s.Value) : null;
    public static TimeSpan? Millis(long? ms) => ms is > 0 ? TimeSpan.FromMilliseconds(ms.Value) : null;
}
