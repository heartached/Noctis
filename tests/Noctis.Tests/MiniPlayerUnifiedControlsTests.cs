using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-27: the mini player's forms (Bar, Card, Full Artwork, Lyrics) and designs (Pill,
/// Sleeve) are one system. Every one carries the same named controls, built from the same
/// shared classes and bound to the same commands; the time labels sit beside the seek bar,
/// whole, at the smallest sizes; the favourite heart follows the track both ways; the volume
/// row swaps into the seek slot and hands back; hidden forms stop animating; a closed mini
/// player's view model lets go of the shared player.
/// </summary>
[Collection("MetadataServiceStatics")]
public class MiniPlayerUnifiedControlsTests
{
    private readonly ITestOutputHelper _out;
    public MiniPlayerUnifiedControlsTests(ITestOutputHelper output) => _out = output;

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

    private static Track T(string title, TimeSpan? length = null) => new()
    {
        Title = title, Artist = "Artist " + title, FilePath = $@"C:\{title}.flac",
        Duration = length ?? TimeSpan.FromMinutes(3),
    };

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

    /// <summary>Form under test: its name prefix, the style that selects it, and a window size.</summary>
    public static IEnumerable<object[]> Forms() => new[]
    {
        new object[] { "Bar", "Classic", 420.0, 188.0 },
        new object[] { "Card", "Classic", 340.0, 432.0 },
        new object[] { "Large", "Classic", 340.0, 520.0 },
        new object[] { "Lyrics", "Classic", 640.0, 412.0 },
        new object[] { "Pill", "Pill", 360.0, 140.0 },
        new object[] { "Sleeve", "Sleeve", 340.0, 372.0 },
    };

    /// <summary>Near the smallest size each form is reachable at (the window's minimum or the
    /// form's threshold, past the resize hysteresis), where clipping would show first, with a
    /// nine-minute track; and every canonical size with an hour-long one (widest labels).
    /// The headless test font is ~1.7x wider than Inter, so the room asserted is a floor.</summary>
    public static IEnumerable<object[]> LabelCases() => new[]
    {
        new object[] { "Bar", "Classic", 240.0, 140.0, 9, 24.0 },
        new object[] { "Card", "Classic", 210.0, 266.0, 9, 24.0 },
        new object[] { "Large", "Classic", 200.0, 300.0, 9, 24.0 },
        new object[] { "Lyrics", "Classic", 560.0, 340.0, 9, 24.0 },
        new object[] { "Pill", "Pill", 306.0, 140.0, 9, 24.0 },
        new object[] { "Sleeve", "Sleeve", 300.0, 340.0, 9, 24.0 },
        new object[] { "Bar", "Classic", 420.0, 188.0, 83, 40.0 },
        new object[] { "Card", "Classic", 340.0, 432.0, 83, 40.0 },
        new object[] { "Large", "Classic", 340.0, 520.0, 83, 40.0 },
        new object[] { "Lyrics", "Classic", 640.0, 412.0, 83, 40.0 },
        new object[] { "Pill", "Pill", 360.0, 140.0, 83, 24.0 },
        new object[] { "Sleeve", "Sleeve", 340.0, 372.0, 83, 40.0 },
    };

    private static MiniPlayerForm FormOf(string prefix) => prefix switch
    {
        "Bar" => MiniPlayerForm.Bar,
        "Card" => MiniPlayerForm.Card,
        "Large" => MiniPlayerForm.LargeIcon,
        "Lyrics" => MiniPlayerForm.Lyrics,
        "Pill" => MiniPlayerForm.Pill,
        _ => MiniPlayerForm.Sleeve,
    };

    private static async Task<(MiniPlayerWindow Win, MiniPlayerViewModel Vm, PlayerViewModel Player)> Open(
        string prefix, string style, double width, double height, Track? track = null)
    {
        var vm = CreateVm(out var player, out _);
        player.ReplaceQueueAndPlay(new List<Track> { track ?? T("Song"), T("Next one") }, 0);
        if (style != "Classic") vm.SetDesignCommand.Execute(style);
        var win = new MiniPlayerWindow { DataContext = vm, Width = width, Height = height };
        win.Show();
        await PumpFor(400);
        // A design opens at its canonical size; put the size under test back.
        win.Width = width; win.Height = height;
        await PumpFor(300);
        Assert.Equal(FormOf(prefix), vm.Form);
        return (win, vm, player);
    }

    private static Control FormRoot(MiniPlayerWindow win, string prefix) => prefix switch
    {
        "Large" => win.FindControl<Panel>("LargeIconFormRoot")!,
        _ => win.FindControl<Control>(prefix + "FormRoot")!,
    };

    [AvaloniaTheory]
    [MemberData(nameof(Forms))]
    public async Task EveryForm_HasTheSameControls_FromTheSameClasses_OnTheSameCommands(string prefix, string style, double w, double h)
    {
        var (win, vm, player) = await Open(prefix, style, w, h);
        try
        {
            Button B(string name) => win.FindControl<Button>(prefix + name)
                ?? throw new Xunit.Sdk.XunitException($"{prefix}{name} missing");
            var expected = new (string Name, string Class, double Size, System.Windows.Input.ICommand? Command)[]
            {
                ("HeartButton", "mini-heart", 30, player.ToggleCurrentTrackFavoriteCommand),
                ("MoreButton", "mini-more", 30, null),
                ("ShuffleButton", "mini-shuffle", 30, player.ToggleShuffleCommand),
                ("PrevButton", "mini-prev", 34, player.PreviousCommand),
                ("PlayButton", "mini-play", 38, player.PlayPauseCommand),
                ("NextButton", "mini-next", 34, player.NextCommand),
                ("RepeatButton", "mini-repeat", 30, player.CycleRepeatCommand),
            };
            foreach (var (name, cls, size, command) in expected)
            {
                var b = B(name);
                Assert.True(b.IsEffectivelyVisible, $"{prefix}{name} hidden");
                Assert.Contains(cls, b.Classes);
                Assert.Equal(size, b.Bounds.Width, 1);
                Assert.Equal(size, b.Bounds.Height, 1);
                if (command != null) Assert.Same(command, b.Command);
                Assert.NotNull(ToolTip.GetTip(b));
            }
            // The round buttons sit on the same glass disc everywhere.
            foreach (var name in new[] { "HeartButton", "MoreButton", "ShuffleButton", "RepeatButton" })
                Assert.Contains("glass-circle", B(name).Classes);

            var seek = win.FindControl<Slider>(prefix + "SeekSlider")!;
            Assert.True(seek.IsEffectivelyVisible);
            Assert.Contains("mini-slider", seek.Classes);
            Assert.Contains("mini-seek", seek.Classes);

            // Title first, artist second, inside the same info stack.
            var info = win.FindControl<StackPanel>(prefix + "InfoHost")!;
            var lines = info.GetVisualDescendants().OfType<MarqueeTextBlock>().ToList();
            Assert.Equal(2, lines.Count);
            Assert.Contains("mini-title", lines[0].Classes);
            Assert.Contains("mini-artist", lines[1].Classes);
            Assert.Equal("Song", lines[0].Text);
            Assert.Equal(14, lines[0].FontSize);
            Assert.Equal(12, lines[1].FontSize);

            // Elapsed and time-left labels on both sides of the seek bar.
            var root = FormRoot(win, prefix);
            var times = root.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("mini-time") && t.IsEffectivelyVisible).ToList();
            Assert.Equal(2, times.Count);
            Assert.Equal("0:00", times.Single(t => t.Classes.Contains("mini-elapsed")).Text);
            Assert.Equal("-3:00", times.Single(t => t.Classes.Contains("mini-remaining")).Text);
        }
        finally { win.Close(); }
    }

    [AvaloniaTheory]
    [MemberData(nameof(LabelCases))]
    public async Task TimeLabels_SitWholeBesideTheSeekBar(string prefix, string style, double w, double h, int minutes, double minSeek)
    {
        var (win, vm, player) = await Open(prefix, style, w, h, T("Long", TimeSpan.FromMinutes(minutes)));
        try
        {
            await PumpFor(200);
            var seek = win.FindControl<Slider>(prefix + "SeekSlider")!;
            var row = (Grid)seek.GetVisualParent()!;
            var elapsed = row.Children.OfType<TextBlock>().Single(t => t.Classes.Contains("mini-elapsed"));
            var left = row.Children.OfType<TextBlock>().Single(t => t.Classes.Contains("mini-remaining"));
            Assert.Equal(minutes >= 60 ? "-1:23:00" : "-9:00", left.Text);
            var client = new Rect(win.ClientSize);
            Rect InWindow(Control c) => new(c.TranslatePoint(default, win)!.Value, c.Bounds.Size);
            foreach (var label in new[] { elapsed, left })
            {
                var r = InWindow(label);
                _out.WriteLine($"{prefix} {w}x{h}: {label.Text} at {r} (desired {label.DesiredSize.Width:0.0})");
                Assert.True(label.IsEffectivelyVisible);
                Assert.Equal(1.0, label.Opacity, 3); // always shown, not hover-only
                Assert.True(label.Bounds.Width + 0.5 >= label.DesiredSize.Width, $"{label.Text} squeezed");
                Assert.True(client.Contains(r), $"{label.Text} {r} outside the window {client}");
                Assert.False(r.Intersects(InWindow(seek)), $"{label.Text} overlaps the seek bar");
            }
            Assert.True(seek.Bounds.Width >= minSeek, $"seek bar only {seek.Bounds.Width}");
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public async Task UnknownLength_ShowsNoCountdown()
    {
        var (win, vm, player) = await Open("Card", "Classic", 340, 432, T("Stream", TimeSpan.Zero));
        try
        {
            player.Duration = TimeSpan.Zero;
            await PumpFor(100);
            var left = win.FindControl<Grid>("CardFormRoot")!.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Classes.Contains("mini-remaining"));
            Assert.False(left.IsVisible);
            player.Duration = TimeSpan.FromMinutes(2);
            await PumpFor(50);
            Assert.True(left.IsVisible);
        }
        finally { win.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("Pill", "Pill", 360.0, 140.0)]
    [InlineData("Sleeve", "Sleeve", 340.0, 372.0)]
    [InlineData("Bar", "Classic", 420.0, 188.0)]
    public async Task Favourite_FollowsTheTrack_BothWays(string prefix, string style, double w, double h)
    {
        var track = T("Fav");
        var (win, vm, player) = await Open(prefix, style, w, h, track);
        try
        {
            var heart = win.FindControl<Button>(prefix + "HeartButton")!.GetVisualDescendants().OfType<HeartIcon>().Single();
            Assert.False(heart.IsFavorite);
            win.FindControl<Button>(prefix + "HeartButton")!.Command!.Execute(null);
            await PumpFor(50);
            Assert.True(track.IsFavorite);
            Assert.True(heart.IsFavorite);
            // Toggled from the main window (same track object): the mini heart follows.
            player.ToggleCurrentTrackFavoriteCommand.Execute(null);
            await PumpFor(50);
            Assert.False(track.IsFavorite);
            Assert.False(heart.IsFavorite);
        }
        finally { win.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("Bar", "Classic", 420.0, 188.0)]
    [InlineData("Lyrics", "Classic", 640.0, 412.0)]
    [InlineData("Pill", "Pill", 360.0, 140.0)]
    [InlineData("Sleeve", "Sleeve", 340.0, 372.0)]
    public async Task VolumeRow_SwapsIntoTheSeekSlot_AndHandsBack(string prefix, string style, double w, double h)
    {
        var saved = MiniPlayerViewModel.VolumeAutoHide;
        MiniPlayerViewModel.VolumeAutoHide = TimeSpan.FromMilliseconds(400);
        var (win, vm, player) = await Open(prefix, style, w, h);
        try
        {
            var seek = win.FindControl<Slider>(prefix + "SeekSlider")!;
            var volume = win.FindControl<Slider>(prefix + "VolumeSlider")!;
            var heightBefore = win.Height;
            Assert.False(volume.IsEffectivelyVisible);

            vm.ToggleVolumeCommand.Execute(null);
            await PumpFor(60);
            Assert.True(volume.IsEffectivelyVisible);
            Assert.False(seek.IsEffectivelyVisible);
            Assert.Equal(heightBefore, win.Height, 1); // no strip docked under the card
            Assert.Equal(MiniDrawer.None, vm.Drawer);
            // The seek bar's twin: same classes, same height, same slot.
            Assert.Contains("mini-slider", volume.Classes);
            Assert.Equal(seek.Bounds.Height, volume.Bounds.Height, 1);
            Assert.Same(seek.GetVisualParent()!.GetVisualParent(), volume.GetVisualParent()!.GetVisualParent());
            Assert.Equal(player.Volume, volume.Value, 1);

            // A change keeps it up; idle hands back to the seek bar and stops the countdown.
            await PumpFor(250);
            player.Volume = 40;
            await PumpFor(250);
            Assert.True(vm.IsVolumeShown);
            await PumpFor(400);
            Assert.False(vm.IsVolumeShown);
            Assert.False(vm.IsVolumeHideTimerRunning);
            Assert.True(seek.IsEffectivelyVisible);
        }
        finally
        {
            MiniPlayerViewModel.VolumeAutoHide = saved;
            win.Close();
        }
    }

    [AvaloniaFact]
    public async Task FullArtwork_KeepsItsOwnVolumeBar_AndHasNoVolumeItem()
    {
        var (win, vm, player) = await Open("Large", "Classic", 340, 520);
        try
        {
            Assert.False(vm.ShowVolumeMenuItem);
            Assert.True(win.FindControl<Slider>("LargeVolumeSlider")!.IsEffectivelyVisible);
            Assert.True(win.FindControl<Slider>("LargeSeekSlider")!.IsEffectivelyVisible);
            vm.ToggleVolumeCommand.Execute(null);
            Assert.False(vm.IsVolumeShown);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public async Task HiddenSleeve_DoesNotSpinItsDisc_OrRunItsBars()
    {
        var (win, vm, player) = await Open("Card", "Classic", 340, 432);
        try
        {
            Assert.True(player.IsPlaying);
            var disc = win.FindControl<MediaArtwork>("SleeveDisc")!;
            var bars = win.FindControl<EqVisualizer>("SleeveEq")!;
            Assert.False(disc.IsSpinning, "the hidden Sleeve disc kept the frame clock busy");
            Assert.False(bars.IsPlaying);

            vm.SetDesignCommand.Execute("Sleeve");
            await PumpFor(700);
            Assert.True(disc.IsSpinning);
            Assert.True(bars.IsPlaying);

            vm.SetDesignCommand.Execute("Pill");
            await PumpFor(700);
            Assert.False(disc.IsSpinning);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public async Task DesignCards_UseTheThemeInk_AndClassicGlassStaysWhite()
    {
        var (win, vm, player) = await Open("Pill", "Pill", 360, 140);
        try
        {
            var pill = win.FindControl<Grid>("PillFormRoot")!;
            var bar = win.FindControl<Grid>("BarFormRoot")!;
            Assert.True(pill.TryFindResource("MiniGlyph", win.ActualThemeVariant, out var pillInk));
            Assert.True(bar.TryFindResource("MiniGlyph", win.ActualThemeVariant, out var barInk));
            Assert.True(win.TryFindResource("SystemControlForegroundBaseMediumHighBrush", win.ActualThemeVariant, out var theme));
            Assert.Same(theme, pillInk);
            Assert.Equal(Color.Parse("#D9FFFFFF"), ((ISolidColorBrush)barInk!).Color);
            // The unplayed seek track reads the palette, not a fixed white wash.
            Assert.True(pill.TryFindResource("MiniDrawerTrack", win.ActualThemeVariant, out var track));
            Assert.NotEqual(Color.Parse("#50FFFFFF"), ((ISolidColorBrush)track!).Color);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void Dispose_LetsGoOfTheSharedPlayer()
    {
        var vm = CreateVm(out var player, out _);
        player.ReplaceQueueAndPlay(new List<Track> { T("A"), T("B"), T("C") }, 0);
        vm.ToggleQueueDrawerCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var rows = vm.QueuePreview.Count;
        vm.ToggleVolumeCommand.Execute(null);
        Assert.True(vm.IsVolumeHideTimerRunning);

        vm.Dispose();
        Assert.False(vm.IsVolumeHideTimerRunning);
        player.AddNext(T("D"), announce: false);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(rows, vm.QueuePreview.Count); // a closed player's queue no longer rebuilds
        vm.Settings.IsMiniStyleSleeve = true;
        Assert.NotEqual(MiniPlayerForm.Sleeve, vm.Form); // nor follows the style picker
    }

    /// <summary>
    /// Position ticks: the seek row must not move while the clock runs (tabular digits keep the
    /// labels' width), and only the form on screen may listen. Measured in one run: the old
    /// arrangement (every form root bound to the view model) against the new one (hidden forms
    /// unbound, SetFormBound), each minus the view-model-only cost of the same ticks, so the
    /// numbers are what the mini player itself adds per tick. Pinned: the bound-only-visible
    /// cost stays well under the all-bound one.
    /// </summary>
    [AvaloniaFact]
    public async Task PositionTicks_TouchOnlyTheVisibleForm_AndLeaveTheSeekRowStill()
    {
        var (win, vm, player) = await Open("Card", "Classic", 340, 432, T("Clock", TimeSpan.FromMinutes(9)));
        try
        {
            var seek = win.FindControl<Slider>("CardSeekSlider")!;
            var root = win.FindControl<Grid>("CardFormRoot")!;
            var card = win.FormRoots[(int)MiniPlayerForm.Card];
            Assert.Same(root, card);
            const int ticks = 400;
            var tick = 0;

            (double Us, double Bytes) Run(bool layout)
            {
                // Warm the paths first, then measure.
                for (var w = 0; w < 50; w++) Step(layout);
                GC.Collect();
                var bytes = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < ticks; i++) Step(layout);
                sw.Stop();
                return (sw.Elapsed.TotalMilliseconds / ticks * 1000,
                        (GC.GetAllocatedBytesForCurrentThread() - bytes) / (double)ticks);
            }
            void Step(bool layout)
            {
                var pos = TimeSpan.FromMilliseconds(tick++ * 250 % 500_000);
                player.Position = pos;
                player.PositionText = PlayerViewModel.FormatTime(pos);
                player.RemainingTimeText = PlayerViewModel.FormatTime(player.Duration - pos);
                player.PositionFraction = pos / player.Duration;
                if (layout) win.UpdateLayout();
            }

            // The view model alone (window unbound entirely): the player's own share.
            foreach (var r in win.FormRoots) r.DataContext = null;
            var vmOnly = Run(layout: true);
            // Before: every form bound, as the window used to be.
            foreach (var r in win.FormRoots) r.ClearValue(StyledElement.DataContextProperty);
            var allBound = Run(layout: true);
            // After: only the visible form bound (what the window does now).
            foreach (var r in win.FormRoots) if (!ReferenceEquals(r, card)) r.DataContext = null;
            var before = seek.Bounds;
            var visibleOnly = Run(layout: true);
            Assert.Equal(before.Width, seek.Bounds.Width, 3);
            Assert.True(root.IsMeasureValid);

            _out.WriteLine($"position tick, view model only: {vmOnly.Us:0} µs, {vmOnly.Bytes:0} B");
            _out.WriteLine($"position tick, all forms bound (before): {allBound.Us:0} µs, {allBound.Bytes:0} B; mini share {allBound.Us - vmOnly.Us:0} µs, {allBound.Bytes - vmOnly.Bytes:0} B");
            _out.WriteLine($"position tick, visible form only (after): {visibleOnly.Us:0} µs, {visibleOnly.Bytes:0} B; mini share {visibleOnly.Us - vmOnly.Us:0} µs, {visibleOnly.Bytes - vmOnly.Bytes:0} B");
            var beforeShare = allBound.Bytes - vmOnly.Bytes;
            var afterShare = visibleOnly.Bytes - vmOnly.Bytes;
            Assert.True(afterShare < beforeShare * 0.75, $"visible-only ticks allocate {afterShare:0} B vs {beforeShare:0} B all-bound");

            // And the window really runs that way: after a form change only the shown root is bound.
            win.Width = 420; win.Height = 188;
            await PumpFor(500);
            Assert.Equal(MiniPlayerForm.Bar, vm.Form);
            foreach (var r in win.FormRoots)
                Assert.Equal(ReferenceEquals(r, win.FindControl<Grid>("BarFormRoot")), r.DataContext != null);
        }
        finally { win.Close(); }
    }

    /// <summary>A track change with Card on screen: no hidden form asks for its artwork (their
    /// covers have no source at all), so nothing hidden is decoded.</summary>
    [AvaloniaFact]
    public async Task TrackChange_DecodesNoArtworkForHiddenForms()
    {
        var (win, vm, player) = await Open("Card", "Classic", 340, 432);
        try
        {
            var next = T("With cover");
            next.AlbumArtworkPath = @"C:\covers\with-cover.jpg";
            player.CurrentTrack = next;
            player.CurrentArtPath = next.AlbumArtworkPath; // what a track change with a cover sets
            await PumpFor(200);
            var card = win.FormRoots[(int)MiniPlayerForm.Card];
            foreach (var root in win.FormRoots.Where(r => !ReferenceEquals(r, card)))
            {
                foreach (var img in root.GetVisualDescendants().OfType<CachedImage>())
                    Assert.True(string.IsNullOrEmpty(img.SourcePath), $"{root.Name}: hidden cover still loads {img.SourcePath}");
                foreach (var art in root.GetVisualDescendants().OfType<MediaArtwork>())
                    Assert.True(string.IsNullOrEmpty(art.SourcePath), $"{root.Name}: hidden disc still loads {art.SourcePath}");
            }
            // The shown card does follow it.
            Assert.Contains(card.GetVisualDescendants().OfType<CachedImage>(), i => i.SourcePath == player.CurrentArtPath);
        }
        finally { win.Close(); }
    }

    // ── Quality tag on every form ─────────────────────────────

    [AvaloniaTheory]
    [MemberData(nameof(Forms))]
    public async Task QualityTag_RidesTheArtistLine_OnEveryForm(string prefix, string style, double w, double h)
    {
        var track = T("Lossless song");
        track.Codec = "FLAC";
        var (win, vm, player) = await Open(prefix, style, w, h, track);
        try
        {
            var info = win.FindControl<StackPanel>(prefix + "InfoHost")!;
            var tag = info.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Classes.Contains("mini-quality"));
            var label = tag.Children.OfType<TextBlock>().Single();
            var full = info.Bounds.Width >= MiniPlayerWindow.QualityFullWidth;
            _out.WriteLine($"{prefix}: info column {info.Bounds.Width:0} -> {(full ? "mark + label" : "mark")}");
            Assert.True(tag.IsEffectivelyVisible, $"{prefix}: no quality tag");
            Assert.Equal("Lossless", label.Text);
            Assert.Equal(full, label.IsVisible);
            // Inside the info column, clear of the heart / More.
            Rect InWindow(Control c) => new(c.TranslatePoint(default, win)!.Value, c.Bounds.Size);
            var tagRect = InWindow(tag);
            var infoRect = InWindow(info);
            Assert.True(tagRect.Right <= infoRect.Right + 0.5, $"{prefix}: tag {tagRect} runs past the info column {infoRect}");
            foreach (var name in new[] { "HeartButton", "MoreButton" })
                Assert.False(tagRect.Intersects(InWindow(win.FindControl<Button>(prefix + name)!)), $"{prefix}: tag under the {name}");

            // No track, no tag.
            player.CurrentTrack = null;
            await PumpFor(60);
            Assert.False(tag.IsEffectivelyVisible);
        }
        finally { win.Close(); }
    }

    // ── Interface Size, RTL text, narrow card with the menu open ─────────────

    /// <summary>The controls of one form that must never clip or overlap.</summary>
    private static List<(string Name, Rect Rect)> LayoutParts(MiniPlayerWindow win, string prefix)
    {
        Rect InWindow(Control c) => new(c.TranslatePoint(default, win)!.Value, c.Bounds.Size);
        var parts = new List<(string, Rect)>();
        foreach (var name in new[] { "HeartButton", "MoreButton", "ShuffleButton", "PrevButton", "PlayButton", "NextButton", "RepeatButton" })
            if (win.FindControl<Button>(prefix + name) is { IsEffectivelyVisible: true } b)
                parts.Add((name, InWindow(b)));
        var seek = win.FindControl<Slider>(prefix + "SeekSlider")!;
        parts.Add(("Seek", InWindow(seek)));
        foreach (var t in ((Grid)seek.GetVisualParent()!).Children.OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
            parts.Add((t.Classes.Contains("mini-elapsed") ? "Elapsed" : "Remaining", InWindow(t)));
        parts.Add(("Info", InWindow(win.FindControl<StackPanel>(prefix + "InfoHost")!)));
        return parts;
    }

    private static void AssertNoClipOrOverlap(MiniPlayerWindow win, string prefix, string context)
    {
        var client = new Rect(win.ClientSize).Inflate(0.5);
        var parts = LayoutParts(win, prefix);
        foreach (var (name, r) in parts)
            Assert.True(client.Contains(r), $"{context}: {name} {r} outside the window {client}");
        for (var i = 0; i < parts.Count; i++)
            for (var j = i + 1; j < parts.Count; j++)
            {
                var overlap = parts[i].Rect.Intersect(parts[j].Rect);
                Assert.True(overlap.Width * overlap.Height < 1,
                    $"{context}: {parts[i].Name} {parts[i].Rect} overlaps {parts[j].Name} {parts[j].Rect}");
            }
    }

    public static IEnumerable<object[]> FormsAtScales() =>
        Forms().SelectMany(f => new[] { 0.6, 1.0, 1.5 }.Select(scale => f.Append(scale).ToArray()));

    /// <summary>Interface Size (GitHub #145) leaves this window at 100% on purpose (its code sizes
    /// the window from its content in the same DIPs, UiScale.Exclude): at 60%, 100% and 150% the
    /// mini player lays out the same, with nothing clipped or overlapping.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(FormsAtScales))]
    public async Task InterfaceSize_LeavesEveryFormWhole(string prefix, string style, double w, double h, double scale)
    {
        UiScale.Install();
        var saved = UiScale.Factor;
        UiScale.SetFactor(scale);
        try
        {
            var (win, vm, player) = await Open(prefix, style, w, h);
            try
            {
                Assert.True(UiScale.GetExclude(win));
                var applied = UiScale.GetApplied(win);
                Assert.True(applied == 0 || Math.Abs(applied - 1) < 0.001, $"the mini player was scaled to {applied}");
                AssertNoClipOrOverlap(win, prefix, $"{prefix} at {scale:P0}");
            }
            finally { win.Close(); }
        }
        finally { UiScale.SetFactor(saved); }
    }

    [AvaloniaTheory]
    [InlineData("Bar", "Classic", 420.0, 188.0)]
    [InlineData("Card", "Classic", 340.0, 432.0)]
    [InlineData("Pill", "Pill", 360.0, 140.0)]
    [InlineData("Sleeve", "Sleeve", 340.0, 372.0)]
    public async Task RightToLeftTitles_StayInTheirColumn_AndTrimWithAnEllipsis(string prefix, string style, double w, double h)
    {
        var track = T("placeholder");
        track.Title = "ای کاش می‌دانستی که چقدر دلم برایت تنگ شده است در این شب‌های بلند زمستانی";
        track.Artist = "محمدرضا شجریان و گروه آوای شیدا و همراهان همیشگی";
        var titleScroll = MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled;
        var artistScroll = MarqueeTextBlock.GlobalMiniPlayerAlbumScrollEnabled;
        MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled = false;
        MarqueeTextBlock.GlobalMiniPlayerAlbumScrollEnabled = false;
        try
        {
            var (win, vm, player) = await Open(prefix, style, w, h, track);
            try
            {
                await PumpFor(300);
                Rect InWindow(Control c) => new(c.TranslatePoint(default, win)!.Value, c.Bounds.Size);
                var info = win.FindControl<StackPanel>(prefix + "InfoHost")!;
                var infoRect = InWindow(info);
                foreach (var line in info.GetVisualDescendants().OfType<MarqueeTextBlock>())
                {
                    var r = InWindow(line);
                    Assert.True(r.X >= infoRect.X - 0.5 && r.Right <= infoRect.Right + 0.5,
                        $"{prefix}: line at {r} leaves its column {infoRect}");
                    var text = line.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == line.Text);
                    Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, text.TextTrimming);
                    Assert.True(text.Bounds.Width <= info.Bounds.Width + 0.5);
                }
                AssertNoClipOrOverlap(win, prefix, $"{prefix} RTL");
            }
            finally { win.Close(); }
        }
        finally
        {
            MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled = titleScroll;
            MarqueeTextBlock.GlobalMiniPlayerAlbumScrollEnabled = artistScroll;
        }
    }

    /// <summary>The owner's clipped "…:09": at the narrowest Bar, with the "…" menu open, both
    /// time labels sit wholly inside the card's rounded outline (the menu may cover them; the
    /// card edge never cuts them).</summary>
    [AvaloniaFact]
    public async Task NarrowestBar_WithTheMenuOpen_TimeLabelsStayInsideTheCard()
    {
        var (win, vm, player) = await Open("Bar", "Classic", 240, 140, T("Clock", TimeSpan.FromMinutes(61)));
        try
        {
            var more = win.FindControl<Button>("BarMoreButton")!;
            more.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await PumpFor(300);
            Assert.True(win.FindControl<Avalonia.Controls.Primitives.Popup>("MorePopup")!.IsOpen);

            var root = win.FindControl<Border>("RootBorder")!;
            var card = new Rect(root.TranslatePoint(default, win)!.Value, root.Bounds.Size).Deflate(root.BorderThickness.Left);
            var radius = root.CornerRadius.TopLeft;
            bool InsideRounded(Point p)
            {
                if (!card.Contains(p)) return false;
                var cx = Math.Clamp(p.X, card.Left + radius, card.Right - radius);
                var cy = Math.Clamp(p.Y, card.Top + radius, card.Bottom - radius);
                return (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy) <= radius * radius;
            }
            var seek = win.FindControl<Slider>("BarSeekSlider")!;
            foreach (var label in ((Grid)seek.GetVisualParent()!).Children.OfType<TextBlock>())
            {
                var r = new Rect(label.TranslatePoint(default, win)!.Value, label.Bounds.Size);
                _out.WriteLine($"{label.Text}: {r} in card {card} r={radius}");
                Assert.True(label.Bounds.Width + 0.5 >= label.DesiredSize.Width, $"{label.Text} squeezed");
                foreach (var corner in new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight })
                    Assert.True(InsideRounded(corner), $"{label.Text} corner {corner} cut by the card edge");
            }
        }
        finally { win.Close(); }
    }

    /// <summary>A persistence whose settings calls do their (simulated) file work synchronously
    /// before returning, as PersistenceService does up to its first real await: parse, DPAPI,
    /// backup copy, ACL and serialize. Whatever thread calls it pays for that work.</summary>
    private sealed class SyncWorkPersistence : TestPersistenceService
    {
        public const int WorkMs = 60;
        public int UiThreadCalls;
        private readonly Thread _ui = Thread.CurrentThread;
        public override Task<AppSettings> LoadSettingsAsync() { Work(); return Task.FromResult(new AppSettings()); }
        public override Task SaveSettingsAsync(AppSettings settings) { Work(); return Task.CompletedTask; }
        private void Work()
        {
            if (Thread.CurrentThread == _ui) Interlocked.Increment(ref UiThreadCalls);
            Thread.Sleep(WorkMs);
        }
    }

    /// <summary>
    /// Owner's log: "[MiniMenu] CloseMorePopup … Popup.Closed", then UI.Stall blockedMs=274 and
    /// Slow.SettingsSave ms=289. Picking a design in the "…" menu saves settings at once
    /// (OnMiniPlayerStyleChanged → SaveAsync), and SaveAsync awaited the persistence calls from
    /// the UI thread, so their synchronous part (and every continuation without
    /// ConfigureAwait(false)) ran there. The file work now runs on the thread pool: a save
    /// whose persistence spends 3 × 60 ms working holds the UI thread for a few ms only.
    /// The loop never yields to the test runner, so every continuation the save posts back
    /// to the dispatcher is timed in RunJobs. Also prints a real PersistenceService's numbers.
    /// </summary>
    [AvaloniaFact]
    public void SettingsSave_RunsItsFileWorkOffTheUiThread()
    {
        var slow = new SyncWorkPersistence();
        var settings = new SettingsViewModel(slow, new FakeLibraryService(), new NoOpPlayHistoryService());
        Drain(() => settings.LoadAsync());
        Assert.True(settings.IsSettingsLoaded);
        slow.UiThreadCalls = 0;
        var samples = new List<double>();
        // The first two saves of a process also pay .NET's reflection warm-up for the
        // ~300-property merge (invoke stubs are built on a property's second use).
        foreach (var style in new[] { "Pill", "Sleeve", "Classic", "Pill", "Sleeve", "Classic" })
        {
            settings.MiniPlayerStyle = style;          // what the menu's design segment does
            samples.Add(Drain(() => settings.SaveAsync()));
        }
        _out.WriteLine($"save with {SyncWorkPersistence.WorkMs} ms synchronous file work per call, UI-thread ms: {string.Join(", ", samples.Select(s => s.ToString("0.0")))}");
        Assert.Equal(0, slow.UiThreadCalls);
        // Before the fix each save held the UI for the three calls' work (203-307 ms measured);
        // the bound leaves room for unrelated dispatcher jobs the drain also runs in-suite.
        Assert.True(samples.Skip(2).Max() < SyncWorkPersistence.WorkMs * 2,
            $"a save held the UI thread {samples.Skip(2).Max():0.0} ms");

        // The real service on a temp folder, for the record (no assertion: disk timing varies).
        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "mini-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var persistence = new PersistenceService(dir);
            Drain(() => persistence.SaveSettingsAsync(new AppSettings()));
            var real = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
            Drain(() => real.LoadAsync());
            var realSamples = new List<double>();
            foreach (var style in new[] { "Pill", "Sleeve", "Classic", "Pill", "Sleeve", "Classic" })
            {
                real.MiniPlayerStyle = style;
                realSamples.Add(Drain(() => real.SaveAsync()));
            }
            _out.WriteLine($"real PersistenceService save, UI-thread ms: {string.Join(", ", realSamples.Select(s => s.ToString("0.0")))}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        static double Drain(Func<Task> start)
        {
            var ui = Stopwatch.StartNew();
            var task = start();                       // the synchronous prefix runs on this thread
            double total = ui.Elapsed.TotalMilliseconds;
            while (!task.IsCompleted)
            {
                var s = Stopwatch.StartNew();
                Dispatcher.UIThread.RunJobs();
                total += s.Elapsed.TotalMilliseconds;
                Thread.Sleep(1);
                if (ui.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("save never finished");
            }
            task.GetAwaiter().GetResult();
            return total;
        }
    }

    /// <summary>Real-Skia renders of the three styles for eyeballing: NOCTIS_TEST_SKIA=1 and
    /// NOCTIS_MINI_UNIFIED_SHOTS_DIR naming the folder.</summary>
    [AvaloniaFact]
    public async Task Shots_ThreeStyles()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_MINI_UNIFIED_SHOTS_DIR");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        AddToPlaylistDialogTests.EnsureAppStyles();
        foreach (var (prefix, style, w, h, volume) in new[]
                 {
                     ("Bar", "Classic", 420.0, 188.0, false), ("Card", "Classic", 340.0, 432.0, false),
                     ("Large", "Classic", 340.0, 520.0, false), ("Pill", "Pill", 360.0, 140.0, false),
                     ("Sleeve", "Sleeve", 340.0, 372.0, false), ("Bar", "Classic", 420.0, 188.0, true),
                     ("Pill", "Pill", 360.0, 140.0, true), ("Sleeve", "Sleeve", 340.0, 372.0, true),
                 })
        {
            var track = T("Midnight City (Extended Mix)", TimeSpan.FromSeconds(243));
            track.IsExplicit = true;
            var (win, vm, player) = await Open(prefix, style, w, h, track);
            try
            {
                win.Background = new SolidColorBrush(Color.Parse("#3A3F47"));
                player.ToggleShuffleCommand.Execute(null);
                player.ToggleCurrentTrackFavoriteCommand.Execute(null);
                player.PositionText = "1:33";
                player.RemainingTimeText = "2:30";
                player.PositionFraction = 0.38;
                if (volume) vm.ToggleVolumeCommand.Execute(null);
                await PumpFor(500);
                var path = Path.Combine(dir, $"{prefix.ToLowerInvariant()}{(volume ? "-volume" : "")}.png");
                win.CaptureRenderedFrame()!.Save(path);
                _out.WriteLine(path);
            }
            finally { win.Close(); }
        }
    }
}
