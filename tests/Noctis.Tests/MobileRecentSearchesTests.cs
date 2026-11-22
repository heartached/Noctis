using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

/// <summary>
/// Search › Recent Searches (beta testers 11-22): the empty field lists the searches kept before,
/// newest first, each once ignoring case, at most ten. A search is kept when a result is opened or
/// the keyboard's Search key is pressed, never per keystroke, and the list survives a restart
/// (AppSettings.MobileRecentSearches).
/// </summary>
public class MobileRecentSearchesTests
{
    private static MobileFixtures.Rig Library(Func<Noctis.Services.PersistenceService, Task>? seed = null)
    {
        var crazy = MobileFixtures.Song("Crazy In Love", artist: "Beyoncé");
        var halo = MobileFixtures.Song("Halo", artist: "Beyoncé", plays: 3);
        var rock = MobileFixtures.Song("Rock & Roll", artist: "Led Zeppelin");
        var album = MobileFixtures.MakeAlbum("Dangerously in Love", "Beyoncé", crazy);
        var rig = MobileFixtures.MakeRig(new[] { crazy, halo, rock }, new[] { album }, seed: seed);
        rig.Library.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = "Beyoncé", AlbumCount = 1, TrackCount = 2 });
        rig.Library.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = "Led Zeppelin", TrackCount = 1 });
        return rig;
    }

    [Fact]
    public void Typing_KeepsNothing_TheSearchKeyKeepsTheSearch()
    {
        using var rig = Library();
        var search = rig.Shell.Search;
        foreach (var text in new[] { "h", "ha", "hal", "halo" }) search.Query = text;
        Assert.Empty(search.RecentSearches);

        search.SubmitCommand.Execute(null);
        Assert.Equal(new[] { "halo" }, search.RecentSearches);

        search.Query = "   ";
        search.SubmitCommand.Execute(null);   // a blank field keeps nothing
        Assert.Equal(new[] { "halo" }, search.RecentSearches);
    }

    [Fact]
    public void OpeningAResult_KeepsTheSearch_NewestFirst_OnceIgnoringCase()
    {
        using var rig = Library();
        var search = rig.Shell.Search;

        search.Query = "zeppelin";
        search.SetScopeCommand.Execute(SearchScope.Songs);
        search.PlaySongCommand.Execute(search.Songs.Single());
        search.SetScopeCommand.Execute(SearchScope.All);

        search.Query = "dangerously";
        search.OpenAlbumCommand.Execute(search.Albums.Single());
        Assert.Equal("Dangerously in Love", rig.Shell.CurrentPage!.Title);
        rig.Shell.GoBack();

        search.Query = " ZEPPELIN ";
        search.SubmitCommand.Execute(null);

        Assert.Equal(new[] { "ZEPPELIN", "dangerously" }, search.RecentSearches);
    }

    [Fact]
    public void TheListHoldsTen()
    {
        var kept = Enumerable.Range(1, 12).Aggregate(new List<string>(), (list, i) => SearchPageViewModel.WithRecent(list, "q" + i));
        Assert.Equal(SearchPageViewModel.RecentCap, kept.Count);
        Assert.Equal("q12", kept[0]);
        Assert.Equal("q3", kept[^1]);
    }

    [Fact]
    public void ARecentSearch_SearchesAgainAndMovesUp_RemoveAndClear_TheHintComesBack()
    {
        using var rig = Library();
        var search = rig.Shell.Search;
        Assert.True(search.ShowPrompt);
        Assert.False(search.ShowRecentSearches);
        foreach (var text in new[] { "halo", "rock", "beyonce" })
        {
            search.Query = text;
            search.SubmitCommand.Execute(null);
        }
        search.Query = string.Empty;
        Assert.True(search.ShowRecentSearches);
        Assert.False(search.ShowPrompt);   // the list takes the hint's place
        Assert.Equal(new[] { "beyonce", "rock", "halo" }, search.RecentSearches);

        search.SearchRecentCommand.Execute("halo");
        Assert.Equal("halo", search.Query);
        Assert.Equal(new[] { "Halo" }, search.Songs.Select(t => t.Title));
        Assert.False(search.ShowRecentSearches);   // results now
        Assert.Equal(new[] { "halo", "beyonce", "rock" }, search.RecentSearches);

        search.Query = string.Empty;
        search.RemoveRecentCommand.Execute("beyonce");
        Assert.Equal(new[] { "halo", "rock" }, search.RecentSearches);

        search.ClearRecentCommand.Execute(null);
        Assert.Empty(search.RecentSearches);
        Assert.False(search.ShowRecentSearches);
        Assert.True(search.ShowPrompt);
    }

    [Fact]
    public async Task RecentSearches_SurviveARestart()
    {
        List<string> saved;
        using (var rig = Library())
        {
            var search = rig.Shell.Search;
            search.Query = "rock";
            search.SubmitCommand.Execute(null);
            search.Query = "halo";
            search.SubmitCommand.Execute(null);
            await search.PendingRecentSave;
            saved = (await rig.Persistence.LoadSettingsAsync()).MobileRecentSearches;
            Assert.Equal(new[] { "halo", "rock" }, saved);
        }

        using var restarted = Library(async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MobileRecentSearches = new List<string>(saved) { "HALO", " " };   // a hand-edited file: dupes and blanks go
            await p.SaveSettingsAsync(s);
        });
        await restarted.Shell.InitializeAsync();
        Assert.Equal(new[] { "halo", "rock" }, restarted.Shell.Search.RecentSearches);
        Assert.True(restarted.Shell.Search.ShowRecentSearches);
    }

    [AvaloniaFact]
    public void Page_ListsTheRecentSearches_AndTheKeyboardsSearchKeyKeepsOne()
    {
        using var rig = Library();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.SelectTabCommand.Execute(MobileTab.Search);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var page = MobileFixtures.Find<SearchPage>(view);
        var box = MobileFixtures.Named<TextBox>(page, "SearchBox");
        Assert.False(MobileFixtures.Named<ScrollViewer>(page, "RecentScroll").IsVisible);

        rig.Shell.Search.Query = "halo";
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });
        Assert.Equal(new[] { "halo" }, rig.Shell.Search.RecentSearches);

        rig.Shell.Search.Query = string.Empty;
        window.UpdateLayout();
        var recent = MobileFixtures.Named<ScrollViewer>(page, "RecentScroll");
        Assert.True(recent.IsVisible);
        Assert.False(MobileFixtures.Named<TextBlock>(page, "SearchPrompt").IsVisible);
        Assert.Contains(recent.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "halo");
        window.Close();
    }
}
