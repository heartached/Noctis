using System.Net;
using System.Reflection;

namespace Noctis.Services.MetadataSearch;

/// <summary>
/// The HTTP side of one metadata provider: pacing, a per-URL response cache, bounded reads,
/// and one polite retry when the service says "slow down". 404 is an answer ("no such id"),
/// every other failure throws so the engine can report the provider as failed rather than
/// pretend it found nothing.
/// </summary>
public sealed class ProviderHttp
{
    /// <summary>Project page used as the contact in User-Agent strings (MusicBrainz requires
    /// "Application/version ( contact-url )" and throttles anonymous agents).</summary>
    public const string ProjectUrl = "https://github.com/heartached/Noctis";

    /// <summary>"Noctis/1.5.9 ( https://github.com/heartached/Noctis )".</summary>
    public static string UserAgent { get; } =
        $"Noctis/{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0"} ( {ProjectUrl} )";

    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly RequestPacer _pacer;
    private readonly LruCache<string, string>? _cache;
    private readonly Action<HttpRequestMessage>? _configure;

    public ProviderHttp(HttpClient http, RequestPacer pacer, LruCache<string, string>? cache = null,
        Action<HttpRequestMessage>? configure = null)
    {
        _http = http;
        _pacer = pacer;
        _cache = cache;
        _configure = configure;
    }

    /// <summary>GETs <paramref name="url"/> as text. Null on 404; throws
    /// <see cref="HttpRequestException"/> on any other failure.</summary>
    public async Task<string?> GetStringAsync(string url, CancellationToken ct)
    {
        if (_cache is not null && _cache.TryGet(url, out var cached)) return cached;

        for (var attempt = 0; ; attempt++)
        {
            await _pacer.WaitAsync(ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Per-request headers win over the shared client's defaults ("Noctis/1.0").
            req.Headers.UserAgent.ParseAdd(UserAgent);
            req.Headers.Accept.ParseAdd("application/json");
            _configure?.Invoke(req);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;

            if ((resp.StatusCode == HttpStatusCode.ServiceUnavailable || resp.StatusCode == HttpStatusCode.TooManyRequests)
                && attempt == 0)
            {
                // MusicBrainz answers 503 and Apple 429/403 when they want us to back off.
                var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(1500);
                await Task.Delay(delay > MaxRetryAfter ? MaxRetryAfter : delay, ct).ConfigureAwait(false);
                continue;
            }

            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode}", null, resp.StatusCode);

            var body = await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct).ConfigureAwait(false);
            _cache?.Set(url, body);
            return body;
        }
    }
}
