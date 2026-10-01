using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>The Library tab's Apple-style list rows (Playlists, Artists, Albums, Songs, plus
/// Favorites and Downloaded switched off) and their Edit mode: switch rows on and off, drag them
/// into order, Done saves to AppSettings.PhoneLibraryRows.</summary>
public class MobileLibraryRowsTests
{
    private static Func<PersistenceService, Task> WithFolder(IEnumerable<string>? rows = null) => async p =>
    {
        var s = await p.LoadSettingsAsync();
        s.MusicFolders.Add("content://tree/music");
        if (rows != null) s.PhoneLibraryRows = rows.ToList();
        await p.SaveSettingsAsync(s);
    };

    private static MobileFixtures.Rig MakeRig(IEnumerable<string>? rows = null)
    {
        var a = MobileFixtures.Song("Alpha", artist: "Band", favourite: true);
        var b = MobileFixtures.Song("Beta", artist: "Band");
        return MobileFixtures.MakeRig(new[] { a, b }, new[] { MobileFixtures.MakeAlbum("First", "Band", a, b) }, WithFolder(rows));
    }

    /// <summary>The row buttons on screen, top to bottom, by their label.</summary>
    private static List<string> ListedTitles(ShellView view) =>
        MobileFixtures.Find<LibraryPage>(view).GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("lib-row") && b.IsEffectivelyVisible)
            .Select(b => ((LibraryRowItem)b.DataContext!).Title)
            .ToList();

    private static Button RowButton(ShellView view, LibraryRowKind kind) =>
        MobileFixtures.Find<LibraryPage>(view).GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("lib-row") && b.DataContext is LibraryRowItem r && r.Kind == kind);

    private static void Tap(Button button) => button.Command!.Execute(button.CommandParameter);

    [AvaloniaFact]
    public void DefaultRows_ArePlaylistsArtistsAlbumsSongs_WithFavoritesListedButOff()
    {
        using var rig = MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        var rows = rig.Shell.LibraryRows.Rows;
        Assert.Equal(new[] { LibraryRowKind.Playlists, LibraryRowKind.Artists, LibraryRowKind.Albums, LibraryRowKind.Songs, LibraryRowKind.Favorites },
            rows.Select(r => r.Kind));                                         // no account service: no Downloaded row
        Assert.Equal(new[] { true, true, true, true, false }, rows.Select(r => r.IsShown));
        Assert.Equal(new[] { "Playlists", "Artists", "Albums", "Songs" }, ListedTitles(view));

        // Each row has its accent glyph (the row-icon styles pick it by the row's key).
        foreach (var kind in new[] { LibraryRowKind.Playlists, LibraryRowKind.Artists, LibraryRowKind.Albums, LibraryRowKind.Songs })
            Assert.NotNull(RowButton(view, kind).GetVisualDescendants().OfType<PathIcon>().First(i => i.Name == "RowIcon").Data);

        // The rows come first, then the sections.
        var rowList = MobileFixtures.Named<ItemsControl>(view, "LibraryRowList");
        var pinned = MobileFixtures.Named<StackPanel>(view, "PinnedSection");
        Assert.True(rowList.TranslatePoint(default, view)!.Value.Y < pinned.TranslatePoint(default, view)!.Value.Y);
        window.Close();
    }

    [AvaloniaFact]
    public void TappingARow_PushesItsList()
    {
        using var rig = MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.LibraryRows.Rows.Single(r => r.Kind == LibraryRowKind.Favorites).IsShown = true;
        window.UpdateLayout();

        var expected = new (LibraryRowKind Kind, Type Page, string Title)[]
        {
            (LibraryRowKind.Playlists, typeof(PlaylistListPageViewModel), "Playlists"),
            (LibraryRowKind.Artists, typeof(ArtistListPageViewModel), "Artists"),
            (LibraryRowKind.Albums, typeof(AlbumGridPageViewModel), "Albums"),
            (LibraryRowKind.Songs, typeof(SongListPageViewModel), "Songs"),
            (LibraryRowKind.Favorites, typeof(SongListPageViewModel), "Favorites"),
        };
        foreach (var (kind, page, title) in expected)
        {
            Tap(RowButton(view, kind));
            Assert.IsType(page, rig.Shell.CurrentPage);
            Assert.Equal(title, rig.Shell.CurrentPage!.Title);
            Assert.Single(rig.Shell.Pages);                                    // pushed, not embedded
            rig.Shell.NavigateBackCommand.Execute(null);
            window.UpdateLayout();
        }
        var favourites = new SongListPageViewModel(rig.Shell, "Favorites", rig.Shell.Library.Favourites);
        Assert.Equal(new[] { "Alpha" }, favourites.Songs.Select(t => t.Title));
        window.Close();
    }

    [AvaloniaFact]
    public async Task DownloadedRow_ExistsOnlyWithAnAccountService_StartsOff_AndListsTheDownloadedSongs()
    {
        using var rig = MakeRig();
        var account = new MobileAccountTests.FakeAccount();
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            Account = account,
            Marshal = a => a(),
            TintFactory = rig.Shell.TintFactory,
        };
        var downloaded = shell.LibraryRows.Rows.Single(r => r.Kind == LibraryRowKind.Downloaded);
        Assert.False(downloaded.IsShown);
        Assert.False(downloaded.IsListed);
        Assert.DoesNotContain(rig.Shell.LibraryRows.Rows, r => r.Kind == LibraryRowKind.Downloaded);

        var beta = rig.Shell.Library.Songs.Single(t => t.Title == "Beta");
        account.DownloadedIds.Add(beta.Id);
        shell.LibraryRows.EditCommand.Execute(null);
        shell.LibraryRows.ActivateCommand.Execute(downloaded);                 // under Edit a tap switches it on
        shell.LibraryRows.DoneCommand.Execute(null);
        await shell.LibraryRows.PendingSave;
        Assert.True(downloaded.IsListed);

        shell.LibraryRows.ActivateCommand.Execute(downloaded);
        var page = Assert.IsType<SongListPageViewModel>(shell.CurrentPage);
        Assert.Equal("Downloaded", page.Title);
        Assert.Equal(new[] { "Beta" }, page.Songs.Select(t => t.Title));
    }

    [AvaloniaFact]
    public async Task EditMode_ShowsChecksAndHandles_TogglesRows_AndDoneSaves()
    {
        using var rig = MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var rows = rig.Shell.LibraryRows;
        var header = MobileFixtures.Named<Grid>(view, "LibraryHeader");
        var done = MobileFixtures.Named<Button>(view, "LibraryEditDoneButton");
        Assert.False(done.IsHitTestVisible);
        Assert.True(MobileFixtures.Named<StackPanel>(view, "HeaderActions").IsHitTestVisible);

        MobileFixtures.Named<Button>(view, "LibraryEditButton").Command!.Execute(null);
        window.UpdateLayout();

        Assert.True(rows.IsEditing);
        Assert.Contains("editing", header.Classes);
        Assert.True(done.IsHitTestVisible);                                    // the round ✓ replaces the pill and profile
        Assert.False(MobileFixtures.Named<StackPanel>(view, "HeaderActions").IsHitTestVisible);
        Assert.False(MobileFixtures.Named<StackPanel>(view, "LibrarySections").IsHitTestVisible);
        Assert.Equal(new[] { "Playlists", "Artists", "Albums", "Songs", "Favorites" }, ListedTitles(view));   // off rows join the list
        var favourites = RowButton(view, LibraryRowKind.Favorites);
        Assert.Contains("editing", favourites.GetVisualDescendants().OfType<Panel>().First(p => p.Classes.Contains("lib-row")).Classes);
        Assert.True(favourites.GetVisualDescendants().OfType<Border>().First(b => b.Name == "RowHandle").IsHitTestVisible);
        var check = favourites.GetVisualDescendants().OfType<Border>().First(b => b.Name == "RowCheck");
        Assert.DoesNotContain("on", check.Classes);

        Tap(favourites);                                                       // a tap under Edit switches, it does not open
        Tap(RowButton(view, LibraryRowKind.Albums));
        window.UpdateLayout();
        Assert.Null(rig.Shell.CurrentPage);
        Assert.Contains("on", check.Classes);
        Assert.Equal(new[] { "Playlists", "Artists", "Albums", "Songs", "Favorites" }, ListedTitles(view));   // still listed while editing

        done.Command!.Execute(null);
        window.UpdateLayout();
        Assert.False(rows.IsEditing);
        Assert.DoesNotContain("editing", header.Classes);
        Assert.Equal(new[] { "Playlists", "Artists", "Songs", "Favorites" }, ListedTitles(view));
        Assert.False(RowButton(view, LibraryRowKind.Songs).GetVisualDescendants().OfType<Border>().First(b => b.Name == "RowHandle").IsHitTestVisible);

        await rows.PendingSave;
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal(new[] { "Playlists", "Artists", "-Albums", "Songs", "Favorites", "-Downloaded" }, saved.PhoneLibraryRows);
        window.Close();
    }

    [AvaloniaFact]
    public async Task HandleDrag_ReordersTheRows_AndTheOrderIsRestoredOnTheNextLaunch()
    {
        List<string> savedRows;
        using (var rig = MakeRig())
        {
            var window = MobileFixtures.Mount(rig.Shell, out var view);
            var page = MobileFixtures.Find<LibraryPage>(view);
            rig.Shell.LibraryRows.EditCommand.Execute(null);
            window.UpdateLayout();

            page.CommitDrag(from: 3, dy: -3 * 52 - 10, rowHeight: 52);         // Songs to the top
            page.CommitDrag(from: 4, dy: -40, rowHeight: 52);                  // Favorites up one, past Albums
            Tap(RowButton(view, LibraryRowKind.Playlists));                    // and Playlists off
            rig.Shell.LibraryRows.DoneCommand.Execute(null);
            window.UpdateLayout();
            Assert.Equal(new[] { "Songs", "Artists", "Albums" }, ListedTitles(view));   // Favorites only moved: still off

            await rig.Shell.LibraryRows.PendingSave;
            savedRows = (await rig.Persistence.LoadSettingsAsync()).PhoneLibraryRows;
            Assert.Equal(new[] { "Songs", "-Playlists", "Artists", "-Favorites", "Albums", "-Downloaded" }, savedRows);
            window.Close();
        }

        // A new launch (fresh rig and shell over a settings file holding that list).
        using var next = MakeRig(savedRows);
        var nextWindow = MobileFixtures.Mount(next.Shell, out var nextView);
        Assert.Equal(new[] { "Songs", "Artists", "Albums" }, ListedTitles(nextView));
        Assert.Equal(new[] { LibraryRowKind.Songs, LibraryRowKind.Playlists, LibraryRowKind.Artists, LibraryRowKind.Favorites, LibraryRowKind.Albums },
            next.Shell.LibraryRows.Rows.Select(r => r.Kind));
        nextWindow.Close();
    }

    /// <summary>The real gesture: press the ≡ handle, the row follows the finger, release over
    /// another row and the list moves; nothing is left translated afterwards.</summary>
    [AvaloniaFact]
    public void PointerDragOnTheHandle_FollowsTheFinger_AndDropsTheRowWhereItIsReleased()
    {
        using var rig = MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.LibraryRows.EditCommand.Execute(null);
        window.UpdateLayout();
        var rowList = MobileFixtures.Named<ItemsControl>(view, "LibraryRowList");
        var songs = RowButton(view, LibraryRowKind.Songs);
        var handle = songs.GetVisualDescendants().OfType<Border>().First(b => b.Name == "RowHandle");
        var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
        var rowHeight = rowList.ContainerFromIndex(3)!.Bounds.Height;

        window.MouseDown(start, Avalonia.Input.MouseButton.Left);
        window.MouseMove(start - new Point(0, 2 * rowHeight + 6));
        window.UpdateLayout();
        var dragged = rowList.ContainerFromIndex(3)!;
        Assert.Equal(-(2 * rowHeight + 6), dragged.RenderTransform!.Value.M32, 1);   // under the finger
        Assert.Equal(1, dragged.ZIndex);                                            // above the rows it passes

        window.MouseUp(start - new Point(0, 2 * rowHeight + 6), Avalonia.Input.MouseButton.Left);
        window.UpdateLayout();
        Assert.Equal(new[] { LibraryRowKind.Playlists, LibraryRowKind.Songs, LibraryRowKind.Artists, LibraryRowKind.Albums, LibraryRowKind.Favorites },
            rig.Shell.LibraryRows.Rows.Select(r => r.Kind));
        Assert.True(rig.Shell.LibraryRows.Rows.Single(r => r.Kind == LibraryRowKind.Songs).IsShown);   // a drag is not a tap
        Assert.All(Enumerable.Range(0, rowList.ItemCount).Select(i => rowList.ContainerFromIndex(i)!),
            c => Assert.True(c.RenderTransform == null && c.ZIndex == 0));
        window.Close();
    }

    [AvaloniaFact]
    public void Done_WithNothingChanged_WritesNothing()
    {
        using var rig = MakeRig();
        rig.Shell.LibraryRows.EditCommand.Execute(null);
        rig.Shell.LibraryRows.DoneCommand.Execute(null);
        Assert.Same(Task.CompletedTask, rig.Shell.LibraryRows.PendingSave);
    }

    [AvaloniaFact]
    public void ATabSwitchOrAPushedPage_FinishesEditing()
    {
        using var rig = MakeRig();
        var rows = rig.Shell.LibraryRows;
        rows.EditCommand.Execute(null);
        rig.Shell.SelectTabCommand.Execute(MobileTab.Search);
        Assert.False(rows.IsEditing);

        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        rows.EditCommand.Execute(null);
        rig.Shell.OpenSettingsCommand.Execute(null);
        Assert.False(rows.IsEditing);
    }

    [Fact]
    public void Decode_SkipsUnknownAndRepeatedKeys_AndAppendsMissingRowsInTheirDefaultState()
    {
        var rows = LibraryRowsViewModel.Decode(new[] { "Songs", "-Artists", "Charts", "songs", "", "3", "Favorites" });
        Assert.Equal(new[]
        {
            (LibraryRowKind.Songs, true), (LibraryRowKind.Artists, false), (LibraryRowKind.Favorites, true),
            (LibraryRowKind.Playlists, true), (LibraryRowKind.Albums, true), (LibraryRowKind.Downloaded, false),
        }, rows);
        Assert.Equal(LibraryRowsViewModel.Defaults, LibraryRowsViewModel.Decode(Array.Empty<string>()));
        Assert.Equal(new[] { "Songs", "-Artists" }, LibraryRowsViewModel.Encode(rows.Take(2)));
    }

    [Theory]
    [InlineData(0, 2, 1, -1)]   // dragging row 0 down over row 2: rows 1 and 2 step up
    [InlineData(0, 2, 2, -1)]
    [InlineData(0, 2, 3, 0)]
    [InlineData(3, 1, 1, 1)]    // dragging row 3 up over row 1: rows 1 and 2 step down
    [InlineData(3, 1, 2, 1)]
    [InlineData(3, 1, 0, 0)]
    [InlineData(2, 2, 1, 0)]
    public void HandleDrag_MakesRoomOnlyBetweenTheStartAndTheRowUnderTheFinger(int from, int over, int index, int shift)
    {
        Assert.Equal(shift, LibraryPage.ShiftFor(index, from, over));
    }
}
