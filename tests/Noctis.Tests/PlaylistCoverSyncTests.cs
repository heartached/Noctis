using System.Text.Json;
using Noctis.Models;
using Noctis.Services.Sync;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Mobile beta 2 (tester Mistery, 11-07): playlists synced but their custom covers did not, since
/// the playlist state had no cover. It now carries one as a hash ("" = none, absent = an older app
/// that says nothing); the desktop's ledger notices a cover set, changed or removed, and a phone's
/// picture lands from the staging folder.
/// </summary>
public class PlaylistCoverSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "plcover-" + Guid.NewGuid().ToString("N"));
    private readonly Persistence _persistence = new();
    private readonly AppSettings _settings = new() { SyncEnabled = true, SyncDeviceId = "desktop-test", SyncDeviceName = "Test PC" };

    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 4, 5, 6 };

    public PlaylistCoverSyncTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _persistence.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Persistence : TestPersistenceService
    {
        public List<Playlist> Playlists { get; } = new();
        public override Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(Playlists.ToList());
    }

    private string File(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    private static PlaylistSyncState? Parse(string json) => SyncJson.Deserialize<PlaylistSyncState>(json);

    // ── The payload ──

    [Fact]
    public void Payload_CarriesTheCover_AndAnOldPayloadStillParses_AsSayingNothing()
    {
        var at = new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);
        var withCover = new PlaylistSyncState("Mix", "", "#112233", new List<Guid> { Guid.NewGuid() }, at, false, "0123456789abcdef");
        var json = SyncJson.Serialize(withCover);
        Assert.Contains("\"cover\":\"0123456789abcdef\"", json);
        var back = Parse(json)!;
        Assert.Equal(withCover.Cover, back.Cover);
        Assert.Equal(withCover.TrackIds, back.TrackIds);

        Assert.Contains("\"cover\":\"\"", SyncJson.Serialize(withCover with { Cover = "" }));   // removed: said
        // Nothing to say: no "cover" at all, byte for byte what an older app writes and reads.
        var none = SyncJson.Serialize(withCover with { Cover = null });
        Assert.DoesNotContain("cover", none);

        // A payload from before covers (an older phone or desktop) parses, and says nothing.
        var old = Parse("{\"name\":\"Mix\",\"description\":\"\",\"color\":\"\",\"trackIds\":[],\"modifiedAt\":\"2026-11-07T10:00:00Z\",\"deleted\":false}")!;
        Assert.Equal("Mix", old.Name);
        Assert.Null(old.Cover);
        Assert.Null(PlaylistSyncState.Tombstone(Guid.Empty, at).Cover);
    }

    [Fact]
    public void From_NamesTheCoverByItsBytes_NoneWhenThereIsNoFile()
    {
        var p = new Playlist { Name = "Mix" };
        Assert.Equal("", PlaylistSyncState.From(p).Cover);
        p.CoverArtPath = Path.Combine(_dir, "missing.jpg");
        Assert.Equal("", PlaylistSyncState.From(p).Cover);
        p.CoverArtPath = File("a.png", Png);
        var hash = PlaylistSyncState.From(p).Cover;
        Assert.True(PlaylistCovers.IsHash(hash));
        Assert.Equal(PlaylistCovers.Hash(Png), hash);
        // Rewritten in place (the desktop writes <id>.jpg again): the new bytes, not a stale memo.
        System.IO.File.WriteAllBytes(p.CoverArtPath, Jpeg);
        System.IO.File.SetLastWriteTimeUtc(p.CoverArtPath, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(PlaylistCovers.Hash(Jpeg), PlaylistSyncState.From(p).Cover);
    }

    [Theory]
    [InlineData("0123456789abcdef", "0123456789abcdef")]
    [InlineData("", "")]
    [InlineData("0123456789ABCDEF", null)]       // not one of ours: says nothing
    [InlineData("../../etc/passwd", null)]
    [InlineData(null, null)]
    public void APushedCover_IsNoneOrAHash_ElseNothing(string? sent, string? kept)
    {
        var sync = new LibrarySyncService(() => _settings, _persistence);
        var state = new PlaylistSyncState("Mix", "", "", new List<Guid>(), DateTime.UtcNow, false, sent);
        var item = sync.Normalize(new SyncItem(SyncKinds.Playlist, Guid.NewGuid().ToString("N"), SyncJson.Serialize(state), DateTime.UtcNow, "phone-1", 0), DateTime.UtcNow);
        Assert.NotNull(item);
        Assert.Equal(kept, Parse(item!.Payload)!.Cover);
    }

    // ── The desktop's ledger notices cover edits ──

    private async Task<PlaylistSyncState?> LedgerAsync(LibrarySyncService sync, Guid id)
    {
        var changes = await sync.GetChangesAsync(0, "phone-12345678", "Phone", TestContext.Current.CancellationToken);
        var item = changes.Items.SingleOrDefault(i => i.Kind == SyncKinds.Playlist && i.Id == id.ToString("N"));
        return item is null ? null : Parse(item.Payload);
    }

    [Fact]
    public async Task TheLedger_FollowsADesktopCover_SetChangedAndRemoved()
    {
        var sync = new LibrarySyncService(() => _settings, _persistence);
        var p = new Playlist { Name = "Mix", ModifiedAt = DateTime.UtcNow.AddMinutes(-10) };
        _persistence.Playlists.Add(p);
        Assert.Equal("", (await LedgerAsync(sync, p.Id))!.Cover);

        p.CoverArtPath = File("one.png", Png);
        p.ModifiedAt = DateTime.UtcNow.AddMinutes(-5);
        Assert.Equal(PlaylistCovers.Hash(Png), (await LedgerAsync(sync, p.Id))!.Cover);
        var seq = sync.CurrentSeq;
        await LedgerAsync(sync, p.Id);
        Assert.Equal(seq, sync.CurrentSeq);   // unchanged: no new change

        p.CoverArtPath = File("two.jpg", Jpeg);
        p.ModifiedAt = DateTime.UtcNow.AddMinutes(-4);
        Assert.Equal(PlaylistCovers.Hash(Jpeg), (await LedgerAsync(sync, p.Id))!.Cover);

        p.CoverArtPath = null;
        p.ModifiedAt = DateTime.UtcNow.AddMinutes(-3);
        Assert.Equal("", (await LedgerAsync(sync, p.Id))!.Cover);
    }

    /// <summary>A ledger row written before covers synced has the same stamp as the playlist: the
    /// cover already set on the desktop must still reach the ledger (an equal stamp loses LWW),
    /// while a playlist without a cover is no news (no new change for every playlist on upgrade).</summary>
    [Fact]
    public async Task ARowFromBeforeCovers_GetsTheCover_ButNoCoverIsNoNews()
    {
        var sync = new LibrarySyncService(() => _settings, _persistence);
        var at = DateTime.UtcNow.AddHours(-1);
        var plain = new Playlist { Name = "Plain", ModifiedAt = at };
        var covered = new Playlist { Name = "Covered", ModifiedAt = at, CoverArtPath = File("c.png", Png) };
        _persistence.Playlists.AddRange(new[] { plain, covered });
        var store = new SyncStore(Path.Combine(_persistence.DataDirectory, "sync", "sync.db"));
        foreach (var p in new[] { plain, covered })
            store.Upsert(SyncKinds.Playlist, p.Id.ToString("N"), SyncJson.Serialize(PlaylistSyncState.From(p) with { Cover = null }), at, "desktop-test");
        var seq = store.CurrentSeq;

        var changes = await sync.GetChangesAsync(seq, "phone-12345678", "Phone", TestContext.Current.CancellationToken);

        var item = Assert.Single(changes.Items);
        Assert.Equal(covered.Id.ToString("N"), item.Id);
        Assert.Equal(PlaylistCovers.Hash(Png), Parse(item.Payload)!.Cover);
    }

    // ── A phone's picture lands on the desktop ──

    [Fact]
    public void Apply_TakesTheStagedPicture_RemovesOnNone_AndIgnoresNothingSaid()
    {
        var covers = Path.Combine(_dir, "playlist_covers");
        var staging = Path.Combine(_dir, "staging");
        Directory.CreateDirectory(covers);
        Directory.CreateDirectory(staging);
        var p = new Playlist { Name = "Mix" };
        var old = Path.Combine(covers, $"{p.Id}.jpg");
        System.IO.File.WriteAllBytes(old, Jpeg);
        p.CoverArtPath = old;

        Assert.False(PlaylistCovers.Apply(p, null, covers, staging));
        Assert.Equal(old, p.CoverArtPath);
        // A hash with no staged picture (never sent) changes nothing.
        Assert.False(PlaylistCovers.Apply(p, PlaylistCovers.Hash(Png), covers, staging));
        Assert.Equal(old, p.CoverArtPath);

        System.IO.File.WriteAllBytes(Path.Combine(staging, PlaylistCovers.Hash(Png)), Png);
        Assert.True(PlaylistCovers.Apply(p, PlaylistCovers.Hash(Png), covers, staging));
        Assert.Equal(Png, System.IO.File.ReadAllBytes(p.CoverArtPath!));
        Assert.Equal(".png", Path.GetExtension(p.CoverArtPath));
        Assert.StartsWith(p.Id.ToString(), Path.GetFileName(p.CoverArtPath));
        Assert.False(System.IO.File.Exists(old));                                       // the old file went
        Assert.False(PlaylistCovers.Apply(p, PlaylistCovers.Hash(Png), covers, staging)); // already shown

        Assert.True(PlaylistCovers.Apply(p, "", covers, staging));
        Assert.Null(p.CoverArtPath);
        Assert.Empty(Directory.GetFiles(covers));
    }

    [Fact]
    public void Store_RefusesWhatIsNotAPicture()
    {
        var covers = Path.Combine(_dir, "playlist_covers");
        Assert.Null(PlaylistCovers.Store(File("x.txt", "hello"u8.ToArray()), Guid.NewGuid(), covers));
        Assert.Null(PlaylistCovers.ImageExtension("RIFF0000WAVE"u8));
        Assert.Equal(".webp", PlaylistCovers.ImageExtension("RIFF0000WEBP"u8));
    }
}
