using System.Net;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Cosmo, 11-02): "a mass search of all your songs … finding word-synced lyrics for all,
/// … just not having to do it by hand". The bulk fetch asks every source switched on, stops at
/// the preferred format, can upgrade plain / line lyrics, backs off busy servers and resumes.
/// No network: the sources are fakes.
/// </summary>
public class LyricsBulkUpgradeTests : IDisposable
{
    private const string Elrc = "[00:01.00]<00:01.00>Hello <00:01.50>world<00:02.00>";
    private const string Lrc = "[00:01.00]Hello world";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "bulkup-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLibraryService _library = new();
    private DateTime _clock = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
    private readonly List<TimeSpan> _waits = new();
    private readonly AppSettings _settings = new();

    public LyricsBulkUpgradeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Source
    {
        public readonly string Name;
        public readonly Func<int, LrcLibResult?> Answer;
        public int Calls;
        public Source(string name, Func<int, LrcLibResult?> answer) { Name = name; Answer = answer; }
        public LyricsBulkSource Bulk => new(Name, _ => true, (a, t, d, c) =>
        {
            var n = Interlocked.Increment(ref Calls);
            return Task.FromResult(Answer(n));
        });
    }

    private static LrcLibResult Word() => new() { SyncedLyrics = Elrc, PlainLyrics = "Hello world" };
    private static LrcLibResult Line() => new() { SyncedLyrics = Lrc, PlainLyrics = "Hello world" };
    private static LrcLibResult Plain() => new() { PlainLyrics = "Hello world" };

    private static LyricsProviderException Busy(string name)
        => new(name, new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable));

    private Track NewTrack(string name = "song")
    {
        var audio = Path.Combine(_dir, name + ".flac");
        File.WriteAllText(audio, "x");
        return new Track { Title = name, Artist = "Artist", FilePath = audio, SourceType = SourceType.Local, Duration = TimeSpan.FromMinutes(3) };
    }

    private LyricsBulkService Service(params Source[] sources) => new(
        null!,
        new LyricsWriter(new LyricsSaveTargetTests.RecordingMetadata(), null,
            new AppWrittenSidecarRegistry(Path.Combine(_dir, "registry.json")), Path.Combine(_dir, "cache"))
        {
            SaveTarget = () => LyricsSaveTarget.Sidecar,
        },
        _library,
        () => _settings,
        () => sources.Select(s => s.Bulk).ToList(),
        Path.Combine(_dir, "ledger.json"))
    {
        Clock = () => _clock,
        Delay = (t, _) => { _waits.Add(t); _clock += t; return Task.CompletedTask; },
    };

    private string Side(Track t, string ext) => Path.ChangeExtension(t.FilePath!, ext);

    [Fact]
    public async Task WordSyncedFromALaterSource_BeatsLineSyncedFromAnEarlierOne()
    {
        var a = new Source("A", _ => Line());
        var b = new Source("B", _ => Word());
        var t = NewTrack();

        var s = await Service(a, b).FetchAsync(new[] { t }, new LyricsBulkOptions(), null, CancellationToken.None);

        Assert.Equal((1, 1), (s.Synced, s.WordSynced));
        Assert.Equal(Elrc, File.ReadAllText(Side(t, ".elrc")));
        Assert.Equal(1, _library.SaveCount);
    }

    [Fact]
    public async Task StopsAtTheFirstWordSyncedAnswer_LaterSourcesAreNotAsked()
    {
        var a = new Source("A", _ => Word());
        var b = new Source("B", _ => Line());
        await Service(a, b).FetchAsync(new[] { NewTrack() }, new LyricsBulkOptions(), null, CancellationToken.None);
        Assert.Equal((1, 0), (a.Calls, b.Calls));
    }

    [Fact]
    public async Task LineSyncedSong_IsSkippedWithoutUpgrade_AndUpgradedWithIt()
    {
        var a = new Source("A", _ => Word());
        var t = NewTrack();
        t.SyncedLyrics = Lrc;

        var plainRun = await Service(a).FetchAsync(new[] { t }, new LyricsBulkOptions(), null, CancellationToken.None);
        Assert.Equal(1, plainRun.Skipped);
        Assert.Equal(0, a.Calls);

        var outcomes = new List<string>();
        var up = await Service(a).FetchAsync(new[] { t }, new LyricsBulkOptions(Upgrade: true),
            new SyncProgress(p => outcomes.Add(p.Outcome)), CancellationToken.None);
        Assert.Equal((1, 1, 1), (up.Synced, up.WordSynced, up.Upgraded));
        Assert.Equal("upgraded to word-synced", Assert.Single(outcomes));
        Assert.True(File.Exists(Side(t, ".elrc")));
    }

    [Fact]
    public async Task Upgrade_NeverSavesSomethingPoorer()
    {
        var a = new Source("A", _ => Plain());
        var t = NewTrack();
        t.SyncedLyrics = Lrc;
        var s = await Service(a).FetchAsync(new[] { t }, new LyricsBulkOptions(Upgrade: true), null, CancellationToken.None);
        Assert.Equal(1, s.Skipped);
        Assert.Equal(0, s.Synced + s.PlainOnly);
        Assert.Equal(Lrc, t.SyncedLyrics);
    }

    [Fact]
    public async Task AlreadyWordSynced_IsNeverLookedUp()
    {
        var a = new Source("A", _ => Word());
        var t = NewTrack();
        File.WriteAllText(Side(t, ".elrc"), Elrc);
        var s = await Service(a).FetchAsync(new[] { t }, new LyricsBulkOptions(Upgrade: true), null, CancellationToken.None);
        Assert.Equal(1, s.Skipped);
        Assert.Equal(0, a.Calls);
    }

    [Fact]
    public async Task BusyAnswer_BacksOffAndRetries_ThenSaves()
    {
        var a = new Source("A", n => n <= 2 ? throw Busy("A") : Word());
        var s = await Service(a).FetchAsync(new[] { NewTrack() }, new LyricsBulkOptions(), null, CancellationToken.None);
        Assert.Equal(1, s.WordSynced);
        Assert.Equal(3, a.Calls);
        // 15 s, then 30 s: the doubling back-off, not a hammering retry.
        Assert.Contains(TimeSpan.FromSeconds(15), _waits);
        Assert.Contains(TimeSpan.FromSeconds(30), _waits);
    }

    [Fact]
    public async Task ASourceBusyAllTheTime_IsLeftAlone_AndTheSongsAreAskedAgainNextRun()
    {
        var a = new Source("A", _ => throw Busy("A"));
        var tracks = Enumerable.Range(0, 4).Select(i => NewTrack("s" + i)).ToArray();
        var s = await Service(a).FetchAsync(tracks, new LyricsBulkOptions(), null, CancellationToken.None);
        Assert.Equal(new[] { "A" }, s.BusySources);
        // Gave up after four busy answers in a row, not 4 songs × 3 tries.
        Assert.Equal(4, a.Calls);

        var b = new Source("A", _ => Word());
        var again = await Service(b).FetchAsync(tracks, new LyricsBulkOptions(), null, CancellationToken.None);
        Assert.Equal(4, again.WordSynced);
    }

    [Fact]
    public async Task NotFound_IsRemembered_SoTheNextRunSkipsIt_UnlessRechecking()
    {
        var a = new Source("A", _ => null);
        var t = NewTrack();
        var first = await Service(a).FetchAsync(new[] { t }, new LyricsBulkOptions(), null, CancellationToken.None);
        Assert.Equal(1, first.NotFound);
        Assert.True(File.Exists(Path.Combine(_dir, "ledger.json")));

        var second = await Service(a).FetchAsync(new[] { t }, new LyricsBulkOptions(), null, CancellationToken.None);
        Assert.Equal((1, 1), (second.Skipped, second.CheckedBefore));
        Assert.Equal(1, a.Calls);

        await Service(a).FetchAsync(new[] { t }, new LyricsBulkOptions(Recheck: true), null, CancellationToken.None);
        Assert.Equal(2, a.Calls);

        // A month on, it is asked again anyway.
        _clock += BulkRunLedger.MaxAge + TimeSpan.FromDays(1);
        await Service(a).FetchAsync(new[] { t }, new LyricsBulkOptions(), null, CancellationToken.None);
        Assert.Equal(3, a.Calls);
    }

    [Fact]
    public async Task PlainOnlyFinds_AreListedForLyricsStudio()
    {
        var a = new Source("A", _ => Plain());
        var t = NewTrack();
        var s = await Service(a).FetchAsync(new[] { t }, new LyricsBulkOptions(), null, CancellationToken.None);
        Assert.Equal(1, s.PlainOnly);
        Assert.Same(t, Assert.Single(s.PlainOnlyTracks));
    }

    [Fact]
    public async Task LineSyncedPreference_SavesALineLevelLrc_AndStopsThere()
    {
        _settings.LyricsPreferredType = nameof(LyricsPreferredType.LineSynced);
        var a = new Source("A", _ => Line());
        var b = new Source("B", _ => Word());
        var t = NewTrack();
        var s = await Service(a, b).FetchAsync(new[] { t }, new LyricsBulkOptions(), null, CancellationToken.None);
        Assert.Equal((1, 0), (s.Synced, s.WordSynced));
        Assert.Equal(0, b.Calls);
        Assert.False(File.Exists(Side(t, ".elrc")));
        Assert.Equal(Lrc, File.ReadAllText(Side(t, ".lrc")));
    }

    [Fact]
    public async Task Cancel_StopsTheRun_AndKeepsWhatWasDone()
    {
        using var cts = new CancellationTokenSource();
        var a = new Source("A", n => { if (n == 2) cts.Cancel(); return Word(); });
        var tracks = Enumerable.Range(0, 6).Select(i => NewTrack("c" + i)).ToArray();
        var s = await Service(a).FetchAsync(tracks, new LyricsBulkOptions(), null, cts.Token);
        Assert.InRange(s.WordSynced, 1, 2);
        Assert.True(a.Calls < 6);
    }

    [Fact]
    public void Gate_SpacesRequests_AndResetsTheBackoffAfterAGoodAnswer()
    {
        var now = new DateTime(2026, 1, 1);
        var waits = new List<TimeSpan>();
        var gate = new PoliteGate(TimeSpan.FromMilliseconds(500), () => now, (t, _) => { waits.Add(t); now += t; return Task.CompletedTask; });
        gate.WaitTurnAsync(default).GetAwaiter().GetResult();
        gate.WaitTurnAsync(default).GetAwaiter().GetResult();
        Assert.Equal(TimeSpan.FromMilliseconds(500), Assert.Single(waits));

        gate.ReportBusy();
        gate.ReportBusy();
        Assert.Equal(TimeSpan.FromSeconds(30), gate.CurrentBackoff);
        gate.ReportOk();
        Assert.Equal(TimeSpan.Zero, gate.CurrentBackoff);
        Assert.False(gate.GaveUp);
        for (var i = 0; i < 10; i++) gate.ReportBusy();
        Assert.Equal(gate.MaxBackoff, gate.CurrentBackoff);
        Assert.True(gate.GaveUp);
    }

    /// <summary>Progress&lt;T&gt; posts to a sync context; this reports inline.</summary>
    private sealed class SyncProgress(Action<LyricsBulkProgress> report) : IProgress<LyricsBulkProgress>
    {
        public void Report(LyricsBulkProgress value) { lock (this) report(value); }
    }
}
