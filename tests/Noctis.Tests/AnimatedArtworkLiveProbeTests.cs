using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Runs the Edit Info → Animated Artwork search against Apple's live endpoints, exactly as the
/// dialog does. Off by default (network); set NOCTIS_LIVE_ANIMATED=1 to run it, and read the
/// per-query outcome in the test output. The offline fixtures in AnimatedArtworkSearchFlowTests
/// are the regression cover; this is the tool for re-checking Apple when they change something.
/// </summary>
public class AnimatedArtworkLiveProbeTests
{
    private readonly ITestOutputHelper _out;

    public AnimatedArtworkLiveProbeTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("YHLQMDLG")]
    [InlineData("YHLQMDLG Bad Bunny")]
    [InlineData("Happier Than Ever Billie Eilish")]
    [InlineData("MAYHEM Lady Gaga")]
    [InlineData("https://music.apple.com/us/album/yhlqmdlg/1500776322")]
    [InlineData("1500776322")]
    [InlineData("https://music.apple.com/us/song/la-dificil/1500776327")]
    [InlineData("https://music.apple.com/us/album/1000000-ep/1628626875")]
    [InlineData("HIT ME HARD AND SOFT Billie Eilish")]
    [InlineData("Die With A Smile Lady Gaga")]
    [InlineData("Short n' Sweet")]
    [InlineData("Short n' Sweet Sabrina Carpenter")]
    [InlineData("https://music.apple.com/gb/album/short-n-sweet-deluxe/1795512297")]
    [InlineData("https://music.apple.com/us/playlist/todays-hits/pl.f4d106fed2bd41149aaacabb233eb5eb")]
    [InlineData("Thomas Dybdahl Teenage Astronauts")] // GitHub #229
    public Task TypedQuery(string typed) => ProbeAsync(typed, null, null);

    /// <summary>Searches from the tags (nothing typed) as a user of the given storefront would:
    /// GitHub #229 missed "Teenage Astronauts" because only the US store was asked.</summary>
    [Theory]
    [InlineData("Teenage Astronauts", "Thomas Dybdahl", "us")]
    [InlineData("Teenage Astronauts", "Thomas Dybdahl", "de")]
    [InlineData("Happier Than Ever", "Billie Eilish", "us")]
    [InlineData("Happier Than Ever", "Billie Eilish", "de")]
    [InlineData("YHLQMDLG", "Bad Bunny", "us")]
    [InlineData("MAYHEM", "Lady Gaga", "de")]
    [InlineData("Short n' Sweet", "Sabrina Carpenter", "us")]
    [InlineData("HIT ME HARD AND SOFT", "Billie Eilish", "us")]
    public Task TagQuery(string album, string artist, string home) => ProbeAsync(album, artist, home);

    private async Task ProbeAsync(string typedOrAlbum, string? artist, string? home)
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_LIVE_ANIMATED") != "1")
            return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var p = new TestPersistenceService();
        var track = artist == null
            ? new Track { Title = "x", Artist = "Someone Else", Album = "Unrelated", FilePath = "C:\\nope.mp3" }
            : new Track { Title = "x", Artist = artist, AlbumArtist = artist, Album = typedOrAlbum, FilePath = "C:\\nope.mp3" };
        var itunes = home == null ? new ITunesArtworkService(http) : new ITunesArtworkService(http) { HomeStorefront = home };
        var vm = new MetadataViewModel(track, new MetadataService(), new FakeLibraryService(), p,
            new FakeAnimatedCoverService(), itunes: itunes);

        vm.AnimatedSearchInput = artist == null ? typedOrAlbum : string.Empty;
        var before = new HashSet<string>(DebugLog.Snapshot().Split('\n'));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await vm.SearchAnimatedCoverCommand.ExecuteAsync(null);

        _out.WriteLine($"'{typedOrAlbum}'{(artist == null ? "" : $" by {artist} from {home}")} -> {vm.AnimatedArtworkSearchResults.Count} result(s) in {sw.ElapsedMilliseconds} ms, " +
                       $"status '{vm.AnimatedSearchStatus}'");
        foreach (var r in vm.AnimatedArtworkSearchResults)
            _out.WriteLine($"   [{r.Score:0.#}] \"{r.CollectionName}\" by {r.ArtistName} ({r.Detail}) " +
                           string.Join(", ", r.Choices.Select(c => $"{c.Label} {c.Variant.Width}x{c.Variant.Height} {c.Variant.Codec}")) +
                           (r.Album.HasTall ? " +tall" : string.Empty));
        foreach (var line in DebugLog.Snapshot().Split('\n')
                     .Where(l => l.Contains("[AnimatedArt]") && !before.Contains(l)))
            _out.WriteLine("   log: " + line);
    }
}
