using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Noctis.Services;
using Noctis.Services.Plugins;

namespace Noctis.PluginCheck;

/// <summary>A public GitHub repository as the API describes it.</summary>
public sealed record RepoFacts(bool Found, bool IsPrivate, bool IsArchived, bool IsFork, string? LicenseSpdx);

/// <summary>The release asset behind the download link, as the API describes it.</summary>
public sealed record AssetFacts(bool ReleaseFound, bool AssetFound, bool IsDraft, long? Size, string? Sha256);

/// <summary>What the GitHub API said about the source repo and the release; null parts were not asked.</summary>
public sealed record RemoteFacts(RepoFacts? Source, AssetFacts? Asset, string? Error);

/// <summary>A link split into its GitHub parts. Only github.com links parse.</summary>
public static partial class GitHubLinks
{
    [GeneratedRegex(@"^https://github\.com/(?<o>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<r>[A-Za-z0-9._-]{1,100}?)(?:\.git)?/?$", RegexOptions.CultureInvariant)]
    private static partial Regex RepoPattern();

    [GeneratedRegex(@"^https://github\.com/(?<o>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<r>[A-Za-z0-9._-]{1,100})/releases/download/(?<t>[^/?#]{1,200})/(?<f>[^/?#]{1,200})$", RegexOptions.CultureInvariant)]
    private static partial Regex AssetPattern();

    /// <summary>https://github.com/owner/repo (a trailing ".git" or "/" is fine).</summary>
    public static bool TryParseRepo(string? url, out string owner, out string repo)
    {
        owner = repo = "";
        var m = RepoPattern().Match((url ?? "").Trim());
        if (!m.Success || m.Groups["r"].Value is "." or "..") return false;
        owner = m.Groups["o"].Value;
        repo = m.Groups["r"].Value;
        return true;
    }

    /// <summary>https://github.com/owner/repo/releases/download/tag/file: a release asset link.</summary>
    public static bool TryParseAsset(string? url, out string owner, out string repo, out string tag, out string file)
    {
        owner = repo = tag = file = "";
        var m = AssetPattern().Match((url ?? "").Trim());
        if (!m.Success) return false;
        owner = m.Groups["o"].Value;
        repo = m.Groups["r"].Value;
        tag = Uri.UnescapeDataString(m.Groups["t"].Value);
        file = Uri.UnescapeDataString(m.Groups["f"].Value);
        return true;
    }
}

/// <summary>The network side of a check: the zip download and two GitHub API reads.</summary>
public static class GitHubFetch
{
    /// <summary>Largest zip the check downloads. Bigger submissions are reviewed by hand.</summary>
    public const long MaxZipBytes = 100L * 1024 * 1024;

    /// <summary>No default auth header: a token is added per request, to api.github.com only.</summary>
    public static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Noctis-PluginCheck", "1.0"));
        return http;
    }

    /// <summary>
    /// Downloads the release zip to <paramref name="path"/>, refusing anything that is not a
    /// GitHub release asset, ends anywhere but GitHub after redirects, or is bigger than
    /// <see cref="MaxZipBytes"/>. Returns null on success, else why it failed.
    /// </summary>
    public static async Task<string?> DownloadZipAsync(HttpClient http, string url, string path, CancellationToken ct = default)
    {
        if (!GitHubLinks.TryParseAsset(url, out _, out _, out _, out _) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !PluginCatalog.IsAllowedDownload(uri, localTest: false))
            return "The release zip link is not a GitHub release asset link.";
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!UpdateService.IsTrustedGitHubUrl(response.RequestMessage?.RequestUri?.AbsoluteUri))
                return "The download was redirected away from GitHub.";
            if (!response.IsSuccessStatusCode) return $"GitHub answered {(int)response.StatusCode} for the release zip.";
            if (response.Content.Headers.ContentLength > MaxZipBytes) return $"The zip is bigger than {MaxZipBytes / (1024 * 1024)} MB.";
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > MaxZipBytes) return $"The zip is bigger than {MaxZipBytes / (1024 * 1024)} MB.";
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return "The release zip could not be downloaded: " + ex.Message;
        }
    }

    /// <summary>Asks the GitHub API about the source repo and the release asset. Never throws.</summary>
    public static async Task<RemoteFacts> FetchAsync(HttpClient http, SubmissionForm form, string? token, CancellationToken ct = default)
    {
        try
        {
            RepoFacts? source = null;
            if (GitHubLinks.TryParseRepo(form[SubmissionForm.Source], out var so, out var sr))
                source = await RepoAsync(http, so, sr, token, ct);

            AssetFacts? asset = null;
            if (GitHubLinks.TryParseAsset(form[SubmissionForm.Download], out var ao, out var ar, out var tag, out var file))
            {
                using var doc = await ApiGetAsync(http, $"repos/{ao}/{ar}/releases/tags/{Uri.EscapeDataString(tag)}", token, ct);
                if (doc is null) asset = new AssetFacts(false, false, false, null, null);
                else
                {
                    var root = doc.RootElement;
                    var draft = root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;
                    asset = new AssetFacts(true, false, draft, null, null);
                    if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                        foreach (var a in assets.EnumerateArray())
                        {
                            if (!a.TryGetProperty("name", out var n) || n.GetString() != file) continue;
                            long? size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : null;
                            // "digest": "sha256:<hex>", which GitHub computes on upload (absent on old assets).
                            var digest = a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                            var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
                            asset = new AssetFacts(true, true, draft, size, sha);
                            break;
                        }
                }
            }
            return new RemoteFacts(source, asset, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException)
        {
            return new RemoteFacts(null, null, ex.Message);
        }
    }

    private static async Task<RepoFacts> RepoAsync(HttpClient http, string owner, string repo, string? token, CancellationToken ct)
    {
        using var doc = await ApiGetAsync(http, $"repos/{owner}/{repo}", token, ct);
        if (doc is null) return new RepoFacts(false, false, false, false, null);
        var r = doc.RootElement;
        string? license = r.TryGetProperty("license", out var l) && l.ValueKind == JsonValueKind.Object
            && l.TryGetProperty("spdx_id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        return new RepoFacts(true,
            r.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True,
            r.TryGetProperty("archived", out var a) && a.ValueKind == JsonValueKind.True,
            r.TryGetProperty("fork", out var f) && f.ValueKind == JsonValueKind.True,
            license);
    }

    /// <summary>GET api.github.com/<paramref name="path"/>; null on 404. The token (if any) only goes here.</summary>
    private static async Task<JsonDocument?> ApiGetAsync(HttpClient http, string path, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/" + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(text);
    }
}
