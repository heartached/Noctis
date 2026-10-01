using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
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

    [Fact]
    public void Search_NonLatinTitlesMatchThemselves()
    {
        using var rig = MobileFixtures.MakeRig(new[]
        {
            MobileFixtures.Song("夜に駆ける"), MobileFixtures.Song("Кино"), MobileFixtures.Song("사랑해요"),
        });
        var search = rig.Shell.Search;

        search.Query = "夜に";
        Assert.Equal(new[] { "夜に駆ける" }, search.Songs.Select(t => t.Title));
        search.Query = "кино";
        Assert.Equal(new[] { "Кино" }, search.Songs.Select(t => t.Title));
        search.Query = "사랑해요";
        Assert.Equal(new[] { "사랑해요" }, search.Songs.Select(t => t.Title));
    }

    /// <summary>The keyboard comes up when the user arrives at Search, not when Back pops a
    /// page opened from the results (it would cover the results they came back to).</summary>
    [AvaloniaFact]
    public void SearchBox_FocusesOnArrival_NotWhenAPageOpenedFromResultsIsPopped()
    {
        using var rig = Library();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var page = MobileFixtures.Find<SearchPage>(view);

        rig.Shell.SelectTabCommand.Execute(MobileTab.Search);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var box = MobileFixtures.Named<TextBox>(page, "SearchBox");   // templated once shown
        Assert.True(box.IsFocused);

        // Tap the artist result, as on device: the tap takes focus off the box.
        rig.Shell.Search.Query = "beyonce";
        window.UpdateLayout();
        var result = MobileFixtures.Named<ItemsControl>(page, "ArtistResults").GetVisualDescendants().OfType<Button>().First();
        var centre = result.TranslatePoint(new Point(result.Bounds.Width / 2, result.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Beyoncé", rig.Shell.CurrentPage!.Title);

        rig.Shell.GoBack();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.True(page.IsVisible);
        Assert.False(box.IsFocused);

        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        window.UpdateLayout();
        rig.Shell.SelectTabCommand.Execute(MobileTab.Search);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void SearchTab_MiniButtonAndLibraryField_AllOpenTheSearchPage()
    {
        using var rig = Library();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        Assert.True(view.FindControl<Button>("SearchTab")!.IsVisible);

        var tab = view.FindControl<Button>("SearchTab")!;
        tab.Command!.Execute(tab.CommandParameter);
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
