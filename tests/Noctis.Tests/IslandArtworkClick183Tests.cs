using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #183 (SvenL7): Settings → Player → "Clicking the artwork" picks what a click on
/// the island's artwork opens — the mini player (default, the behaviour before), the lyrics
/// page or full-screen lyrics — and "Lyrics button" can hide the island's lyrics button.
/// </summary>
[Collection("MetadataServiceStatics")]
public class IslandArtworkClick183Tests : IDisposable
{
    private const double IslandBaseWidth = 536;
    private const double IslandLyricsPageWidth = 340;
    private const double ExtraButtonWidth = 36;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private SettingsViewModel CreateSettings() => new(
        new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());

    private static PlayerViewModel MakePlayer() => new(
        new FakeAudioPlayer(), new FakeLibraryService(),
        new TestPersistenceService(), new FakeAnimatedCoverService());

    // ── Defaults + parsing ──

    [AvaloniaFact]
    public void FreshInstall_KeepsTodaysBehaviour()
    {
        var s = new AppSettings();
        Assert.Equal(nameof(IslandArtworkClick.MiniPlayer), s.PlaybackBarArtworkClick);
        Assert.True(s.PlaybackBarShowLyrics);

        var player = MakePlayer();
        Assert.Equal(IslandArtworkClick.MiniPlayer, player.IslandArtworkClickAction);
        Assert.True(player.IslandLyricsButtonVisible);
    }

    [Theory]
    [InlineData("MiniPlayer", IslandArtworkClick.MiniPlayer)]
    [InlineData("LyricsPage", IslandArtworkClick.LyricsPage)]
    [InlineData("fullscreenlyrics", IslandArtworkClick.FullScreenLyrics)]
    [InlineData(" LyricsPage ", IslandArtworkClick.LyricsPage)]
    [InlineData(null, IslandArtworkClick.MiniPlayer)]
    [InlineData("", IslandArtworkClick.MiniPlayer)]
    [InlineData("1", IslandArtworkClick.MiniPlayer)]
    [InlineData("Nonsense", IslandArtworkClick.MiniPlayer)]
    public void Parse_FallsBackToTheMiniPlayer(string? stored, IslandArtworkClick expected)
        => Assert.Equal(expected, IslandArtworkClicks.Parse(stored));

    // ── Routing ──

    [Theory]
    [InlineData(IslandArtworkClick.MiniPlayer, "mini")]
    [InlineData(IslandArtworkClick.LyricsPage, "lyrics")]
    [InlineData(IslandArtworkClick.FullScreenLyrics, "fullscreen")]
    [InlineData((IslandArtworkClick)99, "mini")]
    public void EachChoice_RoutesTheClickToItsAction(IslandArtworkClick action, string expected)
    {
        var calls = new List<string>();
        IslandArtworkClickRouter.Route(action,
            () => calls.Add("mini"),
            () => calls.Add("lyrics"),
            () => calls.Add("fullscreen"));
        Assert.Equal(new[] { expected }, calls);
    }

    [AvaloniaFact]
    public void ArtworkTooltip_AndAccessibleName_SayWhatTheClickOpens()
    {
        var player = MakePlayer();
        var bar = new PlaybackBarView { DataContext = player };
        var win = new Window { Width = 900, Height = 200, Content = bar };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();
            var art = bar.FindControl<Border>("AlbumArtThumb")!;

            Assert.Equal("Mini Player", ToolTip.GetTip(art));
            Assert.Equal("Mini Player", AutomationProperties.GetName(art));

            player.IslandArtworkClick = nameof(IslandArtworkClick.LyricsPage);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Lyrics", ToolTip.GetTip(art));
            Assert.Equal("Lyrics", AutomationProperties.GetName(art));

            player.IslandArtworkClick = nameof(IslandArtworkClick.FullScreenLyrics);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Full Screen Lyrics", ToolTip.GetTip(art));
            Assert.Equal("Full Screen Lyrics", AutomationProperties.GetName(art));
        }
        finally
        {
            win.Close();
        }
    }

    // ── Lyrics button ──

    [AvaloniaFact]
    public void HidingTheLyricsButton_HandsItsWidthBack()
    {
        var player = MakePlayer();
        var bar = new PlaybackBarView { DataContext = player };
        var win = new Window { Width = 900, Height = 200, Content = bar };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();
            var island = bar.FindControl<Border>("IslandBorder")!;
            var lyrics = bar.FindControl<Button>("LyricsButton")!;

            // Stock: lyrics shown, mini player button on (default).
            Assert.True(lyrics.IsEffectivelyVisible);
            Assert.Equal(IslandBaseWidth + ExtraButtonWidth, island.Width);

            player.IslandShowLyricsButton = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(lyrics.IsVisible);
            Assert.Equal(IslandBaseWidth, island.Width);

            player.IslandShowLyricsButton = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(lyrics.IsVisible);
            Assert.Equal(IslandBaseWidth + ExtraButtonWidth, island.Width);
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>On the lyrics page the artwork is hidden with the track box, so the page's
    /// own bar keeps the lyrics button as its way out, whatever the setting.</summary>
    [AvaloniaFact]
    public void LyricsPageBar_KeepsTheLyricsButton()
    {
        var player = MakePlayer();
        player.IsLyricsPageActive = true;
        player.IslandShowLyricsButton = false;
        Assert.True(player.IslandLyricsButtonVisible);

        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = true };
        var win = new Window { Width = 900, Height = 200, Content = bar };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(bar.FindControl<Button>("LyricsButton")!.IsEffectivelyVisible);
            Assert.Equal(IslandLyricsPageWidth + ExtraButtonWidth, bar.FindControl<Border>("IslandBorder")!.Width);
        }
        finally
        {
            win.Close();
        }
    }

    // ── Settings ──

    [AvaloniaFact]
    public void SettingsMirrorOntoThePlayer_AndTheHintShowsWhenLyricsAreUnreachableFromTheIsland()
    {
        var vm = CreateSettings();
        var player = MakePlayer();
        vm.SetPlayer(player);

        Assert.Equal(3, vm.ArtworkClickOptions.Count);
        Assert.Equal(nameof(IslandArtworkClick.MiniPlayer), vm.SelectedArtworkClickOption!.Value);
        Assert.Equal("Mini Player", vm.SelectedArtworkClickOption.Label);
        Assert.False(vm.ShowArtworkLyricsHint);

        vm.PlaybackBarShowLyrics = false;
        Assert.False(player.IslandShowLyricsButton);
        Assert.True(vm.ShowArtworkLyricsHint);

        vm.SelectedArtworkClickOption = vm.ArtworkClickOptions.Single(o => o.Value == nameof(IslandArtworkClick.LyricsPage));
        Assert.Equal(IslandArtworkClick.LyricsPage, player.IslandArtworkClickAction);
        Assert.False(vm.ShowArtworkLyricsHint);

        // A ComboBox nulls its selection while rebuilding items: the choice stays.
        vm.SelectedArtworkClickOption = null;
        Assert.Equal(nameof(IslandArtworkClick.LyricsPage), vm.PlaybackBarArtworkClick);
    }

    [AvaloniaFact]
    public async Task BothSettings_SurviveSaveAndReload_AndResetRestoresTheDefaults()
    {
        var vm = CreateSettings();
        await vm.LoadAsync();
        Assert.Equal(nameof(IslandArtworkClick.MiniPlayer), vm.PlaybackBarArtworkClick);
        Assert.True(vm.PlaybackBarShowLyrics);

        vm.PlaybackBarArtworkClick = nameof(IslandArtworkClick.FullScreenLyrics);
        vm.PlaybackBarShowLyrics = false;
        await vm.SaveAsync();

        var reloaded = CreateSettings();
        await reloaded.LoadAsync();
        Assert.Equal(nameof(IslandArtworkClick.FullScreenLyrics), reloaded.PlaybackBarArtworkClick);
        Assert.False(reloaded.PlaybackBarShowLyrics);
        Assert.Equal(nameof(IslandArtworkClick.FullScreenLyrics), reloaded.GetSettings().PlaybackBarArtworkClick);
        Assert.False(reloaded.GetSettings().PlaybackBarShowLyrics);

        reloaded.ResetSettingsToDefaults(new AppSettings());
        Assert.Equal(nameof(IslandArtworkClick.MiniPlayer), reloaded.PlaybackBarArtworkClick);
        Assert.True(reloaded.PlaybackBarShowLyrics);
        await reloaded.SaveAsync();

        var afterReset = CreateSettings();
        await afterReset.LoadAsync();
        Assert.Equal(nameof(IslandArtworkClick.MiniPlayer), afterReset.PlaybackBarArtworkClick);
        Assert.True(afterReset.PlaybackBarShowLyrics);
    }

    [AvaloniaFact]
    public async Task AGarbageStoredValue_LoadsAsTheMiniPlayer()
    {
        Directory.CreateDirectory(_root);
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings { PlaybackBarArtworkClick = "Bogus" });

        var vm = CreateSettings();
        await vm.LoadAsync();
        Assert.Equal(nameof(IslandArtworkClick.MiniPlayer), vm.PlaybackBarArtworkClick);
        Assert.Equal(nameof(IslandArtworkClick.MiniPlayer), vm.SelectedArtworkClickOption!.Value);
    }
}
