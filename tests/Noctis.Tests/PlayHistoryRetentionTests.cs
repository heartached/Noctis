using System.Diagnostics;
using System.Text.Json;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The play log was capped at 10,000 events and trimmed the oldest on every play. The
/// Statistics page, the Settings Statistics tab and the Wrap all read it, and the owner's
/// dev profile had logged 8,470 events between June and 10-09 2026, so the current year's
/// Wrap was weeks from silently losing its first months. The cap is now 250,000, and past
/// 20,000 events a save writes only the events logged since play_history.json was last
/// rewritten (play_history.recent.json) instead of the whole 40 MB log.
/// </summary>
public sealed class PlayHistoryRetentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-playlog-" + Guid.NewGuid().ToString("N"));
    private string LogPath => Path.Combine(_dir, "play_history.json");
    private string TailPath => Path.Combine(_dir, "play_history.recent.json");

    public PlayHistoryRetentionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly DateTime Origin = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A log as earlier versions wrote it: oldest first, one play every 3.5 minutes.</summary>
    private static List<PlayHistoryEvent> Synthetic(int count, int tracks = 3_000)
    {
        var ids = Enumerable.Range(0, tracks).Select(i => new Guid(i, 0, 0, new byte[8])).ToArray();
        var events = new List<PlayHistoryEvent>(count);
        for (var i = 0; i < count; i++)
        {
            events.Add(new PlayHistoryEvent
            {
                TrackId = ids[i % tracks],
                Title = $"Song {i % tracks}",
                Artist = $"Artist {i % 400}",
                PlayedAtUtc = Origin.AddSeconds(i * 210.0),
                Skipped = i % 9 == 0
            });
        }
        return events;
    }

    private void WriteLegacyLog(List<PlayHistoryEvent> events)
        => File.WriteAllText(LogPath, JsonSerializer.Serialize(events));

    private static Track NewTrack(string title = "New") => new() { Id = Guid.NewGuid(), Title = title, Artist = "Artist" };

    [Fact]
    public void Cap_HoldsDecadesOfListening()
    {
        // ~44 plays a day measured on the owner's main profile: 250,000 is ~15 years.
        Assert.True(PlayHistoryService.MaxEvents >= 250_000);
    }

    [Fact]
    public async Task RecordPlay_PastTheOldTenThousandCap_KeepsEveryEvent()
    {
        var legacy = Synthetic(10_000);
        WriteLegacyLog(legacy);
        var history = new PlayHistoryService(LogPath);

        for (var i = 0; i < 5; i++) history.RecordPlay(NewTrack());

        Assert.Equal(10_005, history.Events.Count);
        Assert.Equal(legacy[0].PlayedAtUtc, history.Events[0].PlayedAtUtc);

        await history.FlushAsync();
        var reloaded = new PlayHistoryService(LogPath);
        Assert.Equal(10_005, reloaded.Events.Count);
        Assert.Equal(legacy[0].PlayedAtUtc, reloaded.Events[0].PlayedAtUtc);
    }

    [Fact]
    public async Task AtTheCap_OnlyTheOldestAreTrimmed_AndEarlierSnapshotsDontChange()
    {
        const int cap = 100;
        var history = new PlayHistoryService(LogPath, cap);
        var recorded = new List<Guid>();
        IReadOnlyList<PlayHistoryEvent>? kept = null;
        Guid[]? keptIds = null;

        for (var i = 0; i < 1_000; i++)
        {
            var track = NewTrack($"T{i}");
            recorded.Add(track.Id);
            history.RecordPlay(track);
            Assert.Equal(Math.Min(i + 1, cap), history.Events.Count);
            if (i == 150)
            {
                kept = history.Events;
                keptIds = kept.Select(e => e.TrackId).ToArray();
            }
        }

        // The newest `cap` plays, oldest first, never fewer.
        Assert.Equal(recorded.Skip(1_000 - cap), history.Events.Select(e => e.TrackId));
        // A snapshot handed out earlier still shows exactly what it showed then.
        Assert.Equal(keptIds, kept!.Select(e => e.TrackId));
        Assert.Equal(recorded.Skip(151 - cap).Take(cap), keptIds);

        await history.FlushAsync();
        Assert.Equal(recorded.Skip(1_000 - cap),
            new PlayHistoryService(LogPath, cap).Events.Select(e => e.TrackId));
    }

    [Fact]
    public async Task LegacyLogAtScale_SavesOnlyTheTail_AndRoundTrips()
    {
        var legacy = Synthetic(249_000);
        WriteLegacyLog(legacy);
        var baseInfo = new FileInfo(LogPath);
        var (baseLength, baseWritten) = (baseInfo.Length, baseInfo.LastWriteTimeUtc);

        var history = new PlayHistoryService(LogPath);
        var a = NewTrack("A");
        var b = NewTrack("B");
        history.RecordPlay(a);
        history.RecordPlay(b);
        history.RecordSkip(b);
        await history.FlushAsync();

        // The 40 MB log wasn't rewritten for two plays; the tail holds them.
        baseInfo.Refresh();
        Assert.Equal(baseLength, baseInfo.Length);
        Assert.Equal(baseWritten, baseInfo.LastWriteTimeUtc);
        Assert.True(new FileInfo(TailPath).Length < 64 * 1024);

        var reloaded = new PlayHistoryService(LogPath);
        Assert.Equal(249_002, reloaded.Events.Count);
        Assert.Equal(legacy[0].PlayedAtUtc, reloaded.Events[0].PlayedAtUtc);
        Assert.Equal(a.Id, reloaded.Events[^2].TrackId);
        Assert.False(reloaded.Events[^2].Skipped);
        Assert.Equal(b.Id, reloaded.Events[^1].TrackId);
        Assert.True(reloaded.Events[^1].Skipped);

        // Remapping a tail-only id still leaves the base file alone.
        var aMoved = Guid.NewGuid();
        reloaded.RemapTrackIds(new Dictionary<Guid, Guid> { [a.Id] = aMoved });
        await reloaded.FlushAsync();
        baseInfo.Refresh();
        Assert.Equal(baseWritten, baseInfo.LastWriteTimeUtc);
        Assert.Equal(aMoved, new PlayHistoryService(LogPath).Events[^2].TrackId);

        // Remapping ids already in play_history.json rewrites it, in the format every
        // version reads (a plain JSON array), and folds the tail in.
        var moved = Guid.NewGuid();
        reloaded.RemapTrackIds(new Dictionary<Guid, Guid> { [legacy[0].TrackId] = moved });
        await reloaded.FlushAsync();
        Assert.False(File.Exists(TailPath));
        using (var stream = File.OpenRead(LogPath))
        {
            var plain = JsonSerializer.Deserialize<List<PlayHistoryEvent>>(stream)!;
            Assert.Equal(249_002, plain.Count);
            Assert.Equal(moved, plain[0].TrackId);
            Assert.DoesNotContain(plain, e => e.TrackId == legacy[0].TrackId);
            Assert.Equal(b.Id, plain[^1].TrackId);
            Assert.True(plain[^1].Skipped);
        }
        Assert.Equal(249_002, new PlayHistoryService(LogPath).Events.Count);
    }

    [Fact]
    public async Task Tail_IsFoldedIntoTheBaseFile_AfterCompactAfterTailEvents()
    {
        WriteLegacyLog(Synthetic(PlayHistoryService.FullRewriteMaxEvents + 1_000));
        var history = new PlayHistoryService(LogPath);

        for (var i = 0; i < PlayHistoryService.CompactAfterTailEvents - 1; i++) history.RecordPlay(NewTrack());
        await history.FlushAsync();
        Assert.True(File.Exists(TailPath));

        history.RecordPlay(NewTrack());
        await history.FlushAsync();
        Assert.False(File.Exists(TailPath));

        var total = PlayHistoryService.FullRewriteMaxEvents + 1_000 + PlayHistoryService.CompactAfterTailEvents;
        using var stream = File.OpenRead(LogPath);
        Assert.Equal(total, JsonSerializer.Deserialize<List<PlayHistoryEvent>>(stream)!.Count);
        Assert.Equal(total, new PlayHistoryService(LogPath).Events.Count);
    }

    [Fact]
    public async Task TailLeftByAnInterruptedCompaction_IsNotAppliedTwice()
    {
        var legacy = Synthetic(PlayHistoryService.FullRewriteMaxEvents + 10_000);
        WriteLegacyLog(legacy);
        var history = new PlayHistoryService(LogPath);
        history.RecordPlay(NewTrack());
        history.RecordPlay(NewTrack());
        await history.FlushAsync();
        var staleTail = File.ReadAllBytes(TailPath);

        history.RemapTrackIds(new Dictionary<Guid, Guid> { [legacy[0].TrackId] = Guid.NewGuid() });
        await history.FlushAsync();
        Assert.False(File.Exists(TailPath));

        // As if the app died after rewriting play_history.json but before deleting the tail.
        File.WriteAllBytes(TailPath, staleTail);
        Assert.Equal(legacy.Count + 2, new PlayHistoryService(LogPath).Events.Count);
    }

    [Fact]
    public async Task AtTheCap_LoadAndRecordStayCheap()
    {
        WriteLegacyLog(Synthetic(PlayHistoryService.MaxEvents));

        // Measured ~0.15 s on the dev machine (the old ReadAllText path allocated 209 MB);
        // the bounds are wide so a busy CI runner doesn't flake.
        var sw = Stopwatch.StartNew();
        var history = new PlayHistoryService(LogPath);
        await history.PreloadAsync();
        Assert.Equal(PlayHistoryService.MaxEvents, history.Events.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"load took {sw.Elapsed.TotalMilliseconds:F0} ms");

        // RecordPlay copied the whole log into a new snapshot array on every play (~2 MB and
        // ~0.3 ms at this size). Now it appends and republishes a window over the buffer.
        var first = history.Events[0].PlayedAtUtc;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        sw.Restart();
        for (var i = 0; i < 200; i++) history.RecordPlay(NewTrack());
        var recordTime = sw.Elapsed;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(recordTime < TimeSpan.FromSeconds(2), $"200 plays took {recordTime.TotalMilliseconds:F0} ms");
        Assert.True(allocated < 8 * 1024 * 1024, $"200 plays allocated {allocated / 1024} KB");

        // Still exactly at the cap: the 200 oldest went, nothing more.
        Assert.Equal(PlayHistoryService.MaxEvents, history.Events.Count);
        Assert.Equal(first.AddSeconds(200 * 210.0), history.Events[0].PlayedAtUtc);

        sw.Restart();
        await history.FlushAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"tail save took {sw.Elapsed.TotalMilliseconds:F0} ms");
        Assert.True(File.Exists(TailPath));

        var reloaded = new PlayHistoryService(LogPath);
        Assert.Equal(PlayHistoryService.MaxEvents, reloaded.Events.Count);
        Assert.Equal(first.AddSeconds(200 * 210.0), reloaded.Events[0].PlayedAtUtc);
    }
}
