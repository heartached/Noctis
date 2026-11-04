using Noctis.Models;
using Noctis.Services;
using Xunit;
using static Noctis.Services.ITunesArtworkService;

namespace Noctis.Tests;

/// <summary>
/// GitHub #167: animated artwork fetched on opening an album, kept in a size-capped cache under
/// the data folder. Saved covers stay outside the cache; a miss is remembered; a removed cover is
/// not fetched again. No network: the lookup is faked.
/// </summary>
public class AnimatedArtworkAutoFetchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "autoanim-" + Guid.NewGuid().ToString("N"));
    private readonly PersistenceService _persistence;
    private readonly AnimatedCoverService _covers;
    private DateTime _clock = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    private readonly List<TimeSpan> _waits = new();
    private int _finds, _downloads;
    private Func<AnimatedArtworkQuery, IReadOnlyList<RankedAlbumCandidate>> _find;
    private Func<CancellationToken, Task>? _duringDownload;

    public AnimatedArtworkAutoFetchTests()
    {
        _persistence = new PersistenceService(_dir);
        _covers = new AnimatedCoverService(_persistence);
        _find = q => new[] { new RankedAlbumCandidate(Store(q.Album, q.Artist), 140) };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static ArtworkCandidate Store(string album, string artist)
        => new(1, album, artist, "", "", "", "https://music.apple.com/us/album/x/1");

    private static AnimatedArtworkAlbum Loop() => new(
        1, "t", "", "", null, null,
        new[] { new AnimatedArtworkVariant("1080p", "https://mvod.itunes.apple.com/a.m3u8", 1080, 1080, "hvc1", 1, true) },
        null, HasSquare: true, HasTall: false);

    private static IReadOnlyList<Track> Album(string album = "Wings of Love", string artist = "Erasure")
    {
        var id = Guid.NewGuid();
        return new[]
        {
            new Track { Id = Guid.NewGuid(), Title = "t", Album = album, AlbumArtist = artist, Artist = artist, AlbumId = id,
                SourceType = SourceType.Local, FilePath = Path.Combine(Path.GetTempPath(), "nowhere", "01.mp3") },
        };
    }

    private AnimatedArtworkAutoFetcher Fetcher(int bytes = 100)
    {
        var lookup = new AnimatedArtworkBulkService(
            (q, _) => { _finds++; return Task.FromResult(_find(q)); },
            (_, _) => Task.FromResult<AnimatedArtworkAlbum?>(Loop()),
            async (_, path, ct) =>
            {
                _downloads++;
                await File.WriteAllBytesAsync(path, new byte[bytes], ct);
                if (_duringDownload != null) await _duringDownload(ct);
                return true;
            },
            _covers, ledgerPath: null)
        {
            Clock = () => _clock,
            Delay = (t, _) => { _waits.Add(t); _clock += t; return Task.CompletedTask; },
        };
        return new AnimatedArtworkAutoFetcher(lookup, _covers,
            new AnimatedArtworkCache(_persistence, Path.Combine(_dir, "blocked.json"), () => _clock),
            Path.Combine(_dir, "misses.json"),
            () => _clock, (t, _) => { _waits.Add(t); _clock += t; return Task.CompletedTask; });
    }

    [Fact]
    public async Task AClearMatch_GoesIntoTheCache_UnderTheDataFolder_AndShowsAsTheAlbumsCover()
    {
        var album = Album();
        var path = await Fetcher().FetchAsync(album, 1L << 30, CancellationToken.None);

        Assert.Equal(_persistence.GetCachedAnimatedCoverPath(album[0].AlbumId), path);
        Assert.StartsWith(Path.Combine(_dir, "animated_covers", "cache"), path);
        Assert.Equal(path, _covers.Resolve(album[0]));
        Assert.True(_covers.HasStoredCover(album));
        // Nothing left half-written, nothing in the saved (permanent) place.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.part"));
        Assert.False(File.Exists(_persistence.GetAnimatedCoverPath(album[0].AlbumId, null, ".mp4")));
    }

    [Fact]
    public async Task AWrongArtist_IsNeverFetched_AndTheMissIsRemembered()
    {
        // GitHub #167: "Wings of Love" by Erasure must not get "Tears of Love" by Tears for Fears.
        _find = _ => new[] { new RankedAlbumCandidate(Store("Tears of Love", "Tears for Fears"), 140) };
        var fetcher = Fetcher();
        var album = Album();

        Assert.Null(await fetcher.FetchAsync(album, 1L << 30, CancellationToken.None));
        Assert.Null(await fetcher.FetchAsync(album, 1L << 30, CancellationToken.None));

        Assert.Equal(1, _finds); // the second opening did not ask again
        Assert.Equal(0, _downloads);
        Assert.Null(_covers.Resolve(album[0]));
    }

    [Fact]
    public async Task AMiss_IsAskedAgain_AfterItsLifetime()
    {
        _find = _ => new[] { new RankedAlbumCandidate(Store("Other", "Erasure"), 10) };
        var fetcher = Fetcher();
        var album = Album();
        await fetcher.FetchAsync(album, 1L << 30, CancellationToken.None);
        _clock += AnimatedArtworkAutoFetcher.MissLifetime + TimeSpan.FromDays(1);
        await fetcher.FetchAsync(album, 1L << 30, CancellationToken.None);
        Assert.Equal(2, _finds);
    }

    [Fact]
    public async Task NoAnswerAtAll_IsNotRemembered_SoBeingOfflineDoesNotHideAnAlbumForWeeks()
    {
        _find = _ => Array.Empty<RankedAlbumCandidate>();
        var fetcher = Fetcher();
        var album = Album();
        await fetcher.FetchAsync(album, 1L << 30, CancellationToken.None);
        await fetcher.FetchAsync(album, 1L << 30, CancellationToken.None);
        Assert.Equal(2, _finds);
    }

    [Fact]
    public async Task ASavedCover_WinsOverTheCache_AndIsNeverLookedUpAgain()
    {
        var album = Album();
        var picked = Path.Combine(_dir, "picked.mp4");
        File.WriteAllText(picked, "mine");
        var saved = await _covers.ImportAsync(album[0], picked, AnimatedCoverScope.Album);

        Assert.Null(await Fetcher().FetchAsync(album, 1L << 30, CancellationToken.None));
        Assert.Equal(0, _finds);
        Assert.Equal(saved, _covers.Resolve(album[0]));
    }

    [Fact]
    public async Task PickingACoverByHand_ReplacesTheCachedOne()
    {
        var album = Album();
        var cached = await Fetcher().FetchAsync(album, 1L << 30, CancellationToken.None);
        var picked = Path.Combine(_dir, "picked.mp4");
        File.WriteAllText(picked, "mine");

        await _covers.ImportAsync(album[0], picked, AnimatedCoverScope.Album);

        Assert.False(File.Exists(cached));
    }

    [Fact]
    public async Task Removing_DeletesTheCachedCoverToo_AndABlockedAlbumIsNotFetchedAgain()
    {
        var album = Album();
        var fetcher = Fetcher();
        await fetcher.FetchAsync(album, 1L << 30, CancellationToken.None);

        // The album page's Remove Animated Artwork.
        fetcher.Cache.Block(album[0].AlbumId);
        await _covers.RemoveAsync(album[0], AnimatedCoverScope.Album);

        Assert.Null(_covers.Resolve(album[0]));
        Assert.False(_covers.HasStoredCover(album));
        Assert.Null(await fetcher.FetchAsync(album, 1L << 30, CancellationToken.None));
        Assert.Equal(1, _finds);
        // The block survives a restart.
        Assert.True(new AnimatedArtworkCache(_persistence, Path.Combine(_dir, "blocked.json")).IsBlocked(album[0].AlbumId));
    }

    [Fact]
    public async Task TheCache_EvictsTheLeastRecentlyOpenedLoops_UnderTheCap_AndNeverASavedOne()
    {
        var fetcher = Fetcher(bytes: 100);
        var a = Album("A", "X");
        var b = Album("B", "X");
        var c = Album("C", "X");
        var picked = Path.Combine(_dir, "picked.mp4");
        File.WriteAllBytes(picked, new byte[1000]);
        var saved = Album("Saved", "X");
        var savedPath = await _covers.ImportAsync(saved[0], picked, AnimatedCoverScope.Album);

        await fetcher.FetchAsync(a, 250, CancellationToken.None);
        _clock += TimeSpan.FromMinutes(1);
        await fetcher.FetchAsync(b, 250, CancellationToken.None);
        _clock += TimeSpan.FromMinutes(1);
        // Opening A again marks it used: B is now the oldest.
        Assert.Null(await fetcher.FetchAsync(a, 250, CancellationToken.None));
        _clock += TimeSpan.FromMinutes(1);
        await fetcher.FetchAsync(c, 250, CancellationToken.None);

        Assert.True(File.Exists(fetcher.Cache.PathFor(a[0].AlbumId)));
        Assert.False(File.Exists(fetcher.Cache.PathFor(b[0].AlbumId)));
        Assert.True(File.Exists(fetcher.Cache.PathFor(c[0].AlbumId)));
        Assert.Equal(200, fetcher.Cache.SizeBytes());
        Assert.True(File.Exists(savedPath)); // 1000 bytes, outside the cache, never counted
    }

    [Fact]
    public void Clear_DeletesOnlyTheCache()
    {
        var cache = new AnimatedArtworkCache(_persistence, null);
        var id = Guid.NewGuid();
        Directory.CreateDirectory(cache.Folder);
        File.WriteAllText(cache.PathFor(id), "x");
        File.WriteAllText(cache.PartPathFor(id), "x");
        _persistence.EnsureAnimatedCoverDir();
        var saved = _persistence.GetAnimatedCoverPath(id, null, ".mp4");
        File.WriteAllText(saved, "x");

        cache.Clear();

        Assert.Empty(Directory.GetFiles(cache.Folder));
        Assert.True(File.Exists(saved));
    }

    [Fact]
    public async Task LeavingThePage_CancelsTheDownload_AndLeavesNothingBehind()
    {
        using var cts = new CancellationTokenSource();
        _duringDownload = ct => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        var fetcher = Fetcher();
        var album = Album();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetcher.FetchAsync(album, 1L << 30, cts.Token));

        Assert.Null(_covers.Resolve(album[0]));
        Assert.Empty(Directory.GetFiles(fetcher.Cache.Folder));
        // Not a miss: the next opening asks again.
        _duringDownload = null;
        Assert.NotNull(await fetcher.FetchAsync(album, 1L << 30, CancellationToken.None));
    }

    [Fact]
    public async Task Lookups_AreSpacedOut()
    {
        var fetcher = Fetcher();
        await fetcher.FetchAsync(Album("A", "X"), 1L << 30, CancellationToken.None);
        await fetcher.FetchAsync(Album("B", "X"), 1L << 30, CancellationToken.None);
        // find, open, download per album, every request after the first waits its turn.
        Assert.Equal(5, _waits.Count(w => w == AnimatedArtworkAutoFetcher.RequestInterval));
    }

    [Fact]
    public async Task AnAlbumWithoutAnArtist_IsNotLookedUp()
    {
        Assert.Null(await Fetcher().FetchAsync(Album("Album", ""), 1L << 30, CancellationToken.None));
        Assert.Equal(0, _finds);
    }
}
