using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
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

        shell.OpenCurrentArtistCommand.Execute(null);
        Assert.False(shell.IsNowPlayingOpen);
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
        foreach (var name in new[] { "Backdrop", "TitleText", "FavouriteButton", "NowPlayingMore", "SeekBar", "PlayPauseButton", "LyricsButton", "OutputButton", "QueueButton" })
            Assert.True(MobileFixtures.Named<Control>(page, name).IsEffectivelyVisible, name);
        Assert.Equal("-01:30", MobileFixtures.Named<TextBlock>(page, "RemainingText").Text);
        Assert.False(MobileFixtures.Named<Control>(page, "VolumeRow").IsVisible);   // no volume control in tests
        Assert.DoesNotContain(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Video") == true);
        window.Close();
    }

    /// <summary>Review Focus #4: rotating mid-playback keeps the activity, so the portrait
    /// layout must still fit a 915×412 window.</summary>
    [AvaloniaFact]
    public void Landscape_NowPlayingFitsTheWindow()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
        var window = MobileFixtures.Mount(rig.Shell, out var view, width: 915, height: 412);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        window.UpdateLayout();

        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;
        foreach (var name in new[] { "PlayPauseButton", "QueueButton", "SeekBar" })
        {
            var control = MobileFixtures.Named<Control>(page, name);
            var bottom = control.TranslatePoint(new Point(0, control.Bounds.Height), window)!.Value.Y;
            Assert.True(bottom <= 412.5, $"{name} ends at {bottom}");
        }
        Assert.True(MobileFixtures.Named<Viewbox>(page, "ArtworkBox").Bounds.Height < 206);
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

    [AvaloniaFact]
    public void LyricsPage_HidesItsScrollBar_AndTheBannerWithoutAFallback()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        rig.Shell.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();

        var page = MobileFixtures.Find<LyricsPage>(view);
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            MobileFixtures.Named<ScrollViewer>(page, "LyricsScroll").VerticalScrollBarVisibility);
        Assert.False(MobileFixtures.Named<TextBlock>(page, "FallbackBanner").IsVisible);
        window.Close();
    }
}
