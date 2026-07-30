using System.Text.Json;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Covers the store-backed Track lyrics: lyric text no longer lives inline in
/// library.json (it was 56% of the file and always-resident RAM) but in one
/// small per-track file under lyrics_store\, loaded lazily. Exercises the
/// store roundtrip, the lazy read-through/setter write-through on Track, the
/// one-time old-JSON migration (including the crash-mid-migration replay), and
/// that saves emit lyric-free JSON while old lyric-bearing JSON still loads.
/// </summary>
public class TrackLyricsStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    private string StoreDir => Path.Combine(_root, "lyrics_store");

    private PersistenceService CreatePersistence() => new(_root);
    private LyricsStore CreateStore() => new(StoreDir);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // ── Store roundtrip ───────────────────────────────────────

    [Fact]
    public void Store_RoundTrips_BothFields()
    {
        var store = CreateStore();
        var id = Guid.NewGuid();

        store.Write(id, "plain text", "[00:01.00]synced");

        var fresh = CreateStore(); // no LRU warm — forces the disk read
        var pair = fresh.Read(id);
        Assert.Equal("plain text", pair.Plain);
        Assert.Equal("[00:01.00]synced", pair.Synced);
    }

    [Fact]
    public void Store_EmptyWrite_DeletesEntry()
    {
        var store = CreateStore();
        var id = Guid.NewGuid();
        store.Write(id, "plain", "synced");
        Assert.True(store.Exists(id));

        store.Write(id, "", "");

        Assert.False(store.Exists(id));
        Assert.True(CreateStore().Read(id).IsEmpty);
    }

    [Fact]
    public void Store_WriteIfAbsent_NeverClobbersExisting()
    {
        var store = CreateStore();
        var id = Guid.NewGuid();
        store.Write(id, "newer edit", "");

        var wrote = store.WriteIfAbsent(id, "stale json copy", "stale synced");

        Assert.False(wrote);
        Assert.Equal("newer edit", CreateStore().Read(id).Plain);
    }

    // ── Track lazy read / write-through ───────────────────────

    [Fact]
    public void Track_UnattachedToStore_BehavesLikePlainProperties()
    {
        var track = new Track();
        Assert.Equal(string.Empty, track.Lyrics);
        Assert.Equal(string.Empty, track.SyncedLyrics);

        track.Lyrics = "in memory";
        track.SyncedLyrics = "[00:01.00]x";
        Assert.Equal("in memory", track.Lyrics);
        Assert.Equal("[00:01.00]x", track.SyncedLyrics);
    }

    [Fact]
    public void Track_ReadsThroughFromStore_OnFirstAccess()
    {
        var store = CreateStore();
        var id = Guid.NewGuid();
        store.Write(id, "stored plain", "stored synced");

        var track = new Track { Id = id };
        track.MigrateLegacyLyricsToStore(store); // attaches the store (no legacy values)

        Assert.Equal("stored plain", track.Lyrics);
        Assert.Equal("stored synced", track.SyncedLyrics);
    }

    [Fact]
    public void Track_SetterCommit_WritesThroughToStore()
    {
        var store = CreateStore();
        var track = new Track { Id = Guid.NewGuid() };

        track.Lyrics = "edited plain";
        track.SyncedLyrics = "[00:05.00]edited";
        track.CommitLyricsToStore(store);

        var onDisk = CreateStore().Read(track.Id);
        Assert.Equal("edited plain", onDisk.Plain);
        Assert.Equal("[00:05.00]edited", onDisk.Synced);
        // The pending override is released after commit; reads come from the store.
        Assert.Equal("edited plain", track.Lyrics);
    }

    [Fact]
    public void Track_ClearingLyrics_CommitDeletesStoreEntry()
    {
        var store = CreateStore();
        var id = Guid.NewGuid();
        store.Write(id, "plain", "synced");
        var track = new Track { Id = id };
        track.MigrateLegacyLyricsToStore(store);

        // RemoveLyrics semantics: both fields cleared, then a library save.
        track.Lyrics = string.Empty;
        track.SyncedLyrics = string.Empty;
        track.CommitLyricsToStore(store);

        Assert.False(store.Exists(id));
        Assert.Equal(string.Empty, track.Lyrics);
    }

    [Fact]
    public void Track_PrepareForIdChange_CarriesLyricsToNewId()
    {
        var store = CreateStore();
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        store.Write(oldId, "moving plain", "moving synced");
        var track = new Track { Id = oldId };
        track.MigrateLegacyLyricsToStore(store);

        track.PrepareLyricsForIdChange();
        track.Id = newId;
        track.CommitLyricsToStore(store);

        var moved = CreateStore().Read(newId);
        Assert.Equal("moving plain", moved.Plain);
        Assert.Equal("moving synced", moved.Synced);
    }

    // ── Old-JSON migration through PersistenceService ─────────

    private static string OldFormatLibraryJson(params (Guid Id, string Lyrics, string Synced)[] tracks)
    {
        var entries = tracks.Select(t => new Dictionary<string, object>
        {
            ["id"] = t.Id,
            ["filePath"] = TestPaths.Primary("Music", $"{t.Id:N}.mp3"),
            ["title"] = "T",
            ["lyrics"] = t.Lyrics,
            ["syncedLyrics"] = t.Synced
        });
        return JsonSerializer.Serialize(entries);
    }

    private async Task WriteOldLibraryAsync(string json)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "library.json"), json);
    }

    [Fact]
    public async Task Migration_MovesInlineLyricsToStore_AndNextSaveIsLyricFree()
    {
        var id = Guid.NewGuid();
        await WriteOldLibraryAsync(OldFormatLibraryJson((id, "old plain", "[00:01.00]old synced")));

        var svc = CreatePersistence();
        var tracks = await svc.LoadLibraryAsync();

        Assert.NotNull(tracks);
        var track = Assert.Single(tracks!);
        // Migration wrote the store file...
        Assert.Equal("old plain", CreateStore().Read(id).Plain);
        // ...and the track reads through it.
        Assert.Equal("old plain", track.Lyrics);
        Assert.Equal("[00:01.00]old synced", track.SyncedLyrics);

        await svc.SaveLibraryAsync(tracks!);
        var saved = await File.ReadAllTextAsync(Path.Combine(_root, "library.json"));
        Assert.DoesNotContain("\"lyrics\"", saved);
        Assert.DoesNotContain("\"syncedLyrics\"", saved);
        Assert.DoesNotContain("old plain", saved);

        // A fresh service (new process) still reads the lyrics — from the store.
        var reloaded = await CreatePersistence().LoadLibraryAsync();
        Assert.Equal("old plain", Assert.Single(reloaded!).Lyrics);
    }

    [Fact]
    public async Task Migration_AfterCrashMidway_ContinuesWithoutClobbering()
    {
        // Crash scenario: a previous launch migrated track A (store file exists,
        // and the user then edited it), crashed before saving library.json —
        // which therefore still carries BOTH tracks' inline lyrics. The replay
        // must keep A's newer store content and finish B's migration.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        CreateStore().Write(a, "A edited after first migration", "");
        await WriteOldLibraryAsync(OldFormatLibraryJson((a, "A stale inline", "A stale synced"), (b, "B inline", "")));

        var tracks = await CreatePersistence().LoadLibraryAsync();

        Assert.Equal(2, tracks!.Count);
        var store = CreateStore();
        Assert.Equal("A edited after first migration", store.Read(a).Plain);
        Assert.Equal("B inline", store.Read(b).Plain);
    }

    [Fact]
    public async Task Migration_TracksWithoutLyrics_CreateNoStoreFiles()
    {
        var id = Guid.NewGuid();
        await WriteOldLibraryAsync(OldFormatLibraryJson((id, "", "")));

        var tracks = await CreatePersistence().LoadLibraryAsync();

        Assert.Single(tracks!);
        Assert.False(Directory.Exists(StoreDir) && Directory.GetFiles(StoreDir).Length > 0);
    }

    [Fact]
    public async Task SavedLibrary_RoundTripsThroughPersistence_WithStoreBackedLyrics()
    {
        // New-format end-to-end: a lyric edit committed via SaveLibraryAsync
        // survives a full save/load cycle without ever touching library.json.
        var svc = CreatePersistence();
        var track = new Track { Id = Guid.NewGuid(), Title = "T", FilePath = TestPaths.Primary("Music", "t.mp3") };
        track.Lyrics = "written via save";
        await svc.SaveLibraryAsync(new List<Track> { track });

        var reloaded = await CreatePersistence().LoadLibraryAsync();
        Assert.Equal("written via save", Assert.Single(reloaded!).Lyrics);
    }
}
