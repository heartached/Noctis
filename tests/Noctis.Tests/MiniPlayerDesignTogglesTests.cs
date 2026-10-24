using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (vari), mini player: the Pill and Sleeve designs carry the classic card's Shuffle and
/// Repeat toggles (same commands, glyphs, accent "on" state and names) at every size they fit,
/// and on a Pill too narrow for five buttons the toggles step aside.
/// </summary>
[Collection("MetadataServiceStatics")]
public class MiniPlayerDesignTogglesTests
{
    private readonly ITestOutputHelper _out;
    public MiniPlayerDesignTogglesTests(ITestOutputHelper output) => _out = output;

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default) => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default) => Task.FromResult(new List<LrcLibResult>());
    }
    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default) => Task.FromResult<LrcLibResult?>(null);
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
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields, AdvancedTagIO.AdvancedFields original) => false;
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
        app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
    }

    private static async Task PumpFor(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(8);
        }
    }

    private static Track T(string title) => new() { Title = title, Artist = "Artist " + title, FilePath = $@"C:\{title}.flac" };

    private static List<Track> Tracks(int n) => Enumerable.Range(0, n).Select(i => T($"Song {i}")).ToList();

    private sealed record Rig(MiniPlayerWindow Win, MiniPlayerViewModel Vm, PlayerViewModel Player, FakeAudioPlayer Audio)
    {
    }

    private static MiniPlayerViewModel CreateVm(out PlayerViewModel player, out FakeAudioPlayer audio)
    {
        EnsureAppResources();
        var library = new FakeLibraryService();
        audio = new FakeAudioPlayer();
        player = new PlayerViewModel(audio, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(), new TestPersistenceService(), library);
        var settings = new SettingsViewModel(new TestPersistenceService(), library, new NoOpPlayHistoryService());
        return new MiniPlayerViewModel(player, lyrics, settings, library);
    }

    private static Rig Open(IList<Track> queue, int ended, bool keepPlayed, string? design = null,
        double width = 340, double height = 432)
    {
        var vm = CreateVm(out var player, out var audio);
        player.KeepPlayedInQueue = keepPlayed;
        player.ReplaceQueueAndPlay(queue, 0);
        for (var i = 0; i < ended; i++)
        {
            audio.RaiseTrackEnded();
            Dispatcher.UIThread.RunJobs();
        }
        if (design != null) vm.SetDesignCommand.Execute(design);
        var win = new MiniPlayerWindow { DataContext = vm, Width = width, Height = height };
        win.Show();
        return new Rig(win, vm, player, audio);
    }

    [AvaloniaTheory]
    [InlineData("Pill")]
    [InlineData("Sleeve")]
    public async Task Design_HasShuffleAndRepeat_WiredToThePlayer_WithNames(string design)
    {
        AccessibleNames.Install();
        var rig = Open(Tracks(4), ended: 0, keepPlayed: false, design: design);
        try
        {
            await PumpFor(400);
            var shuffle = rig.Win.FindControl<Button>(design + "ShuffleButton")!;
            var repeat = rig.Win.FindControl<Button>(design + "RepeatButton")!;
            Assert.True(shuffle.IsEffectivelyVisible);
            Assert.True(repeat.IsEffectivelyVisible);
            Assert.Same(rig.Player.ToggleShuffleCommand, shuffle.Command);
            Assert.Same(rig.Player.CycleRepeatCommand, repeat.Command);
            string? NameOf(Control c) => ControlAutomationPeer.CreatePeerForElement(c).GetName();
            Assert.Equal(Loc.T("MiniPlayer.ShuffleTip"), NameOf(shuffle));
            Assert.Equal(rig.Player.RepeatModeTip, NameOf(repeat)); // GitHub #144: names the mode

            // The lit glyph is the accent one, as on the classic card.
            IBrush? Lit(Button b) => b.GetVisualDescendants().OfType<Viewbox>().Single(v => v.IsVisible)
                .GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single().Fill;
            var off = Lit(shuffle);
            shuffle.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(rig.Player.IsShuffleEnabled);
            Assert.NotEqual(off, Lit(shuffle));

            var repeatOff = Lit(repeat);
            var glyphs = new List<Geometry?>();
            foreach (var mode in new[] { RepeatMode.All, RepeatMode.One, RepeatMode.Off })
            {
                repeat.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(mode, rig.Player.RepeatMode);
                glyphs.Add(repeat.GetVisualDescendants().OfType<Viewbox>().Single(v => v.IsVisible)
                    .GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single().Data);
                if (mode != RepeatMode.Off) Assert.NotEqual(repeatOff, Lit(repeat));
            }
            Assert.NotEqual(glyphs[0], glyphs[1]); // Repeat One has its own glyph
            Assert.Equal(repeatOff, Lit(repeat));
        }
        finally { rig.Win.Close(); }
    }

    /// <summary>The design's transport row at its canonical size and every larger one: the five
    /// buttons sit inside the card, in order, apart. Below the canonical width the designs'
    /// existing rows (title, seek bar) already run out of room; that is reported, not asserted.</summary>
    [AvaloniaTheory]
    [InlineData("Pill", 352, 140)]
    [InlineData("Pill", 420, 140)]
    [InlineData("Pill", 640, 220)]
    [InlineData("Pill", 960, 840)]
    [InlineData("Sleeve", 340, 372)]
    [InlineData("Sleeve", 420, 460)]
    [InlineData("Sleeve", 960, 840)]
    public async Task Design_TransportRow_FitsTheCard_WithoutOverlap(string design, double width, double height)
    {
        var rig = Open(Tracks(4), ended: 0, keepPlayed: false, design: design, width: width, height: height);
        try
        {
            await PumpFor(400);
            rig.Win.Width = width; rig.Win.Height = height; // the design's canonical size was applied on open
            await PumpFor(300);
            var card = rig.Win.FindControl<Border>(design + "Card")!;
            var row = rig.Win.FindControl<Grid>(design + "Transport")!;
            var buttons = row.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
            Assert.Equal(5, buttons.Count);
            var rects = buttons.Select(b => new Rect(b.TranslatePoint(default, card)!.Value, b.Bounds.Size)).ToList();
            _out.WriteLine($"{design} {width}x{height}: card={card.Bounds.Size} " + string.Join(" ", rects.Select(r => $"[{r.X:0},{r.Width:0}]")));
            var inside = new Rect(card.Bounds.Size);
            foreach (var r in rects) Assert.True(inside.Contains(r), $"button {r} outside the card {inside}");
            var ordered = rects.OrderBy(r => r.X).ToList();
            Assert.Equal(rects, ordered); // shuffle, prev, play, next, repeat — left to right
            for (var i = 1; i < ordered.Count; i++)
                Assert.True(ordered[i].X >= ordered[i - 1].Right + 2, $"buttons {i - 1} and {i} touch: {ordered[i - 1]} {ordered[i]}");
            Assert.Same(rig.Player.ToggleShuffleCommand, buttons[0].Command);
            Assert.Same(rig.Player.CycleRepeatCommand, buttons[^1].Command);
        }
        finally { rig.Win.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(320)]
    [InlineData(306)]
    public async Task Pill_TooNarrowForFive_TheTogglesStepAside_AndTheTransportStaysClear(double width)
    {
        var rig = Open(Tracks(4), ended: 0, keepPlayed: false, design: "Pill", width: width, height: 140);
        try
        {
            await PumpFor(400);
            rig.Win.Width = width;
            await PumpFor(300);
            Assert.False(rig.Win.FindControl<Button>("PillShuffleButton")!.IsVisible);
            Assert.False(rig.Win.FindControl<Button>("PillRepeatButton")!.IsVisible);
            var card = rig.Win.FindControl<Border>("PillCard")!;
            var buttons = rig.Win.FindControl<Grid>("PillTransport")!.GetVisualDescendants().OfType<Button>()
                .Where(b => b.IsEffectivelyVisible).ToList();
            Assert.Equal(3, buttons.Count);
            var rects = buttons.Select(b => new Rect(b.TranslatePoint(default, card)!.Value, b.Bounds.Size)).OrderBy(r => r.X).ToList();
            foreach (var r in rects) Assert.True(new Rect(card.Bounds.Size).Contains(r), $"button {r} outside the card");
            for (var i = 1; i < rects.Count; i++) Assert.True(rects[i].X >= rects[i - 1].Right);

            // Wide enough again: back.
            rig.Win.Width = 352;
            await PumpFor(300);
            Assert.True(rig.Win.FindControl<Button>("PillShuffleButton")!.IsEffectivelyVisible);
            Assert.True(rig.Win.FindControl<Button>("PillRepeatButton")!.IsEffectivelyVisible);
        }
        finally { rig.Win.Close(); }
    }

    /// <summary>
    /// Real-Skia renders for eyeballing: Pill and Sleeve with the toggles off, then Shuffle on and
    /// Repeat One. Explicit only: runs when NOCTIS_TEST_SKIA=1 and NOCTIS_QUEUE_MINI_SHOTS_DIR
    /// names the folder.
    /// </summary>
    [AvaloniaFact]
    public async Task Shots_DesignToggles()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_QUEUE_MINI_SHOTS_DIR");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        // The app's styles carry the accent brushes the lit toggles paint with.
        AddToPlaylistDialogTests.EnsureAppStyles();
        var names = new[] { "Midnight City", "Dreams", "Blue Monday", "Heroes", "Teardrop", "Nightcall", "Strobe", "Intro" };
        List<Track> Named() => names.Select(n => new Track { Title = n, Artist = "Various", FilePath = $@"C:\{n}.flac" }).ToList();

        foreach (var (design, w, h) in new[] { ("Pill", 352.0, 140.0), ("Sleeve", 340.0, 372.0), ("Pill", 520.0, 140.0), ("Sleeve", 460.0, 420.0) })
        {
            var rig = Open(Named(), ended: 0, keepPlayed: false, design: design, width: w, height: h);
            try
            {
                rig.Win.Background = new SolidColorBrush(Color.Parse("#3A3F47"));
                await PumpFor(400);
                rig.Win.Width = w; rig.Win.Height = h;
                await PumpFor(400);
                var path = Path.Combine(dir, $"{design.ToLowerInvariant()}-{w:0}x{h:0}-off.png");
                rig.Win.CaptureRenderedFrame()!.Save(path);
                _out.WriteLine(path);
                rig.Player.ToggleShuffleCommand.Execute(null);
                rig.Player.CycleRepeatCommand.Execute(null);
                rig.Player.CycleRepeatCommand.Execute(null); // Repeat One
                await PumpFor(500);
                path = Path.Combine(dir, $"{design.ToLowerInvariant()}-{w:0}x{h:0}-shuffle-repeat-one.png");
                rig.Win.CaptureRenderedFrame()!.Save(path);
                _out.WriteLine(path);
            }
            finally { rig.Win.Close(); }
        }
    }
}
