using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Localization;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 11-23: a click on the playlist page's hero cover opens its menu: View Cover (the album page's
/// full-size viewer) when there is one picture to show, and Change Cover… (the Edit Playlist
/// dialog's picker, saved straight away). The cover dims with an icon under the pointer.
/// </summary>
[Collection("Localization")]
public class PlaylistHeroCoverTests : IDisposable
{
    public PlaylistHeroCoverTests() => Loc.Instance.SetCulture("en");
    public void Dispose() => Loc.Instance.SetCulture("en");

    private static (PlaylistViewModel Vm, SidebarViewModel Sidebar, Playlist Playlist, PlaylistNavItem Nav) Page(
        string? art1 = null, string? art2 = null, string? cover = null)
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var playlist = new Playlist { Name = "Night Drive", CoverArtPath = cover };
        var nav = new PlaylistNavItem { Key = "p", Label = playlist.Name, PlaylistId = playlist.Id, Art1 = art1, Art2 = art2, CoverArtPath = cover };
        sidebar.Playlists.Add(playlist);
        sidebar.PlaylistItems.Add(nav);
        var vm = new PlaylistViewModel(playlist, player, lib, persistence, sidebar);
        return (vm, sidebar, playlist, nav);
    }

    [AvaloniaFact]
    public async Task ViewCover_ShowsTheCustomCover_OrTheOneAlbumPicture_NotACollageOrColour()
    {
        var shown = new List<string>();
        Task Viewer(ArtworkViewerRequest r) { shown.Add(r.StillPath); return Task.CompletedTask; }

        var (colour, _, _, _) = Page();
        Assert.False(colour.CanViewCover);

        var (collage, _, _, _) = Page(art1: "a.jpg", art2: "b.jpg");
        Assert.False(collage.CanViewCover);

        var (single, _, _, _) = Page(art1: "a.jpg");
        single.ShowArtworkViewer = Viewer;
        Assert.True(single.CanViewCover);
        await single.ViewCoverCommand.ExecuteAsync(null);

        var (custom, _, _, _) = Page(art1: "a.jpg", art2: "b.jpg", cover: "mine.png");
        custom.ShowArtworkViewer = Viewer;
        Assert.True(custom.CanViewCover);
        await custom.ViewCoverCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "a.jpg", "mine.png" }, shown);
    }

    [AvaloniaFact]
    public async Task ChangeCover_PicksAPicture_SavesItAsTheCover_AndTheHeaderFollows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var picked = Path.Combine(dir, "pick.png");
        File.WriteAllBytes(picked, new byte[] { 1, 2, 3 });
        var (vm, sidebar, playlist, nav) = Page(art1: "a.jpg", art2: "b.jpg");
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.PickCoverFile = _ => Task.FromResult<string?>(picked);
        try
        {
            await vm.ChangeCoverCommand.ExecuteAsync(null);

            var dest = playlist.CoverArtPath!;
            Assert.NotNull(dest);
            Assert.Equal("playlist_covers", Path.GetFileName(Path.GetDirectoryName(dest)));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(dest));
            Assert.Equal(dest, nav.CoverArtPath);              // the sidebar row
            Assert.True(vm.HasCustomArt);
            Assert.True(vm.CanViewCover);
            Assert.Contains(nameof(PlaylistViewModel.HasCustomArt), raised);
            Assert.Contains(nameof(PlaylistViewModel.CanViewCover), raised);
            File.Delete(dest);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [AvaloniaFact]
    public async Task ChangeCover_Cancelled_ChangesNothing()
    {
        var (vm, _, playlist, _) = Page(art1: "a.jpg");
        vm.PickCoverFile = _ => Task.FromResult<string?>(null);
        await vm.ChangeCoverCommand.ExecuteAsync(null);
        Assert.Null(playlist.CoverArtPath);
    }

    [AvaloniaFact]
    public void View_HeroCover_HasTheMenu_AndAnEasedHoverDim()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        ContextMenuV2ViewsTests.EnsureAppStyles();
        var (vm, _, _, _) = Page(art1: "a.jpg");
        var view = new PlaylistView { DataContext = vm };
        var win = new Window { Width = 1200, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var cover = view.FindControl<Border>("HeroCover")!;
            Assert.NotNull(cover.Cursor);
            var menu = cover.ContextMenu!;
            menu.Open(cover);
            Dispatcher.UIThread.RunJobs();
            Assert.True(menu.IsOpen);
            Assert.Contains(Noctis.Helpers.MenuV2.MenuClass, menu.Classes);
            var items = menu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(new[] { Loc.T("Playlist.ViewCover"), Loc.T("Playlist.ChangeCover") }, items.Select(i => (string)i.Header!));
            Assert.Same(vm.ViewCoverCommand, items[0].Command);
            Assert.Same(vm.ChangeCoverCommand, items[1].Command);
            Assert.True(items[0].IsVisible);
            menu.Close();

            var dim = view.FindControl<Border>("HeroCoverDim")!;
            Assert.Equal(0, dim.Opacity);
            Assert.NotNull(dim.Transitions);
            Assert.False(dim.IsHitTestVisible);
        }
        finally { win.Close(); }
    }
}
