using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #99: Settings › Library › Organisation can make the Artists name sort skip a
/// leading word ("The Beatles" under B). Off by default with the list ["The"]; the shown
/// names never change, and with the toggle off the order is exactly the old one.
/// </summary>
public class ArtistSortIgnoredWordsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static readonly string[] The = { "The" };

    private static List<Artist> Sample() => new()
    {
        new Artist { Name = "The Beatles", TrackCount = 10, AlbumCount = 2 },
        new Artist { Name = "Abba", TrackCount = 10, AlbumCount = 2 },
        new Artist { Name = "Theory of a Deadman", TrackCount = 10, AlbumCount = 2 },
        new Artist { Name = "Coldplay", TrackCount = 10, AlbumCount = 2 },
    };

    private static List<string> Names(List<ArtistRow> rows)
        => rows.SelectMany(r => r.Artists).Select(a => a.Name).ToList();

    // ── Matching rule ──

    [Theory]
    [InlineData("The Beatles", "Beatles")]
    [InlineData("the weeknd", "weeknd")]
    [InlineData("THE  Strokes", "Strokes")]
    [InlineData("The\tKillers", "Killers")]
    [InlineData("Theory of a Deadman", "Theory of a Deadman")] // no whitespace after the word
    [InlineData("The", "The")]                                 // nothing left to sort by
    [InlineData("The ", "The ")]
    [InlineData("Beatles, The", "Beatles, The")]               // only a LEADING word counts
    [InlineData("", "")]
    public void SortKey_SkipsALeadingWordFollowedByWhitespace(string name, string expected)
        => Assert.Equal(expected, ArtistSortWords.SortKey(name, The));

    [Fact]
    public void SortKey_NoWords_IsTheName()
        => Assert.Equal("The Beatles", ArtistSortWords.SortKey("The Beatles", Array.Empty<string>()));

    [Fact]
    public void SortKey_PrefersTheLongestMatchingWord_AndStripsOnlyOnce()
    {
        var words = new[] { "The", "The The" };
        Assert.Equal("Band", ArtistSortWords.SortKey("The The Band", words));
        Assert.Equal("The", ArtistSortWords.SortKey("The The", The));
        Assert.Equal("Tigres del Norte", ArtistSortWords.SortKey("Los Tigres del Norte", new[] { "The", "Los" }));
    }

    [Fact]
    public void Normalize_TrimsDropsBlanksAndDuplicatesIgnoringCase()
    {
        var normalized = ArtistSortWords.Normalize(new[] { " The ", "", "   ", "the", "Los", "LOS", null! });
        Assert.Equal(new[] { "The", "Los" }, normalized);
        Assert.Empty(ArtistSortWords.Normalize(null));
    }

    // ── Artists grid order ──

    [Fact]
    public void BuildRows_WithoutWords_KeepsThePlainNameOrder()
    {
        var asc = LibraryArtistsViewModel.BuildRows(Sample(), string.Empty, "name", ascending: true);
        Assert.Equal(new[] { "Abba", "Coldplay", "The Beatles", "Theory of a Deadman" }, Names(asc));

        var empty = LibraryArtistsViewModel.BuildRows(Sample(), string.Empty, "name", ascending: true, Array.Empty<string>());
        Assert.Equal(Names(asc), Names(empty));
    }

    [Fact]
    public void BuildRows_WithThe_SortsTheBeatlesUnderB_Ascending()
    {
        var rows = LibraryArtistsViewModel.BuildRows(Sample(), string.Empty, "name", ascending: true, The);
        Assert.Equal(new[] { "Abba", "The Beatles", "Coldplay", "Theory of a Deadman" }, Names(rows));
    }

    [Fact]
    public void BuildRows_WithThe_SortsTheBeatlesUnderB_Descending()
    {
        var off = LibraryArtistsViewModel.BuildRows(Sample(), string.Empty, "name", ascending: false);
        Assert.Equal(new[] { "Theory of a Deadman", "The Beatles", "Coldplay", "Abba" }, Names(off));

        var on = LibraryArtistsViewModel.BuildRows(Sample(), string.Empty, "name", ascending: false, The);
        Assert.Equal(new[] { "Theory of a Deadman", "Coldplay", "The Beatles", "Abba" }, Names(on));
    }

    [Fact]
    public void BuildRows_WithThe_KeepsFavoritesFirst()
    {
        var artists = Sample();
        artists.Single(a => a.Name == "Theory of a Deadman").IsFavorite = true;
        var rows = LibraryArtistsViewModel.BuildRows(artists, string.Empty, "name", ascending: true, The);
        Assert.Equal(new[] { "Theory of a Deadman", "Abba", "The Beatles", "Coldplay" }, Names(rows));
    }

    [Theory]
    [InlineData("songs", true)]
    [InlineData("songs", false)]
    [InlineData("albums", true)]
    [InlineData("albums", false)]
    public void BuildRows_CountSorts_BreakTiesOnTheStrippedName(string mode, bool ascending)
    {
        // Every sample artist has the same counts, so the name tie-break decides.
        var off = LibraryArtistsViewModel.BuildRows(Sample(), string.Empty, mode, ascending);
        Assert.Equal(new[] { "Abba", "Coldplay", "The Beatles", "Theory of a Deadman" }, Names(off));

        var on = LibraryArtistsViewModel.BuildRows(Sample(), string.Empty, mode, ascending, The);
        Assert.Equal(new[] { "Abba", "The Beatles", "Coldplay", "Theory of a Deadman" }, Names(on));
    }

    [Fact]
    public void BuildRows_UnderASearch_RankStillWins()
    {
        // "the" is a prefix match for "The Beatles" but mid-word in "Anthem", so the Beatles
        // lead even though their sort key ("Beatles") comes after "Anthem".
        var artists = new List<Artist>
        {
            new() { Name = "Anthem" },
            new() { Name = "The Beatles" },
        };
        var rows = LibraryArtistsViewModel.BuildRows(artists, "the", "name", ascending: true, The);
        Assert.Equal(new[] { "The Beatles", "Anthem" }, Names(rows));
    }

    [AvaloniaFact]
    public async Task SetSortIgnoredWords_ResortsTheVisibleGrid()
    {
        var lib = new FakeLibraryService();
        lib.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = "The Beatles" });
        lib.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = "Coldplay" });
        lib.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = "Abba" });
        var vm = new LibraryArtistsViewModel(lib) { IsActive = true };

        List<string> Shown() => vm.ArtistRows.SelectMany(r => r.Artists).Select(a => a.Name).ToList();
        await PumpUntil(() => vm.ArtistRows.Count > 0);
        Assert.Equal(new[] { "Abba", "Coldplay", "The Beatles" }, Shown());

        vm.SetSortIgnoredWords(The);
        await PumpUntil(() => Shown()[1] == "The Beatles");
        Assert.Equal(new[] { "Abba", "The Beatles", "Coldplay" }, Shown());

        vm.SetSortIgnoredWords(Array.Empty<string>());
        await PumpUntil(() => Shown()[2] == "The Beatles");
        Assert.Equal(new[] { "Abba", "Coldplay", "The Beatles" }, Shown());
    }

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    // ── Settings ──

    private SettingsViewModel CreateSettings() => new(
        new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());

    [Fact]
    public void FreshInstall_IsOffWithJustThe()
    {
        var fresh = new AppSettings();
        Assert.False(fresh.IgnoreLeadingWordsInArtistSort);
        Assert.Equal(The, fresh.ArtistSortIgnoredWords);
    }

    [AvaloniaFact]
    public async Task ActiveWords_AreEmptyWhileOff_AndAnnouncedOnEveryChange()
    {
        var vm = CreateSettings();
        await vm.LoadAsync();
        Assert.Equal(The, vm.ArtistSortIgnoredWords);
        Assert.Empty(vm.ActiveArtistSortIgnoredWords);

        var raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.ActiveArtistSortIgnoredWords)) raised++;
        };

        vm.IgnoreLeadingWordsInArtistSort = true;
        Assert.Equal(1, raised);
        Assert.Equal(The, vm.ActiveArtistSortIgnoredWords);

        vm.NewArtistSortIgnoredWord = "  Los ";
        vm.AddArtistSortIgnoredWordCommand.Execute(null);
        Assert.Equal(2, raised);
        Assert.Equal(new[] { "The", "Los" }, vm.ActiveArtistSortIgnoredWords);
        Assert.Equal(string.Empty, vm.NewArtistSortIgnoredWord);

        vm.RemoveArtistSortIgnoredWordCommand.Execute("The");
        Assert.Equal(3, raised);
        Assert.Equal(new[] { "Los" }, vm.ActiveArtistSortIgnoredWords);
    }

    [AvaloniaFact]
    public async Task AddWord_IgnoresBlanksAndCaseInsensitiveDuplicates()
    {
        var vm = CreateSettings();
        await vm.LoadAsync();

        vm.NewArtistSortIgnoredWord = "   ";
        vm.AddArtistSortIgnoredWordCommand.Execute(null);
        vm.NewArtistSortIgnoredWord = "the";
        vm.AddArtistSortIgnoredWordCommand.Execute(null);

        Assert.Equal(The, vm.ArtistSortIgnoredWords);
    }

    [AvaloniaFact]
    public async Task ToggleAndWords_SurviveSaveAndReload_AndResetRestoresThe()
    {
        var vm = CreateSettings();
        await vm.LoadAsync();
        vm.IgnoreLeadingWordsInArtistSort = true;
        vm.RemoveArtistSortIgnoredWordCommand.Execute("The");
        vm.NewArtistSortIgnoredWord = "A";
        vm.AddArtistSortIgnoredWordCommand.Execute(null);
        await vm.SaveAsync();

        var reloaded = CreateSettings();
        await reloaded.LoadAsync();
        Assert.True(reloaded.IgnoreLeadingWordsInArtistSort);
        Assert.Equal(new[] { "A" }, reloaded.ArtistSortIgnoredWords);
        Assert.Equal(new[] { "A" }, reloaded.ActiveArtistSortIgnoredWords);

        reloaded.ResetArtistSortIgnoredWordsCommand.Execute(null);
        Assert.Equal(The, reloaded.ArtistSortIgnoredWords);
    }
}
