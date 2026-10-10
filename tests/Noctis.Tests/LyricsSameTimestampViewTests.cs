using Avalonia;
using Avalonia.Controls;
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
/// GitHub #116 on the desktop: a sidecar .lrc whose lines share a timestamp (original,
/// romaji, translation) loads as one entry, renders the romaji and translation under the
/// line on the lyrics page and the side panel, keeps them on the Plain tab, and hands them
/// back to the LRC editor so its save does not drop them from the user's file.
/// </summary>
public class LyricsSameTimestampViewTests
{
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

    private const string Original = "泣きじゃくる子供のように";
    private const string Romaji = "nakijakuru kodomo no you ni";
    private const string Translation = "Como un niño que llora sin consuelo";

    private const string IssueLrc =
        "[00:01.54]" + Original + "\n" +
        "[00:01.54]" + Romaji + "\n" +
        "[00:01.54]" + Translation + "\n" +
        "[00:04.00]next line";

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

    private static async Task Pump(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(8);
        }
    }

    private static async Task<(LyricsViewModel Vm, PlayerViewModel Player, string Dir)> LoadSidecar(string lrc)
    {
        var dir = Path.Combine(Path.GetTempPath(), "noctis-116-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "song.lrc"), lrc);

        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());
        player.Duration = TimeSpan.FromSeconds(10);
        player.CurrentTrack = new Track { Title = "Issue 116", Artist = "Test", FilePath = Path.Combine(dir, "song.mp3") };
        vm.SetLyricsSurfaceVisible(true);
        vm.EnsureLyricsForCurrentTrack();
        await WaitUntil(() => vm.IsSynced && vm.UnsyncedLines.Count > 0, "sidecar .lrc to load");
        return (vm, player, dir);
    }

    [AvaloniaFact]
    public async Task SidecarLrc_SameTimestampLines_RenderTogether_OnPageAndPanel()
    {
        var (vm, player, dir) = await LoadSidecar(IssueLrc);
        var win = new Window { Width = 1400, Height = 800 };
        try
        {
            var entry = Assert.Single(vm.LyricLines, l => l.Text == Original);
            Assert.DoesNotContain(vm.LyricLines, l => l.Text == Romaji || l.Text == Translation);

            var page = new LyricsView { DataContext = vm };
            var panel = new LyricsPanelView { DataContext = vm };
            win.Content = new StackPanel { Children = { page, panel } };
            win.Show();
            await Pump(50);

            foreach (var view in new UserControl[] { page, panel })
            {
                var button = LineButton(view, entry);
                var romaji = Row<TextBlock>(button, "romanization-text");
                var translation = Row<TextBlock>(button, "translation-text");
                Assert.True(romaji.IsVisible);
                Assert.True(translation.IsVisible);
                Assert.Equal(Romaji, romaji.Text);
                Assert.Equal(Translation, translation.Text);
                // File order under the line: romaji, then translation.
                Assert.True(Top(translation, button) >= Top(romaji, button) + romaji.Bounds.Height - 0.5);
            }

            // The Settings toggles still hide the rows.
            player.LyricsShowTranslations = false;
            player.LyricsShowRomanization = false;
            await Pump(50);
            Assert.False(Row<TextBlock>(LineButton(page, entry), "romanization-text").IsVisible);
            Assert.False(Row<TextBlock>(LineButton(page, entry), "translation-text").IsVisible);
        }
        finally
        {
            win.Close();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task PlainTab_KeepsTheRomajiAndTranslation()
    {
        var (vm, _, dir) = await LoadSidecar(IssueLrc);
        try
        {
            var plain = Assert.Single(vm.UnsyncedLines, l => l.Text == Original);
            Assert.Equal(Romaji, plain.Transliteration);
            Assert.Equal(Translation, plain.Translation);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [AvaloniaFact]
    public async Task LrcEditorSeed_WritesTheLayersBackAtTheSameTimestamp()
    {
        var (vm, _, dir) = await LoadSidecar(IssueLrc);
        try
        {
            var seed = vm.BuildSeedFromLoadedLines();

            Assert.NotNull(seed);
            Assert.Contains("[00:01.54]" + Original + "\n[00:01.54]" + Romaji + "\n[00:01.54]" + Translation,
                seed!.Replace("\r\n", "\n"));
            // Saved back, it parses into the same entry.
            var reparsed = LrcParser.Parse(seed);
            var entry = Assert.Single(reparsed, l => l.Text == Original);
            Assert.Equal((Romaji, Translation), (entry.Transliteration, entry.Translation));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private static Button LineButton(Control view, LyricLine line) =>
        view.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.DataContext, line));

    private static T Row<T>(Button line, string cls) where T : Control =>
        line.GetVisualDescendants().OfType<T>().Single(c => c.Classes.Contains(cls));

    private static double Top(Visual v, Visual relativeTo) => v.TranslatePoint(default, relativeTo)!.Value.Y;
}
