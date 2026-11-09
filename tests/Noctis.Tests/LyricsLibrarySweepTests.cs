using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #219: Settings ▸ Lyrics ▸ "Find lyrics for the whole library" — songs without synced lyrics
/// are looked up gradually in the background through the bulk service (its sources, pacing, save
/// rules and the ledger that remembers misses for a month), skipping songs that already have synced
/// lyrics, pausing while offline. Off by default. No network: the bulk service is a fake.
/// </summary>
public class LyricsLibrarySweepTests
{
    private readonly FakeLibraryService _library = new();
    private readonly AppSettings _settings = new();

    private sealed class StubService : ILyricsBulkService
    {
        public readonly List<IReadOnlyList<Track>> Runs = new();
        public LyricsBulkOptions? Options;

        public Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
            => FetchAsync(tracks, new LyricsBulkOptions(), progress, ct);

        public Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, LyricsBulkOptions options, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
        {
            Options = options;
            lock (Runs) Runs.Add(tracks);
            return Task.FromResult(new LyricsBulkSummary(0, 0, 0, 0, 0));
        }

        public Task<int> RemoveAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct) => Task.FromResult(0);
    }

    private bool _online = true;

    private LyricsAutoFindService Sweep(StubService stub) => new(stub, _library, () => _settings, TimeSpan.FromHours(1))
    {
        IsOnline = () => _online,
        BatchPause = TimeSpan.Zero,
    };

    private Track Song(string name, string? synced = null, string? plain = null)
    {
        var t = new Track
        {
            Title = name, Artist = "Artist", FilePath = Path.Combine(Path.GetTempPath(), name + ".flac"), SourceType = SourceType.Local,
            SyncedLyrics = synced ?? string.Empty, Lyrics = plain ?? string.Empty,
        };
        _library.TrackList.Add(t);
        return t;
    }

    [Fact]
    public async Task SettingOff_TheLibraryIsNotLookedUp()
    {
        Song("none");
        var stub = new StubService();
        using var auto = Sweep(stub);
        await auto.SweepAsync();
        Assert.Empty(stub.Runs);
        Assert.Equal(0, auto.PendingCount);
    }

    [Fact]
    public async Task SettingOn_SongsWithoutSyncedLyrics_GoThroughTheBulkLookup()
    {
        _settings.LyricsAutoFindLibrary = true;
        var none = Song("none");
        var plain = Song("plain", plain: "words");
        Song("synced", synced: "[00:01.00]words");
        var marked = Song("marked");
        marked.NoLyrics = true;
        _library.TrackList.Add(new Track { Title = "stream", SourceType = SourceType.Jellyfin, FilePath = "http://x" });
        var stub = new StubService();
        using var auto = Sweep(stub);

        await auto.SweepAsync();

        var run = Assert.Single(stub.Runs);
        Assert.Equal(new[] { none, plain }, run);
        // Default options: the ledger keeps a song not found from being asked again for a month.
        Assert.Equal(new LyricsBulkOptions(), stub.Options);
    }

    [Fact]
    public async Task ABigLibrary_IsLookedUpInBatches()
    {
        _settings.LyricsAutoFindLibrary = true;
        for (var i = 0; i < LyricsAutoFindService.MaxPerRun + 3; i++) Song("t" + i);
        var stub = new StubService();
        using var auto = Sweep(stub);
        await auto.SweepAsync();
        Assert.Equal(new[] { LyricsAutoFindService.MaxPerRun, 3 }, stub.Runs.Select(r => r.Count));
    }

    [Fact]
    public async Task Offline_WaitsWithTheSongsKept_AndResumesOnceOnline()
    {
        _settings.LyricsAutoFindLibrary = true;
        Song("a");
        Song("b");
        _online = false;
        var stub = new StubService();
        using var auto = Sweep(stub);

        await auto.SweepAsync();
        Assert.Empty(stub.Runs);
        Assert.Equal(2, auto.PendingCount);

        _online = true;
        await auto.TickAsync();
        Assert.Equal(2, Assert.Single(stub.Runs).Count);
        Assert.Equal(0, auto.PendingCount);
    }

    [Fact]
    public async Task TurnedOff_DropsTheLibrarySongsWaiting_ButNotNewOnes()
    {
        _settings.LyricsAutoFindLibrary = true;
        _settings.LyricsAutoFindNew = true;
        Song("old");
        _online = false;
        var stub = new StubService();
        using var auto = Sweep(stub);
        await auto.SweepAsync();
        var fresh = new Track { Title = "new", FilePath = Path.Combine(Path.GetTempPath(), "new.flac"), SourceType = SourceType.Local };
        auto.Enqueue(new[] { fresh });
        Assert.Equal(2, auto.PendingCount);

        _settings.LyricsAutoFindLibrary = false;
        auto.StopLibrarySweep();
        _online = true;
        await auto.TickAsync();

        Assert.Same(fresh, Assert.Single(Assert.Single(stub.Runs)));
    }

    [Fact]
    public async Task NewSongs_AreQueued_WhenOnlyTheLibrarySettingIsOn()
    {
        _settings.LyricsAutoFindLibrary = true;
        var stub = new StubService();
        using var auto = Sweep(stub);
        var fresh = new Track { Title = "new", FilePath = Path.Combine(Path.GetTempPath(), "new2.flac"), SourceType = SourceType.Local };
        auto.Enqueue(new[] { fresh });
        await auto.TickAsync();
        Assert.Same(fresh, Assert.Single(Assert.Single(stub.Runs)));
    }
}
