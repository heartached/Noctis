using System;
using System.Linq;
using System.Threading.Tasks;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>A playlist synced from the computer can name songs the phone does not have (not in its
/// catalog, hidden on the phone): its page skips them (<see cref="ShellViewModel.ResolvePlaylist"/>),
/// and every count of it said the bigger number (Discord #mobile-beta, 11-16: "the number of songs
/// do not match the number written on the playlists page").</summary>
public class MobilePlaylistCountTests
{
    private static (MobileFixtures.Rig Rig, Track A, Track B) RigWithSyncedPlaylist()
    {
        var a = MobileFixtures.Song("Alpha");
        var b = MobileFixtures.Song("Beta");
        // Two of the four ids are songs the phone does not have.
        var synced = new Playlist { Name = "Vybz", IsPinned = true, TrackIds = { a.Id, Guid.NewGuid(), b.Id, Guid.NewGuid() } };
        var rig = MobileFixtures.MakeRig(new[] { a, b }, null, async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MusicFolders.Add("content://tree/music");
            await p.SaveSettingsAsync(s);
            await p.SavePlaylistsAsync(new() { synced });
        });
        return (rig, a, b);
    }

    [Fact]
    public void PlaylistsRow_SaysTheSongsItsPageShows()
    {
        var (rig, _, _) = RigWithSyncedPlaylist();
        using var _rig = rig;
        rig.Shell.OpenPlaylistsCommand.Execute(null);
        var list = Assert.IsType<PlaylistListPageViewModel>(rig.Shell.CurrentPage);
        var row = list.Rows.Single();

        rig.Shell.OpenPlaylistCommand.Execute(row.Playlist);
        var page = Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);

        Assert.Equal(2, page.Songs.Count);
        Assert.Equal(page.CountText, row.Subtitle);
        Assert.Equal("2 songs", row.Subtitle);
    }

    [Fact]
    public void PinnedTile_Menu_AndSearchResult_SayTheSameCount()
    {
        var (rig, _, _) = RigWithSyncedPlaylist();
        using var _rig = rig;
        var playlist = rig.Shell.Library.Playlists.Single();

        var tile = rig.Shell.Library.PinnedRail.Single(r => r.Kind == RailItemKind.Playlist);
        Assert.Equal("2 songs", tile.Subtitle);

        rig.Shell.OpenPlaylistSheetCommand.Execute(playlist);
        var sheet = Assert.IsType<ContextSheetViewModel>(rig.Shell.Sheet);
        Assert.Equal("2 songs", sheet.Subtitle);
        rig.Shell.CloseSheet();

        rig.Shell.Search.Query = "Vybz";
        Assert.Contains("2 songs", rig.Shell.Search.Playlists.Single().Subtitle);
    }

    /// <summary>A song the playlist names arriving on the phone later (the next catalog sync) moves
    /// the row's count up with it, not only the page's.</summary>
    [Fact]
    public async Task PlaylistsRow_FollowsTheLibrary_WhenTheSongsChange()
    {
        var (rig, _, b) = RigWithSyncedPlaylist();
        using var _rig = rig;
        rig.Shell.OpenPlaylistsCommand.Execute(null);
        var list = Assert.IsType<PlaylistListPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal("2 songs", list.Rows.Single().Subtitle);

        await rig.Shell.Library.RemoveFromPlaylistAsync(rig.Shell.Library.Playlists.Single(), new[] { b });

        Assert.Equal("1 song", list.Rows.Single().Subtitle);
    }
}
