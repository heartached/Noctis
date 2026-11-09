using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
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

    /// <summary>
    /// The playlist's own page shows its cover at the top (the custom one, else the tiles'
    /// artwork), under the header, with the find field and Play / Shuffle under it, all scrolling
    /// with the songs; Change Cover and Remove Cover from the page's ⋯ show at once. Other song
    /// lists (Songs) have no cover and keep the find field fixed above the list.
    /// </summary>
    [AvaloniaFact]
    public async Task PlaylistPage_ShowsItsCover_OverTheList_AndFollowsChangeAndRemoveCover()
    {
        var (rig, shell, _) = Rig(new PickedPlaylistFile("cover.png", Png));
        using var _r = rig;
        var window = MobileFixtures.Mount(shell, out var view);
        try
        {
            shell.SelectTabCommand.Execute(MobileTab.Library);
            shell.OpenPlaylistCommand.Execute(shell.Library.Playlists.Single());
            MobileFixtures.Land(window);
            var page = (SongListPageViewModel)shell.CurrentPage!;
            var pageView = MobileFixtures.PageView(view, page);
            var cover = MobileFixtures.Named<Border>(pageView, "PlaylistCover");
            var art = MobileFixtures.Named<CachedImage>(pageView, "PlaylistCoverArt");
            var mix = page.Playlist!;
            Assert.True(cover.IsEffectivelyVisible);
            Assert.Equal(shell.Library.PlaylistArtwork(mix), art.SourcePath);   // the tiles' cover
            Assert.Equal(cover.Width, cover.Height);
            Assert.InRange(cover.Width, 160, 300);

            // Under the header's title, centred; the find field and Play under the cover, in the scroller.
            var header = pageView.GetVisualDescendants().OfType<PageHeader>().Single();
            var title = MobileFixtures.Named<FitWidth>(header, "TitleFit");
            Assert.Equal("Night Drive", ((TextBlock)title.Child!).Text);
            var coverTop = cover.TranslatePoint(new Point(0, 0), view)!.Value;
            Assert.True(coverTop.Y >= title.TranslatePoint(new Point(0, title.Bounds.Height), view)!.Value.Y - 0.5);
            Assert.Equal(view.Bounds.Width / 2, coverTop.X + cover.Width / 2, 1);
            var scroll = MobileFixtures.Named<ScrollViewer>(pageView, "SongScroll");
            var find = pageView.GetVisualDescendants().OfType<FindField>().Single();
            Assert.Contains(scroll, find.GetVisualAncestors());
            Assert.True(find.TranslatePoint(new Point(0, 0), view)!.Value.Y >= coverTop.Y + cover.Height - 0.5);

            // Change Cover from the page's ⋯: the page shows the new cover.
            page.ShowPlaylistMenuCommand.Execute(null);
            await shell.Sheet!.ChangeCoverCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            var saved = Assert.Single(await rig.Persistence.LoadPlaylistsAsync());
            Assert.Equal(saved.CoverArtPath, page.CoverPath);
            Assert.Equal(saved.CoverArtPath, art.SourcePath);

            // Remove Cover: back to the tiles' artwork.
            page.ShowPlaylistMenuCommand.Execute(null);
            await shell.Sheet!.RemoveCoverCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(shell.Library.PlaylistArtwork(page.Playlist!), art.SourcePath);
            Assert.NotEqual(saved.CoverArtPath, art.SourcePath);

            // The Songs list: no cover, the find field stays above the scroller.
            shell.SelectTabCommand.Execute(MobileTab.Library);
            shell.OpenSongsCommand.Execute(null);
            MobileFixtures.Land(window);
            var songsView = MobileFixtures.PageView(view, shell.CurrentPage!);
            Assert.False(MobileFixtures.Named<Border>(songsView, "PlaylistCover").IsVisible);
            var songsFind = songsView.GetVisualDescendants().OfType<FindField>().Single();
            Assert.DoesNotContain(MobileFixtures.Named<ScrollViewer>(songsView, "SongScroll"), songsFind.GetVisualAncestors());
        }
        finally { window.Close(); }
    }

    /// <summary>A long playlist's page still realizes only the rows on screen with the cover and
    /// the find field in the scroller: the rows stay virtualized.</summary>
    [AvaloniaFact]
    public void LongPlaylistPage_KeepsItsRowsVirtualized()
    {
        var songs = Enumerable.Range(0, 600).Select(i => MobileFixtures.Song($"Song {i:D3}")).ToArray();
        var mix = new Playlist { Name = "Everything", TrackIds = songs.Select(t => t.Id).ToList() };
        using var rig = MobileFixtures.MakeRig(songs, seed: p => p.SavePlaylistsAsync(new List<Playlist> { mix }));
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
            rig.Shell.OpenPlaylistCommand.Execute(rig.Shell.Library.Playlists.Single());
            MobileFixtures.Land(window);
            var pageView = MobileFixtures.PageView(view, rig.Shell.CurrentPage!);
            var scroll = MobileFixtures.Named<ScrollViewer>(pageView, "SongScroll");
            var list = MobileFixtures.Named<ItemsControl>(pageView, "SongList");
            var most = 0;
            for (var y = 0; y <= 12000; y += 600)
            {
                scroll.Offset = new Vector(0, y);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                most = Math.Max(most, list.GetRealizedContainers().Count());
            }
            Assert.True(scroll.Offset.Y > 10000);
            Assert.InRange(most, 1, 40);
        }
        finally { window.Close(); }
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
