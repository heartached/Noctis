using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Noctis.Services.Plugins;

/// <summary>One plugin of the official list (plugins/index.json), validated.</summary>
public sealed record PluginCatalogEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public required string Version { get; init; }
    public string? MinAppVersion { get; init; }
    public string Type { get; init; } = PluginManifest.TypeDotnet;
    /// <summary>Lowercase platform names; empty = every desktop platform.</summary>
    public IReadOnlyList<string> Platforms { get; init; } = Array.Empty<string>();
    /// <summary>The zip. https on GitHub, except in a <see cref="IsLocalTest"/> list.</summary>
    public required Uri Download { get; init; }
    /// <summary>Lowercase hex SHA-256 of the zip.</summary>
    public required string Sha256 { get; init; }
    /// <summary>Exact size of the zip in bytes.</summary>
    public required long Size { get; init; }
    public string? Homepage { get; init; }
    /// <summary>From a NOCTIS_PLUGIN_INDEX list: http:// and local files are allowed for its downloads.</summary>
    public bool IsLocalTest { get; init; }

    public bool IsCodePlugin => Type == PluginManifest.TypeDotnet;
    public bool SupportsPlatform(string platform) => Platforms.Count == 0 || Platforms.Contains(platform);
}

/// <summary>What the Get plugins button offers for a listed plugin.</summary>
public enum OfficialPluginState { Install, Installed, Update, NeedsNewerApp, NotForThisOs }

/// <summary>The list file could not be used at all (not JSON, another schema, no "plugins" array).</summary>
public sealed class PluginCatalogException : Exception
{
    public PluginCatalogException(string message) : base(message) { }
}

/// <summary>
/// The official plugin list behind Settings → Plugins → Get plugins. It is one file in the repo,
/// <c>plugins/index.json</c>, fetched from main (<see cref="DefaultUrl"/>) when the Plugins page
/// opens; a copy is embedded in the app for when that fails. Entries with a missing or bad field
/// are skipped (listed in <see cref="Warnings"/>), never fatal. See docs/PLUGINS.md.
/// </summary>
public sealed class PluginCatalog
{
    public const int Schema = 1;
    public const string DefaultUrl = "https://raw.githubusercontent.com/heartached/Noctis/main/plugins/index.json";
    /// <summary>Environment variable naming a test list (a file path or URL) to use instead.</summary>
    public const string OverrideVariable = "NOCTIS_PLUGIN_INDEX";
    internal const string EmbeddedResourceName = "Noctis.Plugins.index.json";
    internal const long MaxIndexBytes = 1024 * 1024;

    private PluginCatalog(IReadOnlyList<PluginCatalogEntry> plugins, IReadOnlyList<string> warnings)
    {
        Plugins = plugins;
        Warnings = warnings;
    }

    public IReadOnlyList<PluginCatalogEntry> Plugins { get; }
    /// <summary>One line per skipped entry, for the log.</summary>
    public IReadOnlyList<string> Warnings { get; }

    public static PluginCatalog Empty { get; } = new(Array.Empty<PluginCatalogEntry>(), Array.Empty<string>());

    /// <summary>
    /// Parses a list. <paramref name="localTest"/> (a NOCTIS_PLUGIN_INDEX list) also accepts http://
    /// and file downloads, any host, and download paths relative to <paramref name="baseUri"/>;
    /// otherwise every download must be https on GitHub.
    /// </summary>
    /// <exception cref="PluginCatalogException">Not a schema-1 list.</exception>
    public static PluginCatalog Parse(string json, Uri? baseUri = null, bool localTest = false)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException ex) { throw new PluginCatalogException("The plugin list is not valid JSON: " + ex.Message); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new PluginCatalogException("The plugin list must be a JSON object.");
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out var schemaVersion) || schemaVersion != Schema)
                throw new PluginCatalogException($"The plugin list is not schema {Schema}.");
            if (!root.TryGetProperty("plugins", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new PluginCatalogException("The plugin list has no \"plugins\" array.");

            var plugins = new List<PluginCatalogEntry>();
            var warnings = new List<string>();
            var index = -1;
            foreach (var item in list.EnumerateArray())
            {
                index++;
                var entry = ParseEntry(item, baseUri, localTest, out var why);
                if (entry is null) warnings.Add($"plugins[{index}]: {why}");
                else if (plugins.Any(p => p.Id == entry.Id)) warnings.Add($"plugins[{index}]: \"{entry.Id}\" is listed twice.");
                else plugins.Add(entry);
            }
            return new PluginCatalog(plugins, warnings);
        }
    }

    private static PluginCatalogEntry? ParseEntry(JsonElement e, Uri? baseUri, bool localTest, out string why)
    {
        why = "";
        if (e.ValueKind != JsonValueKind.Object) { why = "not an object."; return null; }

        var id = Text(e, "id");
        if (id is null || !PluginManifest.IsValidId(id)) { why = "missing or bad \"id\"."; return null; }
        var name = Text(e, "name");
        if (string.IsNullOrEmpty(name) || name.Length > 60) { why = "missing or bad \"name\"."; return null; }
        var version = Text(e, "version");
        if (version is null || !PluginVersion.TryParse(version, out _)) { why = "missing or bad \"version\"."; return null; }
        var minApp = Text(e, "minAppVersion");
        if (minApp is { Length: 0 }) minApp = null;
        if (minApp is not null && !PluginVersion.TryParseLoose(minApp, out _)) { why = "bad \"minAppVersion\"."; return null; }
        var type = (Text(e, "type") ?? PluginManifest.TypeDotnet).ToLowerInvariant();
        if (type is not (PluginManifest.TypeDotnet or PluginManifest.TypeContent)) { why = $"unknown \"type\" \"{type}\"."; return null; }

        var platforms = new List<string>();
        if (e.TryGetProperty("platforms", out var pl) && pl.ValueKind == JsonValueKind.Array)
            foreach (var p in pl.EnumerateArray())
                if (p.ValueKind == JsonValueKind.String && p.GetString()!.Trim().ToLowerInvariant() is { Length: > 0 } norm && !platforms.Contains(norm))
                    platforms.Add(norm);

        var download = ResolveDownload(Text(e, "download"), baseUri, localTest);
        if (download is null) { why = "missing or not allowed \"download\"."; return null; }

        var sha = Text(e, "sha256")?.ToLowerInvariant();
        if (sha is null || sha.Length != 64 || !sha.All(char.IsAsciiHexDigit)) { why = "missing or bad \"sha256\"."; return null; }
        if (!e.TryGetProperty("size", out var sz) || sz.ValueKind != JsonValueKind.Number || !sz.TryGetInt64(out var size)
            || size <= 0 || size > PluginInstaller.MaxTotalBytes)
        { why = "missing or bad \"size\"."; return null; }

        var homepage = Text(e, "homepage");
        if (homepage is not null && !(Uri.TryCreate(homepage, UriKind.Absolute, out var hp) && hp.Scheme is "https" or "http")) homepage = null;

        return new PluginCatalogEntry
        {
            Id = id,
            Name = name,
            Author = Text(e, "author") ?? "",
            Description = Text(e, "description") ?? "",
            Version = version,
            MinAppVersion = minApp,
            Type = type,
            Platforms = platforms,
            Download = download,
            Sha256 = sha,
            Size = size,
            Homepage = homepage,
            IsLocalTest = localTest,
        };
    }

    private static Uri? ResolveDownload(string? value, Uri? baseUri, bool localTest)
    {
        if (string.IsNullOrEmpty(value)) return null;
        Uri? uri;
        if (localTest && baseUri is not null) Uri.TryCreate(baseUri, value, out uri);
        else Uri.TryCreate(value, UriKind.Absolute, out uri);
        return uri is not null && IsAllowedDownload(uri, localTest) ? uri : null;
    }

    /// <summary>https on GitHub; a test list may also use http:// and local files, on any host.</summary>
    internal static bool IsAllowedDownload(Uri uri, bool localTest)
    {
        if (!uri.IsAbsoluteUri) return false;
        if (localTest) return uri.Scheme is "https" or "http" || uri.IsFile;
        return UpdateService.IsTrustedGitHubUrl(uri.AbsoluteUri);
    }

    private static string? Text(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : null;

    /// <summary>
    /// The button for <paramref name="entry"/>: Installed when this version or a newer one is
    /// installed; otherwise Not for this OS, Needs a newer Noctis, or Install / Update.
    /// </summary>
    public static OfficialPluginState GetState(PluginCatalogEntry entry, string? installedVersion, string appVersion, string platform)
    {
        if (installedVersion is not null && PluginVersion.Compare(entry.Version, installedVersion) <= 0) return OfficialPluginState.Installed;
        if (!entry.SupportsPlatform(platform)) return OfficialPluginState.NotForThisOs;
        if (entry.MinAppVersion is not null && PluginVersion.TryParseLoose(appVersion, out var app)
            && PluginVersion.TryParseLoose(entry.MinAppVersion, out var min) && app < min)
            return OfficialPluginState.NeedsNewerApp;
        return installedVersion is null ? OfficialPluginState.Install : OfficialPluginState.Update;
    }

    /// <summary>The copy of plugins/index.json built into the app.</summary>
    internal static string ReadEmbeddedText()
    {
        using var stream = typeof(PluginCatalog).Assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {EmbeddedResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The built-in list; empty if it somehow cannot be read.</summary>
    public static PluginCatalog LoadEmbedded()
    {
        try { return Parse(ReadEmbeddedText()); }
        catch (Exception ex) when (ex is PluginCatalogException or InvalidOperationException or IOException)
        {
            DebugLogger.Error(DebugLogger.Category.State, "Plugins.Catalog", $"embedded list: {ex.Message}");
            return Empty;
        }
    }
}

/// <summary>Where the list is read from: <see cref="PluginCatalog.DefaultUrl"/>, or a NOCTIS_PLUGIN_INDEX test list.</summary>
public sealed record PluginCatalogSource(Uri Location, bool IsLocalTest)
{
    /// <summary>Empty → the official list on main. A URL or a file path (or file:// URI) → that test list.</summary>
    public static PluginCatalogSource Resolve(string? overrideValue)
    {
        var value = overrideValue?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(value)) return new PluginCatalogSource(new Uri(PluginCatalog.DefaultUrl), false);
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme is "https" or "http" || uri.IsFile))
            return new PluginCatalogSource(uri.IsFile ? new Uri(Path.GetFullPath(uri.LocalPath)) : uri, true);
        return new PluginCatalogSource(new Uri(Path.GetFullPath(value)), true);
    }
}

/// <param name="IsFallback">The source failed and <see cref="Catalog"/> is the built-in copy.</param>
/// <param name="Error">Why the source failed (for the log), or null.</param>
/// <param name="Source">Null when NOCTIS_PLUGIN_INDEX could not be read as a path or URL.</param>
public sealed record PluginCatalogLoad(PluginCatalog Catalog, bool IsFallback, string? Error, PluginCatalogSource? Source);

public enum PluginDownloadFailure { Network, Mismatch, NotAllowed }

/// <summary>A listed plugin's zip could not be fetched, or was not the one the list describes.</summary>
public sealed class PluginDownloadException : Exception
{
    public PluginDownloadException(PluginDownloadFailure failure, string message, Exception? inner = null) : base(message, inner)
        => Failure = failure;

    public PluginDownloadFailure Failure { get; }
}

/// <summary>
/// Reads the official list and downloads its zips. A download is checked against the list's
/// size and SHA-256 before it is handed back; only then may it reach
/// <see cref="PluginHost.InstallPackage"/>, so nothing unverified touches the plugins folder.
/// </summary>
public sealed class PluginCatalogClient
{
    internal const string TempFilePrefix = "noctis-plugin-download-";

    private readonly HttpClient _http;
    private readonly Func<string?> _readOverride;

    /// <param name="readOverride">The NOCTIS_PLUGIN_INDEX value; tests pass their own.</param>
    public PluginCatalogClient(HttpClient http, Func<string?>? readOverride = null)
    {
        _http = http;
        _readOverride = readOverride ?? (() => Environment.GetEnvironmentVariable(PluginCatalog.OverrideVariable));
    }

    /// <summary>The fallback list. Tests swap it.</summary>
    internal Func<PluginCatalog> Embedded { get; set; } = PluginCatalog.LoadEmbedded;

    /// <summary>Null = <see cref="ResumableDownload.Options.Default"/>; tests shorten the retry delays.</summary>
    internal ResumableDownload.Options? DownloadOptions { get; set; }

    /// <summary>The list from its source, or the built-in copy when that fails (offline, 404, bad file).</summary>
    public async Task<PluginCatalogLoad> LoadAsync(CancellationToken ct = default)
    {
        PluginCatalogSource? source = null;
        try
        {
            source = PluginCatalogSource.Resolve(_readOverride());
            string json;
            if (source.Location.IsFile)
            {
                var path = source.Location.LocalPath;
                if (new FileInfo(path).Length > PluginCatalog.MaxIndexBytes) throw new IOException("The plugin list is larger than 1 MB.");
                json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            }
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, source.Location);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                json = await HttpSafety.ReadStringBoundedAsync(response.Content, PluginCatalog.MaxIndexBytes, ct).ConfigureAwait(false);
            }
            var catalog = PluginCatalog.Parse(json, source.Location, source.IsLocalTest);
            foreach (var w in catalog.Warnings) DebugLogger.Warn(DebugLogger.Category.State, "Plugins.Catalog", $"{source.Location}: {w}");
            if (source.IsLocalTest) DebugLogger.Info(DebugLogger.Category.State, "Plugins.Catalog", $"test list {source.Location}: {catalog.Plugins.Count} plugin(s)");
            return new PluginCatalogLoad(catalog, false, null, source);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or PluginCatalogException
                                       or ArgumentException or NotSupportedException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            DebugLogger.Warn(DebugLogger.Category.State, "Plugins.Catalog", $"{source?.Location}: {ex.GetType().Name}: {ex.Message}; using the built-in list");
            return new PluginCatalogLoad(Embedded(), true, ex.Message, source);
        }
    }

    /// <summary>
    /// Downloads <paramref name="entry"/>'s zip to a temporary file and checks its size and SHA-256
    /// against the list. Returns the file's path; the caller installs it and deletes it.
    /// <paramref name="progress"/> receives (bytes so far, expected size).
    /// </summary>
    /// <exception cref="PluginDownloadException">Not allowed, failed, or not the listed file (the file is removed).</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public async Task<string> DownloadAsync(PluginCatalogEntry entry, Action<long, long>? progress, CancellationToken ct)
    {
        if (!PluginCatalog.IsAllowedDownload(entry.Download, entry.IsLocalTest))
            throw new PluginDownloadException(PluginDownloadFailure.NotAllowed, $"Downloads must be https from GitHub: {entry.Download}");

        var path = Path.Combine(Path.GetTempPath(), TempFilePrefix + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            if (entry.Download.IsFile) await CopyLocalAsync(entry, path, progress, ct).ConfigureAwait(false);
            else await FetchAsync(entry, path, progress, ct).ConfigureAwait(false);

            var length = new FileInfo(path).Length;
            if (length != entry.Size)
                throw new PluginDownloadException(PluginDownloadFailure.Mismatch, $"{entry.Id}: got {length} bytes, the list says {entry.Size}.");
            var hash = await ComputeSha256Async(path, ct).ConfigureAwait(false);
            if (!string.Equals(hash, entry.Sha256, StringComparison.Ordinal))
                throw new PluginDownloadException(PluginDownloadFailure.Mismatch, $"{entry.Id}: SHA-256 {hash}, the list says {entry.Sha256}.");
            DebugLogger.Info(DebugLogger.Category.State, "Plugins.Catalog", $"downloaded {entry.Id} {entry.Version} ({length} bytes, SHA-256 ok)");
            return path;
        }
        catch (Exception ex)
        {
            TryDelete(path);
            if (ex is PluginDownloadException || (ex is OperationCanceledException && ct.IsCancellationRequested)) throw;
            if (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                DebugLogger.Warn(DebugLogger.Category.State, "Plugins.Catalog", $"download {entry.Id}: {ex.GetType().Name}: {ex.Message}");
                throw new PluginDownloadException(PluginDownloadFailure.Network, ex.Message, ex);
            }
            throw;
        }
    }

    private async Task FetchAsync(PluginCatalogEntry entry, string path, Action<long, long>? progress, CancellationToken ct)
    {
        // Stop as soon as the server sends more than the list says: it is not the listed file.
        using var tooBig = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var overflow = false;
        try
        {
            await ResumableDownload.DownloadAsync(
                _http,
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, entry.Download);
                    request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/octet-stream"));
                    return request;
                },
                path,
                resumeExisting: false,
                (done, _) =>
                {
                    if (done > entry.Size && !overflow) { overflow = true; tooBig.Cancel(); }
                    progress?.Invoke(Math.Min(done, entry.Size), entry.Size);
                },
                "Plugins." + entry.Id,
                tooBig.Token,
                DownloadOptions).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (overflow && !ct.IsCancellationRequested)
        {
            throw new PluginDownloadException(PluginDownloadFailure.Mismatch, $"{entry.Id}: the server sent more than the {entry.Size} bytes the list says.");
        }
    }

    private static async Task CopyLocalAsync(PluginCatalogEntry entry, string path, Action<long, long>? progress, CancellationToken ct)
    {
        await using var source = File.OpenRead(entry.Download.LocalPath);
        if (source.Length > entry.Size)
            throw new PluginDownloadException(PluginDownloadFailure.Mismatch, $"{entry.Id}: the file has {source.Length} bytes, the list says {entry.Size}.");
        await using var target = File.Create(path);
        await source.CopyToAsync(target, ct).ConfigureAwait(false);
        progress?.Invoke(source.Length, entry.Size);
    }

    internal static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp folder; the OS cleans it */ }
    }
}
