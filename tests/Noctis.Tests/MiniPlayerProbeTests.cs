using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;
using Xunit.Abstractions;

namespace Noctis.Tests;

/// <summary>
/// Measurement probes for the mini player (evidence, not assertions of taste): where the
/// volume-row icons sit relative to the slider's track, and how long the Search drawer's
/// open actually costs on the UI thread with a large library.
/// </summary>
[Collection("MetadataServiceStatics")]
public class MiniPlayerProbeTests
{
    private readonly ITestOutputHelper _out;
    public MiniPlayerProbeTests(ITestOutputHelper output) => _out = output;

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

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static void EnsureAppResources()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("SearchIcon", null, out _)) return;
        app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null)
        {
            Source = new Uri("avares://Noctis/Assets/Icons.axaml"),
        });
    }

    private static MiniPlayerViewModel MakeViewModel(int trackCount, bool withArt = true)
    {
        var library = new FakeLibraryService();
        for (var i = 0; i < trackCount; i++)
        {
            library.TrackList.Add(new Track
            {
                Title = $"Track {i}", Artist = $"Artist {i % 40}", Album = $"Album {i % 200}",
                FilePath = $@"C:\music\{i}.flac", AlbumArtworkPath = $@"C:\art\{i % 200}.jpg",
            });
        }
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), library,
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), library);
        var settings = new SettingsViewModel(
            new TestPersistenceService(), library, new NoOpPlayHistoryService());
        return new MiniPlayerViewModel(player, lyrics, settings, library);
    }

    private static void Frame()
    {
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task PumpFor(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            Frame();
            await Task.Delay(8);
        }
    }

    private static double CenterY(Visual v, Visual root)
        => v.TransformToVisual(root)!.Value.Transform(new Point(0, v.Bounds.Height / 2)).Y;

    private void ReportVolumeRow(Slider slider, Window win, string label)
    {
        var row = (Grid)slider.GetVisualParent()!;
        var muteButton = (Button)row.Children[0];
        var muteGlyph = muteButton.GetVisualDescendants().OfType<Viewbox>().First(v => v.IsEffectivelyVisible);
        var highGlyph = (Viewbox)row.Children[2];
        var thumb = slider.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().First();
        var track = slider.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Track>().First();
        _out.WriteLine($"[{label}] row h={row.Bounds.Height:F1} slider h={slider.Bounds.Height:F1}");
        _out.WriteLine($"[{label}] centres: row={CenterY(row, win):F2} muteButton={CenterY(muteButton, win):F2} muteGlyph={CenterY(muteGlyph, win):F2} track={CenterY(track, win):F2} thumb={CenterY(thumb, win):F2} highGlyph={CenterY(highGlyph, win):F2}");
        var template = track.GetVisualParent() as Grid;
        if (template != null)
        {
            _out.WriteLine($"[{label}]   template rows: {string.Join(" | ", template.RowDefinitions.Select(r => r.Height.ToString()))}");
            foreach (var child in template.Children)
                _out.WriteLine($"[{label}]   child {child.GetType().Name} '{child.Name}' row={Grid.GetRow(child)} y={child.Bounds.Y:F1} h={child.Bounds.Height:F1} visible={child.IsVisible} margin={child.Margin}");
        }
        for (Visual? v = track; v != null && v != slider.GetVisualParent(); v = v.GetVisualParent())
        {
            var margin = v is Control c ? c.Margin.ToString() : "-";
            var va = v is Control cc ? cc.VerticalAlignment.ToString() : "-";
            _out.WriteLine($"[{label}]   {v.GetType().Name} '{(v as Control)?.Name}' y={v.Bounds.Y:F1} h={v.Bounds.Height:F1} margin={margin} valign={va}");
            // The speaker glyphs and the bar must share a centre line (sub-pixel tolerance).
        Assert.InRange(Math.Abs(CenterY(track, win) - CenterY(muteGlyph, win)), 0, 0.75);
        Assert.InRange(Math.Abs(CenterY(track, win) - CenterY(highGlyph, win)), 0, 0.75);
        Assert.InRange(Math.Abs(CenterY(thumb, win) - CenterY(track, win)), 0, 0.5);
    }
    }

    [AvaloniaFact]
    public async Task Probe_VolumeRowIconCentres()
    {
        EnsureAppResources();
        var vm = MakeViewModel(10);
        var win = new MiniPlayerWindow { DataContext = vm, Width = 340, Height = 520 };
        win.Show();
        await PumpFor(150);
        try
        {
            Assert.Equal(MiniPlayerForm.LargeIcon, vm.Form);
            ReportVolumeRow(win.FindControl<Slider>("LargeVolumeSlider")!, win, "large");

            // The drawer copy: open the volume layer in the Card form.
            // 360 wide: 432/340 is still inside the LargeIcon hysteresis band.
            win.Width = 360; win.Height = 432;
            await PumpFor(150);
            Assert.Equal(MiniPlayerForm.Card, vm.Form);
            vm.Drawer = MiniDrawer.Volume;
            await PumpFor(400);
            ReportVolumeRow(win.FindControl<Slider>("DrawerVolumeSlider")!, win, "drawer");
        }
        finally { win.Close(); }
    }

    private static ItemsControl SearchList(MiniPlayerWindow win, MiniPlayerViewModel vm)
        => win.GetVisualDescendants().OfType<ItemsControl>().First(ic => ReferenceEquals(ic.ItemsSource, vm.SearchResults));

    private double MeasureOpenFrame(MiniPlayerViewModel vm)
    {
        vm.Drawer = MiniDrawer.Search;
        var f = Stopwatch.StartNew();
        Frame();
        return f.Elapsed.TotalMilliseconds;
    }

    [AvaloniaFact]
    public async Task Probe_SearchRowTemplateCost()
    {
        EnsureAppResources();
        var vm = MakeViewModel(6000, withArt: false);
        var win = new MiniPlayerWindow { DataContext = vm, Width = 340, Height = 432 };
        win.Show();
        await PumpFor(200);
        try
        {
            vm.Drawer = MiniDrawer.Search; await PumpFor(300); vm.Drawer = MiniDrawer.None; await PumpFor(300);
            var list = SearchList(win, vm);
            var original = list.ItemTemplate;

            var variants = new (string Name, Avalonia.Controls.Templates.IDataTemplate Template)[]
            {
                ("plain TextBlock", new Avalonia.Controls.Templates.FuncDataTemplate<Track>((t, _) => new TextBlock { Text = t.TitleDisplay })),
                ("MarqueeTextBlock", new Avalonia.Controls.Templates.FuncDataTemplate<Track>((t, _) => new Noctis.Controls.MarqueeTextBlock { Text = t.TitleDisplay, FontSize = 13, IsMiniPlayer = true })),
                ("glass Button+PathIcon", new Avalonia.Controls.Templates.FuncDataTemplate<Track>((t, _) =>
                {
                    var b = new Button { Width = 28, Height = 28, Content = new PathIcon { Width = 13, Height = 13 } };
                    b.Classes.Add("mini-transport"); b.Classes.Add("glass-circle");
                    return b;
                })),
                ("CachedImage (no path)", new Avalonia.Controls.Templates.FuncDataTemplate<Track>((t, _) => new Noctis.Controls.CachedImage { Width = 36, Height = 36, DecodeWidth = 128 })),
                ("mini-row Border only", new Avalonia.Controls.Templates.FuncDataTemplate<Track>((t, _) => { var b = new Border { Height = 40 }; b.Classes.Add("mini-row"); return b; })),
            };
            foreach (var (name, template) in variants)
            {
                list.ItemTemplate = template;
                var a = MeasureOpenFrame(vm); vm.Drawer = MiniDrawer.None; await PumpFor(300);
                var b = MeasureOpenFrame(vm); vm.Drawer = MiniDrawer.None; await PumpFor(300);
                _out.WriteLine($"[template] {name}: {a:F1} ms, {b:F1} ms for {vm.SearchResults.Count} rows");
            }
            list.ItemTemplate = original;
            var o1 = MeasureOpenFrame(vm); vm.Drawer = MiniDrawer.None; await PumpFor(300);
            var o2 = MeasureOpenFrame(vm); vm.Drawer = MiniDrawer.None; await PumpFor(300);
            _out.WriteLine($"[template] ORIGINAL: {o1:F1} ms, {o2:F1} ms for {vm.SearchResults.Count} rows");
        }
        finally { win.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Probe_SearchDrawerOpenCost(bool withArt)
    {
        EnsureAppResources();
        var vm = MakeViewModel(6000, withArt);
        var win = new MiniPlayerWindow { DataContext = vm, Width = 340, Height = 432 };
        win.Show();
        await PumpFor(200);
        try
        {
            Assert.Equal(MiniPlayerForm.Card, vm.Form);
            for (var round = 0; round < 2; round++)
            {
                var sw = Stopwatch.StartNew();
                vm.Drawer = MiniDrawer.Search;
                var setMs = sw.Elapsed.TotalMilliseconds;
                var frames = new List<double>();
                for (var i = 0; i < 12; i++)
                {
                    var f = Stopwatch.StartNew();
                    Frame();
                    frames.Add(f.Elapsed.TotalMilliseconds);
                    await Task.Delay(8);
                }
                _out.WriteLine($"[open art={withArt} #{round}] Drawer=Search set: {setMs:F1} ms; rows now {vm.SearchResults.Count}; frames ms: {string.Join(", ", frames.Select(x => x.ToString("F1")))}");
                vm.Drawer = MiniDrawer.None;
                await PumpFor(400);
            }
        }
        finally { win.Close(); }
    }
}
