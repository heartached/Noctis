using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services.Sync;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Mobile beta 2 (tester Mistery, 11-07): "you can't put a custom playlist cover at all on
/// mobile". A playlist's menu now has Change Cover (a picture from the phone's picker) and, while
/// it has one, Remove Cover; the playlist list, the Pinned rail and the menu show it.
/// </summary>
public class MobilePlaylistCoverTests
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 7, 7, 7 };

    private sealed class ImagePicker(PickedPlaylistFile? image) : IPlaylistFilePicker
    {
        public Task<IReadOnlyList<PickedPlaylistFile>> PickPlaylistFilesAsync() =>
            Task.FromResult<IReadOnlyList<PickedPlaylistFile>>(Array.Empty<PickedPlaylistFile>());

        public Task<PickedPlaylistFile?> PickImageAsync() => Task.FromResult(image);
    }

    private static (MobileFixtures.Rig Rig, ShellViewModel Shell, Track Song) Rig(PickedPlaylistFile? image)
    {
        var a = MobileFixtures.Song("Alpha");
        var mix = new Playlist { Name = "Night Drive", TrackIds = { a.Id }, ModifiedAt = DateTime.UtcNow.AddDays(-2) };
        var rig = MobileFixtures.MakeRig(new[] { a }, seed: p => p.SavePlaylistsAsync(new List<Playlist> { mix }));
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            Marshal = x => x(),
            TintFactory = rig.Shell.TintFactory,
            PlaylistFiles = new ImagePicker(image),
        };
        return (rig, shell, a);
    }

    [Fact]
    public async Task ChangeCover_SavesThePicture_TheListAndMenuShowIt_AndRemoveCoverGoesBack()
    {
        var (rig, shell, song) = Rig(new PickedPlaylistFile("cover.png", Png));
        using var _ = rig;
        var mix = shell.Library.Playlists.Single();
        var before = mix.ModifiedAt;
        Assert.Equal(song.AlbumArtworkPath, shell.Library.PlaylistArtwork(mix));

        // No picker (tests, hosts without one): no Change Cover.
        rig.Shell.OpenPlaylistSheetCommand.Execute(mix);
        Assert.False(rig.Shell.Sheet!.CanChangeCover);
        rig.Shell.CloseSheet();

        shell.OpenPlaylistSheetCommand.Execute(mix);
        Assert.True(shell.Sheet!.CanChangeCover);
        Assert.False(shell.Sheet.CanRemoveCover);
        await shell.Sheet.ChangeCoverCommand.ExecuteAsync(null);

        Assert.False(shell.IsSheetOpen);
        var saved = Assert.Single(await rig.Persistence.LoadPlaylistsAsync());
        Assert.Equal(Png, File.ReadAllBytes(saved.CoverArtPath!));
        Assert.Equal(PlaylistCovers.CoversDirectory(rig.Persistence.DataDirectory), Path.GetDirectoryName(saved.CoverArtPath));
        Assert.True(saved.ModifiedAt > before);                  // what carries it to the computer
        var current = shell.CurrentPlaylist(mix);
        Assert.Equal(saved.CoverArtPath, shell.Library.PlaylistArtwork(current));   // list rows, rail, search

        shell.OpenPlaylistSheetCommand.Execute(current);
        Assert.Equal(saved.CoverArtPath, shell.Sheet!.ArtworkPath);                  // the menu's header
        Assert.True(shell.Sheet.CanRemoveCover);
        await shell.Sheet.RemoveCoverCommand.ExecuteAsync(null);

        saved = Assert.Single(await rig.Persistence.LoadPlaylistsAsync());
        Assert.Null(saved.CoverArtPath);
        Assert.Empty(Directory.GetFiles(PlaylistCovers.CoversDirectory(rig.Persistence.DataDirectory)));
        Assert.Equal(song.AlbumArtworkPath, shell.Library.PlaylistArtwork(shell.CurrentPlaylist(mix)));
    }

    [Fact]
    public async Task ChangeCover_WithAFileThatIsNotAPicture_SaysSo_AndChangesNothing()
    {
        var (rig, shell, _) = Rig(new PickedPlaylistFile("notes.txt", "not a picture"u8.ToArray()));
        using var _r = rig;
        var mix = shell.Library.Playlists.Single();

        await shell.ChangePlaylistCoverAsync(mix);

        Assert.Equal("That file isn't a picture Noctis can use as a cover.", shell.Player.Notice);
        Assert.Null(Assert.Single(await rig.Persistence.LoadPlaylistsAsync()).CoverArtPath);
    }

    [Fact]
    public async Task DeletingAPlaylist_RemovesItsCoverFile()
    {
        var (rig, shell, _) = Rig(new PickedPlaylistFile("cover.png", Png));
        using var _r = rig;
        var mix = shell.Library.Playlists.Single();
        await shell.ChangePlaylistCoverAsync(mix);
        Assert.Single(Directory.GetFiles(PlaylistCovers.CoversDirectory(rig.Persistence.DataDirectory)));

        await shell.DeletePlaylistAsync(shell.CurrentPlaylist(mix));

        Assert.Empty(Directory.GetFiles(PlaylistCovers.CoversDirectory(rig.Persistence.DataDirectory)));
    }
}
