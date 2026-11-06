using Noctis.Models;
using Noctis.Services;
using Xunit;
using static Noctis.Services.ITunesArtworkService;

namespace Noctis.Tests;

/// <summary>
/// Discord (Cosmo, 11-02): animated artwork for the whole library without doing it by hand.
/// Only a clearly matching store album is used; the rest is left alone. No network: fakes.
/// </summary>
public class AnimatedArtworkBulkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "bulkanim-" + Guid.NewGuid().ToString("N"));
    private DateTime _clock = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
    private readonly List<TimeSpan> _waits = new();
    private readonly Covers _covers = new();
    private int _finds, _opens, _downloads;

    public AnimatedArtworkBulkTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Covers : IAnimatedCoverService
    {
        public readonly HashSet<Guid> Has = new();
        public readonly List<(Guid Album, AnimatedCoverScope Scope)> Imported = new();
        public string? Resolve(Track track) => Has.Contains(track.AlbumId) ? "cover.mp4" : null;
        public Task<string> ImportAsync(Track track, string sourcePath, AnimatedCoverScope scope)
        {
            Assert.True(File.Exists(sourcePath));
            Imported.Add((track.AlbumId, scope));
            Has.Add(track.AlbumId);
            return Task.FromResult("cache.mp4");
        }
        public Task RemoveAsync(Track track, AnimatedCoverScope scope) => Task.CompletedTask;
    }

    private static ArtworkCandidate Store(string album, string artist, long id = 1)
        => new(id, album, artist, "", "", "", $"https://music.apple.com/us/album/x/{id}");

    private static AnimatedArtworkAlbum Loop(string title, bool tallOnly = false) => new(
        1, title, "", "", null, null,
        new[] { new AnimatedArtworkVariant("1080p", "https://mvod.itunes.apple.com/a.m3u8", 1080, 1080, "hvc1", 1, true) },
        null, HasSquare: !tallOnly, HasTall: tallOnly);

    private static IReadOnlyList<Track> Album(string album, string artist)
    {
        var id = Guid.NewGuid();
        return new[] { new Track { Title = "t", Album = album, AlbumArtist = artist, Artist = artist, AlbumId = id, SourceType = SourceType.Local } };
    }

    private AnimatedArtworkBulkService Service(Func<AnimatedArtworkQuery, IReadOnlyList<RankedAlbumCandidate>> find,
        Func<ArtworkCandidate, AnimatedArtworkAlbum?> open) => new(
        (q, _) => { _finds++; return Task.FromResult(find(q)); },
        (c, _) => { _opens++; return Task.FromResult(open(c)); },
        (v, path, _) => { _downloads++; File.WriteAllText(path, "mp4"); return Task.FromResult(true); },
        _covers,
        Path.Combine(_dir, "ledger.json"))
    {
        Clock = () => _clock,
        Delay = (t, _) => { _waits.Add(t); _clock += t; return Task.CompletedTask; },
    };

    [Fact]
    public async Task ClearMatch_IsDownloadedAndSavedAlbumScoped()
    {
        var album = Album("Happier Than Ever", "Billie Eilish");
        var svc = Service(_ => new[] { new RankedAlbumCandidate(Store("Happier Than Ever", "Billie Eilish"), 140) },
            _ => Loop("Happier Than Ever"));

        var s = await svc.RunAsync(new[] { album }, recheck: false, null, CancellationToken.None);

        Assert.Equal(1, s.Found);
        Assert.Equal((album[0].AlbumId, AnimatedCoverScope.Album), Assert.Single(_covers.Imported));
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "noctis-bulkanim-*.mp4"));
    }

    [Theory]
    [InlineData("Happier Than Ever (Karaoke Version)", "Billie Eilish")]
    [InlineData("Happier Than Ever", "Some Tribute Band")]
    [InlineData("Different Album", "Billie Eilish")]
    public async Task NearMiss_IsLeftAlone(string storeAlbum, string storeArtist)
    {
        var svc = Service(_ => new[] { new RankedAlbumCandidate(Store(storeAlbum, storeArtist), 90) }, _ => Loop(storeAlbum));
        var s = await svc.RunAsync(new[] { Album("Happier Than Ever", "Billie Eilish") }, false, null, CancellationToken.None);
        Assert.Equal(1, s.NotFound);
        Assert.Equal(0, _opens);
        Assert.Empty(_covers.Imported);
    }

    [Fact]
    public async Task DifferentTitleByADifferentArtist_IsNeverPicked()
    {
        // GitHub #167: "Wings of Love" by Erasure must not end up with Tears for Fears' cover.
        var svc = Service(_ => new[] { new RankedAlbumCandidate(Store("Tears of Love", "Tears for Fears"), 140) },
            _ => Loop("Tears of Love"));
        var s = await svc.RunAsync(new[] { Album("Wings of Love", "Erasure") }, false, null, CancellationToken.None);
        Assert.Equal(1, s.NotFound);
        Assert.Equal(0, _opens);
        Assert.False(AnimatedArtworkBulkService.IsConfident(Store("Tears of Love", "Tears for Fears"), "Wings of Love", "Erasure"));
        Assert.False(AnimatedArtworkBulkService.IsConfident(Store("Wings of Love", "Tears for Fears"), "Wings of Love", "Erasure"));
        Assert.False(AnimatedArtworkBulkService.IsConfident(Store("Tears of Love", "Erasure"), "Wings of Love", "Erasure"));
        Assert.True(AnimatedArtworkBulkService.IsConfident(Store("Wings of Love (Remastered)", "Erasure"), "Wings of Love", "Erasure"));
    }

    [Fact]
    public async Task TheLookup_CarriesTheAlbumsYearAndSongCount()
    {
        AnimatedArtworkQuery? asked = null;
        var album = Album("Wings of Love", "Erasure");
        album[0].Year = 1988;
        var svc = Service(q => { asked = q; return Array.Empty<RankedAlbumCandidate>(); }, _ => null);
        await svc.RunAsync(new[] { album }, false, null, CancellationToken.None);
        Assert.Equal((1988, 1), (asked!.Year, asked.TrackCount));
    }

    [Fact]
    public async Task TallOnlyLoop_IsNotUsed()
    {
        var svc = Service(_ => new[] { new RankedAlbumCandidate(Store("Album", "Artist"), 140) }, _ => Loop("Album", tallOnly: true));
        var s = await svc.RunAsync(new[] { Album("Album", "Artist") }, false, null, CancellationToken.None);
        Assert.Equal(1, s.NotFound);
        Assert.Equal(0, _downloads);
    }

    [Fact]
    public async Task AlbumWithACover_IsSkippedWithoutAsking()
    {
        var album = Album("Album", "Artist");
        _covers.Has.Add(album[0].AlbumId);
        var s = await Service(_ => throw new InvalidOperationException("asked"), _ => null)
            .RunAsync(new[] { album }, false, null, CancellationToken.None);
        Assert.Equal(1, s.Skipped);
        Assert.Equal(0, _finds);
    }

    [Fact]
    public async Task NoneFound_IsRemembered_UntilRecheck()
    {
        var album = Album("Album", "Artist");
        var svc = Service(_ => Array.Empty<RankedAlbumCandidate>(), _ => null);
        await svc.RunAsync(new[] { album }, false, null, CancellationToken.None);
        var second = await svc.RunAsync(new[] { album }, false, null, CancellationToken.None);
        Assert.Equal((1, 1), (second.Skipped, second.CheckedBefore));
        Assert.Equal(1, _finds);
        await svc.RunAsync(new[] { album }, true, null, CancellationToken.None);
        Assert.Equal(2, _finds);
    }

    [Fact]
    public async Task RequestsAreSpacedOut()
    {
        var albums = Enumerable.Range(0, 3).Select(i => Album("A" + i, "X")).ToArray();
        await Service(_ => Array.Empty<RankedAlbumCandidate>(), _ => null).RunAsync(albums, false, null, CancellationToken.None);
        Assert.Equal(3, _finds);
        Assert.Equal(2, _waits.Count(w => w == TimeSpan.FromSeconds(1.5)));
    }

    [Fact]
    public async Task Cancel_KeepsWhatWasSaved_AndReturns()
    {
        using var cts = new CancellationTokenSource();
        var albums = Enumerable.Range(0, 5).Select(i => Album("A" + i, "X")).ToArray();
        var svc = Service(q => { if (q.Album == "A1") cts.Cancel(); return new[] { new RankedAlbumCandidate(Store(q.Album, "X"), 140) }; },
            c => Loop(c.CollectionName));
        var s = await svc.RunAsync(albums, false, null, cts.Token);
        Assert.Equal(1, s.Found);
        Assert.True(_finds <= 2);
    }

    [Fact]
    public void GroupAlbums_OneGroupPerAlbum_LeavingOutUnknownAndStreamed()
    {
        var a = Guid.NewGuid();
        var tracks = new[]
        {
            new Track { Album = "B", AlbumArtist = "Z", AlbumId = a, SourceType = SourceType.Local },
            new Track { Album = "B", AlbumArtist = "Z", AlbumId = a, SourceType = SourceType.Local },
            new Track { Album = "Unknown Album", AlbumId = Guid.NewGuid(), SourceType = SourceType.Local },
            new Track { Album = "", AlbumId = Guid.NewGuid(), SourceType = SourceType.Local },
        };
        var groups = AnimatedArtworkBulkService.GroupAlbums(tracks);
        Assert.Equal(2, Assert.Single(groups).Count);
    }
}
