using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #124 "keep played songs in the queue", mini player side: with Settings ▸ Playback ▸
/// Keep Played Songs on, the Queue drawer lists the played songs (oldest first, dimmed) under
/// a "Played" header above the "Up Next" header and rows, in the SAME virtualized list, and
/// opens on the boundary. Off, the drawer is exactly the Up Next list it always was.
/// </summary>
[Collection("MetadataServiceStatics")]
public class MiniPlayerPlayedQueueTests
{
    private readonly ITestOutputHelper _out;
    public MiniPlayerPlayedQueueTests(ITestOutputHelper output) => _out = output;

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

    private static Track T(string title) => new()
    {
        Title = title,
        Artist = "Artist " + title,
        FilePath = $@"C:\{title}.flac",
    };

    private sealed record Rig(MiniPlayerWindow Win, MiniPlayerViewModel Vm, PlayerViewModel Player, FakeAudioPlayer Audio)
    {
        public ItemsControl List => Win.FindControl<ItemsControl>("QueueList")!;
        public ScrollViewer Scroller => Win.FindControl<ScrollViewer>("QueueScroller")!;
        public Border Sheet => Win.FindControl<Border>("DrawerSheet")!;
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

    /// <summary>Plays <paramref name="queue"/> from the top and lets <paramref name="ended"/>
    /// songs end naturally, so they land in History the way they do in the app.</summary>
    private static Rig Open(IList<Track> queue, int ended, bool keepPlayed)
    {
        var vm = CreateVm(out var player, out var audio);
        player.KeepPlayedInQueue = keepPlayed;
        player.ReplaceQueueAndPlay(queue, 0);
        for (var i = 0; i < ended; i++)
        {
            audio.RaiseTrackEnded();
            Dispatcher.UIThread.RunJobs();
        }

        var win = new MiniPlayerWindow { DataContext = vm, Width = 340, Height = 432 };
        win.Show();
        return new Rig(win, vm, player, audio);
    }

    private static async Task OpenQueue(Rig rig)
    {
        rig.Vm.ToggleQueueDrawerCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        rig.Sheet.Height = 200; // headless windows do not grow; the sheet's height comes from the resize
        rig.Win.UpdateLayout();
        await PumpFor(500);
    }

    private static Button PlayButtonOf(Rig rig, int previewIndex)
    {
        rig.List.ScrollIntoView(previewIndex);
        rig.Win.UpdateLayout();
        var container = rig.List.ContainerFromIndex(previewIndex)!;
        return container.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("glass-circle"));
    }

    private static void Click(Button b)
    {
        Assert.True(b.Command!.CanExecute(b.CommandParameter));
        b.Command.Execute(b.CommandParameter);
        Dispatcher.UIThread.RunJobs();
    }

    private static List<Track> Tracks(int n) => Enumerable.Range(0, n).Select(i => T($"Song {i}")).ToList();

    /// <summary>The "…" menu rows read their labels to screen readers. Their content is a
    /// StackPanel with no tooltip to mirror, so UIA read them as "Avalonia.Controls.StackPanel"
    /// (measured live 10-10); the Lyrics / Pin rows follow their state.</summary>
    [AvaloniaFact]
    public void MoreMenuRows_ReadTheirLabels()
    {
        AccessibleNames.Install(); // as App does: string tooltips name icon rows (Equalizer)
        var rig = Open(Tracks(2), ended: 0, keepPlayed: false);
        var popup = rig.Win.FindControl<Avalonia.Controls.Primitives.Popup>("MorePopup")!;
        var rows = ((Control)popup.Child!).GetLogicalDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("mini-menu-item")).ToList();
        Assert.True(rows.Count >= 7, $"{rows.Count} menu rows");

        string? NameOf(Control c) => Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(c).GetName();
        var names = rows.Select(NameOf).ToList();
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n) || n!.StartsWith("Avalonia."), $"row read as '{n}'"));

        // Not English literals: LocalizationTests may switch the culture in parallel.
        var lyricsRow = rows.Single(b => b.Command == rig.Vm.ToggleLyricsFormCommand);
        Assert.Equal(rig.Vm.LyricsMenuLabel, NameOf(lyricsRow));
        var closed = NameOf(lyricsRow);
        var wasLyrics = rig.Vm.IsLyricsForm;
        rig.Vm.ToggleLyricsFormCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(rig.Vm.LyricsMenuLabel, NameOf(lyricsRow));
        // The form only switches where the current design allows it (state other tests in
        // the run can leave); when it does, the name follows ("Lyrics" → "Hide Lyrics").
        if (rig.Vm.IsLyricsForm != wasLyrics)
            Assert.NotEqual(closed, NameOf(lyricsRow));
        rig.Win.Close();
    }

    [AvaloniaFact]
    public async Task SettingOff_DrawerListsUpNextOnly_AsBefore()
    {
        var tracks = Tracks(8);
        var rig = Open(tracks, ended: 3, keepPlayed: false);
        try
        {
            Assert.Equal(3, rig.Player.History.Count); // songs DID play
            await OpenQueue(rig);

            Assert.False(rig.Vm.QueuePreviewHasPlayed);
            Assert.All(rig.Vm.QueuePreview, r => Assert.False(Assert.IsType<MiniQueueRow>(r).IsPlayed));
            Assert.Equal(tracks.Skip(4), rig.Vm.QueuePreview.Cast<MiniQueueRow>().Select(r => r.Track));
            Assert.Equal(-1, rig.Vm.QueueBoundaryIndex);

            // The fixed "Up Next" header is the one on screen; no in-list headers, nothing dimmed.
            var fixedHeader = rig.Scroller.GetVisualParent()!.GetVisualChildren().OfType<Grid>().First();
            Assert.True(fixedHeader.IsVisible);
            Assert.DoesNotContain(rig.List.GetVisualDescendants().OfType<Grid>(), g => g.Classes.Contains("mini-queue-header"));
            Assert.DoesNotContain(rig.List.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("mini-played"));
            Assert.Equal(0, rig.Scroller.Offset.Y);
        }
        finally { rig.Win.Close(); }
    }

    [AvaloniaFact]
    public async Task SettingOn_PlayedRowsOldestFirst_AboveUpNext_WithHeaders_Dimmed()
    {
        var tracks = Tracks(8);
        var rig = Open(tracks, ended: 3, keepPlayed: true);
        try
        {
            await OpenQueue(rig);

            Assert.True(rig.Vm.QueuePreviewHasPlayed);
            var rows = rig.Vm.QueuePreview;
            // [Played] s0 s1 s2 [Up Next] s4 s5 s6 s7  (s3 is playing)
            Assert.Equal(1 + 3 + 1 + 4, rows.Count);
            Assert.True(Assert.IsType<MiniQueueHeader>(rows[0]).IsPlayed);
            for (var i = 0; i < 3; i++)
            {
                var r = Assert.IsType<MiniQueueRow>(rows[1 + i]);
                Assert.True(r.IsPlayed);
                Assert.Equal(i, r.Index);
                Assert.Same(tracks[i], r.Track);
            }
            var upNextHeader = Assert.IsType<MiniQueueHeader>(rows[4]);
            Assert.False(upNextHeader.IsPlayed);
            Assert.False(upNextHeader.ShowTruncated);
            Assert.Equal(tracks.Skip(4), rows.Skip(5).Cast<MiniQueueRow>().Select(r => r.Track));
            Assert.All(rows.Skip(5).Cast<MiniQueueRow>(), r => Assert.False(r.IsPlayed));
            Assert.Equal(3, rig.Vm.QueueBoundaryIndex);

            // On screen: the fixed header steps aside, the in-list headers read Played / Up Next,
            // played rows are dimmed and Up Next rows are not.
            var fixedHeader = rig.Scroller.GetVisualParent()!.GetVisualChildren().OfType<Grid>().First();
            Assert.False(fixedHeader.IsVisible);
            rig.Scroller.Offset = default;
            rig.Win.UpdateLayout();
            var headerTexts = rig.List.GetVisualDescendants().OfType<Grid>()
                .Where(g => g.Classes.Contains("mini-queue-header"))
                .SelectMany(g => g.GetVisualChildren().OfType<TextBlock>().Where(t => t.IsVisible).Take(1))
                .Select(t => t.Text).ToList();
            Assert.Contains("Played", headerTexts);
            var played = PlayButtonOf(rig, 1).FindAncestorOfType<Border>()!;
            Assert.Contains("mini-played", played.Classes);
            Assert.Equal(0.55, played.Opacity, 3);
            var next = PlayButtonOf(rig, 5).FindAncestorOfType<Border>()!;
            Assert.DoesNotContain("mini-played", next.Classes);
            Assert.Equal(1.0, next.Opacity, 3);
        }
        finally { rig.Win.Close(); }
    }

    [AvaloniaFact]
    public async Task SettingOn_Virtualized_TruncatesUpNext_AndOpensOnTheBoundary()
    {
        // 40 played, 1 playing, 120 up next (past the 100-row cap).
        var tracks = Tracks(161);
        var rig = Open(tracks, ended: 40, keepPlayed: true);
        try
        {
            Assert.Equal(40, rig.Player.PlayedTracks.Count);
            rig.Vm.ToggleQueueDrawerCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            // The boundary scroll runs while the sheet still has no height (the app's open
            // slide starts from 0); give the sheet its height afterwards, as the slide does.
            await PumpFor(100);
            rig.Sheet.Height = 200;
            rig.Win.UpdateLayout();
            await PumpFor(500);

            Assert.True(rig.Vm.QueuePreviewTruncated);
            Assert.Equal(1 + 40 + 1 + 100, rig.Vm.QueuePreview.Count);
            Assert.True(Assert.IsType<MiniQueueHeader>(rig.Vm.QueuePreview[41]).ShowTruncated);

            var panel = rig.List.GetVisualDescendants().OfType<Panel>().First(p => p.GetVisualParent() is ItemsPresenter);
            Assert.IsType<VirtualizingStackPanel>(panel);
            var liveRows = rig.List.GetVisualDescendants().OfType<MarqueeTextBlock>().Count();
            _out.WriteLine($"liveRows={liveRows}, offset={rig.Scroller.Offset}, viewport={rig.Scroller.Viewport}, extent={rig.Scroller.Extent}");
            Assert.InRange(liveRows, 1, (int)Math.Ceiling(rig.Scroller.Viewport.Height / 48.0) + 3);

            // Opened on the boundary: the last played row and the Up Next header are in view,
            // the first played row is not.
            bool InView(int index)
            {
                if (rig.List.ContainerFromIndex(index) is not { } c || !c.IsVisible) return false;
                var top = c.TranslatePoint(default, rig.Scroller)!.Value.Y;
                return top >= -0.5 && top + c.Bounds.Height <= rig.Scroller.Viewport.Height + 0.5;
            }
            Assert.True(InView(40), "last played row in view");
            Assert.True(InView(41), "Up Next header in view");
            Assert.False(InView(1), "first played row not in view");
            // ...and it is the top row: the rest of the viewport goes to Up Next.
            var lastPlayedTop = rig.List.ContainerFromIndex(40)!.TranslatePoint(default, rig.Scroller)!.Value.Y;
            Assert.InRange(lastPlayedTop, -1, 1);
        }
        finally { rig.Win.Close(); }
    }

    [AvaloniaFact]
    public async Task PlayedRowButton_PlaysThatRowsIndex_EvenForARepeatedSong()
    {
        var a = T("A"); var b = T("B"); var c = T("C"); var d = T("D");
        // a b a end → played [a, b, a], c plays, d next.
        var rig = Open(new List<Track> { a, b, a, c, d }, ended: 3, keepPlayed: true);
        try
        {
            Assert.Equal(new[] { a, b, a }, rig.Player.PlayedTracks);
            await OpenQueue(rig);

            // Row for PlayedTracks[2] — the SECOND a — is preview index 3.
            var row = Assert.IsType<MiniQueueRow>(rig.Vm.QueuePreview[3]);
            Assert.Same(a, row.Track);
            Assert.Equal(2, row.Index);
            Click(PlayButtonOf(rig, 3));

            // Playing the most recent a: only c (the one that was playing) and d follow it.
            // IndexOf would have picked the first a and queued b, a, c, d after it.
            Assert.Same(a, rig.Player.CurrentTrack);
            Assert.Equal(new[] { c, d }, rig.Player.UpNext);
            Assert.Equal(new[] { a, b }, rig.Player.PlayedTracks);
        }
        finally { rig.Win.Close(); }
    }

    [AvaloniaFact]
    public async Task PlayedRowButton_FirstPlayedRow_PlaysOldest()
    {
        var a = T("A"); var b = T("B"); var c = T("C"); var d = T("D");
        var rig = Open(new List<Track> { a, b, a, c, d }, ended: 3, keepPlayed: true);
        try
        {
            await OpenQueue(rig);
            Click(PlayButtonOf(rig, 1)); // PlayedTracks[0], the first a

            Assert.Same(a, rig.Player.CurrentTrack);
            Assert.Equal(new[] { b, a, c, d }, rig.Player.UpNext);
            Assert.Empty(rig.Player.PlayedTracks);
        }
        finally { rig.Win.Close(); }
    }

    /// <summary>Same index-not-IndexOf rule for Up Next (a pre-existing bug: the second copy
    /// of a song queued twice played the FIRST copy, keeping everything between).</summary>
    [AvaloniaFact]
    public async Task UpNextRowButton_PlaysThatRowsIndex_EvenForARepeatedSong()
    {
        var x = T("X"); var a = T("A"); var b = T("B");
        var rig = Open(new List<Track> { x, a, b, a }, ended: 0, keepPlayed: false);
        try
        {
            await OpenQueue(rig);
            Assert.Equal(3, rig.Vm.QueuePreview.Count);
            Click(PlayButtonOf(rig, 2)); // the second a

            Assert.Same(a, rig.Player.CurrentTrack);
            Assert.Empty(rig.Player.UpNext);
        }
        finally { rig.Win.Close(); }
    }

    [AvaloniaFact]
    public async Task OpenDrawer_RefreshesLive_OnTrackEnd_AndOnSettingToggle()
    {
        var tracks = Tracks(6);
        var rig = Open(tracks, ended: 1, keepPlayed: true);
        try
        {
            await OpenQueue(rig);
            Assert.Equal(new[] { tracks[0] }, PlayedRows(rig));
            Assert.Equal(tracks.Skip(2), UpNextRows(rig));

            rig.Audio.RaiseTrackEnded();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { tracks[0], tracks[1] }, PlayedRows(rig));
            Assert.Equal(tracks.Skip(3), UpNextRows(rig));

            // Setting off while open: back to the plain Up Next list.
            rig.Player.KeepPlayedInQueue = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(rig.Vm.QueuePreviewHasPlayed);
            Assert.Empty(PlayedRows(rig));
            Assert.Equal(tracks.Skip(3), UpNextRows(rig));
            Assert.DoesNotContain(rig.Vm.QueuePreview, r => r is MiniQueueHeader);

            rig.Player.KeepPlayedInQueue = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(rig.Vm.QueuePreviewHasPlayed);
            Assert.Equal(new[] { tracks[0], tracks[1] }, PlayedRows(rig));
        }
        finally { rig.Win.Close(); }
    }

    private static List<Track> PlayedRows(Rig rig) =>
        rig.Vm.QueuePreview.OfType<MiniQueueRow>().Where(r => r.IsPlayed).Select(r => r.Track).ToList();

    private static List<Track> UpNextRows(Rig rig) =>
        rig.Vm.QueuePreview.OfType<MiniQueueRow>().Where(r => !r.IsPlayed).Select(r => r.Track).ToList();

    /// <summary>
    /// Real-Skia render of the Queue drawer, before (setting off) and after (setting on).
    /// Explicit only: runs when NOCTIS_TEST_SKIA=1 and NOCTIS_MINI_PLAYED_SHOTS_DIR names the folder.
    /// </summary>
    [AvaloniaFact]
    public async Task Shots_QueueDrawer_WithAndWithoutPlayedSongs()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_MINI_PLAYED_SHOTS_DIR");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var app = Application.Current!;
        if (!app.Resources.ContainsKey("InterSemiBold")) app.Resources["InterSemiBold"] = FontFamily.Default;

        var names = new[]
        {
            "Midnight City", "Dreams", "Blue Monday", "Heroes", "Teardrop", "Nightcall",
            "Strobe", "Intro", "Breathe", "Archangel", "Windowlicker", "Roygbiv",
        };
        foreach (var keep in new[] { false, true })
        {
            var tracks = names.Select(n => new Track { Title = n, Artist = "Various", FilePath = $@"C:\{n}.flac" }).ToList();
            var rig = Open(tracks, ended: 5, keepPlayed: keep);
            try
            {
                rig.Win.Background = new SolidColorBrush(Color.Parse("#1C1C1E"));
                rig.Vm.ToggleQueueDrawerCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                await PumpFor(100);
                rig.Sheet.Height = 200;
                rig.Win.UpdateLayout();
                await PumpFor(900);

                void Shot(string name)
                {
                    var full = Path.Combine(dir, $"_{name}-full.png");
                    rig.Win.CaptureRenderedFrame()!.Save(full);
                    var origin = rig.Sheet.TranslatePoint(default, rig.Win)!.Value;
                    var path = Path.Combine(dir, name + ".png");
                    Crop(full, path, new SKRectI((int)origin.X, (int)origin.Y - 40,
                        (int)(origin.X + rig.Sheet.Bounds.Width), (int)(origin.Y + rig.Sheet.Bounds.Height)));
                    File.Delete(full);
                    _out.WriteLine(path);
                }

                Shot(keep ? "after" : "before");
                if (keep)
                {
                    // Scrolled up: the Played header over the dimmed rows.
                    rig.Scroller.Offset = default;
                    await PumpFor(400);
                    Shot("after-top");
                }
            }
            finally { rig.Win.Close(); }
        }
    }

    private static void Crop(string src, string dst, SKRectI rect)
    {
        using var bmp = SKBitmap.Decode(src);
        rect = SKRectI.Intersect(rect, new SKRectI(0, 0, bmp.Width, bmp.Height));
        using var sub = new SKBitmap(rect.Width, rect.Height);
        bmp.ExtractSubset(sub, rect);
        using var img = SKImage.FromBitmap(sub);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(dst);
        data.SaveTo(fs);
    }
}
