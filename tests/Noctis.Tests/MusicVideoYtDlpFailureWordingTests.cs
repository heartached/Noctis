using Avalonia.Headless.XUnit;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services.YouTube;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Music videos from YouTube, 11-26: the readable "couldn't read your browser sign-in" messages
/// matched yt-dlp's exact wording, so a reworded yt-dlp error fell back to a bare failure. With
/// the browser sign-in on, a failure is now a sign-in problem by context (an ERROR line about
/// cookies / keyring / DPAPI / decrypting / a browser profile, or a run that stopped before
/// YouTube was asked anything), shown with yt-dlp's own line; any other unrecognized failure
/// shows yt-dlp's first ERROR line, in the dialog and the Settings note. Offline: yt-dlp is a
/// fake runner.
/// </summary>
public class MusicVideoYtDlpFailureWordingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-mvword-" + Guid.NewGuid().ToString("N")[..8]);

    public MusicVideoYtDlpFailureWordingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private YtDlpTool Tool(string stderr, Func<string?>? cookies = null)
    {
        var tool = new YtDlpTool(new HttpClient(), _dir, () => string.Empty, cookies);
        Directory.CreateDirectory(tool.ToolsDirectory);
        File.WriteAllText(tool.InstalledPath, "fake");
        tool.JsRuntimes = (true, false);
        tool.LatestVersionFetcher = _ => Task.FromResult<string?>("2026.10.21");
        tool.Updater = _ => Task.CompletedTask;
        tool.Runner = (exe, args, onLine, ct) => args is ["--version"]
            ? Task.FromResult((0, "2026.10.21\n", string.Empty))
            : Task.FromResult((1, string.Empty, stderr));
        return tool;
    }

    // ── The known cookie messages stay ──

    [Theory]
    [InlineData("[Cookies] Extracting cookies from chrome\nERROR: Could not copy Chrome cookie database. See  https://github.com/yt-dlp/yt-dlp/issues/7271  for more info", "chrome", YtDlpFailureKind.CookiesUnreadable)]
    [InlineData("Extracting cookies from edge\nERROR: Failed to decrypt with DPAPI. See  https://github.com/yt-dlp/yt-dlp/issues/10927  for more info", "edge", YtDlpFailureKind.CookiesUnreadable)]
    [InlineData("ERROR: could not find firefox cookies database in \"C:\\Users\\me\\AppData\\Roaming\\Mozilla\\Firefox\\Profiles\"", "firefox", YtDlpFailureKind.CookiesNotFound)]
    [InlineData("ERROR: unsupported browser specified for cookies: \"netscape\". Supported browsers are: brave, chrome", "brave", YtDlpFailureKind.CookiesNotFound)]
    public async Task KnownCookieFailures_KeepTheirOwnMessages(string stderr, string browser, YtDlpFailureKind kind)
    {
        Assert.Equal(kind, YtDlpParsing.ClassifyFailure(stderr, cookiesFromBrowser: true));
        var ex = await Assert.ThrowsAsync<YtDlpAccessException>(() => Tool(stderr, () => browser).SearchAsync("q", 8, CancellationToken.None));
        Assert.Equal(kind, ex.Kind);
        Assert.Equal(YtDlpParsing.FailureMessage(kind, browser), ex.Message);
        Assert.Equal(Loc.T(kind == YtDlpFailureKind.CookiesNotFound ? "YtDlp.CookiesNotFound" : "YtDlp.CookiesUnreadable", YtDlpParsing.CookieBrowserName(browser)), ex.Message);
    }

    // ── Wordings yt-dlp might use one day ──

    [Theory]
    [InlineData("ERROR: Failed to load cookies from firefox profile", "Failed to load cookies from firefox profile")]
    [InlineData("[Cookies] Extracting cookies from chrome\nERROR: could not read browser keyring", "could not read browser keyring")]
    [InlineData("ERROR: Unable to decrypt cookie value (DPAPI)", "Unable to decrypt cookie value (DPAPI)")]
    [InlineData("WARNING: something\nERROR: The cookie database schema version 99 is not supported\nERROR: second line", "The cookie database schema version 99 is not supported")]
    [InlineData("ERROR: Browser profile \"default-release\" is in use", "Browser profile \"default-release\" is in use")]
    // Stopped before any YouTube request: no "[youtube]" line at all.
    [InlineData("ERROR: something new went wrong while starting", "something new went wrong while starting")]
    public async Task FutureCookieWordings_GetTheGenericSignInMessage_WithYtDlpsLine(string stderr, string line)
    {
        Assert.Equal(YtDlpFailureKind.CookiesOther, YtDlpParsing.ClassifyFailure(stderr, cookiesFromBrowser: true));
        var ex = await Assert.ThrowsAsync<YtDlpAccessException>(() => Tool(stderr, () => "firefox").ResolveStreamUrlAsync("dQw4w9WgXcQ", 1080, CancellationToken.None));
        Assert.Equal(YtDlpFailureKind.CookiesOther, ex.Kind);
        Assert.Equal(Loc.T("YtDlp.CookiesFailed", "Firefox", line), ex.Message);
    }

    [Fact]
    public async Task GenericSignInMessage_StripsTheUserProfilePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var stderr = $"ERROR: Failed to load cookies from {Path.Combine(home, "AppData", "Roaming", "Mozilla", "Firefox", "Profiles", "ab.default")}\n"
                     + "ERROR: also C:\\Users\\someone\\AppData\\x";
        var ex = await Assert.ThrowsAsync<YtDlpAccessException>(() => Tool(stderr, () => "firefox").SearchAsync("q", 8, CancellationToken.None));
        Assert.DoesNotContain(home, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine("~", "AppData", "Roaming", "Mozilla", "Firefox", "Profiles", "ab.default"), ex.Message);

        Assert.Equal("Failed to load cookies from ~\\AppData\\Local\\x", YtDlpParsing.FirstErrorLine("ERROR: Failed to load cookies from C:\\Users\\someone\\AppData\\Local\\x"));
        Assert.Equal("no file at ~/.mozilla/firefox", YtDlpParsing.FirstErrorLine("ERROR: no file at /home/someone/.mozilla/firefox"));
        Assert.Equal("no file at ~/Library/Cookies", YtDlpParsing.FirstErrorLine("ERROR: no file at /Users/someone/Library/Cookies"));
    }

    [Theory]
    [InlineData("ERROR: [youtube] FGBhQbmPwH8: Sign in to confirm you're not a bot. Use --cookies-from-browser or --cookies for the authentication.", YtDlpFailureKind.BotCheck)]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden", YtDlpFailureKind.Other)] // blocked: the update + retry path
    [InlineData("ERROR: [youtube] abc: Video unavailable", YtDlpFailureKind.Other)]
    [InlineData("ERROR: [youtube:search] Unable to download API page: <urlopen error timed out>", YtDlpFailureKind.Other)]
    public void WithTheSignInOn_YouTubesOwnFailures_AreNotSignInProblems(string stderr, YtDlpFailureKind kind)
        => Assert.Equal(kind, YtDlpParsing.ClassifyFailure(stderr, cookiesFromBrowser: true));

    [Fact]
    public void WithTheSignInOn_ADownloadThatReachedYouTube_IsNotASignInProblem()
    {
        // A download prints its "[youtube]" lines to stdout; its stderr may hold only a later error.
        const string stderr = "ERROR: Postprocessing: Conversion failed!";
        Assert.Equal(YtDlpFailureKind.CookiesOther, YtDlpParsing.ClassifyFailure(stderr, cookiesFromBrowser: true));
        Assert.Equal(YtDlpFailureKind.Other, YtDlpParsing.ClassifyFailure(stderr, cookiesFromBrowser: true,
            stdout: "[youtube] Extracting URL: https://www.youtube.com/watch?v=abc\n[download] 100% of 10.00MiB"));
    }

    // ── The setting off: never a sign-in problem ──

    [Theory]
    [InlineData("ERROR: Could not copy Chrome cookie database.")]
    [InlineData("ERROR: Failed to load cookies from firefox profile")]
    [InlineData("ERROR: could not read browser keyring")]
    [InlineData("ERROR: something new went wrong while starting")]
    public async Task SettingOff_NeverASignInProblem(string stderr)
    {
        Assert.Equal(YtDlpFailureKind.Other, YtDlpParsing.ClassifyFailure(stderr, cookiesFromBrowser: false));
        Assert.Equal(YtDlpFailureKind.Other, YtDlpParsing.ClassifyFailure(stderr));
        var ex = await Assert.ThrowsAsync<YtDlpRunException>(() => Tool(stderr).SearchAsync("q", 8, CancellationToken.None));
        Assert.Equal(YtDlpParsing.FirstErrorLine(stderr), ex.Message);
    }

    // ── Any other failure: yt-dlp's first ERROR line, in the dialog and the Settings note ──

    private const string Unknown = "WARNING: [youtube] abc: some warning\nERROR: [youtube] abc: The page needs a new player version\nERROR: [youtube] abc: a later line";
    private const string UnknownLine = "[youtube] abc: The page needs a new player version";

    [Fact]
    public async Task UnknownFailure_IsYtDlpsFirstErrorLine()
    {
        Assert.Equal(UnknownLine, YtDlpParsing.FirstErrorLine(Unknown));
        var ex = await Assert.ThrowsAsync<YtDlpRunException>(() => Tool(Unknown, () => "firefox").GetInfoAsync("https://youtu.be/dQw4w9WgXcQ", CancellationToken.None));
        Assert.Equal(UnknownLine, ex.Message);
    }

    [AvaloniaFact]
    public async Task UnknownFailure_ShowsItsLine_InTheDialog_AndTheSettingsNote()
    {
        var tool = Tool(Unknown);
        var service = new MusicVideoStreamService(tool, new MusicVideoChoiceStore(Path.Combine(_dir, "choices.json")));
        var track = new Track { Id = Guid.NewGuid(), Title = "Never Gonna Give You Up", Artist = "Rick Astley", Duration = TimeSpan.FromSeconds(213), FilePath = @"C:\m.flac" };
        var picker = new MusicVideoPickerViewModel(track, service);

        await picker.SearchCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("MusicVideo.SearchFailed", UnknownLine), picker.SearchStatus);

        Assert.NotNull(service.LastProblem);
        Assert.Equal(YtDlpFailureKind.Other, service.LastProblem!.Kind);
        Assert.Equal(Loc.T("MusicVideo.LastLookupFailed", UnknownLine), service.LastProblem.Message);
        Assert.False(service.IsCoolingDown); // only a bot check pauses

        var settings = new SettingsViewModel(new TestPersistenceService(), new FakeLibraryService(), new FakeHistoryLog());
        settings.AttachMusicVideoStreams(service);
        Assert.Equal(Loc.T("MusicVideo.LastLookupFailed", UnknownLine), settings.MusicVideoProblemText);
    }

    [Fact]
    public async Task AGoneVideo_IsNotASettingsNote()
    {
        var tool = Tool("ERROR: [youtube] abc: Video unavailable");
        var service = new MusicVideoStreamService(tool, new MusicVideoChoiceStore(Path.Combine(_dir, "choices.json")));
        await Assert.ThrowsAsync<YtDlpRunException>(() => service.LookUpLinkAsync("https://youtu.be/dQw4w9WgXcQ", CancellationToken.None));
        Assert.Null(service.LastProblem);
    }
}
