using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services;

public class LrcLibService : ILrcLibService
{
    private const string BaseUrl = "https://lrclib.net/api";
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, LrcLibResult?> _cache = new();

    public LrcLibService(HttpClient httpClient)
    {
        _http = httpClient;
    }

    public async Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds)
    {
        var cacheKey = $"get:{artist}|{trackName}|{Math.Round(durationSeconds)}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            var url = $"{BaseUrl}/get?artist_name={Uri.EscapeDataString(artist)}" +
                      $"&track_name={Uri.EscapeDataString(trackName)}" +
                      $"&duration={Math.Round(durationSeconds)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Noctis/1.0");

            using var response = await _http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _cache[cacheKey] = null;
                return null;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<LrcLibResult>(json);

            _cache[cacheKey] = result;
            return result;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public async Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName)
    {
        try
        {
            var url = $"{BaseUrl}/search?artist_name={Uri.EscapeDataString(artist)}" +
                      $"&track_name={Uri.EscapeDataString(trackName)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Noctis/1.0");

            using var response = await _http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new List<LrcLibResult>();

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var results = JsonSerializer.Deserialize<List<LrcLibResult>>(json) ?? new List<LrcLibResult>();

            return results;
        }
        catch (HttpRequestException)
        {
            return new List<LrcLibResult>();
        }
    }
}
