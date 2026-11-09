using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctis.Localization;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Library's list pages share one header (PageHeader, the Artists page's look; tester 11-07):
/// "‹ Library" in the accent with the tools at that row's right, the full-size large title under
/// it, then the page's find field.
/// </summary>
public class MobileLibraryHeadersTests
{
    private static MobileFixtures.Rig MakeRig()
    {
        var a = MobileFixtures.Song("Alpha", "Aurora Lane", favourite: true);
        var b = MobileFixtures.Song("Beta", "Tycho");
        var mix = new Playlist { Name = "Night Drive", TrackIds = { a.Id, b.Id } };
        var rig = MobileFixtures.MakeRig(new[] { a, b }, new[] { MobileFixtures.MakeAlbum("Dive", "Tycho", b) },
            seed: p => p.SavePlaylistsAsync(new List<Playlist> { mix }));
        rig.Library.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = "Tycho", AlbumCount = 1, TrackCount = 1 });
        return rig;
    }

    private static PageHeader HeaderOf(ShellView view, MobilePage page) =>
        MobileFixtures.PageView(view, page).GetVisualDescendants().OfType<PageHeader>().Single();

    [AvaloniaFact]
    public void EveryLibraryListPage_HasTheArtistsHeader_BackLabelOverAFullSizeTitle()
    {
        Loc.Instance.SetCulture("en");
        using var rig = MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            var opens = new (string Title, Action Open)[]
            {
                ("Songs", () => rig.Shell.OpenSongsCommand.Execute(null)),
                ("Albums", () => rig.Shell.OpenAlbumsCommand.Execute(null)),
                ("Artists", () => rig.Shell.OpenArtistsCommand.Execute(null)),
                ("Playlists", () => rig.Shell.OpenPlaylistsCommand.Execute(null)),
                ("Favorites", () => rig.Shell.OpenFavouritesCommand.Execute(null)),
                ("Recently Added", () => rig.Shell.OpenRecentlyAddedCommand.Execute(null)),
                ("Pinned", () => rig.Shell.OpenPinnedCommand.Execute(null)),
            };
            double? titleTop = null;
            foreach (var (title, open) in opens)
            {
                rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
                open();
                MobileFixtures.Land(window);
                var page = rig.Shell.CurrentPage!;
                Assert.Equal(title, page.Title);
                var header = HeaderOf(view, page);
                Assert.True(header.IsEffectivelyVisible, title);
                Assert.Equal("Library", MobileFixtures.Named<TextBlock>(header, "BackLabel").Text);

                // The title at the full large-title size (34 bold), under the back row, at the
                // same place on every page.
                var fit = MobileFixtures.Named<FitWidth>(header, "TitleFit");
                var text = (TextBlock)fit.Child!;
                // (The headless test font's glyphs are wider than Inter's: only the short titles
                // are sure to fit there; "Recently Added" fits on the device.)
                if (title.Length <= 9) Assert.True(fit.Scale == 1, $"{title}: scaled to {fit.Scale}");
                Assert.Equal(34, text.FontSize);
                var back = MobileFixtures.Named<Button>(header, "BackButton");
                var backBottom = back.TranslatePoint(new Point(0, back.Bounds.Height), view)!.Value.Y;
                var top = fit.TranslatePoint(new Point(0, 0), view)!.Value;
                Assert.True(top.Y >= backBottom - 0.5, $"{title}: title at {top.Y}, back row ends at {backBottom}");
                titleTop ??= top.Y;
                Assert.Equal(titleTop.Value, top.Y, 1);
                Assert.Equal(20, top.X, 1);

                // Back pops the page.
                back.Command!.Execute(null);
                Assert.Null(rig.Shell.CurrentPage);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheTools_SitOnTheBackRow_NotBesideTheTitle()
    {
        Loc.Instance.SetCulture("en");
        using var rig = MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
            rig.Shell.OpenSongsCommand.Execute(null);
            MobileFixtures.Land(window);
            var header = HeaderOf(view, rig.Shell.CurrentPage!);
            var tools = header.GetVisualDescendants().OfType<ListTools>().Single();
            Assert.True(tools.IsEffectivelyVisible);
            Assert.Same(rig.Shell.CurrentPage, tools.DataContext);
            var fit = MobileFixtures.Named<FitWidth>(header, "TitleFit");
            var toolsBottom = tools.TranslatePoint(new Point(0, tools.Bounds.Height), view)!.Value.Y;
            Assert.True(toolsBottom <= fit.TranslatePoint(new Point(0, 0), view)!.Value.Y + 0.5);
            // Its right edge lines up with the find field's below it.
            var find = MobileFixtures.PageView(view, rig.Shell.CurrentPage!).GetVisualDescendants().OfType<FindField>().Single();
            Assert.Equal(find.TranslatePoint(new Point(find.Bounds.Width, 0), view)!.Value.X,
                tools.TranslatePoint(new Point(tools.Bounds.Width, 0), view)!.Value.X, 1);

            // Select mode's bar covers the back row; the title stays.
            var songs = (SongListPageViewModel)rig.Shell.CurrentPage!;
            songs.BeginSelectCommand.Execute(null);
            MobileFixtures.Land(window);
            Assert.True(header.IsNavHidden);
            Assert.False(MobileFixtures.Named<Grid>(header, "NavRow").IsHitTestVisible);
            Assert.True(MobileFixtures.Named<FitWidth>(header, "TitleFit").IsEffectivelyVisible);
            Assert.True(MobileFixtures.Named<SelectionHeader>(view, "SelectBar").IsEffectivelyVisible);
            songs.Selection.EndCommand.Execute(null);
            MobileFixtures.Land(window);
            Assert.True(MobileFixtures.Named<Grid>(header, "NavRow").IsHitTestVisible);

            // A playlist's page: the tools and its ⋯ on the same row.
            rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
            rig.Shell.OpenPlaylistsCommand.Execute(null);
            MobileFixtures.Land(window);
            var listHeader = HeaderOf(view, rig.Shell.CurrentPage!);
            Assert.True(listHeader.GetVisualDescendants().OfType<PlaylistTools>().Single().IsEffectivelyVisible);
            rig.Shell.OpenPlaylistCommand.Execute(rig.Shell.Library.Playlists.Single());
            MobileFixtures.Land(window);
            var playlistHeader = HeaderOf(view, rig.Shell.CurrentPage!);
            Assert.Equal("Night Drive", ((TextBlock)MobileFixtures.Named<FitWidth>(playlistHeader, "TitleFit").Child!).Text);
            // Back names the page it returns to.
            Assert.Equal("Playlists", MobileFixtures.Named<TextBlock>(playlistHeader, "BackLabel").Text);
            var more = MobileFixtures.Named<Button>(playlistHeader, "PlaylistMoreButton");
            var moreY = more.TranslatePoint(new Point(0, 0), view)!.Value.Y;
            var backY = MobileFixtures.Named<Button>(playlistHeader, "BackButton").TranslatePoint(new Point(0, 0), view)!.Value.Y;
            Assert.Equal(backY, moreY, 1);
        }
        finally { window.Close(); }
    }

    /// <summary>A long translated title shrinks to fit (no smaller than 70 %) and never runs past
    /// the page's right margin; the back label follows the language too.</summary>
    [AvaloniaFact]
    public void ALongTranslatedTitle_ShrinksToFit_AndTheBackLabelFollowsTheLanguage()
    {
        using var rig = MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        const string Long = "Очень-очень длинные песни";
        try
        {
            Loc.Instance.SetCulture("ru");
            Loc.Instance.SetOverlays(new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["ru"] = new Dictionary<string, string> { ["Nav.Songs"] = Long },
            });
            rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
            rig.Shell.OpenSongsCommand.Execute(null);
            MobileFixtures.Land(window);
            var header = HeaderOf(view, rig.Shell.CurrentPage!);
            var fit = MobileFixtures.Named<FitWidth>(header, "TitleFit");
            Assert.Equal(Long, ((TextBlock)fit.Child!).Text);
            Assert.True(fit.Scale < 1, "the long title did not shrink");
            Assert.True(fit.Scale >= 0.7 - 1e-9, $"shrank below 70 % ({fit.Scale})");
            var right = fit.TranslatePoint(new Point(fit.Bounds.Width, 0), view)!.Value.X;
            Assert.True(right <= view.Bounds.Width - 20 + 0.5, $"title ends at {right}");
            Assert.Equal(Loc.T("Tab.Library"), MobileFixtures.Named<TextBlock>(header, "BackLabel").Text);
            Assert.NotEqual("Library", Loc.T("Tab.Library"));
        }
        finally
        {
            Loc.Instance.SetOverlays(null);
            Loc.Instance.SetCulture("en");
            window.Close();
        }
    }
}
