using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>Phone lyrics: off-thread load per track, audible-position sync, remembered layer toggles.</summary>
public class MobileLyricsViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // Issue #61's example line (1.0–4.0 s, five words) plus a second line at 5.0 s.
    internal const string IssueTtml = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" xml:lang="ja">
          <body><div>
            <p begin="1.000" end="4.000"><span begin="1.000" end="1.600">例えば</span><span begin="1.600" end="2.200">俺が</span><span begin="2.200" end="2.800">俺じゃ</span><span begin="2.800" end="3.300">ない</span><span begin="3.300" end="4.000">として</span><span ttm:role="x-translation" xml:lang="en">For example, if I were not myself</span><span ttm:role="x-roman" xml:lang="ja-Latn">tatoeba ore ga ore ja nai to shite</span></p>
            <p begin="5.000" end="6.000"><span begin="5.000" end="6.000">次</span></p>
          </div></body>
        </tt>
        """;

    private static Track NewTrack(string name, string lyrics = "") =>
        new() { Id = Guid.NewGuid(), Title = name, FilePath = $"content://x/{name}", Duration = TimeSpan.FromSeconds(60), Lyrics = lyrics };

    private (LyricsPageViewModel Vm, NowPlayingViewModel Np, FakeAudioPlayer Player, FakeTrackFiles Files, PersistenceService Persistence)
        Make(Func<Func<LoadedLyrics>, Task<LoadedLyrics>>? runBackground = null)
    {
        var player = new FakeAudioPlayer();
        var persistence = new PersistenceService(_root);
        var np = new NowPlayingViewModel(player, new FakeLibraryService(), persistence, marshal: a => a());
        var files = new FakeTrackFiles();
        var vm = new LyricsPageViewModel(player, np, files, persistence,
            runBackground ?? (work => Task.FromResult(work())));
        return (vm, np, player, files, persistence);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(15);
        Assert.True(condition());
    }

    [Fact]
    public async Task IssueExample_TtmlSidecar_SweepsTheRightWord_WithTranslationUnderneath()
    {
        var (vm, np, player, files, _) = Make();
        files.Sidecars[".ttml"] = IssueTtml;

        np.PlayTracks(new[] { NewTrack("a") }, 0);
        await WaitForAsync(() => vm.HasLyrics);
        player.RaisePositionChanged(TimeSpan.FromSeconds(2.5));
        vm.OnFrame(1000);

        Assert.True(vm.IsSynced);
        Assert.True(vm.HasTranslations);
        Assert.True(vm.HasRomanizations);
        Assert.Equal(0, vm.ActiveLineIndex);
        var line = vm.Lines[0];
        Assert.Equal("For example, if I were not myself", line.Translation);
        Assert.Equal(2, line.CurrentWordIndex);               // 俺じゃ, 2.2–2.8 s
        Assert.True(line.IsActive);
    }

    [Fact]
    public async Task OutputLatency_IsSubtracted_LikeTheDesktop()
    {
        var (vm, np, player, files, _) = Make();
        files.Sidecars[".ttml"] = IssueTtml;
        player.OutputLatency = TimeSpan.FromMilliseconds(600);

        np.PlayTracks(new[] { NewTrack("a") }, 0);
        await WaitForAsync(() => vm.HasLyrics);
        player.RaisePositionChanged(TimeSpan.FromSeconds(2.5));
        vm.OnFrame(1000);

        Assert.Equal(1, vm.Lines[0].CurrentWordIndex);        // audible 1.9 s → 俺が, 1.6–2.2 s
    }

    [Fact]
    public async Task StaleLoad_NeverOverwritesTheNewerTrack()
    {
        var pending = new List<(Func<LoadedLyrics> Work, TaskCompletionSource<LoadedLyrics> Done)>();
        var (vm, np, _, files, _) = Make(work =>
        {
            var done = new TaskCompletionSource<LoadedLyrics>();
            pending.Add((work, done));
            return done.Task;
        });

        files.Sidecars[".lrc"] = "[00:01.00]from A";
        np.PlayTracks(new[] { NewTrack("a"), NewTrack("b") }, 0);
        var loadA = pending[^1];
        files.Sidecars[".lrc"] = "[00:01.00]from B";
        np.NextCommand.Execute(null);
        var loadB = pending[^1];

        loadB.Done.SetResult(loadB.Work());
        await WaitForAsync(() => vm.HasLyrics);
        loadA.Done.SetResult(LyricsLoader.Load(NewTrack("a"), new FakeTrackFiles { Sidecars = { [".lrc"] = "[00:01.00]from A" } }, false));
        await Task.Delay(100);

        Assert.Equal("from B", Assert.Single(vm.Lines).Text);
    }

    [Fact]
    public async Task PausedSeekBackwards_MovesTheActiveLine()
    {
        var (vm, np, player, files, _) = Make();
        files.Sidecars[".ttml"] = IssueTtml;

        np.PlayTracks(new[] { NewTrack("a") }, 0);
        await WaitForAsync(() => vm.HasLyrics);
        player.RaisePositionChanged(TimeSpan.FromSeconds(5.5));
        vm.OnFrame(1000);
        Assert.Equal(1, vm.ActiveLineIndex);

        np.TogglePlayPauseCommand.Execute(null);              // pause
        player.RaisePositionChanged(TimeSpan.FromSeconds(1.5)); // seek while paused

        Assert.Equal(0, vm.ActiveLineIndex);
    }

    /// <summary>Two voices overlap 3–4 s: both lines are lit at full opacity with their own
    /// word progress, and the ramp runs from the nearer of the two.</summary>
    [Fact]
    public async Task OverlappingDuetLines_AreBothLitAtFullOpacity()
    {
        var (vm, np, player, files, _) = Make();
        files.Sidecars[".ttml"] = """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
              <body><div>
                <p begin="1.000" end="4.000" ttm:agent="v1"><span begin="1.000" end="2.000">one</span> <span begin="2.000" end="4.000">two</span></p>
                <p begin="3.000" end="5.000" ttm:agent="v2"><span begin="3.000" end="5.000">three</span></p>
                <p begin="8.000" end="9.000" ttm:agent="v1"><span begin="8.000" end="9.000">four</span></p>
                <p begin="10.000" end="11.000" ttm:agent="v1"><span begin="10.000" end="11.000">five</span></p>
              </div></body>
            </tt>
            """;

        np.PlayTracks(new[] { NewTrack("a") }, 0);
        await WaitForAsync(() => vm.HasLyrics);
        player.RaisePositionChanged(TimeSpan.FromSeconds(3.5));
        vm.OnFrame(1000);

        Assert.Equal(1, vm.ActiveLineIndex);                  // the newest line anchors the scroll
        Assert.True(vm.Lines[0].IsActive);
        Assert.True(vm.Lines[1].IsActive);
        Assert.Equal(1, vm.Lines[0].CurrentWordIndex);        // "two", 2–4 s, still sweeping
        Assert.Equal(0, vm.Lines[1].CurrentWordIndex);
        Assert.Equal(1.0, vm.Lines[0].LineOpacity, 6);
        Assert.Equal(1.0, vm.Lines[1].LineOpacity, 6);
        Assert.Equal(0.55, vm.Lines[2].LineOpacity, 6);
        Assert.Equal(0.32, vm.Lines[3].LineOpacity, 6);
    }

    /// <summary>Nothing is sung before the first line, so nothing is dimmed (desktop
    /// UpdateLineOpacities(-1)); the ramp only starts once a line is active.</summary>
    [Fact]
    public async Task BeforeTheFirstLine_EveryLineIsAtFullOpacity()
    {
        var (vm, np, player, files, _) = Make();
        files.Sidecars[".lrc"] = "[00:01.00]one\n[00:03.00]two\n[00:05.00]three";

        np.PlayTracks(new[] { NewTrack("a") }, 0);
        await WaitForAsync(() => vm.HasLyrics);
        player.RaisePositionChanged(TimeSpan.Zero);
        vm.OnFrame(1000);

        Assert.Equal(-1, vm.ActiveLineIndex);
        Assert.All(vm.Lines, l => Assert.Equal(1.0, l.LineOpacity, 6));

        player.RaisePositionChanged(TimeSpan.FromSeconds(1.5));
        vm.OnFrame(2000);
        Assert.Equal(0, vm.ActiveLineIndex);
        Assert.Equal(0.55, vm.Lines[1].LineOpacity, 6);
    }

    [Fact]
    public async Task LayerToggles_AreRemembered()
    {
        var (vm, _, _, _, persistence) = Make();
        await vm.InitializeAsync();
        vm.ShowTranslation = false;
        await vm.PendingSave;       // never read settings.json while a save may be mid-write
        Assert.False((await persistence.LoadSettingsAsync()).LyricsShowTranslations);

        var (again, _, _, _, _) = Make();
        await again.InitializeAsync();

        Assert.False(again.ShowTranslation);
        Assert.True(again.ShowRomanization);
    }

    [Fact]
    public async Task PlainLyrics_StayStatic()
    {
        var (vm, np, player, _, _) = Make();

        np.PlayTracks(new[] { NewTrack("a", lyrics: "one\ntwo") }, 0);
        await WaitForAsync(() => vm.HasLyrics);
        player.RaisePositionChanged(TimeSpan.FromSeconds(3));
        vm.OnFrame(1000);

        Assert.False(vm.IsSynced);
        Assert.False(vm.WantsFrames);
        Assert.Equal(-1, vm.ActiveLineIndex);
        Assert.All(vm.Lines, l => Assert.True(l.IsActive));
    }

    [Fact]
    public async Task NoLyrics_SaysSo()
    {
        var (vm, np, _, _, _) = Make();

        np.PlayTracks(new[] { NewTrack("a") }, 0);
        await WaitForAsync(() => !vm.IsLoading);

        Assert.False(vm.HasLyrics);
        Assert.Equal("No Lyrics", vm.StatusText);
        Assert.Equal("This song has no lyrics file or lyrics in its tags.", vm.StatusDetail);
        Assert.True(vm.ShowsEmptyState);
    }

    [Fact]
    public void FontScale_ScalesLineAndLayerSizes()
    {
        var (vm, _, _, _, _) = Make();
        vm.FontScale = 1.3;

        Assert.Equal(LyricsPageViewModel.BaseFontSize * 1.3, vm.LineFontSize, 6);
        Assert.Equal(vm.LineFontSize * 0.6, vm.LayerFontSize, 6);
    }

    /// <summary>Owner 11-14 (S23, 360 dp): the fixed 34 made three-word lines. Text now follows
    /// the column width: Apple Music's ~30 on a 393 pt iPhone, smaller on a narrower phone,
    /// capped at the old size on a wide one; the Settings size and system scale still multiply.</summary>
    [Theory]
    [InlineData(360, 27.36)]
    [InlineData(393, 29.868)]
    [InlineData(412, 31.312)]
    [InlineData(800, 34)]
    [InlineData(200, 22)]
    public void LyricText_FollowsTheColumnWidth(double width, double expected)
    {
        var (vm, _, _, _, _) = Make();
        vm.ViewWidth = width;
        Assert.Equal(expected, vm.AutoBaseFontSize, 3);
        vm.TextScale = 1.2;
        vm.FontScale = 1.1;
        Assert.Equal(expected * 1.2 * 1.1, vm.LineFontSize, 3);
    }

    /// <summary>Beta 6 (#mobile-beta, online lookup off): after a song change the page went blank,
    /// showed "No Lyrics" for a moment, then the lyrics. The empty state never shows while the new
    /// song is still being looked up, not even between two of the load's own writes, from the
    /// switch to the lyrics; and a song with none says so only once its lookup is done.</summary>
    [Fact]
    public async Task ASongChange_NeverShowsTheEmptyState_BeforeTheLookupEnds()
    {
        var pending = new List<(Func<LoadedLyrics> Work, TaskCompletionSource<LoadedLyrics> Done)>();
        var (vm, np, _, files, _) = Make(work =>
        {
            var done = new TaskCompletionSource<LoadedLyrics>();
            pending.Add((work, done));
            return done.Task;
        });
        np.PlayTracks(new[] { NewTrack("none"), NewTrack("with"), NewTrack("none2") }, 0);
        pending[^1].Done.SetResult(pending[^1].Work());
        await WaitForAsync(() => vm.ShowsEmptyState);          // the first song has none

        var shown = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LyricsPageViewModel.ShowsEmptyState) or nameof(LyricsPageViewModel.HasLyrics)
                or nameof(LyricsPageViewModel.IsLoading))
                shown.Add(vm.ShowsEmptyState);
        };
        files.Sidecars[".lrc"] = "[00:01.00]found";
        np.NextCommand.Execute(null);
        Assert.False(vm.ShowsEmptyState);
        Assert.True(vm.IsLoading);
        pending[^1].Done.SetResult(pending[^1].Work());
        await WaitForAsync(() => vm.HasLyrics);
        Assert.DoesNotContain(true, shown);

        // To a song with none: blank while it is looked up, "No Lyrics" once that is done.
        shown.Clear();
        files.Sidecars.Clear();
        np.NextCommand.Execute(null);
        Assert.False(vm.ShowsEmptyState);
        Assert.DoesNotContain(true, shown);
        pending[^1].Done.SetResult(pending[^1].Work());
        await WaitForAsync(() => vm.ShowsEmptyState);
        Assert.False(vm.IsLoading);
    }

    /// <summary>The page remembers what each song's lookup found (no sidecar, the tags' text), so
    /// playing it again does no file work; a finished rescan forgets it all, folder listings too.</summary>
    [Fact]
    public async Task ReplayingASong_SkipsTheLookup_UntilARescanFinishes()
    {
        var player = new FakeAudioPlayer();
        var persistence = new PersistenceService(_root);
        var library = new FakeLibraryService();
        var np = new NowPlayingViewModel(player, library, persistence, marshal: a => a());
        var files = new FakeTrackFiles { Audio = new byte[] { 1, 2, 3 } };
        var vm = new LyricsPageViewModel(player, np, files, persistence, work => Task.FromResult(work()));
        var shell = new ShellViewModel(new LibraryViewModel(library, persistence, new NoFolders(), marshal: a => a()), np, vm);
        var song = NewTrack("again", lyrics: "plain words");

        np.PlayTracks(new[] { song, NewTrack("other") }, 0);
        await WaitForAsync(() => vm.HasLyrics);
        np.NextCommand.Execute(null);
        np.PreviousCommand.Execute(null);
        await WaitForAsync(() => vm.HasLyrics);
        Assert.Equal("plain words", Assert.Single(vm.Lines).Text);
        Assert.Equal(2, files.SidecarReads);                    // once per song, not again for the replay
        Assert.Equal(2, files.AudioOpens);

        shell.Library.IsScanning = true;
        shell.Library.IsScanning = false;                       // a rescan finished
        Assert.Equal(0, vm.Lookups.Count);
        Assert.Equal(1, files.FolderForgets);
        np.NextCommand.Execute(null);
        await WaitForAsync(() => !vm.HasLyrics && !vm.IsLoading);
        Assert.Equal(3, files.SidecarReads);
    }

    private sealed class NoFolders : Noctis.Mobile.Services.IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }
}
