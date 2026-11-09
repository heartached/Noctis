using Avalonia.Headless.XUnit;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #194 (plain lyrics → look for the preferred synced kind, keep the plain ones when
/// nothing better turns up) and #195 (bulk lyrics: review before saving, new songs in the
/// background). No network: the sources are fakes.
/// </summary>
public class LyricsUpgradeAndReviewTests : IDisposable
{
    private const string Elrc = "[00:01.00]<00:01.00>Hello <00:01.50>world<00:02.00>";
    private const string Lrc = "[00:01.00]Hello world";
    private const string OwnPlain = "My own plain words";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "lyrup-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLibraryService _library = new();
    private readonly AppSettings _settings = new();
    private readonly LyricsSaveTargetTests.RecordingMetadata _metadata = new();
    private DateTime _clock = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    public LyricsUpgradeAndReviewTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Source
    {
        public readonly string Name;
        public readonly Func<string, LrcLibResult?> Answer;
        public int Calls;
        public Source(string name, Func<string, LrcLibResult?> answer) { Name = name; Answer = answer; }
        public LyricsBulkSource Bulk => new(Name, _ => true, (a, t, d, c) =>
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Answer(t));
        });
    }

    private static LrcLibResult Word() => new() { SyncedLyrics = Elrc, PlainLyrics = "Hello world" };
    private static LrcLibResult Line() => new() { SyncedLyrics = Lrc, PlainLyrics = "Hello world" };
    private static LrcLibResult Plain() => new() { PlainLyrics = "Someone else's words" };

    private Track NewTrack(string name = "song", string? plain = null)
    {
        var audio = Path.Combine(_dir, name + ".flac");
        File.WriteAllText(audio, "x");
        var t = new Track { Title = name, Artist = "Artist", FilePath = audio, SourceType = SourceType.Local, Duration = TimeSpan.FromMinutes(3) };
        if (plain != null) t.Lyrics = plain; // as a scan reads them from the file's tags
        return t;
    }

    private LyricsBulkService Service(params Source[] sources) => new(
        null!,
        new LyricsWriter(_metadata, null, new AppWrittenSidecarRegistry(Path.Combine(_dir, "registry.json")), Path.Combine(_dir, "cache"))
        {
            SaveTarget = () => LyricsSaveTarget.Sidecar,
        },
        _library,
        () => _settings,
        () => sources.Select(s => s.Bulk).ToList(),
        Path.Combine(_dir, "ledger.json"))
    {
        Clock = () => _clock,
        Delay = (t, _) => { _clock += t; return Task.CompletedTask; },
    };

    private string Side(Track t, string ext) => Path.ChangeExtension(t.FilePath!, ext);

    // ── #194: upgrade plain lyrics (the lyrics page's lookup: SyncedOnly) ──

    [Fact]
    public async Task PlainSong_SyncedFind_IsSavedAsALyricsFile_AndTheTagsAreLeftAlone()
    {
        var a = new Source("A", _ => Line());
        var t = NewTrack(plain: OwnPlain);

        var s = await Service(a).FetchAsync(new[] { t }, new LyricsBulkOptions(SyncedOnly: true), null, CancellationToken.None);

        Assert.Equal((1, 1), (s.Synced, s.Upgraded));
        Assert.Equal(Lrc, File.ReadAllText(Side(t, ".lrc")));
        // "Save lyrics to" = lyrics files: the plain lyrics in the audio file's tags are not touched.
        Assert.Empty(_metadata.LyricsWrites);
        Assert.Equal(0, _metadata.TrackWrites);
    }

    [Fact]
    public async Task PlainSong_NothingFound_KeepsItsPlainLyrics()
    {
        var a = new Source("A", _ => null);
        var t = NewTrack(plain: OwnPlain);

        var s = await Service(a).FetchAsync(new[] { t }, new LyricsBulkOptions(SyncedOnly: true), null, CancellationToken.None);

        Assert.Equal(1, a.Calls);
        Assert.Equal(0, s.Synced + s.PlainOnly);
        Assert.Equal(OwnPlain, t.Lyrics);
        Assert.False(File.Exists(Side(t, ".lrc")));
        Assert.Empty(_metadata.LyricsWrites);
    }

    [Fact]
    public async Task SyncedOnly_APlainFind_NeverReplacesOrAddsPlainLyrics()
    {
        var a = new Source("A", _ => Plain());
        var withPlain = NewTrack("p", OwnPlain);
        var without = NewTrack("n");

        var s = await Service(a).FetchAsync(new[] { withPlain, without }, new LyricsBulkOptions(SyncedOnly: true), null, CancellationToken.None);

        Assert.Equal(0, s.Synced + s.PlainOnly);
        Assert.Equal(OwnPlain, withPlain.Lyrics);
        Assert.True(string.IsNullOrEmpty(without.Lyrics));
        Assert.Equal(0, _library.SaveCount);
    }

    [Fact]
    public async Task PreferredTypePlain_PlainSongIsNotLookedUp()
    {
        _settings.LyricsPreferredType = nameof(LyricsPreferredType.Plain);
        var a = new Source("A", _ => Word());
        var t = NewTrack(plain: OwnPlain);

        await Service(a).FetchAsync(new[] { t }, new LyricsBulkOptions(SyncedOnly: true), null, CancellationToken.None);

        Assert.Equal(0, a.Calls);
        Assert.Equal(OwnPlain, t.Lyrics);
    }

    // ── #195: review before saving ──

    [Fact]
    public async Task ReviewRun_SavesNothing_AndListsTheFindsWithKindAndSource()
    {
        var a = new Source("A", title => title == "found" ? Word() : null);
        var found = NewTrack("found");
        var missing = NewTrack("missing");
        var outcomes = new List<string>();

        var s = await Service(a).FetchAsync(new[] { found, missing }, new LyricsBulkOptions(ReviewOnly: true),
            new SyncProgress(p => outcomes.Add(p.Outcome)), CancellationToken.None);

        var find = Assert.Single(s.Finds);
        Assert.Same(found, find.Track);
        Assert.Equal(("A", LyricsSearchSelector.WordSyncedRank, 0), (find.Source, find.Rank, find.HadRank));
        Assert.Contains("found word-synced", outcomes);
        Assert.False(File.Exists(Side(found, ".elrc")));
        Assert.True(string.IsNullOrEmpty(found.SyncedLyrics));
        Assert.Equal(0, _library.SaveCount);
    }

    [Fact]
    public async Task ReviewRun_DoesNotMarkSongsAsLookedUp_UntilTheirFindsAreSaved()
    {
        var a = new Source("A", _ => Word());
        var t = NewTrack();
        await Service(a).FetchAsync(new[] { t }, new LyricsBulkOptions(ReviewOnly: true), null, CancellationToken.None);
        var again = await Service(a).FetchAsync(new[] { t }, new LyricsBulkOptions(ReviewOnly: true), null, CancellationToken.None);
        Assert.Equal(2, a.Calls);
        Assert.Single(again.Finds);
    }

    [Fact]
    public async Task Apply_SavesTheKeptFinds_LikeANormalRun()
    {
        var a = new Source("A", _ => Word());
        var keep = NewTrack("keep", OwnPlain);
        var drop = NewTrack("drop");
        var service = Service(a);
        var review = await service.FetchAsync(new[] { keep, drop }, new LyricsBulkOptions(ReviewOnly: true), null, CancellationToken.None);
        Assert.Equal(2, review.Finds.Count);

        var s = await service.ApplyAsync(review.Finds.Where(f => f.Track == keep).ToList(), null, CancellationToken.None);

        Assert.Equal((1, 1, 1), (s.Synced, s.WordSynced, s.Upgraded));
        Assert.Equal(Elrc, File.ReadAllText(Side(keep, ".elrc")));
        Assert.False(File.Exists(Side(drop, ".elrc")));
        Assert.Equal(1, _library.SaveCount);
    }

    [AvaloniaFact]
    public async Task Dialog_ReviewFirst_TicksEveryFind_AndSavesOnlyTheTickedOnes()
    {
        var tracks = new[] { new Track { Title = "a" }, new Track { Title = "b" }, new Track { Title = "c" } };
        var stub = new StubService
        {
            Finds = tracks.Take(2).Select(t => new LyricsBulkFind(t, Line(), "LRCLIB", 2, 1)).ToList(),
        };
        var vm = new BulkLyricsViewModel(tracks, stub, remove: false) { ReviewFirst = true };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(stub.Options!.ReviewOnly);
        Assert.True(vm.IsReviewing);
        Assert.False(vm.IsDone);
        Assert.Equal(new[] { true, true, false }, vm.Rows.Select(r => r.CanChoose));
        Assert.Equal("Line-synced", vm.Rows[0].KindText);
        Assert.Equal("LRCLIB", vm.Rows[0].Status);
        Assert.Equal("Save Selected (2)", vm.ApplyText);

        vm.Rows[1].IsChosen = false;
        Assert.Equal("Save Selected (1)", vm.ApplyText);
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Same(tracks[0], Assert.Single(stub.Applied!).Track);
        Assert.False(vm.IsReviewing);
        Assert.True(vm.IsDone);
        Assert.False(vm.Rows[0].CanChoose);
    }

    [AvaloniaFact]
    public async Task Dialog_ReviewFirst_NothingFound_EndsLikeANormalRun()
    {
        var stub = new StubService();
        var vm = new BulkLyricsViewModel(new[] { new Track { Title = "a" } }, stub, remove: false) { ReviewFirst = true };
        await vm.StartCommand.ExecuteAsync(null);
        Assert.False(vm.IsReviewing);
        Assert.True(vm.IsDone);
        Assert.Null(stub.Applied);
    }

    private sealed class StubService : ILyricsBulkService
    {
        public LyricsBulkOptions? Options;
        public IReadOnlyList<LyricsBulkFind> Finds = Array.Empty<LyricsBulkFind>();
        public IReadOnlyList<LyricsBulkFind>? Applied;
        public readonly List<IReadOnlyList<Track>> Runs = new();

        public Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
            => FetchAsync(tracks, new LyricsBulkOptions(), progress, ct);

        public Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, LyricsBulkOptions options, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
        {
            Options = options;
            lock (Runs) Runs.Add(tracks);
            return Task.FromResult(new LyricsBulkSummary(0, 0, 0, 0, 0) { Finds = Finds });
        }

        public Task<LyricsBulkSummary> ApplyAsync(IReadOnlyList<LyricsBulkFind> finds, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
        {
            Applied = finds;
            return Task.FromResult(new LyricsBulkSummary(finds.Count, 0, 0, 0, 0));
        }

        public Task<int> RemoveAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct) => Task.FromResult(0);
    }

    // ── #195: find lyrics for new songs ──

    private LyricsAutoFindService AutoFind(StubService stub) =>
        new(stub, _library, () => _settings, TimeSpan.FromHours(1));

    [Fact]
    public async Task NewSongs_SettingOff_NothingIsQueued()
    {
        var stub = new StubService();
        using var auto = AutoFind(stub);
        auto.Enqueue(new[] { NewTrack() });
        Assert.Equal(0, auto.PendingCount);
        await auto.TickAsync();
        Assert.Empty(stub.Runs);
    }

    [Fact]
    public async Task NewSongs_SettingOn_LocalSongsGoThroughOneBulkRun()
    {
        _settings.LyricsAutoFindNew = true;
        var stub = new StubService();
        using var auto = AutoFind(stub);
        var local = NewTrack("local");
        var marked = NewTrack("marked");
        marked.NoLyrics = true;
        var stream = new Track { Title = "stream", SourceType = SourceType.Jellyfin, FilePath = "http://x" };

        auto.Enqueue(new[] { local, marked, stream, local });
        Assert.Equal(1, auto.PendingCount);
        await auto.TickAsync();

        Assert.Same(local, Assert.Single(Assert.Single(stub.Runs)));
        Assert.Equal(new LyricsBulkOptions(), stub.Options);
        Assert.Equal(0, auto.PendingCount);
    }

    [Fact]
    public async Task NewSongs_ABigImport_IsLookedUpInBatches()
    {
        _settings.LyricsAutoFindNew = true;
        var stub = new StubService();
        using var auto = AutoFind(stub);
        var many = Enumerable.Range(0, LyricsAutoFindService.MaxPerRun + 5)
            .Select(i => new Track { Title = "t" + i, FilePath = Path.Combine(_dir, i + ".flac"), SourceType = SourceType.Local })
            .ToList();
        auto.Enqueue(many);
        await auto.TickAsync();
        Assert.Equal(new[] { LyricsAutoFindService.MaxPerRun, 5 }, stub.Runs.Select(r => r.Count));
    }

    [Fact]
    public async Task NewSongs_TurnedOffBeforeTheRun_AreDropped()
    {
        _settings.LyricsAutoFindNew = true;
        var stub = new StubService();
        using var auto = AutoFind(stub);
        auto.Enqueue(new[] { NewTrack() });
        _settings.LyricsAutoFindNew = false;
        await auto.TickAsync();
        Assert.Empty(stub.Runs);
        Assert.Equal(0, auto.PendingCount);
    }

    /// <summary>Progress&lt;T&gt; posts to a sync context; this reports inline.</summary>
    private sealed class SyncProgress(Action<LyricsBulkProgress> report) : IProgress<LyricsBulkProgress>
    {
        public void Report(LyricsBulkProgress value) { lock (this) report(value); }
    }
}
