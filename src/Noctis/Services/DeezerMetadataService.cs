using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Noctis.Services;

/// <summary>
/// Deezer-backed tag lookup (keyless). Used as a redundancy fallback alongside MusicBrainz so
/// identification still works when one source is down or returns nothing. All work runs off the
/// UI thread (callers await from a background context).
/// </summary>
public sealed class DeezerMetadataService
{
    private readonly HttpClient _http;

    public DeezerMetadataService(HttpClient http) => _http = http;

    /// <summary>Returns Deezer tag suggestions for the given tags, best first; empty on failure.</summary>
    public async Task<IReadOnlyList<TagSuggestion>> SearchAsync(
        string artist, string title, string album, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title))
            return Array.Empty<TagSuggestion>();

        try
        {
            var url = DeezerApi.BuildSearchUrl(artist, title, album);
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Array.Empty<TagSuggestion>();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return DeezerApi.ParseSearch(json);
        }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<TagSuggestion>(); }
    }
}
