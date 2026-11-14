using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Tester 11-14: "add songs from inside a playlist like in the desktop version". A playlist's page
/// (and its ⋯ menu) has Add Songs: the library on a sheet, a tap ticks a song, Done appends the
/// ticked ones to the end in tick order; songs already there show as added and are not added
/// twice; nothing ticked changes nothing; a smart playlist does not offer it.
/// </summary>
public class MobileAddSongsTests
{
    private static (MobileFixtures.Rig Rig, Track[] Songs, Playlist Mix) RigWithPlaylist(Action<Playlist>? shape = null)
    {
        var songs = new[] { MobileFixtures.Song("Alpha"), MobileFixtures.Song("Beta"), MobileFixtures.Song("Gamma"), MobileFixtures.Song("Delta") };
        var mix = new Playlist
        {
            Name = "Night Drive",
            TrackIds = { songs[0].Id },
            TrackAddedAt = { [songs[0].Id] = DateTime.UtcNow.AddDays(-3) },
            ModifiedAt = DateTime.UtcNow.AddDays(-2),
        };
        shape?.Invoke(mix);
        var rig = MobileFixtures.MakeRig(songs, seed: p => p.SavePlaylistsAsync(new List<Playlist> { mix }));
        return (rig, songs, mix);
    }

    private static AddSongsPageViewModel OpenPicker(MobileFixtures.Rig rig)
    {
        rig.Shell.OpenPlaylistCommand.Execute(rig.Shell.Library.Playlists.Single());
        var page = (SongListPageViewModel)rig.Shell.CurrentPage!;
        Assert.True(page.CanAddSongs);
        page.AddSongsCommand.Execute(null);
        var picker = Assert.IsType<AddSongsPageViewModel>(rig.Shell.CurrentPage);
        Assert.True(picker.IsSheet);
        return picker;
    }

    private static AddSongRow Row(AddSongsPageViewModel picker, Track track) => picker.Rows.Single(r => r.Track.Id == track.Id);

    [Fact]
    public async Task Done_AppendsTheTickedSongs_InTickOrder_AtTheEnd()
    {
        var (rig, s, mix) = RigWithPlaylist();
        using var _ = rig;
        var picker = OpenPicker(rig);

        picker.ToggleCommand.Execute(Row(picker, s[3]));   // Delta first, then Beta
        picker.ToggleCommand.Execute(Row(picker, s[1]));
        Assert.True(Row(picker, s[3]).IsAdded);
        Assert.Equal(2, picker.PickedCount);
        Assert.Equal("2 Selected", picker.HeaderText);
        await picker.DoneCommand.ExecuteAsync(null);

        Assert.IsNotType<AddSongsPageViewModel>(rig.Shell.CurrentPage);   // the sheet closed
        var saved = Assert.Single(await rig.Persistence.LoadPlaylistsAsync());
        Assert.Equal(new[] { s[0].Id, s[3].Id, s[1].Id }, saved.TrackIds);
        Assert.True(saved.TrackAddedAt.ContainsKey(s[3].Id));
        Assert.True(saved.ModifiedAt > mix.ModifiedAt);   // what carries it to the computer
        Assert.Equal("Added to Night Drive", rig.Shell.Player.Notice);
        var page = (SongListPageViewModel)rig.Shell.CurrentPage!;
        Assert.Equal(new[] { s[0], s[3], s[1] }, page.Songs);   // the open page follows
    }

    [Fact]
    public async Task SongsAlreadyInThePlaylist_ShowAsAdded_CannotBeTicked_AndAreNotAddedTwice()
    {
        var (rig, s, _) = RigWithPlaylist();
        using var _r = rig;
        var picker = OpenPicker(rig);

        var alpha = Row(picker, s[0]);
        Assert.True(alpha.IsInPlaylist);
        Assert.True(alpha.IsAdded);
        picker.ToggleCommand.Execute(alpha);
        Assert.False(alpha.IsPicked);
        Assert.Equal(0, picker.PickedCount);

        // A song ticked, then the playlist gains it elsewhere before Done: added once.
        picker.ToggleCommand.Execute(Row(picker, s[2]));
        await rig.Shell.Library.AddToPlaylistAsync(rig.Shell.Library.Playlists.Single(), new[] { s[2] });
        await picker.DoneCommand.ExecuteAsync(null);
        var saved = Assert.Single(await rig.Persistence.LoadPlaylistsAsync());
        Assert.Equal(new[] { s[0].Id, s[2].Id }, saved.TrackIds);
    }

    [Fact]
    public async Task Untick_LeavesTheSongOut_AndSearchFiltersTheRows_KeepingTicks()
    {
        var (rig, s, _) = RigWithPlaylist();
        using var _r = rig;
        var picker = OpenPicker(rig);
        Assert.Equal(4, picker.Rows.Count);

        picker.ToggleCommand.Execute(Row(picker, s[1]));
        picker.ToggleCommand.Execute(Row(picker, s[2]));
        picker.ToggleCommand.Execute(Row(picker, s[1]));   // Beta unticked
        picker.FilterText = "gam";
        Assert.Equal(new[] { s[2] }, picker.Rows.Select(r => r.Track));
        Assert.True(picker.Rows[0].IsPicked);
        picker.FilterText = "zzz";
        Assert.True(picker.ShowEmpty);

        await picker.DoneCommand.ExecuteAsync(null);
        var saved = Assert.Single(await rig.Persistence.LoadPlaylistsAsync());
        Assert.Equal(new[] { s[0].Id, s[2].Id }, saved.TrackIds);
    }

    [Fact]
    public async Task Done_WithNothingTicked_OrCancel_ChangesNothing()
    {
        var (rig, s, mix) = RigWithPlaylist();
        using var _r = rig;
        var picker = OpenPicker(rig);
        await picker.DoneCommand.ExecuteAsync(null);
        Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);
        var saved = Assert.Single(await rig.Persistence.LoadPlaylistsAsync());
        Assert.Equal(new[] { s[0].Id }, saved.TrackIds);
        Assert.Equal(mix.ModifiedAt, saved.ModifiedAt);
        Assert.True(string.IsNullOrEmpty(rig.Shell.Player.Notice));

        picker = OpenPicker(rig);
        picker.ToggleCommand.Execute(Row(picker, s[1]));
        picker.CancelCommand.Execute(null);
        Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);
        saved = Assert.Single(await rig.Persistence.LoadPlaylistsAsync());
        Assert.Equal(new[] { s[0].Id }, saved.TrackIds);
    }

    [Fact]
    public void SmartPlaylist_DoesNotOfferAddSongs_OnItsPageOrMenu()
    {
        var (rig, _, _) = RigWithPlaylist(p => p.IsSmartPlaylist = true);
        using var _r = rig;
        rig.Shell.OpenPlaylistCommand.Execute(rig.Shell.Library.Playlists.Single());
        var page = (SongListPageViewModel)rig.Shell.CurrentPage!;
        Assert.False(page.CanAddSongs);
        page.AddSongsCommand.Execute(null);
        Assert.Same(page, rig.Shell.CurrentPage);
        page.ShowPlaylistMenuCommand.Execute(null);
        Assert.False(rig.Shell.Sheet!.CanAddSongs);
        Assert.True(rig.Shell.Sheet.CanEditPlaylist);   // Rename stays
    }

    [Fact]
    public void Songs_ListHasNoAddSongs_AndThePlaylistMenuOpensThePicker()
    {
        var (rig, _, _) = RigWithPlaylist();
        using var _r = rig;
        rig.Shell.OpenSongsCommand.Execute(null);
        Assert.False(((SongListPageViewModel)rig.Shell.CurrentPage!).CanAddSongs);

        rig.Shell.OpenPlaylistSheetCommand.Execute(rig.Shell.Library.Playlists.Single());
        Assert.True(rig.Shell.Sheet!.CanAddSongs);
        rig.Shell.Sheet.AddSongsCommand.Execute(null);
        Assert.False(rig.Shell.IsSheetOpen);
        Assert.IsType<AddSongsPageViewModel>(rig.Shell.CurrentPage);
    }

    [AvaloniaFact]
    public void Mounted_ThePlaylistPage_HasTheAddSongsButton_WhichOpensThePicker()
    {
        var (rig, s, _) = RigWithPlaylist();
        using var _r = rig;
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.OpenPlaylistCommand.Execute(rig.Shell.Library.Playlists.Single());
            MobileFixtures.Land(window);
            var page = MobileFixtures.PageView(view, rig.Shell.CurrentPage!);
            var add = MobileFixtures.Named<Button>(page, "PlaylistAddSongsButton");
            Assert.True(add.IsEffectivelyVisible);
            Assert.Equal("Add Songs", Avalonia.Automation.AutomationProperties.GetName(add));

            add.Command!.Execute(add.CommandParameter);
            MobileFixtures.Land(window);
            var picker = (AddSongsPageViewModel)rig.Shell.CurrentPage!;
            var sheet = MobileFixtures.PageView(view, picker);
            Assert.True(MobileFixtures.Named<Button>(sheet, "AddSongsDoneButton").IsEffectivelyVisible);
            Assert.True(MobileFixtures.Named<Button>(sheet, "AddSongsCancelButton").IsEffectivelyVisible);
            var list = MobileFixtures.Named<ItemsControl>(sheet, "AddSongsList");
            var alphaRow = list.ContainerFromIndex(picker.Rows.IndexOf(Row(picker, s[0])))!;
            Assert.True(alphaRow.GetVisualDescendants().OfType<PathIcon>().Single(i => i.Name == "AddedMark").IsEffectivelyVisible);
            var betaRow = list.ContainerFromIndex(picker.Rows.IndexOf(Row(picker, s[1])))!;
            Assert.True(betaRow.GetVisualDescendants().OfType<PathIcon>().Single(i => i.Name == "AddMark").IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
