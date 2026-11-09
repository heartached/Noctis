using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-20: the phone edits playlists as the desktop does (SidebarViewModel.RenamePlaylist /
/// DeletePlaylistAsync, PlaylistViewModel.RemoveTrack) — Rename on a glass sheet with a pill
/// field, Delete Playlist behind the glass confirm, Remove from Playlist from a song's ⋯ on its
/// playlist's page — and says "Added to …" on the notice banner after Add to Playlist.
/// </summary>
public class MobilePlaylistEditingTests
{
    private static (MobileFixtures.Rig Rig, Track A, Track B, Playlist Mix) RigWithPlaylist(Action<Playlist>? shape = null)
    {
        var a = MobileFixtures.Song("Alpha");
        var b = MobileFixtures.Song("Beta");
        var mix = new Playlist
        {
            Name = "Night Drive",
            TrackIds = { a.Id, b.Id },
            TrackAddedAt = { [a.Id] = DateTime.UtcNow.AddDays(-3), [b.Id] = DateTime.UtcNow.AddDays(-2) },
            ModifiedAt = DateTime.UtcNow.AddDays(-2),
        };
        shape?.Invoke(mix);
        var rig = MobileFixtures.MakeRig(new[] { a, b }, seed: p => p.SavePlaylistsAsync(new List<Playlist> { mix }));
        return (rig, a, b, mix);
    }

    [Fact]
    public async Task RemoveFromPlaylist_OnlyOnItsPage_DropsTheSongAndItsAddedDate_AndBumpsModifiedAt()
    {
        var (rig, a, b, mix) = RigWithPlaylist();
        using var _ = rig;
        var before = mix.ModifiedAt;

        // Elsewhere (the Songs list) a song's menu has no Remove from Playlist.
        rig.Shell.OpenTrackSheetCommand.Execute(a);
        Assert.False(rig.Shell.Sheet!.CanRemoveFromPlaylist);
        rig.Shell.CloseSheet();

        rig.Shell.OpenPlaylistCommand.Execute(rig.Shell.Library.Playlists.Single());
        var page = (SongListPageViewModel)rig.Shell.CurrentPage!;
        page.OpenSongMenuCommand.Execute(a);
        Assert.True(rig.Shell.Sheet!.CanRemoveFromPlaylist);
        await rig.Shell.Sheet.RemoveFromPlaylistCommand.ExecuteAsync(null);

        Assert.False(rig.Shell.IsSheetOpen);
        var saved = Assert.Single(await rig.Persistence.LoadPlaylistsAsync());
        Assert.Equal(new[] { b.Id }, saved.TrackIds);
        Assert.False(saved.TrackAddedAt.ContainsKey(a.Id));
        Assert.True(saved.TrackAddedAt.ContainsKey(b.Id));
        Assert.True(saved.ModifiedAt > before);
        Assert.Equal(new[] { b }, page.Songs);   // the open page follows at once
    }

    /// <summary>A smart playlist's songs come from its rules (the desktop refuses it too).</summary>
    [Fact]
    public void RemoveFromPlaylist_IsNotOfferedOnASmartPlaylist()
    {
        var (rig, a, _, _) = RigWithPlaylist(p => p.IsSmartPlaylist = true);
        using var _r = rig;
        rig.Shell.OpenPlaylistCommand.Execute(rig.Shell.Library.Playlists.Single());
        ((SongListPageViewModel)rig.Shell.CurrentPage!).OpenSongMenuCommand.Execute(a);
        Assert.False(rig.Shell.Sheet!.CanRemoveFromPlaylist);
    }

    [Fact]
    public async Task Rename_AsksOnTheGlassSheet_TrimsTheName_AndTheOpenPageFollows()
    {
        var (rig, _, _, mix) = RigWithPlaylist();
        using var _r = rig;
        rig.Shell.OpenPlaylistCommand.Execute(rig.Shell.Library.Playlists.Single());
        var page = (SongListPageViewModel)rig.Shell.CurrentPage!;
        Assert.True(page.IsPlaylist);

        page.ShowPlaylistMenuCommand.Execute(null);
        Assert.True(rig.Shell.Sheet!.CanEditPlaylist);
        rig.Shell.Sheet.RenamePlaylistCommand.Execute(null);
        Assert.False(rig.Shell.IsSheetOpen);   // the menu gives way to the sheet
        var prompt = rig.Shell.Prompt!;
        Assert.Equal("Rename Playlist", prompt.Title);
        Assert.True(prompt.HasField);
        Assert.Equal("Night Drive", prompt.FieldText);

        prompt.FieldText = "   ";
        Assert.False(prompt.CanConfirm);   // no empty name, as the desktop
        await prompt.Actions[0].RunCommand.ExecuteAsync(null);
        Assert.True(rig.Shell.IsPromptOpen);

        prompt.FieldText = "  Late Drive ";
        await prompt.SubmitCommand.ExecuteAsync(null);   // Enter on the keyboard
        Assert.False(rig.Shell.IsPromptOpen);
        var saved = Assert.Single(await rig.Persistence.LoadPlaylistsAsync());
        Assert.Equal("Late Drive", saved.Name);
        Assert.True(saved.ModifiedAt > mix.ModifiedAt);
        Assert.Equal("Late Drive", page.Title);
    }

    [Fact]
    public async Task Rename_ToTheSameName_SavesNothing()
    {
        var (rig, _, _, mix) = RigWithPlaylist();
        using var _r = rig;
        rig.Shell.AskRenamePlaylist(rig.Shell.Library.Playlists.Single());
        await rig.Shell.Prompt!.Actions[0].RunCommand.ExecuteAsync(null);
        var saved = Assert.Single(await rig.Persistence.LoadPlaylistsAsync());
        Assert.Equal(mix.ModifiedAt, saved.ModifiedAt);
    }

    [Fact]
    public async Task DeletePlaylist_AsksFirst_ThenItGoes_AndItsPageCloses()
    {
        var (rig, _, _, _) = RigWithPlaylist();
        using var _r = rig;
        rig.Shell.OpenPlaylistCommand.Execute(rig.Shell.Library.Playlists.Single());
        var page = (SongListPageViewModel)rig.Shell.CurrentPage!;
        page.ShowPlaylistMenuCommand.Execute(null);
        rig.Shell.Sheet!.DeletePlaylistCommand.Execute(null);

        var prompt = rig.Shell.Prompt!;
        Assert.Equal("Delete “Night Drive”?", prompt.Title);
        Assert.Equal("This can't be undone. The songs stay in your library.", prompt.Message);
        var delete = Assert.Single(prompt.Actions);
        Assert.Equal("Delete Playlist", delete.Label);
        Assert.True(delete.IsDestructive);

        // Back cancels: the playlist and its page stay.
        Assert.True(rig.Shell.TryHandleBack());
        Assert.False(rig.Shell.IsPromptOpen);
        Assert.Same(page, rig.Shell.CurrentPage);
        Assert.Single(await rig.Persistence.LoadPlaylistsAsync());

        rig.Shell.AskDeletePlaylist(page.Playlist!);
        await rig.Shell.Prompt!.Actions[0].RunCommand.ExecuteAsync(null);
        Assert.Empty(await rig.Persistence.LoadPlaylistsAsync());
        Assert.Empty(rig.Shell.Library.Playlists);
        Assert.Null(rig.Shell.CurrentPage);   // back on the Playlists list
        Assert.Equal(0, rig.Shell.Library.PlaylistCount);
    }

    [Fact]
    public async Task AddToPlaylist_SaysAddedToIt_OnTheNoticeBanner_OrThatTheSongIsThereAlready()
    {
        var (rig, a, _, _) = RigWithPlaylist();
        using var _r = rig;
        var c = MobileFixtures.Song("Gamma");
        rig.Library.TrackList.Add(c);

        rig.Shell.OpenTrackSheetCommand.Execute(c);
        await rig.Shell.Sheet!.AddToPlaylistCommand.ExecuteAsync(rig.Shell.Sheet.Playlists.Single());
        Assert.Equal("Added to Night Drive", rig.Shell.Player.Notice);

        rig.Shell.OpenTrackSheetCommand.Execute(a);
        await rig.Shell.Sheet!.AddToPlaylistCommand.ExecuteAsync(rig.Shell.Sheet.Playlists.Single());
        Assert.Equal("Already in Night Drive", rig.Shell.Player.Notice);

        rig.Shell.OpenTrackSheetCommand.Execute(a);
        rig.Shell.Sheet!.NewPlaylistName = " Road Trip ";
        await rig.Shell.Sheet.CreatePlaylistCommand.ExecuteAsync(null);
        Assert.Equal("Added to Road Trip", rig.Shell.Player.Notice);
    }

    /// <summary>The page and the list: the playlist's page carries its own ⋯ in the title row
    /// (not on Songs), a playlist row's ⋯ opens the playlist's menu with Rename and Delete
    /// Playlist (the desktop's glyphs), a song row's ⋯ on the page adds Remove from Playlist.</summary>
    [AvaloniaFact]
    public void Mounted_ThePagesAndRowsOverflowButtons_OpenTheEditRows()
    {
        var (rig, _, _, _) = RigWithPlaylist();
        using var _r = rig;
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.OpenSongsCommand.Execute(null);
            MobileFixtures.Land(window);
            var songs = MobileFixtures.PageView(view, rig.Shell.CurrentPage!);
            Assert.False(MobileFixtures.Named<Button>(songs, "PlaylistMoreButton").IsEffectivelyVisible);

            rig.Shell.SelectTabCommand.Execute(MobileTab.Playlists);
            MobileFixtures.Land(window);
            var rowMore = MobileFixtures.Named<ItemsControl>(view, "PlaylistList").GetVisualDescendants().OfType<Button>()
                .First(b => b.Name == "PlaylistMoreButton");
            rowMore.Command!.Execute(rowMore.CommandParameter);
            window.UpdateLayout();
            Assert.True(MobileFixtures.Named<Button>(view, "MenuRename").IsEffectivelyVisible);
            Assert.True(MobileFixtures.Named<Button>(view, "MenuDeletePlaylist").IsEffectivelyVisible);
            Assert.False(MobileFixtures.Named<Button>(view, "MenuRemoveFromPlaylist").IsEffectivelyVisible);
            rig.Shell.CloseSheet();

            rig.Shell.OpenPlaylistCommand.Execute(rig.Shell.Library.Playlists.Single());
            MobileFixtures.Land(window);
            var page = MobileFixtures.PageView(view, rig.Shell.CurrentPage!);
            Assert.True(MobileFixtures.Named<Button>(page, "PlaylistMoreButton").IsEffectivelyVisible);
            var songMore = MobileFixtures.Named<ItemsControl>(page, "SongList").ContainerFromIndex(0)!
                .GetVisualDescendants().OfType<Button>().First(b => b.Name == "MoreButton");
            songMore.Command!.Execute(songMore.CommandParameter);
            window.UpdateLayout();
            Assert.True(MobileFixtures.Named<Button>(view, "MenuRemoveFromPlaylist").IsEffectivelyVisible);
            Assert.False(MobileFixtures.Named<Button>(view, "MenuRename").IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The prompt is the one glass sheet over everything: open it shows the question,
    /// a tap on the dim cancels it (nothing is deleted).</summary>
    [AvaloniaFact]
    public async Task Mounted_ThePrompt_ShowsOverThePage_AndATapOnTheDimCancels()
    {
        var (rig, _, _, _) = RigWithPlaylist();
        using var _r = rig;
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.AskDeletePlaylist(rig.Shell.Library.Playlists.Single());
            window.UpdateLayout();
            var sheet = view.FindControl<PromptSheet>("Prompt")!;
            Assert.True(sheet.IsVisible);
            Assert.Equal("Delete “Night Drive”?", MobileFixtures.Named<TextBlock>(sheet, "PromptTitle").Text);
            Assert.False(MobileFixtures.Named<TextBox>(sheet, "PromptField").IsVisible);
            var actions = MobileFixtures.Named<ItemsControl>(sheet, "PromptActions").GetVisualDescendants().OfType<Button>().ToList();
            Assert.Contains(Assert.Single(actions).Classes, c => c == "destructive");

            window.MouseDown(new Avalonia.Point(200, 80), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
            Assert.False(rig.Shell.IsPromptOpen);
            Assert.Single(await rig.Persistence.LoadPlaylistsAsync());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Beta 2 feedback: "We can make a new playlist with the ⋯ dropdown on a song, but can't
    /// make it at the Playlists page." The page's tools pill has + (New Playlist), with no playlists
    /// yet too: the Rename prompt's field, empty, and Create.</summary>
    [AvaloniaFact]
    public async Task ThePlaylistsTab_MakesANewPlaylist_FromItsPlusButton()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Alpha") });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.SelectTabCommand.Execute(MobileTab.Playlists);
            window.UpdateLayout();
            var root = view.FindControl<Grid>("PlaylistsRoot")!;
            var plus = root.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NewPlaylistButton" && b.IsEffectivelyVisible);
            Assert.Equal("New Playlist", Avalonia.Automation.AutomationProperties.GetName(plus));
            plus.Command!.Execute(plus.CommandParameter);
            window.UpdateLayout();

            var prompt = rig.Shell.Prompt!;
            Assert.Equal("New Playlist", prompt.Title);
            Assert.True(prompt.HasField);
            Assert.Equal(string.Empty, prompt.FieldText);
            Assert.False(prompt.CanConfirm);
            Assert.Equal("Create", prompt.Actions.Single().Label);
            var sheet = view.FindControl<PromptSheet>("Prompt")!;
            Assert.True(MobileFixtures.Named<TextBox>(sheet, "PromptField").IsVisible);

            prompt.FieldText = "  Road Trip ";
            await prompt.SubmitCommand.ExecuteAsync(null);
            Assert.False(rig.Shell.IsPromptOpen);
            Assert.Equal("Road Trip", Assert.Single(await rig.Persistence.LoadPlaylistsAsync()).Name);
            Assert.Equal(new[] { "Road Trip" }, rig.Shell.PlaylistsRoot.Rows.Select(r => r.Name));
        }
        finally
        {
            window.Close();
        }
    }

}
