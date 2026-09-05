using Noctis.Services.Sync;
using Xunit;

namespace Noctis.Tests;

public sealed class SyncStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-sync-" + Guid.NewGuid().ToString("N"));
    private readonly SyncStore _store;

    public SyncStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new SyncStore(Path.Combine(_dir, "sync.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static readonly DateTime T0 = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Upsert_AssignsIncreasingSequences_AndChangesSinceReturnsThem()
    {
        Assert.Equal(0, _store.CurrentSeq);
        Assert.True(_store.Upsert(SyncKinds.Track, "a", "{\"rating\":3}", T0, "desktop"));
        Assert.True(_store.Upsert(SyncKinds.Track, "b", "{\"rating\":5}", T0, "desktop"));
        Assert.Equal(2, _store.CurrentSeq);

        var all = _store.ChangesSince(0);
        Assert.Equal(new[] { "a", "b" }, all.Select(i => i.Id));
        Assert.Equal(new long[] { 1, 2 }, all.Select(i => i.Seq));

        var later = _store.ChangesSince(1);
        Assert.Single(later);
        Assert.Equal("b", later[0].Id);
    }

    [Fact]
    public void OlderWrite_IsIgnored_NewerWins()
    {
        _store.Upsert(SyncKinds.Track, "a", "new", T0.AddMinutes(5), "phone");
        Assert.False(_store.Upsert(SyncKinds.Track, "a", "old", T0, "desktop"));
        Assert.Equal("new", _store.Get(SyncKinds.Track, "a")!.Payload);

        Assert.True(_store.Upsert(SyncKinds.Track, "a", "newest", T0.AddMinutes(10), "desktop"));
        var item = _store.Get(SyncKinds.Track, "a")!;
        Assert.Equal("newest", item.Payload);
        Assert.Equal("desktop", item.Device);
        Assert.Equal(2, item.Seq); // re-stamped with a fresh sequence so pullers see it
    }

    [Fact]
    public void EqualTimestamps_TieBreakByDevice_Deterministically()
    {
        _store.Upsert(SyncKinds.Playlist, "p", "from-a", T0, "device-a");
        Assert.True(_store.Upsert(SyncKinds.Playlist, "p", "from-b", T0, "device-b"));   // "device-b" > "device-a"
        Assert.False(_store.Upsert(SyncKinds.Playlist, "p", "from-a", T0, "device-a"));  // never flips back
        Assert.Equal("from-b", _store.Get(SyncKinds.Playlist, "p")!.Payload);
    }

    [Fact]
    public void SamePayloadSameTime_IsIdempotent()
    {
        Assert.True(_store.Upsert(SyncKinds.Track, "a", "x", T0, "d"));
        Assert.False(_store.Upsert(SyncKinds.Track, "a", "x", T0, "d"));
        Assert.Equal(1, _store.CurrentSeq);
    }

    [Fact]
    public void Devices_AreRecorded_WithTheirLastSequence()
    {
        _store.TouchDevice("phone-1", "Pixel 8", 0);
        _store.TouchDevice("phone-1", "", 7);
        var devices = _store.Devices();
        Assert.Single(devices);
        Assert.Equal("Pixel 8", devices[0].Name);
        Assert.Equal(7, devices[0].LastSeq);
    }

    [Fact]
    public void Payloads_RoundTripThroughSyncJson()
    {
        var state = new TrackSyncState(true, 4, false, 12, T0, T0.AddDays(-1));
        var json = SyncJson.Serialize(state);
        var back = SyncJson.Deserialize<TrackSyncState>(json)!;
        Assert.Equal(state, back);

        var pl = new PlaylistSyncState("Mix", "desc", "#ff0000", new List<Guid> { Guid.NewGuid() }, T0, false);
        var plBack = SyncJson.Deserialize<PlaylistSyncState>(SyncJson.Serialize(pl))!;
        Assert.Equal(pl.Name, plBack.Name);
        Assert.Equal(pl.TrackIds, plBack.TrackIds);
        Assert.Null(SyncJson.Deserialize<TrackSyncState>("not json"));
    }
}
