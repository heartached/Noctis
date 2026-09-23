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
        Assert.Equal("No lyrics", vm.StatusText);
    }

    [Fact]
    public void FontScale_ScalesLineAndLayerSizes()
    {
        var (vm, _, _, _, _) = Make();
        vm.FontScale = 1.3;

        Assert.Equal(LyricsPageViewModel.BaseFontSize * 1.3, vm.LineFontSize, 6);
        Assert.Equal(vm.LineFontSize * 0.6, vm.LayerFontSize, 6);
    }
}
