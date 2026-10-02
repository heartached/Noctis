using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Picking a song in the Studio's list (owner, 10-01: "it freezes for a second"). The view
/// model's part is ~2 ms; the stall was the review pane building and laying out a row (a
/// TextBox, buttons, a word strip) for every line of the song at once. Measured here with
/// the app's styles over 76-line songs of 8 words: 0.7-3.3 s per pick with a StackPanel,
/// ~60-85 ms with the virtualizing panel.
/// </summary>
public class LyricsStudioSelectionPerfTests : IDisposable
{
    private const int LinesPerSong = 76;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "studio-sel-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ITestOutputHelper _out;

    public LyricsStudioSelectionPerfTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [AvaloniaFact]
    public void PickingASong_BuildsOnlyTheLinesInView_AndEveryLineStaysReachable()
    {
        EnsureAppResources();
        var vm = StudioWithRestoredReviews(4);
        var panel = new LyricsStudioPanel { DataContext = vm };
        var win = new Window { Width = 1000, Height = 720, Content = panel, RequestedThemeVariant = ThemeVariant.Dark };
        win.Show();
        try
        {
            Pump(300);
            for (var round = 0; round < 4; round++)
            {
                var target = vm.Queue[(round + 1) % vm.Queue.Count];
                var sw = Stopwatch.StartNew();
                vm.Selected = target;
                win.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                var rows = Rows(panel);
                _out.WriteLine($"pick {round}: {sw.Elapsed.TotalMilliseconds:0.0} ms, {rows.Count} of {vm.ReviewLines.Count} line rows built");

                Assert.Equal(LinesPerSong, vm.ReviewLines.Count);
                Assert.NotEmpty(rows);
                Assert.True(rows.Count < LinesPerSong / 2, $"{rows.Count} of {LinesPerSong} line rows were built for one pick");
                Assert.Same(vm.ReviewLines[0], rows[0].DataContext);
            }

            // Scrolled to the end, the last line is there to edit.
            var scroller = panel.GetVisualDescendants().OfType<ItemsControl>()
                .Single(c => ReferenceEquals(c.ItemsSource, vm.ReviewLines))
                .FindAncestorOfType<ScrollViewer>()!;
            scroller.Offset = new Vector(0, scroller.Extent.Height);
            for (var i = 0; i < 5; i++) { win.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
            Assert.Contains(Rows(panel), r => ReferenceEquals(r.DataContext, vm.ReviewLines[^1]));
        }
        finally { win.Close(); }
    }

    private static List<Border> Rows(Control panel) =>
        panel.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("review-line") && b.IsEffectivelyVisible)
            .OrderBy(b => b.TranslatePoint(new Point(0, 0), panel)?.Y ?? double.MaxValue)
            .ToList();

    /// <summary>Songs with a finished, unsaved review each ("Restored · review" in the list).</summary>
    private LyricsStudioViewModel StudioWithRestoredReviews(int songs)
    {
        var drafts = new LyricsStudioDraftStore(Path.Combine(_root, "drafts"));
        var tracks = new List<Track>();
        for (var i = 0; i < songs; i++)
        {
            var t = new Track { Title = $"Song {i}", Artist = "Artist", FilePath = Path.Combine(_root, $"{i}.mp3"), Duration = TimeSpan.FromMinutes(4) };
            tracks.Add(t);
            var lines = Enumerable.Range(0, LinesPerSong).Select(l =>
            {
                var start = TimeSpan.FromSeconds(5 + l * 3);
                var words = Enumerable.Range(0, 8)
                    .Select(w => new AlignedWord($"word{w}", start + TimeSpan.FromMilliseconds(w * 300), start + TimeSpan.FromMilliseconds(w * 300 + 280)))
                    .ToList();
                return new AlignedLine(string.Join(' ', words.Select(w => w.Text)), start, start + TimeSpan.FromSeconds(2.5), words, 0.9, false);
            }).ToList();
            drafts.Save(t.Id, LyricsStudioDraft.From(new LyricsStudioResult(t, lines, LyricsStudioSource.ExistingFile, 0.9, "en", 600)));
        }
        return new LyricsStudioViewModel(tracks, new NullEngine(), new LyricsWriter(null!, null), new FakeLibraryService(), null,
            () => new AppSettings { LyricsStudioWordTimings = true }, _ => { }, drafts);
    }

    private static void Pump(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(8);
        }
    }

    // Same app styles as LyricsStudioUiProbeTests, so the rows are built from the real templates.
    private static void EnsureAppResources()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("ChevronDownThinIcon", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
            {
                Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml"),
            });
        if (!app.Styles.OfType<StyleInclude>().Any(s => s.Source?.ToString().Contains("Noctis.UI/Assets/Styles.axaml") == true))
        {
            if (!app.Resources.ContainsKey("InterSemiBold"))
                app.Resources["InterSemiBold"] = new FontFamily("avares://Noctis.UI/Assets/Fonts/Inter-SemiBold.ttf#Inter SemiBold");
            app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
            {
                Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml"),
            });
        }
    }

    private sealed class NullEngine : ILyricsStudioEngine
    {
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; } = new(Path.Combine(Path.GetTempPath(), "noctis-null-models"));
        public IDisposable OpenSession(WhisperModelSize model) => new MemoryStream();
        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
