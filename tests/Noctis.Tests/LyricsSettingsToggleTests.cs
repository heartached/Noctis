using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The three lyrics toggles on the Appearance tab (Flowing Lyrics Background,
/// Fullscreen Lyrics Focus, Join Split Words) each flip a PlayerViewModel flag that
/// the lyrics page must answer LIVE — no navigation, no restart. Until now only their
/// fresh-install defaults were pinned; these pin the behaviour behind each switch.
/// </summary>
public class LyricsSettingsToggleTests
{
    // ── Harness (mirrors LyricsBackwardSeekTests) ──

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private static (LyricsViewModel Vm, PlayerViewModel Player) MakeViewModel()
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());
        return (vm, player);
    }

    private const int LineCount = 31;

    private static string MakeLrc() => string.Join("\n",
        Enumerable.Range(0, LineCount).Select(i =>
        {
            var t = TimeSpan.FromSeconds(i * 3);
            return $"[{t.Minutes:00}:{t.Seconds:00}.00]Line {i}";
        }));

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(condition(), $"timed out waiting for: {what}");
    }

    private static async Task<(LyricsViewModel Vm, PlayerViewModel Player)> MountSyncedLyrics(Track track)
    {
        var (vm, player) = MakeViewModel();
        player.Duration = TimeSpan.FromSeconds(LineCount * 3);
        player.CurrentTrack = track;
        vm.SetLyricsSurfaceVisible(true);
        vm.EnsureLyricsForCurrentTrack();
        await WaitUntil(() => vm.IsSynced, "synced lyrics to load");
        return (vm, player);
    }

    // ── Fullscreen Lyrics Focus ──

    [AvaloniaFact]
    public async Task FullscreenFocus_FlippedWhileFullscreen_RedimsInPlace()
    {
        var (vm, player) = await MountSyncedLyrics(new Track
        {
            Title = "Focus", Artist = "Test",
            FilePath = Path.Combine(Path.GetTempPath(), "noctis-focus-no-such-file.mp3"),
            SyncedLyrics = MakeLrc(),
        });

        player.Position = TimeSpan.FromSeconds(30);   // line 10 active
        var active = vm.ActiveLineIndex;
        Assert.Equal(10, active);

        vm.IsFullScreenPageActive = true;
        // Default page ramp: three lines away is still faintly visible.
        Assert.Equal(0.18, vm.LyricLines[active + 3].LineOpacity, 3);

        player.LyricsFullScreenFocusEnabled = true;
        Assert.True(vm.IsLyricsFocusActive);
        Assert.Equal(1.0, vm.LyricLines[active].LineOpacity, 3);
        Assert.Equal(0.5, vm.LyricLines[active + 1].LineOpacity, 3);
        Assert.Equal(0.22, vm.LyricLines[active + 2].LineOpacity, 3);
        Assert.Equal(0.0, vm.LyricLines[active + 3].LineOpacity, 3);

        player.LyricsFullScreenFocusEnabled = false;
        Assert.False(vm.IsLyricsFocusActive);
        Assert.Equal(0.18, vm.LyricLines[active + 3].LineOpacity, 3);
    }

    [AvaloniaFact]
    public async Task FullscreenFocus_DoesNothingOutsideFullscreen()
    {
        var (vm, player) = await MountSyncedLyrics(new Track
        {
            Title = "Focus windowed", Artist = "Test",
            FilePath = Path.Combine(Path.GetTempPath(), "noctis-focus-windowed-no-such-file.mp3"),
            SyncedLyrics = MakeLrc(),
        });
        player.Position = TimeSpan.FromSeconds(30);
        var active = vm.ActiveLineIndex;

        player.LyricsFullScreenFocusEnabled = true;
        Assert.False(vm.IsLyricsFocusActive);
        Assert.Equal(0.18, vm.LyricLines[active + 3].LineOpacity, 3);
    }

    // ── Minimum Line Opacity (Discord, Mistery 09-20: "jump to a part of the song
    //    from its lyrics is basically impossible" — lines past ±9 sat at opacity 0 and
    //    IsClickable=false, so most of the song could not be clicked) ──

    [AvaloniaFact]
    public async Task MinLineOpacity_KeepsEveryLineVisibleAndClickable()
    {
        var (vm, player) = await MountSyncedLyrics(new Track
        {
            Title = "Floor", Artist = "Test",
            FilePath = Path.Combine(Path.GetTempPath(), "noctis-floor-no-such-file.mp3"),
            SyncedLyrics = MakeLrc(),
        });

        player.Position = TimeSpan.FromSeconds(30);   // line 10 active
        var active = vm.ActiveLineIndex;
        Assert.Equal(10, active);

        // Default: far lines are gone and can't be clicked — the reported problem.
        Assert.Equal(0.0, vm.LyricLines[active + 12].LineOpacity, 3);
        Assert.False(vm.LyricLines[active + 12].IsClickable);
        Assert.False(vm.LyricLines[0].IsClickable);

        player.LyricsMinLineOpacity = 25;

        // Every non-active line is at least the floor and takes clicks again …
        for (var i = 0; i < vm.LyricLines.Count; i++)
        {
            if (i == active) continue;
            Assert.True(vm.LyricLines[i].LineOpacity >= 0.25 - 1e-9, $"line {i} = {vm.LyricLines[i].LineOpacity}");
            Assert.True(vm.LyricLines[i].IsClickable, $"line {i} should be clickable");
        }
        // … the near ramp above the floor is untouched, and the active line stays crisp.
        Assert.Equal(1.0, vm.LyricLines[active].LineOpacity, 3);
        Assert.Equal(0.55, vm.LyricLines[active + 1].LineOpacity, 3);
        Assert.Equal(0.32, vm.LyricLines[active + 2].LineOpacity, 3);
        Assert.Equal(0.25, vm.LyricLines[active + 3].LineOpacity, 3);   // 0.18 floored
        Assert.Equal(0.25, vm.LyricLines[active + 12].LineOpacity, 3);

        // Fullscreen focus honours the floor too: it is a minimum, not a mode.
        vm.IsFullScreenPageActive = true;
        player.LyricsFullScreenFocusEnabled = true;
        Assert.Equal(0.5, vm.LyricLines[active + 1].LineOpacity, 3);
        Assert.Equal(0.25, vm.LyricLines[active + 3].LineOpacity, 3);
        Assert.True(vm.LyricLines[active + 3].IsClickable);
        player.LyricsFullScreenFocusEnabled = false;
        vm.IsFullScreenPageActive = false;

        // Back to 0: the original ramp returns, far lines hidden and unclickable again.
        player.LyricsMinLineOpacity = 0;
        Assert.Equal(0.18, vm.LyricLines[active + 3].LineOpacity, 3);
        Assert.Equal(0.0, vm.LyricLines[active + 12].LineOpacity, 3);
        Assert.False(vm.LyricLines[active + 12].IsClickable);
    }

    // ── Flowing Lyrics Background ──

    [AvaloniaFact]
    public async Task FlowingLight_FlippedWhileThePageIsUp_StartsAndStopsTheDrift()
    {
        var (vm, player) = MakeViewModel();
        Assert.True(vm.IsColorModeArtwork, "harness: artwork background mode expected by default");

        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 800, Content = view };
        try
        {
            win.Show();
            await Pump(100);

            var layer = view.FindControl<Panel>("FlowLayerHost");
            Assert.NotNull(layer);
            Assert.False(layer!.IsVisible);
            Assert.False(Flow(view).Enabled);

            player.LyricsFlowingLightEnabled = true;
            await Pump(100);
            Assert.True(layer.IsVisible, "flowing-artwork layer should show as soon as the toggle is on");
            Assert.True(Flow(view).Enabled, "flow animator should be enabled");
            Assert.True(Flow(view).IsRunning, "flow animator should be on the frame clock");

            player.LyricsFlowingLightEnabled = false;
            await Pump(100);
            Assert.False(layer.IsVisible);
            Assert.False(Flow(view).Enabled);
            Assert.False(Flow(view).IsRunning);
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>"Drift (no beat)" (Discord, aaron / Mistery 09-21): the same Drift layers,
    /// with the animator's beat target pinned at zero; plain Drift keeps reacting.</summary>
    [AvaloniaFact]
    public async Task FlowingStyle_DriftCalm_KeepsTheDriftLayers_ButParksTheBeat()
    {
        var (vm, player) = MakeViewModel();
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 800, Content = view };
        try
        {
            win.Show();
            await Pump(50);

            player.LyricsFlowingStyle = FlowingStyles.DriftCalm;
            player.LyricsFlowingLightEnabled = true;
            await Pump(100);

            var layer = view.FindControl<Panel>("FlowLayerHost");
            var kawarp = view.FindControl<Noctis.Controls.KawarpBackground>("KawarpLayer");
            Assert.True(layer!.IsVisible, "Drift (no beat) still draws the Drift layers");
            Assert.False(kawarp!.IsVisible);
            Assert.True(Flow(view).Enabled);
            Assert.False(Flow(view).BeatReactive, "the beat target must be parked");

            player.LyricsFlowingStyle = FlowingStyles.Drift;
            await Pump(100);
            Assert.True(Flow(view).BeatReactive);
        }
        finally
        {
            win.Close();
        }
    }

    private static Noctis.Helpers.FlowingArtworkAnimator Flow(LyricsView view) =>
        (Noctis.Helpers.FlowingArtworkAnimator)typeof(LyricsView)
            .GetField("_flow", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(view)!;

    private static async Task Pump(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(8);
        }
    }

    // ── Join Split Words ──

    private const string SplitWordTtml = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
          <body>
            <div>
              <p begin="00:01.000" end="00:04.000">
                <span begin="00:01.000" end="00:01.400">Is </span>
                <span begin="00:01.400" end="00:01.800">that </span>
                <span begin="00:01.800" end="00:02.100">a </span>
                <span begin="00:02.100" end="00:02.400">com</span>
                <span begin="00:02.400" end="00:02.700">pro</span>
                <span begin="00:02.700" end="00:03.400">mise?</span>
              </p>
              <p begin="00:05.000" end="00:07.000">
                <span begin="00:05.000" end="00:05.500">Second </span>
                <span begin="00:05.500" end="00:06.000">line</span>
              </p>
            </div>
          </body>
        </tt>
        """;

    [AvaloniaFact]
    public async Task JoinSplitWords_FlippedOn_ReparsesTheOpenTrack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "noctis-join-split-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var trackPath = Path.Combine(dir, "song.mp3");
            await File.WriteAllTextAsync(Path.Combine(dir, "song.ttml"), SplitWordTtml);

            var (vm, player) = MakeViewModel();
            Assert.False(player.LyricsJoinSplitWords, "harness: toggle should start off");
            player.Duration = TimeSpan.FromSeconds(10);
            player.CurrentTrack = new Track { Title = "Join", Artist = "Test", FilePath = trackPath };
            vm.SetLyricsSurfaceVisible(true);
            vm.EnsureLyricsForCurrentTrack();
            await WaitUntil(() => vm.IsSynced, "TTML sidecar to load");

            var line = vm.LyricLines.First(l => l.Text.StartsWith("Is that", StringComparison.Ordinal));
            Assert.Equal(6, line.Words!.Count);
            Assert.Equal("Is that a com pro mise?", line.Text);

            player.LyricsJoinSplitWords = true;
            await WaitUntil(
                () => vm.LyricLines.Any(l => l.Text == "Is that a compromise?"),
                "the track to be re-parsed with joined words");
            var joined = vm.LyricLines.First(l => l.Text == "Is that a compromise?");
            Assert.Equal(4, joined.Words!.Count);
            // The joined word still sweeps on its three syllables' own clocks.
            Assert.Equal(3, joined.Words[3].Syllables!.Count);

            player.LyricsJoinSplitWords = false;
            await WaitUntil(
                () => vm.LyricLines.Any(l => l.Text == "Is that a com pro mise?"),
                "the track to be re-parsed with authored spacing");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── Translation / Romanization / Background Vocals (GitHub #78) ──

    private const string LayeredTtml = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
          <head><metadata><iTunesMetadata xmlns="http://music.apple.com/lyric-ttml-internal">
            <translations><translation xml:lang="en"><text for="L1">The heavy rain</text><text for="L2">The end</text></translation></translations>
            <transliterations><transliteration xml:lang="ja-Latn">
              <text for="L1"><span begin="00:01.000" end="00:01.400">fu</span><span begin="00:01.400" end="00:02.000">ri</span></text>
              <text for="L2">owari</text>
            </transliteration></transliterations>
          </iTunesMetadata></metadata></head>
          <body><div>
            <p begin="00:01.000" end="00:03.000" itunes:key="L1"><span begin="00:01.000" end="00:01.400">ふ</span><span begin="00:01.400" end="00:02.000">り</span><span ttm:role="x-bg"><span begin="00:02.000" end="00:02.800">(ah)</span></span></p>
            <p begin="00:04.000" end="00:06.000" itunes:key="L2"><span begin="00:04.000" end="00:05.000">終わり</span></p>
          </div></body>
        </tt>
        """;

    [AvaloniaFact]
    public async Task LyricLayerToggles_HideTheRowsInPlace_WithoutReloading()
    {
        var dir = Path.Combine(Path.GetTempPath(), "noctis-layers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var win = new Window { Width = 1400, Height = 800 };
        try
        {
            var trackPath = Path.Combine(dir, "song.mp3");
            await File.WriteAllTextAsync(Path.Combine(dir, "song.ttml"), LayeredTtml);

            var (vm, player) = MakeViewModel();
            player.Duration = TimeSpan.FromSeconds(10);
            player.CurrentTrack = new Track { Title = "Layers", Artist = "Test", FilePath = trackPath };
            vm.SetLyricsSurfaceVisible(true);
            vm.EnsureLyricsForCurrentTrack();
            await WaitUntil(() => vm.IsSynced, "TTML sidecar to load");

            var timed = vm.LyricLines.Single(l => l.Text == "ふり");
            var untimed = vm.LyricLines.Single(l => l.Text == "終わり");
            var swaps = 0;
            vm.LyricsSwapped += (_, _) => swaps++;

            var page = new LyricsView { DataContext = vm };
            var panel = new LyricsPanelView { DataContext = vm };
            win.Content = new StackPanel { Children = { page, panel } };
            win.Show();
            await Pump(50);

            foreach (var view in new UserControl[] { page, panel })
            {
                var t = LineButton(view, timed);
                var u = LineButton(view, untimed);

                // Order under the line: original → background vocals → romanization → translation.
                var main = t.GetVisualDescendants().OfType<ItemsControl>().First(c => c.Classes.Contains("word-layer"));
                var bg = Row<ItemsControl>(t, "bg-vocals");
                var romaji = Row<ItemsControl>(t, "romanization");
                var translation = Row<TextBlock>(t, "translation-text");
                Assert.True(bg.IsVisible && romaji.IsVisible && translation.IsVisible);
                Assert.False(Row<TextBlock>(t, "romanization-text").IsVisible);   // timed → karaoke row instead
                Assert.True(Top(bg, t) >= Top(main, t) + main.Bounds.Height - 0.5);
                Assert.True(Top(romaji, t) >= Top(bg, t) + bg.Bounds.Height - 0.5);
                Assert.True(Top(translation, t) >= Top(romaji, t) + romaji.Bounds.Height - 0.5);
                Assert.Equal("The heavy rain", translation.Text);

                Assert.True(Row<TextBlock>(u, "romanization-text").IsVisible);     // untimed → static text
                Assert.False(Row<ItemsControl>(u, "romanization").IsVisible);
                Assert.Equal("owari", Row<TextBlock>(u, "romanization-text").Text);
            }

            player.LyricsShowTranslations = false;
            player.LyricsShowRomanization = false;
            player.LyricsShowBackgroundVocals = false;
            await Pump(50);

            foreach (var view in new UserControl[] { page, panel })
            {
                var t = LineButton(view, timed);
                Assert.False(Row<ItemsControl>(t, "bg-vocals").IsVisible);
                Assert.False(Row<ItemsControl>(t, "romanization").IsVisible);
                Assert.False(Row<TextBlock>(t, "translation-text").IsVisible);
                Assert.False(Row<TextBlock>(LineButton(view, untimed), "romanization-text").IsVisible);
            }

            player.LyricsShowRomanization = true;
            await Pump(50);
            Assert.True(Row<ItemsControl>(LineButton(page, timed), "romanization").IsVisible);
            Assert.False(Row<TextBlock>(LineButton(page, timed), "translation-text").IsVisible);

            // Display-only: the same line objects stay loaded, no swap ran.
            Assert.Same(timed, vm.LyricLines.Single(l => l.Text == "ふり"));
            Assert.Equal(0, swaps);
        }
        finally
        {
            win.Close();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static Button LineButton(Control view, LyricLine line) =>
        view.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.DataContext, line));

    private static T Row<T>(Button line, string cls) where T : Control =>
        line.GetVisualDescendants().OfType<T>().Single(c => c.Classes.Contains(cls));

    private static double Top(Visual v, Visual relativeTo) => v.TranslatePoint(default, relativeTo)!.Value.Y;
}
