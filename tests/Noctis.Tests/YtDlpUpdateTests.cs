using Noctis.Services.YouTube;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// yt-dlp self-update + blocked-download retry (09-24 report: "HTTP Error 403: Forbidden" from a
/// stale yt-dlp on Noctis 1.5.2). Pure helpers plus the tool driven through its internal seams.
/// </summary>
public class YtDlpUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-ytdlp-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private const string Blocked403 = "WARNING: something\nERROR: unable to download video data: HTTP Error 403: Forbidden\n";

    // ── Versions ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026.08.19", "2026.03.17", 1)]
    [InlineData("2026.03.17", "2026.08.19", -1)]
    [InlineData("2026.08.19", "2026.08.19", 0)]
    [InlineData("2026.08.19.1", "2026.08.19", 1)]
    [InlineData("2026.08.19", "2026.08.19.0", 0)]
    [InlineData("2026.10.01", "2026.9.30", 1)]
    [InlineData("v2026.08.19", "2026.08.19", 0)]
    [InlineData("2026.08.19", null, 1)]
    [InlineData("garbage", "2026.01.01", -1)]
    [InlineData(null, null, 0)]
    public void CompareVersions_OrdersYtDlpVersions(string? a, string? b, int expected)
    {
        Assert.Equal(expected, Math.Sign(YtDlpParsing.CompareVersions(a, b)));
    }

    [Fact]
    public void IsNewer_NeedsARealLatestVersion()
    {
        Assert.True(YtDlpParsing.IsNewer("2026.08.19", "2026.07.04"));
        Assert.False(YtDlpParsing.IsNewer("2026.08.19", "2026.08.19"));
        Assert.False(YtDlpParsing.IsNewer(null, "2026.07.04"));
        Assert.False(YtDlpParsing.IsNewer("not-a-version", null));
    }

    [Fact]
    public void ParseLatestTag_ReadsGitHubReleaseJson()
    {
        Assert.Equal("2026.08.19", YtDlpParsing.ParseLatestTag("""{"tag_name":"2026.08.19","name":"yt-dlp 2026.08.19"}"""));
        Assert.Null(YtDlpParsing.ParseLatestTag("""{"message":"API rate limit exceeded"}"""));
        Assert.Null(YtDlpParsing.ParseLatestTag("""{"tag_name":"nightly"}"""));
        Assert.Null(YtDlpParsing.ParseLatestTag("<html>"));
    }

    [Fact]
    public void ShouldCheckForUpdate_ThrottlesTo24Hours_UnlessForced()
    {
        var now = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(YtDlpParsing.ShouldCheckForUpdate(null, now, force: false));
        Assert.False(YtDlpParsing.ShouldCheckForUpdate(now.AddHours(-23), now, force: false));
        Assert.True(YtDlpParsing.ShouldCheckForUpdate(now.AddHours(-24), now, force: false));
        Assert.True(YtDlpParsing.ShouldCheckForUpdate(now.AddHours(-1), now, force: true));
        // A timestamp from the future (clock moved back) must not freeze checks.
        Assert.True(YtDlpParsing.ShouldCheckForUpdate(now.AddDays(3), now, force: false));
    }

    // ── Blocked detection + messages ─────────────────────────────────────────

    [Theory]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden", true)]
    [InlineData("Error opening input: Server returned 403 Forbidden (access denied)\nERROR: ffmpeg exited", false)]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm you’re not a bot. Use --cookies-from-browser", true)]
    [InlineData("ERROR: [youtube] abc: Requested format is not available. Use --list-formats", true)]
    [InlineData("ERROR: [youtube] abc: Video unavailable", false)]
    [InlineData("ERROR: unable to download video data: HTTP Error 404: Not Found", false)]
    [InlineData("", false)]
    public void IsBlockedError_MatchesYouTubeRefusals(string stderr, bool expected)
    {
        Assert.Equal(expected, YtDlpParsing.IsBlockedError(stderr));
    }

    [Fact]
    public void BlockedMessage_DependsOnWhetherAnUpdateCouldHelp()
    {
        var upToDate = YtDlpParsing.BlockedMessage("2026.08.19", "2026.08.19");
        Assert.StartsWith("YouTube blocked this download. yt-dlp is up to date (version 2026.08.19).", upToDate);
        Assert.DoesNotContain("ERROR", upToDate);

        var outdated = YtDlpParsing.BlockedMessage("2026.07.04", "2026.08.19");
        Assert.Contains("2026.07.04", outdated);
        Assert.Contains("2026.08.19", outdated);
        Assert.Contains("out of date", outdated);

        var unknown = YtDlpParsing.BlockedMessage("2026.07.04", null);
        Assert.StartsWith("YouTube blocked this download (yt-dlp version 2026.07.04).", unknown);
    }

    [Fact]
    public void VersionLabel_AddsUpdateAvailableOnlyWhenNewer()
    {
        Assert.Equal("yt-dlp 2026.08.19", YtDlpParsing.VersionLabel("2026.08.19", "2026.08.19"));
        Assert.Equal("yt-dlp 2026.07.04 · update available", YtDlpParsing.VersionLabel("2026.07.04", "2026.08.19"));
        Assert.Equal("yt-dlp 2026.07.04", YtDlpParsing.VersionLabel("2026.07.04", null));
        Assert.Equal(string.Empty, YtDlpParsing.VersionLabel(null, "2026.08.19"));
    }

    [Fact]
    public void StderrTail_RedactsStreamUrls_AndKeepsTheLastLines()
    {
        var stderr = string.Join('\n', Enumerable.Range(1, 12).Select(i => $"line {i}"))
                     + "\nError opening input file https://rr2---sn-x.googlevideo.com/videoplayback?expire=1&ip=2601%3A18c&itag=401.\n"
                     + Blocked403;
        var tail = YtDlpParsing.StderrTail(stderr);
        Assert.DoesNotContain("googlevideo", tail);
        Assert.DoesNotContain("2601", tail);
        Assert.Contains("<stream-url>", tail);
        Assert.EndsWith("ERROR: unable to download video data: HTTP Error 403: Forbidden", tail);
        Assert.DoesNotContain("line 1 ", tail + " ");
    }

    // ── JS runtime ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, true, "2026.08.19", "node")]
    [InlineData(true, true, "2026.08.19", null)]
    [InlineData(false, false, "2026.08.19", null)]
    [InlineData(false, true, "2025.10.22", null)] // before --js-runtimes existed: the flag would be rejected
    [InlineData(false, true, null, null)]
    public void PickJsRuntime_NodeOnlyWhenDenoMissingAndYtDlpSupportsIt(bool deno, bool node, string? version, string? expected)
    {
        Assert.Equal(expected, YtDlpParsing.PickJsRuntime(deno, node, version));
    }

    [Fact]
    public void ArgsBuilders_PassJsRuntimeBeforeTheUrl()
    {
        const string url = "https://www.youtube.com/watch?v=NvU2OQPyq-8";
        foreach (var args in new[]
                 {
                     YtDlpParsing.InfoArgs(url, "node"),
                     YtDlpParsing.DownloadArgs(url, "out/%(id)s.%(ext)s", null, "node"),
                     YtDlpParsing.VideoDownloadArgs(url, "out/%(id)s.%(ext)s", "C:/ffmpeg/bin", 0, "node"),
                 })
        {
            var i = args.ToList().IndexOf("--js-runtimes");
            Assert.True(i >= 0);
            Assert.Equal("node", args[i + 1]);
            Assert.Equal(url, args[^1]);
        }
        Assert.Equal("node", YtDlpParsing.SearchArgs("song", 5, "node").SkipWhile(a => a != "--js-runtimes").Skip(1).First());
        Assert.DoesNotContain("--js-runtimes", YtDlpParsing.VideoDownloadArgs(url, "o", null, 720));
        Assert.DoesNotContain("--js-runtimes", YtDlpParsing.InfoArgs(url));
    }

    // ── Tool: update check / throttle / never touching foreign copies ────────

    private sealed class Fake
    {
        public string Version = "2026.07.04";
        public string? Latest = "2026.08.19";
        public DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        public int FetchCalls, UpdateCalls;
        public readonly List<IReadOnlyList<string>> Runs = new();
        public readonly Queue<(int Code, string Stderr)> Outcomes = new();
    }

    private YtDlpTool MakeTool(Fake fake, string? overridePath = null, bool installAppCopy = true)
    {
        var tool = new YtDlpTool(new HttpClient(), _root, () => overridePath ?? string.Empty);
        if (installAppCopy)
        {
            Directory.CreateDirectory(tool.ToolsDirectory);
            File.WriteAllText(tool.InstalledPath, "old");
        }
        tool.Clock = () => fake.Now;
        tool.JsRuntimes = (true, false);
        tool.LatestVersionFetcher = _ => { fake.FetchCalls++; return Task.FromResult(fake.Latest); };
        tool.Updater = _ =>
        {
            fake.UpdateCalls++;
            fake.Version = fake.Latest!;
            File.WriteAllText(tool.InstalledPath, "new");
            return Task.CompletedTask;
        };
        tool.Runner = (exe, args, onLine, ct) =>
        {
            if (args.Count == 1 && args[0] == "--version") return Task.FromResult((0, fake.Version + "\n", string.Empty));
            fake.Runs.Add(args);
            var (code, stderr) = fake.Outcomes.Count > 0 ? fake.Outcomes.Dequeue() : (0, string.Empty);
            var o = args.ToList().IndexOf("-o");
            if (o >= 0 && code == 0)
            {
                var file = args[o + 1].Replace("%(id)s", "vid").Replace("%(ext)s", "mp4");
                File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
                onLine?.Invoke("[download] 100.0% of 3B");
            }
            var stdout = args.Contains("--dump-json") && code == 0 ? """{"id":"vid","title":"A - B"}""" + "\n" : string.Empty;
            return Task.FromResult((code, stdout, stderr));
        };
        return tool;
    }

    [Fact]
    public async Task CheckForUpdate_UpdatesAppInstalledCopy_AndIsThrottledAndPersisted()
    {
        var fake = new Fake();
        var tool = MakeTool(fake);

        var first = await tool.CheckForUpdateAsync(force: false, CancellationToken.None);
        Assert.True(first.Updated);
        Assert.True(first.IsAppInstalled);
        Assert.Equal("2026.08.19", first.InstalledVersion);
        Assert.False(first.UpdateAvailable);
        Assert.Equal(1, fake.FetchCalls);
        Assert.Equal(1, fake.UpdateCalls);

        // Within 24 h: no network lookup, and a fresh instance reads the persisted timestamp.
        fake.Now = fake.Now.AddHours(20);
        await tool.CheckForUpdateAsync(force: false, CancellationToken.None);
        var second = MakeTool(fake, installAppCopy: false);
        var again = await second.CheckForUpdateAsync(force: false, CancellationToken.None);
        Assert.Equal(1, fake.FetchCalls);
        Assert.Equal("2026.08.19", again.LatestVersion);
        Assert.Equal("2026.08.19", second.LatestKnownVersion);

        // A forced check ignores the throttle; after 24 h the quiet one runs again.
        await tool.CheckForUpdateAsync(force: true, CancellationToken.None);
        Assert.Equal(2, fake.FetchCalls);
        fake.Now = fake.Now.AddHours(25);
        await tool.CheckForUpdateAsync(force: false, CancellationToken.None);
        Assert.Equal(3, fake.FetchCalls);
        Assert.Equal(1, fake.UpdateCalls); // already current: nothing more to install
    }

    [Fact]
    public async Task CheckForUpdate_NeverReplacesAUserConfiguredCopy()
    {
        var fake = new Fake();
        var custom = Path.Combine(_root, "custom", "yt-dlp.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(custom)!);
        File.WriteAllText(custom, "user copy");
        var tool = MakeTool(fake, overridePath: custom, installAppCopy: false);

        var status = await tool.CheckForUpdateAsync(force: true, CancellationToken.None);
        Assert.False(status.IsAppInstalled);
        Assert.False(status.Updated);
        Assert.True(status.UpdateAvailable);
        Assert.Equal(0, fake.UpdateCalls);
        Assert.Equal("user copy", File.ReadAllText(custom));
        Assert.False(File.Exists(tool.InstalledPath));
    }

    [Fact]
    public async Task CheckForUpdate_SwallowsNetworkErrors()
    {
        var fake = new Fake();
        var tool = MakeTool(fake);
        tool.LatestVersionFetcher = _ => throw new HttpRequestException("offline");
        var status = await tool.CheckForUpdateAsync(force: false, CancellationToken.None);
        Assert.False(status.Updated);
        Assert.Null(status.LatestVersion);
        Assert.Equal(0, fake.UpdateCalls);

        var session = await tool.EnsureSessionUpdateCheckAsync();
        Assert.Same(await tool.EnsureSessionUpdateCheckAsync(), session); // once per session
    }

    // ── Tool: blocked → update → retry once ──────────────────────────────────

    [Fact]
    public async Task DownloadVideo_Blocked_UpdatesAndRetriesOnce_ThenSucceeds()
    {
        var fake = new Fake();
        var tool = MakeTool(fake);
        fake.Outcomes.Enqueue((1, Blocked403));
        var statuses = new List<string>();
        var target = Path.Combine(_root, "dl");
        Directory.CreateDirectory(target);

        var produced = await tool.DownloadVideoAsync("https://www.youtube.com/watch?v=vid", target, null, 0, null, CancellationToken.None, statuses.Add);

        Assert.True(File.Exists(produced));
        Assert.Equal(2, fake.Runs.Count);
        Assert.Equal(1, fake.UpdateCalls);
        Assert.Equal(1, fake.FetchCalls);
        Assert.Equal(new[] { "Updating yt-dlp and retrying…" }, statuses);
        // The failed attempt's scratch folder is gone; only the successful one remains.
        Assert.Single(Directory.GetDirectories(target));
    }

    [Fact]
    public async Task Download_StillBlockedAfterRetry_ShowsFriendlyMessage_AndRetriesOnlyOnce()
    {
        var fake = new Fake { Version = "2026.08.19" };
        var tool = MakeTool(fake);
        fake.Outcomes.Enqueue((1, Blocked403));
        fake.Outcomes.Enqueue((1, Blocked403));
        fake.Outcomes.Enqueue((1, Blocked403));
        var target = Path.Combine(_root, "dl");
        Directory.CreateDirectory(target);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tool.DownloadAsync("https://www.youtube.com/watch?v=vid", target, null, null, CancellationToken.None));

        Assert.Equal(2, fake.Runs.Count);
        Assert.Equal(0, fake.UpdateCalls); // already current
        Assert.StartsWith("YouTube blocked this download. yt-dlp is up to date (version 2026.08.19).", ex.Message);
        Assert.IsType<YtDlpRunException>(ex.InnerException);
        Assert.Empty(Directory.GetDirectories(target));
    }

    [Fact]
    public async Task Download_BlockedWithUserCopy_NoUpdateNoRetry_SaysUpdateAvailable()
    {
        var fake = new Fake();
        var custom = Path.Combine(_root, "custom", "yt-dlp.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(custom)!);
        File.WriteAllText(custom, "user copy");
        var tool = MakeTool(fake, overridePath: custom, installAppCopy: false);
        fake.Outcomes.Enqueue((1, Blocked403));
        var target = Path.Combine(_root, "dl");
        Directory.CreateDirectory(target);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tool.DownloadVideoAsync("https://www.youtube.com/watch?v=vid", target, null, 0, null, CancellationToken.None));

        Assert.Single(fake.Runs);
        Assert.Equal(0, fake.UpdateCalls);
        Assert.Contains("out of date", ex.Message);
        Assert.Equal("user copy", File.ReadAllText(custom));
    }

    [Fact]
    public async Task Download_OtherFailure_KeepsRawError_AndDoesNotUpdate()
    {
        var fake = new Fake();
        var tool = MakeTool(fake);
        fake.Outcomes.Enqueue((1, "ERROR: [youtube] vid: Video unavailable\n"));
        var target = Path.Combine(_root, "dl");
        Directory.CreateDirectory(target);

        var ex = await Assert.ThrowsAsync<YtDlpRunException>(() =>
            tool.DownloadAsync("https://www.youtube.com/watch?v=vid", target, null, null, CancellationToken.None));

        Assert.Equal("ERROR: [youtube] vid: Video unavailable", ex.Message);
        Assert.Single(fake.Runs);
        Assert.Equal(0, fake.FetchCalls);
        Assert.Equal(0, fake.UpdateCalls);
    }

    [Fact]
    public async Task GetInfo_BotCheck_UpdatesAndRetries()
    {
        var fake = new Fake();
        var tool = MakeTool(fake);
        fake.Outcomes.Enqueue((1, "ERROR: [youtube] vid: Sign in to confirm you're not a bot\n"));

        var info = await tool.GetInfoAsync("https://www.youtube.com/watch?v=vid", CancellationToken.None);

        Assert.Equal("vid", info!.Id);
        Assert.Equal(2, fake.Runs.Count);
        Assert.Equal(1, fake.UpdateCalls);
    }

    [Fact]
    public async Task Runs_PassNodeWhenDenoIsMissing()
    {
        var fake = new Fake { Version = "2026.08.19" };
        var tool = MakeTool(fake);
        tool.JsRuntimes = (false, true);

        await tool.GetInfoAsync("https://www.youtube.com/watch?v=vid", CancellationToken.None);

        var args = fake.Runs.Single().ToList();
        Assert.Equal("node", args[args.IndexOf("--js-runtimes") + 1]);
    }
}
