using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Very long word-timed lyrics (a word-synced audiobook, Discord nutf!xx 10-28) build the
/// word layer only near the sung line; the other lines are <see cref="KaraokeRestingWords"/>,
/// which must sit exactly where the word layer would, so moving the window moves nothing.
/// </summary>
public class LyricsRestingWordsTests
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

    private static LyricsViewModel MakeViewModel()
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        return new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static string T(double s) => TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss\.fff");

    /// <summary>An audiobook-like TTML: <paramref name="lines"/> word-timed lines of mixed
    /// length, with an over-wide word (DownOnly clamp), duet voices (end/centre alignment)
    /// and lines whose last word carries the overflow pad.</summary>
    internal static string BookTtml(int lines, int seed = 11)
    {
        var rnd = new Random(seed);
        string[] vocab = ["the", "of", "and", "remembered", "quietly", "I", "a", "together,", "everything", "window.",
            "Supercalifragilisticexpialidociousnessesandmore", "it's", "neighbourhood", "map", "dumbbells", "carry"];
        var sb = new StringBuilder();
        sb.Append("<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\">")
          .Append("<head><metadata><ttm:agent type=\"person\" xml:id=\"v1\"/><ttm:agent type=\"person\" xml:id=\"v2\"/><ttm:agent type=\"group\" xml:id=\"v1000\"/></metadata></head><body><div>");
        double t = 1;
        for (int i = 0; i < lines; i++)
        {
            var n = rnd.Next(1, 45);
            var agent = (i % 17) switch { 5 => "v2", 11 => "v1000", _ => "v1" };
            sb.Append($"<p begin=\"{T(t)}\" end=\"{T(t + n * 0.4)}\" ttm:agent=\"{agent}\">");
            for (int w = 0; w < n; w++)
            {
                var word = vocab[rnd.Next(vocab.Length)];
                sb.Append($"<span begin=\"{T(t)}\" end=\"{T(t + 0.35)}\">{word}</span>");
                if (w < n - 1) sb.Append(' ');
                t += 0.4;
            }
            sb.Append("</p>");
            t += 0.5;
        }
        return sb.Append("</div></body></tt>").ToString();
    }

    private static Button LineButton(LyricsView view, int index)
    {
        var host = view.FindControl<ItemsControl>("LyricsItemsControl")!;
        return (Button)host.ContainerFromIndex(index)!.GetVisualDescendants().OfType<Button>().First();
    }

    [AvaloniaFact]
    public void LongLyrics_RestAwayFromTheSungLine_AndTheWindowFollowsIt()
    {
        var (lines, _) = TtmlParser.Parse(BookTtml(200));
        Assert.True(lines!.Sum(l => l.Words?.Count ?? 0) > LyricsViewModel.WordLayerWindowMinWords);
        var vm = MakeViewModel();
        vm.LyricLines.ReplaceAll(lines);
        Assert.All(vm.LyricLines.Skip(LyricsViewModel.WordLayerWindowLines + 1), l => Assert.False(l.IsWordLayerLive));
        Assert.True(vm.LyricLines[0].IsWordLayerLive);

        var update = typeof(LyricsViewModel).GetMethod("UpdateActiveLine", BindingFlags.Instance | BindingFlags.NonPublic)!;
        typeof(LyricsViewModel).GetField("_hasSyncedLyrics", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, true);
        update.Invoke(vm, [vm.LyricLines[120].Timestamp!.Value + TimeSpan.FromMilliseconds(200)]);
        var active = vm.ActiveLineIndex;
        Assert.InRange(active, 119, 121);
        // ±1 for the list leading into the next line (the ramp then centres on it).
        for (int i = 0; i < vm.LyricLines.Count; i++)
            if (Math.Abs(i - active) > LyricsViewModel.WordLayerWindowLines + 1)
                Assert.False(vm.LyricLines[i].IsWordLayerLive, $"line {i} (active {active})");
        Assert.True(vm.LyricLines[active].IsWordLayerLive);
        Assert.True(vm.LyricLines[active + 1].IsWordLayerLive);
        Assert.False(vm.LyricLines[active - 10].IsWordLayerLive);
        Assert.False(vm.LyricLines[0].IsWordLayerLive);
    }

    [AvaloniaFact]
    public void ShortLyrics_KeepEveryWordLayer()
    {
        var (lines, _) = TtmlParser.Parse(BookTtml(20));
        Assert.True(lines!.Sum(l => l.Words?.Count ?? 0) <= LyricsViewModel.WordLayerWindowMinWords);
        var vm = MakeViewModel();
        vm.LyricLines.ReplaceAll(lines);
        Assert.All(vm.LyricLines, l => Assert.True(l.IsWordLayerLive));
    }

    /// <summary>Each line laid out at rest and with its word layer: same size, same line box,
    /// every word in the same cell.</summary>
    [AvaloniaTheory]
    [InlineData(1280.0)]
    [InlineData(900.0)]
    public void RestingLine_LaysOutExactlyLikeTheWordLayer(double windowWidth)
    {
        var (lines, _) = TtmlParser.Parse(BookTtml(160, seed: (int)windowWidth));
        var vm = MakeViewModel();
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = windowWidth, Height = 800 };
        try
        {
            win.Show();
            win.Content = view;
            Pump();
            vm.LyricLines.ReplaceAll(lines!);
            Pump();
            Assert.True(view.GetVisualDescendants().Count() < 20_000, "the resting lines should not build word cells");

            var cellsField = typeof(KaraokeRestingWords).GetField("_cells", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var checkedLines = 0;
            for (int i = LyricsViewModel.WordLayerWindowLines + 1; i < vm.LyricLines.Count; i++)
            {
                var line = vm.LyricLines[i];
                if (!line.ShowWords) continue;
                Assert.False(line.IsWordLayerLive);
                var button = LineButton(view, i);
                var resting = button.GetVisualDescendants().OfType<KaraokeRestingWords>().Single();
                Assert.True(resting.IsVisible);
                var restBounds = resting.Bounds;
                var buttonBounds = button.Bounds;
                var cells = ((Rect[])cellsField.GetValue(resting)!).ToArray();

                line.IsWordLayerLive = true;
                Pump();
                var layer = button.GetVisualDescendants().OfType<ItemsControl>().First(c => c.Classes.Contains("word-layer"));
                Assert.True(layer.IsVisible);
                Assert.False(resting.IsVisible);
                Assert.Equal(restBounds, layer.Bounds);
                Assert.Equal(buttonBounds, button.Bounds);
                var panel = layer.GetVisualDescendants().OfType<WrapPanel>().First();
                Assert.Equal(cells.Length, panel.Children.Count);
                for (int c = 0; c < cells.Length; c++)
                    Assert.True(cells[c] == panel.Children[c].Bounds,
                        $"line {i} word {c} '{line.Words![c].Text}': rest {cells[c]} vs layer {panel.Children[c].Bounds}");

                line.IsWordLayerLive = false;
                Pump();
                Assert.Equal(buttonBounds, button.Bounds);
                checkedLines++;
            }
            Assert.True(checkedLines > 100);
        }
        finally
        {
            win.Close();
        }
    }
}
