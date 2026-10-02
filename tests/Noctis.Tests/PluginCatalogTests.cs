using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The official plugin list (plugins/index.json) behind Settings → Plugins → Get plugins:
/// parsing (bad entries skipped), button states, where the list comes from (live, embedded
/// fallback, NOCTIS_PLUGIN_INDEX override) and the verified download. No network: every
/// request goes to a fake handler.
/// </summary>
public class PluginCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-catalog-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public PluginCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    internal const string GoodUrl = "https://github.com/heartached/Noctis/releases/download/plugin-mixxx-v1.0.0/Noctis.Plugins.Mixxx-1.0.0.zip";
    private static readonly string Sha = new('a', 64);

    /// <summary>One index entry; null leaves the field out, <paramref name="extra"/> adds or overrides fields.</summary>
    internal static Dictionary<string, object?> Entry(
        string? id = "dev.noctis.plugins.mixxx", string? name = "Mixxx", string? version = "1.0.0",
        string? download = GoodUrl, string? sha256 = null, object? size = null, string? minAppVersion = "1.5.9",
        string[]? platforms = null, string? type = "dotnet", params (string Key, object? Value)[] extra)
    {
        var e = new Dictionary<string, object?>
        {
            ["id"] = id, ["name"] = name, ["author"] = "Noctis", ["description"] = "Imports BPM and key.",
            ["version"] = version, ["minAppVersion"] = minAppVersion, ["type"] = type,
            ["platforms"] = platforms ?? new[] { "windows", "macos", "linux" },
            ["download"] = download, ["sha256"] = sha256 ?? Sha, ["size"] = size ?? 1234L,
            ["homepage"] = "https://github.com/heartached/Noctis/tree/main/plugins/Noctis.Plugins.Mixxx",
        };
        foreach (var (k, v) in extra) e[k] = v;
        foreach (var k in e.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) e.Remove(k);
        return e;
    }

    internal static string Index(params object[] entries) => JsonSerializer.Serialize(new { schema = 1, plugins = entries });

    // ── Parsing ──

    [Fact]
    public void Parse_ReadsEveryField()
    {
        var catalog = PluginCatalog.Parse(Index(Entry()));

        var e = Assert.Single(catalog.Plugins);
        Assert.Equal("dev.noctis.plugins.mixxx", e.Id);
        Assert.Equal("Mixxx", e.Name);
        Assert.Equal("Noctis", e.Author);
        Assert.Equal("Imports BPM and key.", e.Description);
        Assert.Equal("1.0.0", e.Version);
        Assert.Equal("1.5.9", e.MinAppVersion);
        Assert.Equal("dotnet", e.Type);
        Assert.True(e.IsCodePlugin);
        Assert.Equal(new[] { "windows", "macos", "linux" }, e.Platforms);
        Assert.Equal(new Uri(GoodUrl), e.Download);
        Assert.Equal(Sha, e.Sha256);
        Assert.Equal(1234, e.Size);
        Assert.Equal("https://github.com/heartached/Noctis/tree/main/plugins/Noctis.Plugins.Mixxx", e.Homepage);
        Assert.False(e.IsLocalTest);
        Assert.Empty(catalog.Warnings);
    }

    [Fact]
    public void Parse_SkipsBrokenEntries_AndKeepsTheRest()
    {
        var catalog = PluginCatalog.Parse(Index(
            Entry(id: null),
            Entry(id: "Mixxx"),
            Entry(name: null),
            Entry(version: "1.0"),
            Entry(download: null),
            Entry(download: "http://github.com/heartached/Noctis/releases/download/x/a.zip"),
            Entry(download: "https://example.com/a.zip"),
            Entry(download: "Noctis.Plugins.Mixxx-1.0.0.zip"),
            Entry(sha256: "abc"),
            Entry(sha256: new string('g', 64)),
            Entry(size: 0L),
            Entry(size: "big"),
            Entry(size: 1.5),
            Entry(type: "python"),
            Entry(minAppVersion: "soon"),
            "not an object",
            Entry(id: "dev.noctis.plugins.good", sha256: new string('B', 64), extra: ("future", new { nested = true })),
            Entry(id: "dev.noctis.plugins.good", name: "Duplicate")));

        var e = Assert.Single(catalog.Plugins);
        Assert.Equal("dev.noctis.plugins.good", e.Id);
        Assert.Equal(new string('b', 64), e.Sha256); // stored lowercase
        Assert.Equal(17, catalog.Warnings.Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"plugins\": []}")]
    [InlineData("{\"schema\": 2, \"plugins\": []}")]
    [InlineData("{\"schema\": \"1\", \"plugins\": []}")]
    [InlineData("{\"schema\": 1}")]
    [InlineData("{\"schema\": 1, \"plugins\": {}}")]
    public void Parse_RejectsFilesItCannotUse(string json)
        => Assert.Throws<PluginCatalogException>(() => PluginCatalog.Parse(json));

    [Fact]
    public void Parse_Platforms_AreLowercased_UnknownKept_EmptyMeansEverywhere()
    {
        var catalog = PluginCatalog.Parse(Index(
            Entry(id: "dev.test.a", platforms: new[] { "Windows", "android" }),
            Entry(id: "dev.test.b", platforms: Array.Empty<string>()),
            Entry(id: "dev.test.c", extra: ("platforms", null))));

        var a = catalog.Plugins[0];
        Assert.Equal(new[] { "windows", "android" }, a.Platforms);
        Assert.True(a.SupportsPlatform("windows"));
        Assert.False(a.SupportsPlatform("linux"));
        Assert.True(catalog.Plugins[1].SupportsPlatform("linux"));
        Assert.True(catalog.Plugins[2].SupportsPlatform("macos"));
    }

    // ── Button state ──

    [Theory]
    [InlineData(null, "1.5.9", "windows", OfficialPluginState.Install)]
    [InlineData(null, "1.6.0", "linux", OfficialPluginState.Install)]
    [InlineData("1.0.0", "1.5.9", "windows", OfficialPluginState.Installed)]
    [InlineData("1.2.0", "1.5.9", "windows", OfficialPluginState.Installed)] // a newer local build
    [InlineData("0.9.0", "1.5.9", "windows", OfficialPluginState.Update)]
    [InlineData("1.0.0-beta", "1.5.9", "windows", OfficialPluginState.Update)]
    [InlineData(null, "1.5.8", "windows", OfficialPluginState.NeedsNewerApp)]
    [InlineData("0.9.0", "1.5.8", "windows", OfficialPluginState.NeedsNewerApp)]
    [InlineData(null, "1.5.9", "freebsd", OfficialPluginState.NotForThisOs)]
    [InlineData(null, "1.5.8", "freebsd", OfficialPluginState.NotForThisOs)]
    [InlineData("1.0.0", "1.5.8", "freebsd", OfficialPluginState.Installed)]
    public void State_FollowsInstalledVersion_AppVersion_AndPlatform(string? installed, string app, string platform, OfficialPluginState expected)
    {
        var entry = Assert.Single(PluginCatalog.Parse(Index(Entry())).Plugins);
        Assert.Equal(expected, PluginCatalog.GetState(entry, installed, app, platform));
    }

    [Fact]
    public void State_WithoutMinAppVersion_NeverAsksForANewerApp()
    {
        var entry = Assert.Single(PluginCatalog.Parse(Index(Entry(minAppVersion: null))).Plugins);
        Assert.Equal(OfficialPluginState.Install, PluginCatalog.GetState(entry, null, "1.0.0", "windows"));
    }

    // ── Where the list comes from ──

    [Fact]
    public void Source_DefaultsToTheRawFileOnMain()
    {
        foreach (var value in new[] { null, "", "   " })
        {
            var s = PluginCatalogSource.Resolve(value);
            Assert.Equal(new Uri("https://raw.githubusercontent.com/heartached/Noctis/main/plugins/index.json"), s.Location);
            Assert.False(s.IsLocalTest);
        }
    }

    [Theory]
    [InlineData("https://example.com/test/index.json")]
    [InlineData("http://localhost:8000/index.json")]
    public void Source_OverrideUrl_IsALocalTestList(string value)
    {
        var s = PluginCatalogSource.Resolve(value);
        Assert.Equal(new Uri(value), s.Location);
        Assert.True(s.IsLocalTest);
    }

    [Fact]
    public void Source_OverrideFilePath_IsALocalTestList()
    {
        var path = Path.Combine(_root, "index.json");
        foreach (var value in new[] { path, "\"" + path + "\"", new Uri(path).AbsoluteUri })
        {
            var s = PluginCatalogSource.Resolve(value);
            Assert.True(s.Location.IsFile);
            Assert.Equal(Path.GetFullPath(path), s.Location.LocalPath);
            Assert.True(s.IsLocalTest);
        }
    }

    [Fact]
    public void LocalTestList_AllowsHttpAnyHostAndFilesNextToTheIndex()
    {
        var indexPath = Path.Combine(_root, "index.json");
        var json = Index(
            Entry(id: "dev.test.rel", download: "Noctis.Plugins.Mixxx-1.0.0.zip"),
            Entry(id: "dev.test.http", download: "http://localhost:8000/p.zip"),
            Entry(id: "dev.test.other", download: "https://example.com/p.zip"),
            Entry(id: "dev.test.ftp", download: "ftp://example.com/p.zip"));

        var catalog = PluginCatalog.Parse(json, new Uri(indexPath), localTest: true);

        Assert.Equal(new[] { "dev.test.rel", "dev.test.http", "dev.test.other" }, catalog.Plugins.Select(p => p.Id));
        Assert.All(catalog.Plugins, p => Assert.True(p.IsLocalTest));
        Assert.Equal(Path.Combine(_root, "Noctis.Plugins.Mixxx-1.0.0.zip"), catalog.Plugins[0].Download.LocalPath);
        Assert.Single(catalog.Warnings);
    }

    [Fact]
    public async Task Load_UsesTheLiveList_WhenItLoads()
    {
        var handler = new FakeHandler { [PluginCatalog.DefaultUrl] = Bytes(Index(Entry(version: "1.1.0"))) };
        var client = new PluginCatalogClient(new HttpClient(handler), () => null) { Embedded = () => PluginCatalog.Parse(Index(Entry())) };

        var load = await client.LoadAsync();

        Assert.False(load.IsFallback);
        Assert.Null(load.Error);
        Assert.Equal("1.1.0", Assert.Single(load.Catalog.Plugins).Version);
        Assert.Equal(new[] { PluginCatalog.DefaultUrl }, handler.Requests);
    }

    [Fact]
    public async Task Load_FallsBackToTheEmbeddedList_WhenOfflineMissingOrBroken()
    {
        var embedded = PluginCatalog.Parse(Index(Entry()));
        foreach (var handler in new[]
                 {
                     new FakeHandler(), // 404: main does not have the file yet
                     new FakeHandler { Throw = new HttpRequestException("No such host is known.") },
                     new FakeHandler { [PluginCatalog.DefaultUrl] = Bytes("{\"schema\": 9, \"plugins\": []}") },
                 })
        {
            var client = new PluginCatalogClient(new HttpClient(handler), () => null) { Embedded = () => embedded };

            var load = await client.LoadAsync();

            Assert.True(load.IsFallback);
            Assert.False(string.IsNullOrEmpty(load.Error));
            Assert.Same(embedded, load.Catalog);
        }
    }

    [Fact]
    public async Task Load_ReadsTheOverrideFile_InsteadOfTheNetwork()
    {
        var indexPath = Path.Combine(_root, "index.json");
        File.WriteAllText(indexPath, Index(Entry(download: "local.zip")));
        var handler = new FakeHandler();
        var client = new PluginCatalogClient(new HttpClient(handler), () => indexPath);

        var load = await client.LoadAsync();

        Assert.False(load.IsFallback);
        Assert.Empty(handler.Requests);
        var e = Assert.Single(load.Catalog.Plugins);
        Assert.True(e.IsLocalTest);
        Assert.Equal(Path.Combine(_root, "local.zip"), e.Download.LocalPath);
    }

    [Fact]
    public void EmbeddedList_IsTheRepositoryFile_AndParsesWithTheStrictRules()
    {
        var repo = File.ReadAllText(PluginSandbox.RepoFile("plugins/index.json"));
        Assert.Equal(repo.ReplaceLineEndings(), PluginCatalog.ReadEmbeddedText().ReplaceLineEndings());

        var catalog = PluginCatalog.LoadEmbedded();
        Assert.Empty(catalog.Warnings);
        Assert.Contains(catalog.Plugins, p => p.Id == "dev.noctis.plugins.mixxx");
    }

    /// <summary>The list and each plugin's own plugin.json must say the same thing, or the
    /// button would offer one version and install another.</summary>
    [Fact]
    public void RepoIndex_AgreesWithEachListedPluginJson()
    {
        var catalog = PluginCatalog.Parse(File.ReadAllText(PluginSandbox.RepoFile("plugins/index.json")));
        Assert.NotEmpty(catalog.Plugins);
        Assert.Empty(catalog.Warnings);

        var manifests = Directory.GetDirectories(PluginSandbox.RepoFile("plugins"))
            .Where(d => File.Exists(Path.Combine(d, PluginManifest.FileName)))
            .Select(d => (Dir: d, Manifest: PluginManifest.TryLoad(d)!))
            .ToList();

        foreach (var entry in catalog.Plugins)
        {
            var match = manifests.Where(m => m.Manifest.Id == entry.Id).ToList();
            var (dir, m) = Assert.Single(match);
            Assert.Equal(m.Name, entry.Name);
            Assert.Equal(m.Version, entry.Version);
            Assert.Equal(m.MinAppVersion, entry.MinAppVersion);
            Assert.Equal(m.Type, entry.Type);
            Assert.Equal(m.Platforms, entry.Platforms);

            // The release asset is the zip PluginPackage.targets writes, on a plugin-<short>-v<version> tag.
            var folder = Path.GetFileName(dir);
            var zip = $"{folder}-{m.Version}.zip";
            var shortName = folder["Noctis.Plugins.".Length..].ToLowerInvariant();
            Assert.Equal($"https://github.com/heartached/Noctis/releases/download/plugin-{shortName}-v{m.Version}/{zip}", entry.Download.AbsoluteUri);
            Assert.Equal($"https://github.com/heartached/Noctis/tree/main/plugins/{folder}", entry.Homepage);
        }
    }

    // ── Download ──

    private static readonly byte[] Zip = Enumerable.Range(0, 50_000).Select(i => (byte)(i * 7 + 3)).ToArray();

    private static PluginCatalogEntry ZipEntry(byte[] bytes, string? sha = null, long? size = null, string url = GoodUrl)
        => Assert.Single(PluginCatalog.Parse(Index(Entry(download: url, sha256: sha ?? Hash(bytes), size: size ?? bytes.LongLength))).Plugins);

    [Fact]
    public async Task Download_ChecksSizeAndHash_ThenHandsBackTheZip()
    {
        var handler = new FakeHandler { [GoodUrl] = Zip };
        var client = new PluginCatalogClient(new HttpClient(handler), () => null);
        var progress = new List<long>();

        var path = await client.DownloadAsync(ZipEntry(Zip), (done, _) => progress.Add(done), CancellationToken.None);
        try
        {
            Assert.Equal(Zip, File.ReadAllBytes(path));
            Assert.Equal(Zip.Length, progress[^1]);
            Assert.Equal("application/octet-stream", handler.LastAccept);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Download_WithTheWrongHash_IsRefused_AndLeavesNothingBehind()
    {
        var before = TempZips();
        var client = new PluginCatalogClient(new HttpClient(new FakeHandler { [GoodUrl] = Zip }), () => null);

        var ex = await Assert.ThrowsAsync<PluginDownloadException>(
            () => client.DownloadAsync(ZipEntry(Zip, sha: new string('0', 64)), null, CancellationToken.None));

        Assert.Equal(PluginDownloadFailure.Mismatch, ex.Failure);
        Assert.Equal(before, TempZips());
    }

    [Fact]
    public async Task Download_WithTheWrongSize_IsRefused()
    {
        foreach (var size in new[] { Zip.LongLength - 1, Zip.LongLength + 1 })
        {
            var client = new PluginCatalogClient(new HttpClient(new FakeHandler { [GoodUrl] = Zip }), () => null);
            var ex = await Assert.ThrowsAsync<PluginDownloadException>(
                () => client.DownloadAsync(ZipEntry(Zip, size: size), null, CancellationToken.None));
            Assert.Equal(PluginDownloadFailure.Mismatch, ex.Failure);
        }
    }

    [Fact]
    public async Task Download_ServerError_IsANetworkFailure()
    {
        var client = new PluginCatalogClient(new HttpClient(new FakeHandler()), () => null);
        var ex = await Assert.ThrowsAsync<PluginDownloadException>(
            () => client.DownloadAsync(ZipEntry(Zip), null, CancellationToken.None));
        Assert.Equal(PluginDownloadFailure.Network, ex.Failure);
    }

    [Fact]
    public async Task Download_RefusesHttpAndFiles_OutsideALocalTestList()
    {
        var client = new PluginCatalogClient(new HttpClient(new FakeHandler()), () => null);
        var good = ZipEntry(Zip);
        foreach (var uri in new[] { "http://github.com/a.zip", new Uri(Path.Combine(_root, "a.zip")).AbsoluteUri, "https://example.com/a.zip" })
        {
            var ex = await Assert.ThrowsAsync<PluginDownloadException>(
                () => client.DownloadAsync(good with { Download = new Uri(uri) }, null, CancellationToken.None));
            Assert.Equal(PluginDownloadFailure.NotAllowed, ex.Failure);
        }
    }

    [Fact]
    public async Task Download_FromALocalTestList_CopiesALocalFile()
    {
        var zipPath = Path.Combine(_root, "local.zip");
        File.WriteAllBytes(zipPath, Zip);
        var entry = Assert.Single(PluginCatalog.Parse(Index(Entry(download: "local.zip", sha256: Hash(Zip), size: Zip.LongLength)),
            new Uri(Path.Combine(_root, "index.json")), localTest: true).Plugins);
        var client = new PluginCatalogClient(new HttpClient(new FakeHandler()), () => null);

        var path = await client.DownloadAsync(entry, null, CancellationToken.None);
        try
        {
            Assert.NotEqual(zipPath, path);
            Assert.Equal(Zip, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    // ── Helpers ──

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    private static string[] TempZips() => Directory.GetFiles(Path.GetTempPath(), PluginCatalogClient.TempFilePrefix + "*").OrderBy(f => f).ToArray();

    /// <summary>Serves fixed bodies by URL; anything else is a 404.</summary>
    internal sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _bodies = new();
        public byte[] this[string url] { set => _bodies[url] = value; }
        public Exception? Throw { get; init; }
        public List<string> Requests { get; } = new();
        public string? LastAccept { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requests) Requests.Add(url);
            LastAccept = request.Headers.Accept.FirstOrDefault()?.MediaType;
            if (Throw is not null) throw Throw;
            if (!_bodies.TryGetValue(url, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent(Array.Empty<byte>()) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }
}
