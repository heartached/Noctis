using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>The redesigned Now Playing page and the lyrics page carry-overs.</summary>
public class MobileNowPlayingPageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class FakeVolume : IVolumeControl
    {
        public double Level { get; set; } = 0.5;
        public event EventHandler? Changed;
        public void KeyPressed(double level) { Level = level; Changed?.Invoke(this, EventArgs.Empty); }
    }

    private sealed class FakeOutputs : IOutputSwitcher
    {
        public int Shown { get; private set; }
        public bool Show() { Shown++; return true; }
    }

    [Fact]
    public void ElapsedAndRemaining_FollowThePosition()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });   // 90 s
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);

        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(30));

        Assert.Equal("00:30", rig.Shell.Player.ElapsedText);
        Assert.Equal("-01:00", rig.Shell.Player.RemainingText);
        Assert.Equal("1:02:03", NowPlayingViewModel.FormatTime(new TimeSpan(1, 2, 3)));
    }

    [Fact]
    public void VolumeSlider_DrivesTheDeviceVolume_AndFollowsTheHardwareKeys()
    {
        var player = new FakeAudioPlayer();
        var volume = new FakeVolume();
        var vm = new NowPlayingViewModel(player, new FakeLibraryService(), new PersistenceService(_root), marshal: a => a(), volume: volume);
        Assert.True(vm.HasVolumeControl);
        Assert.Equal(0.5, vm.VolumeLevel);

        vm.VolumeLevel = 0.8;
        Assert.Equal(0.8, volume.Level);

        volume.KeyPressed(0.2);
        Assert.Equal(0.2, vm.VolumeLevel);
        Assert.False(new NowPlayingViewModel(player, new FakeLibraryService(), new PersistenceService(_root), marshal: a => a()).HasVolumeControl);
    }

    [Fact]
    public async Task HeartMoreArtistAndOutput_ActOnTheCurrentTrack()
    {
        var t = MobileFixtures.Song("Tone", artist: "Band");
        using var rig = MobileFixtures.MakeRig(new[] { t });
        var outputs = new FakeOutputs();
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics) { Outputs = outputs };
        shell.Player.PlayTracks(new[] { t }, 0);
        shell.OpenNowPlayingCommand.Execute(null);

        await shell.ToggleCurrentFavouriteCommand.ExecuteAsync(null);
        Assert.True(t.IsFavorite);

        shell.OpenCurrentTrackSheetCommand.Execute(null);
        Assert.Same(t, shell.Sheet!.Track);
        Assert.True(shell.IsNowPlayingOpen);                        // the sheet opens over the player
        shell.CloseSheet();

        shell.ShowOutputCommand.Execute(null);
        Assert.Equal(1, outputs.Shown);

        // The artist line pops Go to Album / Go to Artist (Apple's); Go to Artist opens the artist.
        shell.OpenCurrentLinksCommand.Execute(null);
        Assert.True(shell.IsNowPlayingOpen);
        Assert.Same(t, Assert.IsType<TrackLinksMenuViewModel>(shell.PopMenu).Track);
        shell.LinksMenu!.GoToArtistCommand.Execute(null);
        Assert.False(shell.IsNowPlayingOpen);
        Assert.False(shell.IsMenuOpen);
        Assert.Equal("Band", shell.CurrentPage!.Title);
    }

    [AvaloniaFact]
    public void NowPlaying_HasTheMockupsControls_AndNoVideoChip()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        window.UpdateLayout();

        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;
        foreach (var name in new[] { "Backdrop", "TitleText", "GrabberButton", "NowPlayingMore", "SeekBar", "PlayPauseButton", "LyricsButton", "OutputButton", "QueueButton" })
            Assert.True(MobileFixtures.Named<Control>(page, name).IsEffectivelyVisible, name);
        Assert.Equal("-01:30", MobileFixtures.Named<TextBlock>(page, "RemainingText").Text);
        Assert.False(MobileFixtures.Named<Control>(page, "VolumeRow").IsVisible);   // no volume control in tests
        Assert.DoesNotContain(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Video") == true);

        // Apple's page (owner 10-13): no heart by the title (it is in the ⋯ menu), one transport
        // row with Play/Pause a little larger. Beta 2: Shuffle and Repeat at that row's ends
        // (testers could not find them on the Queue page), on the same row as the transport.
        Assert.DoesNotContain(page.GetVisualDescendants().OfType<Control>(), c => c.Name == "FavouriteButton");
        var shuffle = MobileFixtures.Named<Button>(page, "ShuffleButton");
        var repeat = MobileFixtures.Named<Button>(page, "RepeatButton");
        Assert.True(shuffle.IsEffectivelyVisible && repeat.IsEffectivelyVisible);
        var play = MobileFixtures.Named<Button>(page, "PlayPauseButton");
        var playY = play.TranslatePoint(new Point(0, play.Bounds.Height / 2), page)!.Value.Y;
        Assert.Equal(playY, shuffle.TranslatePoint(new Point(0, shuffle.Bounds.Height / 2), page)!.Value.Y, 0);
        Assert.False(shuffle.Classes.Contains("on"));
        rig.Shell.Player.ToggleShuffleCommand.Execute(null);
        Assert.True(shuffle.Classes.Contains("on"));
        rig.Shell.Player.CycleRepeatCommand.Execute(null);
        Assert.True(repeat.Classes.Contains("on"));
        rig.Shell.Player.CycleRepeatCommand.Execute(null);
        Assert.True(MobileFixtures.Named<PathIcon>(page, "RepeatOneGlyph").IsVisible);
        var transport = new[] { "PreviousButton", "PlayPauseButton", "NextButton" }
            .Select(n => MobileFixtures.Named<Button>(page, n)).ToList();
        Assert.Single(transport.Select(b => b.TranslatePoint(default, page)!.Value.Y).Distinct());
        double Glyph(Button b) => b.GetVisualDescendants().OfType<TransportGlyph>().Single().Width;
        Assert.True(Glyph(transport[1]) > Glyph(transport[0]) && Glyph(transport[0]) == Glyph(transport[2]));
        // The cover sits back while paused.
        var frame = MobileFixtures.Named<Border>(page, "ArtworkFrame");
        Assert.False(frame.Classes.Contains("paused"));
        rig.Shell.Player.TogglePlayPauseCommand.Execute(null);
        Assert.True(frame.Classes.Contains("paused"));
        window.Close();
    }

    /// <summary>
    /// The bottom row and the transport follow the desktop player bar (owner 10-19: "the same as
    /// the Windows app ... same sizes"): Lyrics and Queue draw the bar's Lyrics and Queue glyphs
    /// in one box, the middle button its Volume speaker at the bar's 16 of 18, and Play/Pause is
    /// the bar's 20 to the skips' 18.
    /// </summary>
    [AvaloniaFact]
    public void TheBottomRowAndTransport_UseTheDesktopBarsGlyphsAndProportions()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        window.UpdateLayout();
        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;
        PathIcon Glyph(string button) => MobileFixtures.Named<Button>(page, button).GetVisualDescendants().OfType<PathIcon>().First(i => i.IsVisible);
        Geometry Key(string key) => (Geometry)page.FindResource(key)!;

        Assert.Same(Key("PhLyrics"), Glyph("LyricsButton").Data);
        Assert.Same(Key("PhVolumeHigh"), Glyph("OutputButton").Data);
        Assert.Same(Key("PhQueue"), Glyph("QueueButton").Data);
        var box = Glyph("LyricsButton").Bounds.Width;
        Assert.InRange(box, 22, 26);
        Assert.Equal(box, Glyph("QueueButton").Bounds.Width);
        Assert.Equal(box * 16 / 18, Glyph("OutputButton").Bounds.Width, 1.0);   // layout rounding
        var row = new[] { "LyricsButton", "OutputButton", "QueueButton" }
            .Select(n => MobileFixtures.Named<Button>(page, n)).ToList();
        Assert.Single(row.Select(b => Math.Round(b.TranslatePoint(new Point(0, b.Bounds.Height / 2), page)!.Value.Y, 1)).Distinct());

        // The transport draws Apple's press motion (TransportGlyph) in the same boxes.
        TransportGlyph Transport(string button) => MobileFixtures.Named<Button>(page, button).GetVisualDescendants().OfType<TransportGlyph>().Single();
        var skip = Transport("PreviousButton").Bounds.Width;
        Assert.Equal(skip, Transport("NextButton").Bounds.Width);
        Assert.Equal(skip * 20 / 18, Transport("PlayPauseButton").Bounds.Width, 1.0);
        window.Close();
    }

    /// <summary>
    /// The wash under the player (owner 10-14, after Apple Music's): the cover blurred to colour
    /// and stretched over the whole screen, a vertical shade light where the cover sits and
    /// darkest at the foot (so white text reads on any cover: a white cover under the transport
    /// row is at most 0.18 relative luminance, 4.5:1 against white), its stops close enough that
    /// no step is a band, and a tile of faint noise over it. Nothing in it animates or binds
    /// per frame; a file without a badge shows no badge.
    /// </summary>
    [AvaloniaFact]
    public void TheWash_IsTheCoverStretched_ShadedDownward_AndDithered()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });   // no codec: no badge
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        window.UpdateLayout();
        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;

        var backdrop = MobileFixtures.Named<Image>(page, "Backdrop");
        Assert.Equal(Avalonia.Media.Stretch.Fill, backdrop.Stretch);
        Assert.Null(backdrop.Transitions);

        var shade = MobileFixtures.Named<Border>(page, "WashShade");
        var gradient = Assert.IsAssignableFrom<Avalonia.Media.ILinearGradientBrush>(shade.Background);
        var stops = gradient.GradientStops.OrderBy(s => s.Offset).ToList();
        Assert.True(stops.Count >= 5, "a long shade needs close stops");
        Assert.All(stops, s => Assert.Equal(0, s.Color.R + s.Color.G + s.Color.B));   // black, only its alpha varies
        for (var i = 1; i < stops.Count; i++)
        {
            Assert.True(stops[i].Color.A >= stops[i - 1].Color.A, "the shade only darkens downward");
            Assert.True(stops[i].Color.A - stops[i - 1].Color.A <= 40, $"stop {i} steps {stops[i].Color.A - stops[i - 1].Color.A}/255 at once");
        }
        var top = stops[0].Color.A / 255.0;
        var bottom = stops[^1].Color.A / 255.0;
        Assert.InRange(top, 0.25, 0.4);              // the cover's colours stay bright around the cover
        Assert.InRange(bottom, 0.7, 0.85);           // Apple's reference: a third of the brightness at the foot
        // Under the transport row (about 72% down) a white cover must leave white text 4.5:1.
        var transport = MobileFixtures.Named<Button>(page, "PlayPauseButton").TranslatePoint(default, page)!.Value.Y / page.Bounds.Height;
        var alphaThere = AlphaAt(stops, transport);
        var luminance = Math.Pow(1 - alphaThere, 2.2);   // sRGB white dimmed by the shade, linearised
        Assert.True((1.05) / (luminance + 0.05) >= 4.5, $"white text over a white cover at {transport:0.00}: shade {alphaThere:0.00}");

        var dither = MobileFixtures.Named<Border>(page, "WashDither");
        var tile = Assert.IsAssignableFrom<Avalonia.Media.IImageBrush>(dither.Background);
        Assert.Equal(Avalonia.Media.TileMode.Tile, tile.TileMode);
        Assert.Equal(Avalonia.Media.Stretch.Fill, tile.Stretch);   // fills its cell at any density (None left gaps on the phone)
        Assert.Same(NowPlayingPage.Dither, dither.Background);                 // one tile per process

        // The layers lie under the controls, in order: cover wash, shade, dither.
        var panel = Assert.IsType<Panel>(backdrop.Parent);
        Assert.Equal(new Control[] { backdrop, shade, dither }, panel.Children.Take(3));
        Assert.False(backdrop.IsHitTestVisible || shade.IsHitTestVisible || dither.IsHitTestVisible);

        Assert.False(MobileFixtures.Named<Button>(page, "QualityBadge").IsVisible);
        window.Close();
    }

    /// <summary>The dither really draws: over black, a scatter of pixels a few levels up, none
    /// brighter than its amplitude (a tile that renders nothing leaves the shade's bands).</summary>
    [AvaloniaFact]
    public void TheDither_DrawsFaintNoise()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var window = new Window
        {
            Width = 128, Height = 128, Background = Avalonia.Media.Brushes.Black,
            Content = new Border { Background = NowPlayingPage.Dither },
        };
        window.Show();
        var frame = window.CaptureRenderedFrame()!;
        int lit = 0, max = 0, total = 0;
        using (var fb = frame.Lock())
        {
            var row = new byte[fb.RowBytes];
            for (var y = 0; y < 128; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, row.Length);
                for (var x = 0; x < 128; x++, total++)
                {
                    int g = row[x * 4 + 1];
                    if (g > 0) lit++;
                    max = Math.Max(max, g);
                }
            }
        }
        window.Close();
        Assert.True(lit > total / 5, $"{lit} of {total} pixels lit");
        Assert.InRange(max, 2, NowPlayingPage.DitherAmplitude);
    }

    private static double AlphaAt(List<Avalonia.Media.IGradientStop> stops, double offset)
    {
        for (var i = 1; i < stops.Count; i++)
        {
            if (offset > stops[i].Offset) continue;
            var t = (offset - stops[i - 1].Offset) / (stops[i].Offset - stops[i - 1].Offset);
            return (stops[i - 1].Color.A + (stops[i].Color.A - stops[i - 1].Color.A) * t) / 255.0;
        }
        return stops[^1].Color.A / 255.0;
    }

    /// <summary>Scrubbing the timeline: the elapsed and remaining times follow the finger while
    /// it drags (device 10-13: both froze at the drag's start until the finger lifted, so there
    /// was no telling where the release would land), and the release seeks there.</summary>
    [AvaloniaFact]
    public void ScrubbingTheTimeline_MovesTheTimesWithTheFinger_AndSeeksOnRelease()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });   // 90 s
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        MobileFixtures.Land(window);   // the player takes touches once it has risen

        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;
        var seek = MobileFixtures.Named<Avalonia.Controls.Primitives.RangeBase>(page, "SeekBar");
        Point At(double fraction) =>
            seek.TranslatePoint(new Point(seek.Bounds.Width * fraction, seek.Bounds.Height / 2), window)!.Value;

        window.MouseDown(At(0.25), Avalonia.Input.MouseButton.Left);
        window.MouseMove(At(0.5));
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.InRange(seek.Value, 40, 50);
        var held = TimeSpan.FromSeconds(seek.Value);
        Assert.Equal(NowPlayingViewModel.FormatTime(held), MobileFixtures.Named<TextBlock>(page, "ElapsedText").Text);
        Assert.Equal("-" + NowPlayingViewModel.FormatTime(TimeSpan.FromSeconds(90) - held),
            MobileFixtures.Named<TextBlock>(page, "RemainingText").Text);
        Assert.Empty(rig.Player.Seeks);                                  // nothing seeks mid-drag

        window.MouseUp(At(0.5), Avalonia.Input.MouseButton.Left);
        Assert.Equal(seek.Value, Assert.Single(rig.Player.Seeks).TotalSeconds, 3);
        window.Close();
    }

    /// <summary>Apple's bars (owner 10-13): no thumb; held, the capsule grows (a transition on
    /// Expansion, released back to 0), and the volume follows the finger to the device at once,
    /// a tap anywhere setting it there.</summary>
    [AvaloniaFact]
    public void TheVolumeCapsule_GrowsWhileHeld_AndPutsTheVolumeUnderTheFinger()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
        var volume = new FakeVolume();
        var nowPlaying = new NowPlayingViewModel(rig.Player, rig.Library, rig.Persistence, marshal: a => a(), volume: volume);
        var shell = new ShellViewModel(rig.Shell.Library, nowPlaying,
            new LyricsPageViewModel(rig.Player, nowPlaying, new FakeTrackFiles(), rig.Persistence, work => Task.FromResult(work())));
        var window = MobileFixtures.Mount(shell, out var view);
        shell.Player.PlayTracks(rig.Library.TrackList, 0);
        shell.OpenNowPlayingCommand.Execute(null);
        MobileFixtures.Land(window);   // the player takes touches once it has risen

        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;
        var slider = MobileFixtures.Named<CapsuleSlider>(page, "VolumeSlider");
        Point At(double fraction) =>
            slider.TranslatePoint(new Point(slider.Bounds.Width * fraction, slider.Bounds.Height / 2), window)!.Value;

        window.MouseDown(At(0.8), Avalonia.Input.MouseButton.Left);
        Assert.True(slider.IsScrubbing);
        Assert.Equal(1, slider.GetBaseValue(CapsuleSlider.ExpansionProperty).Value);
        Assert.Equal(0.8, volume.Level, 2);
        window.MouseMove(At(0.3));
        Assert.Equal(0.3, volume.Level, 2);
        window.MouseUp(At(0.3), Avalonia.Input.MouseButton.Left);
        Assert.False(slider.IsScrubbing);
        Assert.Equal(0, slider.GetBaseValue(CapsuleSlider.ExpansionProperty).Value);
        Assert.Equal(0.3, shell.Player.VolumeLevel, 2);
        window.Close();
    }

    /// <summary>Review Focus #4: rotating mid-playback keeps the activity, so the page must
    /// fit a 915×412 window as the device has it — volume row shown, status bar on top and
    /// the 3-button navigation bar on the right — with the cover still visible, not squeezed
    /// out by the controls.</summary>
    [AvaloniaFact]
    public void Landscape_NowPlayingFitsTheWindow_WithTheCoverStillShown()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
        var nowPlaying = new NowPlayingViewModel(rig.Player, rig.Library, rig.Persistence, marshal: a => a(), volume: new FakeVolume());
        var shell = new ShellViewModel(rig.Shell.Library, nowPlaying,
            new LyricsPageViewModel(rig.Player, nowPlaying, new FakeTrackFiles(), rig.Persistence, work => Task.FromResult(work())));
        var window = MobileFixtures.Mount(shell, out var view, width: 915, height: 412);
        view.ApplySafeArea(new Thickness(0, 24, 48, 0));
        shell.Player.PlayTracks(rig.Library.TrackList, 0);
        shell.OpenNowPlayingCommand.Execute(null);
        window.UpdateLayout();

        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;
        Assert.True(MobileFixtures.Named<Control>(page, "VolumeRow").IsVisible);
        foreach (var name in new[] { "SeekBar", "PreviousButton", "PlayPauseButton", "NextButton", "NowPlayingMore",
                     "VolumeSlider", "LyricsButton", "OutputButton", "QueueButton" })
        {
            var control = MobileFixtures.Named<Control>(page, name);
            var topLeft = control.TranslatePoint(new Point(0, 0), window)!.Value;
            var bottomRight = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), window)!.Value;
            Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0, $"{name} has no size");
            Assert.True(topLeft.X >= -0.5 && topLeft.Y >= -0.5 && bottomRight.X <= 915.5 && bottomRight.Y <= 412.5,
                $"{name} spans {topLeft}-{bottomRight}");
        }
        var cover = MobileFixtures.Named<Viewbox>(page, "ArtworkBox").Bounds.Height;
        Assert.True(cover >= 96, $"cover is {cover} high");
        window.Close();
    }

    [Fact]
    public async Task MalformedTtml_ShowsItsTextUnsynced_UnderNoSyncedLyrics()
    {
        var files = new FakeTrackFiles();
        files.Sidecars[".ttml"] = "<tt><head><metadata><ttm:title>Meta</ttm:title></metadata></head><body><p begin='bad'>Broken <span>line</p></body>";
        var track = MobileFixtures.Song("Tone");

        var loaded = LyricsLoader.Load(track, files, joinSplitWords: false);
        Assert.Equal(LyricsSource.SidecarUnparsed, loaded.Source);
        Assert.False(loaded.IsSynced);
        Assert.Equal(new[] { "Broken line" }, loaded.Lines.Select(l => l.Text));

        var player = new FakeAudioPlayer();
        var persistence = new PersistenceService(_root);
        var nowPlaying = new NowPlayingViewModel(player, new FakeLibraryService(), persistence, marshal: a => a());
        var lyrics = new LyricsPageViewModel(player, nowPlaying, files, persistence, work => Task.FromResult(work()));
        await lyrics.InitializeAsync();
        nowPlaying.PlayTracks(new[] { track }, 0);

        Assert.True(lyrics.HasLyrics);
        Assert.True(lyrics.IsRawFallback);
        Assert.Equal("No synced lyrics", lyrics.StatusText);
    }

    /// <summary>
    /// The raw fallback runs on sidecar text anyone can drop next to a song. A backtracking
    /// regex over an unclosed &lt;head / &lt;br goes quadratic (40 KB took seconds, a few
    /// hundred KB minutes of a pinned pool thread with the page stuck on "Loading lyrics…").
    /// </summary>
    [Fact]
    public void RawTextFallback_StaysLinear_OnUnclosedTags()
    {
        var hostile = "<tt>" + string.Concat(Enumerable.Repeat("<head <br ", 10_000));   // 100 KB, never closed
        var clock = System.Diagnostics.Stopwatch.StartNew();
        LyricsLoader.RawTextLines(hostile);
        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
    }

    [Fact]
    public void RawTextFallback_BreaksOnPrefixedParagraphs_DropsTheDoctype_AndSkipsHugeFiles()
    {
        Assert.Equal(new[] { "One", "Two" },
            LyricsLoader.RawTextLines("<tt:tt><tt:body><tt:p begin='x'>One</tt:p><tt:p>Two</tt:p></tt:body>"));
        Assert.Equal(new[] { "Words" },
            LyricsLoader.RawTextLines("<!DOCTYPE tt [ <!ENTITY e \"x\"> ]><tt><body><p>Words</p></body></tt>"));

        var files = new FakeTrackFiles();
        files.Sidecars[".ttml"] = "<tt><body><p begin='bad'>" + new string('a', 1_100_000) + "</body>";
        Assert.Equal(LyricsSource.None, LyricsLoader.Load(MobileFixtures.Song("Tone"), files, joinSplitWords: false).Source);
    }

    /// <summary>Apple Music's lyrics show a thin auto-hiding scroll indicator at the right edge
    /// (the owner's reference, 10-20), so the scroller keeps its overlay bar (Auto), not Hidden.</summary>
    [AvaloniaFact]
    public void LyricsPage_ShowsAThinScrollIndicator_AndHidesTheBannerWithoutAFallback()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        rig.Shell.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();

        var page = MobileFixtures.Find<LyricsPage>(view);
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            MobileFixtures.Named<ScrollViewer>(page, "LyricsScroll").VerticalScrollBarVisibility);
        Assert.False(MobileFixtures.Named<TextBlock>(page, "FallbackBanner").IsVisible);
        window.Close();
    }
}
