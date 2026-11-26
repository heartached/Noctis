using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-26: "Choose Artwork — From Apple Music". An artist's name lists their artwork library
/// (their releases only, all of them), and "artist title" finds the release even where the iTunes
/// album index has a hole ("DON'T TRY THIS - EP"). Fixtures are the real answers, trimmed
/// (Fixtures/ArtworkLibrary, recorded 2026-11-26), so nothing here touches the network.
/// </summary>
public class ArtworkLibrarySearchTests
{
    private const long ChaseAtlantic = 879496517;

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ArtworkLibrary", name));

    private static IReadOnlyList<ITunesArtworkService.ArtworkCandidate> Discography()
        => ITunesArtworkService.ParseCandidates(Fixture("lookup-artist-879496517.json"));

    // ── normalisation and matching ──

    [Theory]
    [InlineData("DON'T TRY THIS - EP", "dont try this")]
    [InlineData("DON’T TRY THIS - EP", "dont try this")]
    [InlineData("Beyoncé - Single", "beyonce")]
    [InlineData("  Part Three  -  EP ", "part three")]
    [InlineData("BEAUTY IN DEATH (DELUXE EDITION)", "beauty in death deluxe edition")]
    public void Normalize_FoldsCaseAccentsApostrophesAndStoreSuffix(string title, string expected)
        => Assert.Equal(expected, ArtworkLibrarySearch.Normalize(title));

    [Theory]
    [InlineData("DON'T TRY THIS - EP", "DONT TRY THIS", 100)]
    [InlineData("DON'T TRY THIS - EP", "don't try this", 100)]
    [InlineData("Nostalgia - EP", "DONT TRY THIS", 0)]
    [InlineData("BEAUTY IN DEATH (DELUXE EDITION)", "beauty in death", 80)]
    [InlineData("LOST IN HEAVEN (HIGH AS HELL) [Deluxe]", "high as hell", 60)]
    [InlineData("OHMAMI (With Maggie Lindemann) - Single", "ohmami maggie", 40)]
    [InlineData("Paradise - EP", "paradi se", 90)]
    [InlineData("Café Tacvba", "cafe tacvba", 100)]
    public void TitleScore_RanksExactThenPrefixThenRunThenWords(string title, string wanted, int expected)
        => Assert.Equal(expected, ArtworkLibrarySearch.TitleScore(title, wanted));

    [Fact]
    public void IsSingleOrEp_ReadsTheStoreSuffix()
    {
        Assert.True(ArtworkLibrarySearch.IsSingleOrEp("DON'T TRY THIS - EP"));
        Assert.True(ArtworkLibrarySearch.IsSingleOrEp("MOLLY - Single"));
        Assert.False(ArtworkLibrarySearch.IsSingleOrEp("PHASES"));
        Assert.False(ArtworkLibrarySearch.IsSingleOrEp("Single Ladies"));
    }

    // ── artist resolution (the query → request plan) ──

    [Fact]
    public void ResolveArtist_ExactName_IsAnArtistOnlySearch()
    {
        var artists = ITunesArtworkService.ParseArtists(Fixture("search-artist-chase-atlantic.json"));
        var match = ArtworkLibrarySearch.ResolveArtist("CHASE ATLANTIC", artists, preferArtist: null);
        Assert.NotNull(match);
        Assert.Equal(("Chase Atlantic", ChaseAtlantic, ""), (match!.Name, match.Id, match.Rest));
    }

    [Fact]
    public void ResolveArtist_ArtistPrefix_LeavesTheTitle()
    {
        var artists = ITunesArtworkService.ParseArtists(Fixture("search-artist-chase-atlantic-dont-try-this.json"));
        var match = ArtworkLibrarySearch.ResolveArtist("CHASE ATLANTIC DONT TRY THIS", artists, null);
        Assert.Equal(("Chase Atlantic", ChaseAtlantic, "dont try this"), (match!.Name, match.Id, match.Rest));

        // Title first, artist last reads the same.
        match = ArtworkLibrarySearch.ResolveArtist("Don't Try This - Chase Atlantic", artists, null);
        Assert.Equal("dont try this", match!.Rest);
    }

    [Fact]
    public void ResolveArtist_FromAlbumCredits_WhenTheArtistSearchFoundNothing()
    {
        var hits = ITunesArtworkService.ParseCandidates(Fixture("search-album-chase-atlantic-dont-try-this.json"));
        var match = ArtworkLibrarySearch.ResolveArtist("chase atlantic dont try this",
            hits.Select(h => (h.ArtistName, h.ArtistId)), null);
        Assert.Equal(ChaseAtlantic, match!.Id);
    }

    [Fact]
    public void ResolveArtist_NoNameInTheQuery_IsAPlainSearch()
    {
        var artists = ITunesArtworkService.ParseArtists(Fixture("search-artist-chase-atlantic.json"));
        // "The Neighbourhood" is a candidate but is not in the query.
        Assert.Null(ArtworkLibrarySearch.ResolveArtist("wiped out", artists, null));
        // Part of a name is not the name.
        Assert.Null(ArtworkLibrarySearch.ResolveArtist("chase", artists, null));
    }

    [Fact]
    public void ResolveArtist_LongestName_ThenTheTaggedArtist()
    {
        var candidates = new[] { ("Chase", 1L), ("Chase Atlantic", 2L) };
        Assert.Equal(2, ArtworkLibrarySearch.ResolveArtist("chase atlantic phases", candidates, null)!.Id);
        var twins = new[] { ("Nirvana", 10L), ("Nirvana", 11L) };
        Assert.Equal(10, ArtworkLibrarySearch.ResolveArtist("nirvana", twins, null)!.Id);
    }

    [Fact]
    public void ArtistSearchTerms_TagQuery_AlsoSearchesTheTaggedArtist()
    {
        Assert.Equal(new[] { "CHASE ATLANTIC DONT TRY THIS" },
            ArtworkLibrarySearch.ArtistSearchTerms("CHASE ATLANTIC DONT TRY THIS", null));
        Assert.Equal(new[] { "Chase Atlantic BEAUTY IN DEATH", "Chase Atlantic" },
            ArtworkLibrarySearch.ArtistSearchTerms("Chase Atlantic BEAUTY IN DEATH", "Chase Atlantic feat. Maggie Lindemann"));
        // Not in the query: not searched.
        Assert.Equal(new[] { "PHASES" }, ArtworkLibrarySearch.ArtistSearchTerms("PHASES", "Someone Else"));
    }

    [Fact]
    public void RequestUrls_AreTheArtistSearchAndTheWholeDiscography()
    {
        Assert.Equal("https://itunes.apple.com/search?term=CHASE%20ATLANTIC&media=music&entity=musicArtist&country=us&limit=5",
            ITunesArtworkService.ArtistSearchUrl("CHASE ATLANTIC", "US"));
        // /lookup caps at 200 (limit=300 answers 200, offset is ignored): newest 200, one request.
        Assert.Equal("https://itunes.apple.com/lookup?id=879496517&entity=album&limit=200&sort=recent&country=gb",
            ITunesArtworkService.DiscographyUrl(ChaseAtlantic, "gb"));
        // No attribute=albumTerm: that filter is what let tribute records outrank the band.
        Assert.DoesNotContain("attribute", ITunesArtworkService.LibraryAlbumSearchUrl("x", "us"));
    }

    // ── laying out the answer ──

    [Fact]
    public void Arrange_ArtistOnly_ListsTheirLibrary_NoOtherArtists()
    {
        var artist = new ArtworkLibrarySearch.ArtistMatch("Chase Atlantic", ChaseAtlantic, "");
        var hits = ITunesArtworkService.ParseCandidates(Fixture("search-album-chase-atlantic.json"));
        var result = ArtworkLibrarySearch.Arrange("CHASE ATLANTIC", artist, Discography(), hits);

        Assert.Equal("Chase Atlantic", result.ArtistName);
        Assert.Equal(new[] { ArtworkSectionKind.Albums, ArtworkSectionKind.SinglesAndEps, ArtworkSectionKind.AppearsOn },
            result.Sections.Select(s => s.Kind));
        var own = result.Sections.Where(s => s.Kind != ArtworkSectionKind.AppearsOn).SelectMany(s => s.Items).ToList();
        Assert.All(own, c => Assert.Equal(ChaseAtlantic, c.ArtistId));
        // The search's strangers are nowhere (tributes, L'Arc-en-Ciel, Travis Scott…).
        foreach (var stranger in new[] { "Lullaby Versions of Chase Atlantic - Single", "CHASE - EP",
                     "Birds In The Trap Sing McKnight", "Piano Dreamers Play Chase Atlantic (Instrumental)" })
            Assert.DoesNotContain(result.All, c => c.CollectionName == stranger);

        var albums = result.Sections[0].Items.Select(c => c.CollectionName).ToList();
        Assert.Equal("LOST IN HEAVEN (HIGH AS HELL) [Deluxe]", albums[0]); // newest first
        Assert.Contains("PHASES", albums);
        Assert.DoesNotContain(albums, a => a.EndsWith(" - EP", StringComparison.Ordinal) || a.EndsWith(" - Single", StringComparison.Ordinal));
        var singles = result.Sections[1].Items.Select(c => c.CollectionName).ToList();
        Assert.Contains("DON'T TRY THIS - EP", singles);
        Assert.Equal("WHEREVER YOU ARE - Single", singles[0]);
        // The explicit and the clean MAMACITA are one tile.
        Assert.Single(singles, s => s == "MAMACITA - Single");
        // Credited on someone else's record: its own section, last.
        Assert.Contains(result.Sections[2].Items, c => c.CollectionName.StartsWith("hurts 2 hate somebody", StringComparison.Ordinal));
        // 29 own rows less the clean MAMACITA, and 7 they are credited on.
        Assert.Equal(28, result.ReleaseCount);
        Assert.Equal(7, result.Sections[2].Items.Count);
    }

    [Fact]
    public void Arrange_ArtistAndTitle_PutsTheReleaseFirst_ThenTheRestOfTheirs()
    {
        var artist = new ArtworkLibrarySearch.ArtistMatch("Chase Atlantic", ChaseAtlantic, "dont try this");
        var hits = ITunesArtworkService.ParseCandidates(Fixture("search-album-chase-atlantic-dont-try-this.json"));
        var result = ArtworkLibrarySearch.Arrange("CHASE ATLANTIC DONT TRY THIS", artist, Discography(), hits);

        Assert.Equal(ArtworkSectionKind.Matches, result.Sections[0].Kind);
        Assert.Equal("DON'T TRY THIS - EP", Assert.Single(result.Sections[0].Items).CollectionName);
        Assert.Equal(ArtworkSectionKind.MoreByArtist, result.Sections[1].Kind);
        Assert.Contains(result.Sections[1].Items, c => c.CollectionName == "Nostalgia - EP");
    }

    [Fact]
    public void Arrange_TitleMatches_BestFirst()
    {
        var artist = new ArtworkLibrarySearch.ArtistMatch("Chase Atlantic", ChaseAtlantic, "beauty in death");
        var result = ArtworkLibrarySearch.Arrange("chase atlantic beauty in death", artist, Discography(), Array.Empty<ITunesArtworkService.ArtworkCandidate>());
        Assert.Equal(new[] { "BEAUTY IN DEATH", "BEAUTY IN DEATH (DELUXE EDITION)" },
            result.Sections[0].Items.Select(c => c.CollectionName));
    }

    [Fact]
    public void Arrange_NoArtist_IsAPlainList_TitleMatchesFirst()
    {
        var hits = ITunesArtworkService.ParseCandidates(Fixture("search-album-chase-atlantic.json"));
        var result = ArtworkLibrarySearch.Arrange("Nostalgia", null, Array.Empty<ITunesArtworkService.ArtworkCandidate>(), hits);
        var section = Assert.Single(result.Sections);
        Assert.Equal(ArtworkSectionKind.Results, section.Kind);
        Assert.Null(result.ArtistName);
        Assert.Equal("Nostalgia - EP", section.Items[0].CollectionName);
    }

    // ── the service end to end, on the recorded answers ──

    /// <summary>Answers the requests the library search makes from the fixtures; records them.</summary>
    internal sealed class Apple
    {
        public readonly List<string> Urls = new();

        public Task<HttpResponseMessage> Respond(HttpRequestMessage req)
        {
            var url = req.RequestUri!.AbsoluteUri;
            lock (Urls) Urls.Add(url);
            string? body = null;
            var dtt = url.Contains("DONT%20TRY%20THIS", StringComparison.Ordinal);
            if (url.StartsWith("https://music.apple.com/", StringComparison.Ordinal) && url.Contains("/search?", StringComparison.Ordinal))
                body = dtt ? Fixture("web-search-chase-atlantic-dont-try-this.html") : "<html></html>";
            else if (url.Contains("entity=musicArtist", StringComparison.Ordinal))
                body = Fixture(dtt ? "search-artist-chase-atlantic-dont-try-this.json" : "search-artist-chase-atlantic.json");
            else if (url.Contains("/search?", StringComparison.Ordinal))
                body = Fixture(dtt ? "search-album-chase-atlantic-dont-try-this.json" : "search-album-chase-atlantic.json");
            else if (url.Contains($"/lookup?id={ChaseAtlantic}&", StringComparison.Ordinal))
                body = Fixture("lookup-artist-879496517.json");
            else if (url.Contains("/lookup?id=", StringComparison.Ordinal))
                body = Fixture("lookup-album-1448869074.json");
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static ITunesArtworkService Service(Apple apple)
        => new(new HttpClient(new AnimatedArtworkChooserTests.StubHandler(apple.Respond))) { HomeStorefront = "us" };

    [Fact]
    public async Task Search_ArtistName_ReturnsTheirWholeLibrary()
    {
        var apple = new Apple();
        var result = await Service(apple).SearchArtworkLibraryAsync("CHASE ATLANTIC", preferArtist: null);
        Assert.Equal("Chase Atlantic", result.ArtistName);
        Assert.Contains(apple.Urls, u => u.Contains("/lookup?id=879496517&entity=album&limit=200&sort=recent&country=us", StringComparison.Ordinal));
        Assert.Contains(result.All, c => c.CollectionName == "DON'T TRY THIS - EP");
        Assert.DoesNotContain(result.All, c => c.ArtistName == "The Cat and Owl");
        Assert.True(result.ReleaseCount >= 28, $"{result.ReleaseCount} releases");
    }

    [Fact]
    public async Task Search_ArtistAndTitle_FindsTheEpTheAlbumIndexMisses()
    {
        var apple = new Apple();
        var result = await Service(apple).SearchArtworkLibraryAsync("CHASE ATLANTIC DONT TRY THIS", preferArtist: "Someone Else");
        // The recorded album search has only "Nostalgia - EP": the EP comes from the discography.
        Assert.Equal("DON'T TRY THIS - EP", result.All.First().CollectionName);
        Assert.Equal(ArtworkSectionKind.Matches, result.Sections[0].Kind);
        // A typed query is searched as typed: the tagged artist is not.
        Assert.DoesNotContain(apple.Urls, u => u.Contains("Someone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service(new Apple()).SearchArtworkLibraryAsync("CHASE ATLANTIC", null, ct: cts.Token));
    }

    [Fact]
    public async Task ViewModel_TypedArtist_FillsTheSections_AndTheHeader()
    {
        var apple = new Apple();
        var track = new Track { Title = "OHMAMI", Artist = "Chase Atlantic", Album = "BEAUTY IN DEATH", FilePath = "C:/m/nope.flac" };
        var vm = new MetadataViewModel(track, new SearchPopupsShotsTests.OkTags(), new FakeLibraryService(), new TestPersistenceService(),
            new FakeAnimatedCoverService(), itunes: Service(apple));
        await vm.InitializeAsync();

        vm.ArtworkSearchInput = "CHASE ATLANTIC";
        await vm.SearchArtworkCommand.ExecuteAsync(null);

        Assert.True(vm.HasArtworkSearchResults, vm.ArtworkSearchStatus);
        Assert.Equal("Chase Atlantic", vm.ArtworkLibraryArtist);
        Assert.Matches(@"^\d+ releases$", vm.ArtworkLibraryDetail);
        Assert.Equal(new[] { "Albums", "Singles & EPs", "Appears On" }, vm.ArtworkSearchSections.Select(s => s.Title));
        // The flat list (what a pick reads) holds the same tiles, in the same order.
        Assert.Equal(vm.ArtworkSearchSections.SelectMany(s => s.Items), vm.ArtworkSearchResults);
        Assert.Contains(vm.ArtworkSearchResults, r => r.Detail == "2019 · Explicit" && r.CollectionName == "DON'T TRY THIS - EP");

        // From the tags (empty box): the album first, then the rest of the artist's.
        vm.ArtworkSearchInput = string.Empty;
        await vm.SearchArtworkCommand.ExecuteAsync(null);
        // No album artist tag: the library's "Unknown Artist" placeholder is not searched for.
        Assert.Equal("BEAUTY IN DEATH — Chase Atlantic", vm.ArtworkSearchQueryHint);
        Assert.Equal("BEAUTY IN DEATH", vm.ArtworkSearchResults[0].CollectionName);
        Assert.Equal(new[] { "Best Matches", "More by Chase Atlantic" }, vm.ArtworkSearchSections.Select(s => s.Title));
    }
}
