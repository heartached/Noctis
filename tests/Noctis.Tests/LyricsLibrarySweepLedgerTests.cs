using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #219 review: the daily library sweep must not read every song's lyrics files only to
/// find the ledger already says "checked before". It asks the ledger first, from what the library
/// row knows, and queues only songs never looked up or due again. And a background run and a
/// Find Lyrics run on the same songs must neither lose each other's ledger entries nor save a
/// poorer find over a richer one.
/// </summary>
public class LyricsLibrarySweepLedgerTests : IDisposable
{
    private const string Elrc = "[00:01.00]<00:01.00>Hello <00:01.50>world<00:02.00>";
    private const string Lrc = "[00:01.00]Hello world";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "sweepledger-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLibraryService _library = new();
    private readonly AppSettings _settings = new() { LyricsAutoFindLibrary = true };
    private DateTime _clock = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
    private int _detections;

    public LyricsLibrarySweepLedgerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>Forwards to the real service and records what each run was handed.</summary>
    private sealed class Recording(ILyricsBulkService inner) : ILyricsBulkService
    {
        public readonly List<IReadOnlyList<Track>> Runs = new();
        public Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
            => FetchAsync(tracks, new LyricsBulkOptions(), progress, ct);
        public Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, LyricsBulkOptions options, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
        {
            lock (Runs) Runs.Add(tracks);
            return inner.FetchAsync(tracks, options, progress, ct);
        }
        public IReadOnlyList<Track> WithoutRecentLookups(IReadOnlyList<Track> tracks) => inner.WithoutRecentLookups(tracks);
        public Task<int> RemoveAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
            => inner.RemoveAsync(tracks, progress, ct);
    }

    private LyricsBulkService Bulk(Func<LrcLibResult?> answer, Func<Task>? beforeAnswer = null) => new(
        null!,
        new LyricsWriter(new LyricsSaveTargetTests.RecordingMetadata(), null,
            new AppWrittenSidecarRegistry(Path.Combine(_dir, "registry.json")), Path.Combine(_dir, "cache"))
        {
            SaveTarget = () => LyricsSaveTarget.Sidecar,
        },
        _library,
        () => _settings,
        () => new[]
        {
            new LyricsBulkSource("A", _ => true, async (a, t, d, c) =>
            {
                if (beforeAnswer is not null) await beforeAnswer();
                return answer();
            }),
        },
        Path.Combine(_dir, "ledger.json"))
    {
        Clock = () => _clock,
        Delay = (t, _) => { _clock += t; return Task.CompletedTask; },
        DetectFormats = (tracks, ct) =>
        {
            Interlocked.Add(ref _detections, tracks.Count);
            return ExistingLyricsLoader.DetectFormats(tracks, ct);
        },
    };

    private LyricsAutoFindService Sweep(ILyricsBulkService bulk) =>
        new(bulk, _library, () => _settings, TimeSpan.FromHours(1)) { IsOnline = () => true, BatchPause = TimeSpan.Zero };

    private Track Song(string name, string? plain = null)
    {
        var audio = Path.Combine(_dir, name + ".flac");
        File.WriteAllText(audio, "x");
        var t = new Track
        {
            Title = name, Artist = "Artist", FilePath = audio, SourceType = SourceType.Local,
            Duration = TimeSpan.FromMinutes(3), Lyrics = plain ?? string.Empty,
        };
        _library.TrackList.Add(t);
        return t;
    }

    [Fact]
    public async Task SteadyStateSweep_QueuesNothing_AndReadsNoFiles_UntilTheRetryWindowPasses()
    {
        Song("none1");
        Song("none2");
        Song("plain", plain: "my words");
        var bulk = new Recording(Bulk(() => null));
        using var auto = Sweep(bulk);

        await auto.SweepAsync();
        Assert.Equal(3, Assert.Single(bulk.Runs).Count);
        Assert.Equal(3, _detections);

        // The next day: every song was looked up and nothing better turned up.
        _clock += TimeSpan.FromDays(1);
        _detections = 0;
        await auto.SweepAsync();
        Assert.Single(bulk.Runs);
        Assert.Equal(0, _detections);
        Assert.Equal(0, auto.PendingCount);

        // Past the retry window they are due again.
        _clock += BulkRunLedger.MaxAge;
        await auto.SweepAsync();
        Assert.Equal(2, bulk.Runs.Count);
        Assert.Equal(3, bulk.Runs[1].Count);
    }

    [Fact]
    public async Task ASongThatLostItsLyrics_IsQueuedAgain()
    {
        // Looked up as plain (rank 1) and nothing better found; the row now has no lyrics (rank 0):
        // the ledger entry no longer covers it, so it is asked again — err on the side of queuing.
        var t = Song("p", plain: "words");
        var bulk = new Recording(Bulk(() => null));
        using var auto = Sweep(bulk);
        await auto.SweepAsync();
        t.Lyrics = string.Empty;
        _clock += TimeSpan.FromDays(1);
        await auto.SweepAsync();
        Assert.Equal(2, bulk.Runs.Count);
    }

    [Fact]
    public void LedgerSaves_FromTwoRuns_KeepBothRunsEntries()
    {
        var path = Path.Combine(_dir, "ledger.json");
        var a = new BulkRunLedger(path, () => _clock);
        var b = new BulkRunLedger(path, () => _clock);
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        a.Record(idA, 0);
        b.Record(idB, 1);
        a.Save();
        b.Save(); // used to overwrite the file with b's entries only

        var reread = new BulkRunLedger(path, () => _clock);
        Assert.True(reread.CheckedRecently(idA, 0));
        Assert.True(reread.CheckedRecently(idB, 1));
    }

    [Fact]
    public async Task TwoRunsOnOneSong_ThePoorerFindDoesNotReplaceTheRicherOne()
    {
        // Run L (background) and run W (Find Lyrics) both start while the song has no lyrics.
        // W saves word-synced first; L's line-synced find, judged against what the song had when
        // L started, used to be written over it.
        var t = Song("both");
        var lineMayAnswer = new TaskCompletionSource();
        var line = Bulk(() => new LrcLibResult { SyncedLyrics = Lrc, PlainLyrics = "Hello world" }, () => lineMayAnswer.Task);
        var word = Bulk(() => new LrcLibResult { SyncedLyrics = Elrc, PlainLyrics = "Hello world" });

        var lineRun = line.FetchAsync(new[] { t }, new LyricsBulkOptions(), null, CancellationToken.None);
        var wordSummary = await word.FetchAsync(new[] { t }, new LyricsBulkOptions(), null, CancellationToken.None);
        lineMayAnswer.SetResult();
        var lineSummary = await lineRun;

        Assert.Equal(1, wordSummary.WordSynced);
        Assert.Equal(0, lineSummary.Synced);
        Assert.Equal(Elrc, t.SyncedLyrics);
        Assert.Equal(Elrc, File.ReadAllText(Path.ChangeExtension(t.FilePath!, ".elrc")));
    }
}
