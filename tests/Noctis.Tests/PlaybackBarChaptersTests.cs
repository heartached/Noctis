using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Audiobook chapters (.m4b, Discord Luwi): the player reads the current file's chapters off
/// the UI thread, and the bar's "…" menu lists them under Chapters ▸ — title and start time,
/// the playing one ticked — seeking to a chapter on click. Files without chapters (or with a
/// lone one, nothing to jump between) show no Chapters entry.
/// </summary>
[Collection("ArtistCredit global configuration")]
public class PlaybackBarChaptersTests
{
    public PlaybackBarChaptersTests() => ArtistCredit.ResetToDefaults();

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Audio", name);

    private static (PlayerViewModel Player, FakeAudioPlayer Audio) Player(string filePath)
    {
        var audio = new FakeAudioPlayer();
        var player = new PlayerViewModel(audio, new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        player.Duration = TimeSpan.FromSeconds(3);
        player.CurrentTrack = new Track
        {
            Id = Guid.NewGuid(), Title = "Tiny Audiobook", Artist = "Noctis Narrator", Album = "Tiny Audiobook",
            Duration = TimeSpan.FromSeconds(3), FilePath = filePath,
        };
        // Loaded and paused: a seek reaches the engine (a Stopped track only records it).
        player.State = PlaybackState.Paused;
        return (player, audio);
    }

    [AvaloniaFact]
    public async Task AudiobookTrack_LoadsItsChapters_AndAChapterSeeksToItsStart()
    {
        var (player, audio) = Player(Fixture("aac_chapters_tiny.m4b"));
        await player.ChaptersLoaded;

        Assert.True(player.HasChapters);
        Assert.Equal(new[] { "Opening Credits", "Chapter One — Начало", "Epilogue" }, player.Chapters.Select(c => c.Title));

        player.SeekToChapterCommand.Execute(player.Chapters[2]);
        Assert.InRange(audio.Seeks[^1].TotalMilliseconds, 2499, 2501);
    }

    [AvaloniaFact]
    public async Task NextTrackWithoutChapters_ClearsThem()
    {
        var (player, _) = Player(Fixture("aac_chapters_tiny.m4b"));
        await player.ChaptersLoaded;
        Assert.True(player.HasChapters);

        player.CurrentTrack = new Track { Id = Guid.NewGuid(), Title = "Song", FilePath = Fixture("alac_96k_tiny.m4a") };
        Assert.False(player.HasChapters); // cleared at once, not after the read
        await player.ChaptersLoaded;
        Assert.False(player.HasChapters);
        Assert.Empty(player.Chapters);

        player.CurrentTrack = new Track { Id = Guid.NewGuid(), Title = "Mp3", FilePath = "C:/m/no-such.mp3" };
        await player.ChaptersLoaded;
        Assert.False(player.HasChapters);
    }

    [AvaloniaFact]
    public async Task OptionsMenu_ListsTheChapters_TicksThePlayingOne_AndSeeksOnClick()
    {
        var (player, audio) = Player(Fixture("aac_chapters_tiny.m4b"));
        await player.ChaptersLoaded;
        player.Position = TimeSpan.FromSeconds(1.2);

        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 900, Height = 600, Content = bar };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        var menu = Assert.IsType<MenuFlyout>(bar.FindControl<Button>("OptionsButton")!.Flyout);
        menu.ShowAt(bar);
        Dispatcher.UIThread.RunJobs();
        try
        {
            var chapters = bar.FindControl<MenuItem>("ChaptersMenuItem")!;
            Assert.True(chapters.IsVisible);
            var rows = chapters.Items.OfType<MenuItem>().ToList();
            Assert.Equal(3, rows.Count);
            Assert.Equal("Chapter One — Начало", ((TextBlock)((Grid)rows[1].Header!).Children[0]).Text);
            Assert.Equal("0:01", ((TextBlock)((Grid)rows[1].Header!).Children[1]).Text);
            // The header is a Grid, so screen readers need the row's name spelled out.
            Assert.Equal("Chapter One — Начало, 0:01", AutomationProperties.GetName(rows[1]));
            // Only the chapter under the playhead carries the tick.
            Assert.Null(rows[0].Icon);
            Assert.Contains("mv2-check", Assert.IsType<LineIcon>(rows[1].Icon).Classes);
            Assert.Null(rows[2].Icon);

            rows[0].Command!.Execute(rows[0].CommandParameter);
            Assert.Equal(TimeSpan.Zero, audio.Seeks[^1]);
        }
        finally { menu.Hide(); win.Close(); }
    }

    /// <summary>Discord "Chapter Button in Player Island": an opt-in island button (off by
    /// default) that shows only while the track has chapters and adds its 36px like the other
    /// extras, gone again for a song without chapters.</summary>
    [AvaloniaFact]
    public async Task IslandChaptersButton_IsOptIn_ShowsOnlyWithChapters_AndBudgetsItsWidth()
    {
        Assert.False(new AppSettings().PlaybackBarShowChapters);
        var (player, _) = Player(Fixture("aac_chapters_tiny.m4b"));
        await player.ChaptersLoaded;

        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 1000, Height = 200, Content = bar };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var island = bar.FindControl<Border>("IslandBorder")!;
            var button = bar.FindControl<Button>("ChaptersButton")!;
            var stock = island.Width;
            Assert.False(button.IsVisible);

            player.IslandShowChapters = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(button.IsEffectivelyVisible);
            Assert.Equal(stock + 36, island.Width);
            Assert.Equal("Chapters", ToolTip.GetTip(button));
            Assert.Equal("Chapters", AutomationProperties.GetName(button));

            player.CurrentTrack = new Track { Id = Guid.NewGuid(), Title = "Song", FilePath = "C:/m/no-such.mp3" };
            await player.ChaptersLoaded;
            Dispatcher.UIThread.RunJobs();
            Assert.False(button.IsVisible);
            Assert.Equal(stock, island.Width);
        }
        finally { win.Close(); }
    }

    /// <summary>The island button opens the same chapter list as "…" → Chapters: title and start,
    /// the playing chapter ticked, a click seeks there.</summary>
    [AvaloniaFact]
    public async Task IslandChaptersMenu_ListsTheChapters_TicksThePlayingOne_AndSeeksOnClick()
    {
        var (player, audio) = Player(Fixture("aac_chapters_tiny.m4b"));
        await player.ChaptersLoaded;
        player.Position = TimeSpan.FromSeconds(1.2);
        player.IslandShowChapters = true;

        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 1000, Height = 600, Content = bar };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        var button = bar.FindControl<Button>("ChaptersButton")!;
        var menu = Assert.IsType<MenuFlyout>(button.Flyout);
        menu.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        try
        {
            var rows = menu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(3, rows.Count);
            Assert.True(rows[0].IsAttachedToVisualTree());
            Assert.Equal("Chapter One — Начало, 0:01", AutomationProperties.GetName(rows[1]));
            Assert.Null(rows[0].Icon);
            Assert.Contains("mv2-check", Assert.IsType<LineIcon>(rows[1].Icon).Classes);

            rows[2].Command!.Execute(rows[2].CommandParameter);
            Assert.InRange(audio.Seeks[^1].TotalMilliseconds, 2499, 2501);
        }
        finally { menu.Hide(); win.Close(); }
    }

    [AvaloniaFact]
    public void OptionsMenu_HidesChapters_ForAFileWithoutThem()
    {
        var (player, _) = Player("C:/m/work.mp3");
        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 900, Height = 600, Content = bar };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        var menu = Assert.IsType<MenuFlyout>(bar.FindControl<Button>("OptionsButton")!.Flyout);
        menu.ShowAt(bar);
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.False(bar.FindControl<MenuItem>("ChaptersMenuItem")!.IsVisible);
        }
        finally { menu.Hide(); win.Close(); }
    }
}
