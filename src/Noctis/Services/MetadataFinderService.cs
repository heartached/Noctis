using System.Diagnostics;
using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Orchestrates fingerprint identification. Pure URL/parsing logic lives in
/// <see cref="AcoustIdApi"/> / <see cref="MusicBrainzApi"/>; this service owns the side
/// effects: locating and running <c>fpcalc</c>, the HTTP calls, and rate limiting. All work
/// runs off the UI thread (callers await it from a background context).
/// </summary>
public sealed class MetadataFinderService : IMetadataFinderService
{
    private readonly HttpClient _http;
    private readonly Func<AppSettings> _settings;

    // MusicBrainz asks for <= 1 request/second; AcoustID allows ~3/second. A single gate at
    // the slower rate keeps us well-behaved for both without per-host bookkeeping.
    private readonly SemaphoreSlim _rateGate = new(1, 1);
    private DateTime _lastRequestUtc = DateTime.MinValue;

    public MetadataFinderService(HttpClient http, Func<AppSettings> settings)
    {
        _http = http;
        _settings = settings;
    }

    public bool HasFingerprinting => ResolveFpcalc() != null;

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_settings().AcoustIdApiKey);

    public async Task<IReadOnlyList<TagSuggestion>> IdentifyAsync(Track track, CancellationToken ct = default)
    {
        var fpcalc = ResolveFpcalc();
        var apiKey = _settings().AcoustIdApiKey;

        if (fpcalc != null && !string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                var print = await RunFpcalcAsync(fpcalc, track.FilePath, ct).ConfigureAwait(false);
                if (print is { Fingerprint.Length: > 0 })
                {
                    var url = AcoustIdApi.BuildLookupUrl(apiKey, print.Duration, print.Fingerprint);
                    var json = await GetAsync(url, ct).ConfigureAwait(false);
                    var hits = AcoustIdApi.ParseLookup(json);
                    if (hits.Count > 0) return hits;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* fall through to text search */ }
        }

        // Text-search fallback (also the no-key / no-fpcalc path).
        try
        {
            var url = MusicBrainzApi.BuildRecordingSearchUrl(track.PrimaryArtist, track.Title, track.Album);
            var json = await GetAsync(url, ct).ConfigureAwait(false);
            return MusicBrainzApi.ParseRecordingSearch(json);
        }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<TagSuggestion>(); }
    }

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        await _rateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var since = DateTime.UtcNow - _lastRequestUtc;
            if (since < TimeSpan.FromSeconds(1))
                await Task.Delay(TimeSpan.FromSeconds(1) - since, ct).ConfigureAwait(false);

            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            _lastRequestUtc = DateTime.UtcNow;
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _rateGate.Release();
        }
    }

    private sealed record FpResult(double Duration, string Fingerprint);

    private static async Task<FpResult?> RunFpcalcAsync(string fpcalc, string filePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;

        var psi = new ProcessStartInfo
        {
            FileName = fpcalc,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-json");
        psi.ArgumentList.Add(filePath);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) return null;

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return null;

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
            ? d.GetDouble() : 0;
        var fingerprint = root.TryGetProperty("fingerprint", out var f) && f.ValueKind == JsonValueKind.String
            ? f.GetString() ?? "" : "";
        return new FpResult(duration, fingerprint);
    }

    private string? ResolveFpcalc()
    {
        var configured = _settings().FpcalcPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var exe = OperatingSystem.IsWindows() ? "fpcalc.exe" : "fpcalc";
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full)) return full;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }
}
