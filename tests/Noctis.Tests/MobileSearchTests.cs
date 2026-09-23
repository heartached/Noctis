using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>The Search tab: matching rules, ranking, caps, and how the page is reached.</summary>
public class MobileSearchTests
{
    private static MobileFixtures.Rig Library()
    {
        var crazy = MobileFixtures.Song("Crazy In Love", artist: "Beyoncé");
        var halo = MobileFixtures.Song("Halo", artist: "Beyoncé", plays: 3);
        var rock = MobileFixtures.Song("Rock & Roll", artist: "Led Zeppelin");
        var album = MobileFixtures.MakeAlbum("Dangerously in Love", "Beyoncé", crazy);
        var rig = MobileFixtures.MakeRig(new[] { crazy, halo, rock }, new[] { album });
        rig.Library.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = "Beyoncé", AlbumCount = 1, TrackCount = 2 });
        rig.Library.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = "Led Zeppelin", TrackCount = 1 });
        return rig;
    }

    [Fact]
    public void Search_FindsSongsAlbumsAndArtists_IgnoringAccentsAndCase()
    {
        using var rig = Library();
        var search = rig.Shell.Search;

        search.Query = "beyonce";

        Assert.Equal(new[] { "Halo", "Crazy In Love" }, search.Songs.Select(t => t.Title));   // more plays first
        Assert.Equal(new[] { "Dangerously in Love" }, search.Albums.Select(a => a.Name));
        Assert.Equal(new[] { "Beyoncé" }, search.Artists.Select(a => a.Name));
        Assert.False(search.ShowNoResults);
    }

    [Fact]
    public void Search_EmptyQueryShowsThePrompt_PunctuationUsesTheRawText_AndMissesSayNoResults()
    {
        using var rig = Library();
        var search = rig.Shell.Search;
        Assert.True(search.ShowPrompt);
        Assert.Empty(search.Songs);

        search.Query = "&";
        Assert.Equal(new[] { "Rock & Roll" }, search.Songs.Select(t => t.Title));

        search.Query = "zzzz";
        Assert.True(search.ShowNoResults);
        Assert.False(search.ShowPrompt);
    }

    [Fact]
    public void Search_TitlePrefixRanksFirst_AndEachSectionIsCapped()
    {
        var songs = Enumerable.Range(0, 40).Select(i => MobileFixtures.Song($"Some Love {i:D2}", plays: 10)).ToList();
        songs.Add(MobileFixtures.Song("Love Me Do"));
        using var rig = MobileFixtures.MakeRig(songs.ToArray());

        rig.Shell.Search.Query = "love";

        Assert.Equal(SearchPageViewModel.SongCap, rig.Shell.Search.Songs.Count);
        Assert.Equal("Love Me Do", rig.Shell.Search.Songs[0].Title);
    }

    [Fact]
    public void PlaySong_FromResults_PlaysTheResultList_AndAnArtistOpensItsPage()
    {
        using var rig = Library();
        var search = rig.Shell.Search;
        search.Query = "beyonce";

        search.PlaySongCommand.Execute(search.Songs[1]);
        Assert.Equal("Crazy In Love", rig.Shell.Player.CurrentTrack!.Title);

        search.OpenArtistCommand.Execute(search.Artists[0]);
        Assert.Equal("Beyoncé", rig.Shell.CurrentPage!.Title);
    }

    [AvaloniaFact]
    public void SearchTab_MiniButtonAndLibraryField_AllOpenTheSearchPage()
    {
        using var rig = Library();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        Assert.True(view.FindControl<Button>("SearchTab")!.IsVisible);

        var field = MobileFixtures.Named<Button>(view, "LibrarySearchField");
        field.Command!.Execute(field.CommandParameter);
        window.UpdateLayout();
        Assert.True(rig.Shell.IsSearchRootVisible);
        var page = MobileFixtures.Find<SearchPage>(view);
        Assert.True(page.IsVisible);

        rig.Shell.Search.Query = "halo";
        window.UpdateLayout();
        Assert.Contains(MobileFixtures.Named<ItemsControl>(page, "SongResults").GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Halo");

        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        window.UpdateLayout();
        var mini = view.FindControl<Button>("MiniSearchButton")!;
        Assert.True(mini.IsVisible);
        mini.Command!.Execute(mini.CommandParameter);
        Assert.True(rig.Shell.IsSearchSelected);
        window.Close();
    }
}
