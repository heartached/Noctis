using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 2026-09-21 Discord / GitHub batch. Source scans pin the XAML facts (the visualizer's
/// paint order, the Folders page's drag start); the rest exercise the helpers behind
/// the lyrics header credits, the flowing-style constants, the drop-to-queue expansion,
/// the derived theme's window background and the mid-scan queue guard (GitHub #72).
/// </summary>
public class DiscordBatch20260921Tests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Noctis.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static string ReadView(string name) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Noctis", "Views", name));

    // ── Visualizer hidden under Kawarp (Discord, Mistery) ──

    [Fact]
    public void LyricsPage_DeclaresTheVisualizer_AfterKawarpAndThePluginHost()
    {
        // Equal ZIndex siblings paint in declaration order: Kawarp fills its whole bounds
        // opaquely, so the spectrum must come later or it is painted over entirely.
        var xaml = ReadView("LyricsView.axaml");
        var spectrum = xaml.IndexOf("x:Name=\"LyricsSpectrum\"", StringComparison.Ordinal);
        var kawarp = xaml.IndexOf("x:Name=\"KawarpLayer\"", StringComparison.Ordinal);
        var plugins = xaml.IndexOf("x:Name=\"PluginLayerHost\"", StringComparison.Ordinal);
        Assert.True(spectrum > 0 && kawarp > 0 && plugins > 0, "all three background surfaces must exist");
        Assert.True(spectrum > kawarp, "LyricsSpectrum must be declared after KawarpLayer");
        Assert.True(spectrum > plugins, "LyricsSpectrum must be declared after PluginLayerHost");
    }

    // ── Island shuffle glyph stays white while on (Discord, Mistery) ──

    [Fact]
    public void IslandShuffle_GlyphTurnsAccent_WhenOn_LikeRepeat()
    {
        // Repeat swaps its glyph to AccentColorBrush while on; Shuffle only tinted the
        // pill and kept the white arrows. Both glyph variants must exist, gated on
        // IsShuffleEnabled, so the arrows follow the accent the way the mini player's do.
        var xaml = ReadView("PlaybackBarView.axaml");
        var button = xaml.IndexOf("Command=\"{Binding ToggleShuffleCommand}\"", StringComparison.Ordinal);
        Assert.True(button > 0, "island shuffle button must exist");
        var section = xaml.Substring(button, 2000);
        Assert.Contains("Fill=\"{DynamicResource AccentColorBrush}\"", section);
        Assert.Contains("IsVisible=\"{Binding IsShuffleEnabled}\"", section);
        Assert.Contains("IsVisible=\"{Binding !IsShuffleEnabled}\"", section);
    }

    // ── Drag & drop from the Folders page (Discord, Luwi) ──

    [Fact]
    public void FoldersPage_TrackRows_StartTheSharedFileDrag()
    {
        var xaml = ReadView("LibraryFoldersView.axaml");
        Assert.Contains("xmlns:helpers=\"using:Noctis.Helpers\"", xaml);
        Assert.Contains("helpers:DragFileBehavior.EnableFileDrag=\"True\"", xaml);
    }

    // ── Drift (no beat) (Discord, aaron / Mistery) ──

    [Theory]
    [InlineData("Drift", true, true)]
    [InlineData("DriftCalm", false, true)]
    [InlineData("Kawarp", true, false)]
    [InlineData("KawarpCalm", false, false)]
    [InlineData("SomePlugin", true, false)]
    public void FlowingStyles_ClassifyBeatAndDrift(string style, bool beat, bool drift)
    {
        Assert.Equal(beat, FlowingStyles.IsBeatReactive(style));
        Assert.Equal(drift, FlowingStyles.IsDrift(style));
    }

    // ── Lyrics header credits (Discord, aaron) ──

    [Fact]
    public void BuildArtistTokens_SplitsACommaCredit_MarkingTheLast()
    {
        var tokens = LyricsViewModel.BuildArtistTokens("Kanye West, GLC, Consequence");
        Assert.Equal(new[] { "Kanye West", "GLC", "Consequence" }, tokens.Select(t => t.Name));
        Assert.Equal(new[] { false, false, true }, tokens.Select(t => t.IsLast));
    }

    [Fact]
    public void BuildArtistTokens_SingleArtist_IsOneToken_EmptyIsNone()
    {
        var one = LyricsViewModel.BuildArtistTokens("Kendrick Lamar");
        Assert.Single(one);
        Assert.True(one[0].IsLast);
        Assert.Empty(LyricsViewModel.BuildArtistTokens(""));
        Assert.Empty(LyricsViewModel.BuildArtistTokens(null));
    }

    [Fact]
    public void ResolveAlbumTrackCount_PrefersTheAlbumsGroupedCount_OverThePerDiscTag()
    {
        var track = new Track { Title = "United In Grief", TrackCount = 9, DiscNumber = 1, DiscCount = 3 };
        Assert.Equal(19, LyricsViewModel.ResolveAlbumTrackCount(track, new Album { TrackCount = 19 }));
        Assert.Equal(9, LyricsViewModel.ResolveAlbumTrackCount(track, null));
        Assert.Equal(9, LyricsViewModel.ResolveAlbumTrackCount(track, new Album { TrackCount = 0 }));
    }

    // ── GitHub #71: drops without import ──

    [Fact]
    public void ExpandDroppedAudioFiles_WalksFoldersInPathOrder_AndSkipsNonAudio()
    {
        var root = Path.Combine(Path.GetTempPath(), "noctis-drop-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "album", "disc 2"));
            File.WriteAllText(Path.Combine(root, "album", "02 b.mp3"), "x");
            File.WriteAllText(Path.Combine(root, "album", "01 a.flac"), "x");
            File.WriteAllText(Path.Combine(root, "album", "cover.jpg"), "x");
            File.WriteAllText(Path.Combine(root, "album", "disc 2", "01 c.flac"), "x");
            File.WriteAllText(Path.Combine(root, "loose.ogg"), "x");
            File.WriteAllText(Path.Combine(root, "notes.txt"), "x");

            var files = DroppedFilesService.ExpandAudioFiles(new[]
            {
                Path.Combine(root, "album"),
                Path.Combine(root, "loose.ogg"),
                Path.Combine(root, "notes.txt"),
                Path.Combine(root, "missing.flac"),
            });

            Assert.Equal(new[]
            {
                Path.Combine(root, "album", "01 a.flac"),
                Path.Combine(root, "album", "02 b.mp3"),
                Path.Combine(root, "album", "disc 2", "01 c.flac"),
                Path.Combine(root, "loose.ogg"),
            }, files);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// Unpadded track numbers ("1 -", "2 -", "10 -") came out 1, 10, 2 — ordinal path order —
    /// so a dropped folder played (and, with #108, filled a new playlist) out of order.
    /// File-manager order is numeric-aware, like the Folders view.
    /// </summary>
    [Fact]
    public void ExpandDroppedAudioFiles_OrdersNumbersNaturally()
    {
        var root = Path.Combine(Path.GetTempPath(), "noctis-drop-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Disc 10"));
            Directory.CreateDirectory(Path.Combine(root, "Disc 2"));
            foreach (var name in new[] { "10 - c.mp3", "2 - b.mp3", "1 - a.mp3" })
                File.WriteAllText(Path.Combine(root, name), "x");
            File.WriteAllText(Path.Combine(root, "Disc 10", "1 - z.mp3"), "x");
            File.WriteAllText(Path.Combine(root, "Disc 2", "1 - y.mp3"), "x");

            var files = DroppedFilesService.ExpandAudioFiles(new[] { root });

            Assert.Equal(new[]
            {
                Path.Combine(root, "1 - a.mp3"),
                Path.Combine(root, "2 - b.mp3"),
                Path.Combine(root, "10 - c.mp3"),
                Path.Combine(root, "Disc 2", "1 - y.mp3"),
                Path.Combine(root, "Disc 10", "1 - z.mp3"),
            }, files);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ── Theme editor's background reaches the window root (Discord, Mistery) ──

    [AvaloniaFact]
    public void DerivedTheme_PaintsTheWindowRoot_WithTheMainBackground()
    {
        var dict = ThemeDerivation.Derive(new CustomThemeDefinition
        {
            BaseMode = "Dark",
            MainBackgroundHex = "#123456",
            SidebarBackgroundHex = "#1A2238",
            AccentHex = "#E74856",
        });
        var window = Assert.IsType<Avalonia.Media.SolidColorBrush>(dict["AppWindowBackgroundBrush"]);
        Assert.Equal(Avalonia.Media.Color.Parse("#123456"), window.Color);
    }

    // ── GitHub #72: a scan's partial publishes must not purge the queue ──

    [AvaloniaFact]
    public void LibraryUpdated_WhilePublishingPartial_LeavesTheQueueAndPlaybackAlone()
    {
        var player = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        var vm = new PlayerViewModel(player, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var a = new Track { Title = "A", FilePath = "a.flac" };
        var b = new Track { Title = "B", FilePath = "b.flac" };
        var c = new Track { Title = "C", FilePath = "c.flac" };
        library.TrackList.AddRange(new[] { a, b, c });
        vm.ReplaceQueueAndPlay(new List<Track> { a, b, c }, 0);
        Assert.Same(a, vm.CurrentTrack);
        Assert.Equal(2, vm.UpNext.Count);

        // The progressive fill has only re-enumerated "c" so far.
        library.TrackList.Clear();
        library.TrackList.Add(c);
        library.IsPublishingPartial = true;
        library.RaiseLibraryUpdated();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Same(a, vm.CurrentTrack);
        Assert.Equal(new[] { "B", "C" }, vm.UpNext.Select(t => t.Title));

        // The authoritative publish (a and b really are gone) still reconciles.
        library.IsPublishingPartial = false;
        library.RaiseLibraryUpdated();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal("C", vm.CurrentTrack?.Title);
        Assert.Empty(vm.UpNext);
    }
}
