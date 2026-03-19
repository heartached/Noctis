using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// WebDAV connector baseline with connection validation.
/// </summary>
public sealed class WebDavMediaSourceConnector : IMediaSourceConnector
{
    private readonly HttpClient _http;

    public WebDavMediaSourceConnector(HttpClient http) => _http = http;

    public SourceType SourceType => SourceType.WebDav;
    public string Name => "WebDAV";

    public async Task<bool> ValidateConnectionAsync(SourceConnection connection, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connection.BaseUriOrPath))
            return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, connection.BaseUriOrPath);
            using var response = await _http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public Task<IReadOnlyList<Track>> ScanAsync(SourceConnection connection, CancellationToken ct = default)
    {
        // Placeholder in this phase; catalog sync is implemented through source APIs later.
        return Task.FromResult<IReadOnlyList<Track>>(Array.Empty<Track>());
    }

    public Task<Stream?> OpenTrackStreamAsync(SourceConnection connection, Track track, CancellationToken ct = default)
    {
        return Task.FromResult<Stream?>(null);
    }

    public Task<bool> DownloadTrackAsync(SourceConnection connection, Track track, string destinationPath, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }
}

