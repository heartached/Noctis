using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Covers the per-track user-state journal in library.db: pure mutations (rating,
/// favorite, play count, snooze) are written as small journal rows instead of
/// re-serializing the entire library.json, and the journal overlays the JSON on load.
/// </summary>
public class TrackUserStateJournalTests
{
    private static Track MakeTrack(string title = "Song") => new()
    {
        Id = Guid.NewGuid(),
        FilePath = TestPaths.Primary("Music", $"{title}.flac"),
        Title = title,
        Artist = "Artist",
        Album = "Album",
        AlbumArtist = "Artist",
        Genre = "Pop",
        Duration = TimeSpan.FromSeconds(123),
        FileSize = 1000,
        LastModified = DateTime.UtcNow,
        DateAdded = DateTime.UtcNow,
        SourceType = SourceType.Local
    };

    [Fact]
    public async Task UpsertAndLoad_RoundTripsAllFields()
    {
        using var persistence = new TestPersistenceService();
        var index = new SqliteLibraryIndexService(persistence);

        var track = MakeTrack();
        track.PlayCount = 7;
        track.LastPlayed = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        track.Rating = 4;
        track.IsDisliked = true;
        track.IsFavorite = true;
        track.FavoritedAt = new DateTime(2026, 6, 1, 8, 30, 0, DateTimeKind.Utc);
        track.SnoozedUntil = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        track.SavedPositionMs = 45_000;

        await index.UpsertUserStateAsync(new[] { track });
        var state = await index.LoadUserStateAsync();

        var (id, row) = Assert.Single(state);
        Assert.Equal(track.Id, id);
        Assert.Equal(7, row.PlayCount);
        Assert.Equal(track.LastPlayed, row.LastPlayed);
        Assert.Equal(4, row.Rating);
        Assert.True(row.IsDisliked);
        Assert.True(row.IsFavorite);
        Assert.Equal(track.FavoritedAt, row.FavoritedAt);
        Assert.Equal(track.SnoozedUntil, row.SnoozedUntil);
        Assert.Equal(45_000, row.SavedPositionMs);

        // Upsert (not insert-only): a second write updates the same row.
        track.Rating = 2;
        track.IsFavorite = false;
        track.FavoritedAt = null;
        await index.UpsertUserStateAsync(new[] { track });
        state = await index.LoadUserStateAsync();
        Assert.Equal(2, state[track.Id].Rating);
        Assert.False(state[track.Id].IsFavorite);
        Assert.Null(state[track.Id].FavoritedAt);
    }

    [Fact]
    public async Task SeedFromJson_OnlyWhenEmpty()
    {
        using var persistence = new TestPersistenceService();
        var index = new SqliteLibraryIndexService(persistence);

        var track = MakeTrack();
        track.Rating = 5;
        await index.SeedUserStateIfEmptyAsync(new[] { track });

        var state = await index.LoadUserStateAsync();
        Assert.Equal(5, state[track.Id].Rating);

        // Non-empty journal: a second seed must not overwrite anything.
        var other = MakeTrack("Other");
        other.Rating = 1;
        track.Rating = 3;
        await index.SeedUserStateIfEmptyAsync(new[] { track, other });

        state = await index.LoadUserStateAsync();
        Assert.Equal(5, state[track.Id].Rating);
        Assert.False(state.ContainsKey(other.Id));
    }

    [Fact]
    public async Task Journal_SurvivesMirrorReplaceAllDeleteAndClear()
    {
        using var persistence = new TestPersistenceService();
        var index = new SqliteLibraryIndexService(persistence);

        var track = MakeTrack();
        track.PlayCount = 42;
        track.Rating = 5;
        await index.UpsertUserStateAsync(new[] { track });

        // Simulate a scan: full delete+reinsert of the mirror with a different set,
        // then removal + clear. None of it may touch the journal — rows for
        // currently-absent Ids are deliberately retained (the track may return).
        await index.ReplaceAllAsync(new[] { MakeTrack("Other") });
        await index.DeleteTracksAsync(new[] { track.Id });
        await index.ClearAsync();

        var state = await index.LoadUserStateAsync();
        Assert.Equal(42, state[track.Id].PlayCount);
        Assert.Equal(5, state[track.Id].Rating);
    }

    [Fact]
    public async Task LoadAsync_OverlaysJournalOverJson()
    {
        using var persistence = new JournalTestPersistence();
        var index = new SqliteLibraryIndexService(persistence);
        var library = new LibraryService(
            new FakeMetadataService(), persistence, index, new FakeAuditTrail());

        // JSON (stale) values.
        var track = MakeTrack();
        track.Rating = 1;
        track.PlayCount = 3;
        track.IsFavorite = false;
        persistence.LibraryTracks = new List<Track> { track };

        // Journal (newer) values for the same Id.
        var journaled = MakeTrack();
        journaled.Id = track.Id;
        journaled.Rating = 5;
        journaled.PlayCount = 9;
        journaled.LastPlayed = new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc);
        journaled.IsFavorite = true;
        journaled.FavoritedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await index.UpsertUserStateAsync(new[] { journaled });

        await library.LoadAsync();

        var loaded = Assert.Single(library.Tracks, t => t.Id == track.Id);
        Assert.Equal(5, loaded.Rating);
        Assert.Equal(9, loaded.PlayCount);
        Assert.Equal(journaled.LastPlayed, loaded.LastPlayed);
        Assert.True(loaded.IsFavorite);
        // The journaled favorite timestamp must win over the IsFavorite setter's fresh stamp.
        Assert.Equal(journaled.FavoritedAt, loaded.FavoritedAt);
    }

    [Fact]
    public async Task LoadAsync_SeedsJournalFromJsonOnFirstRun()
    {
        using var persistence = new JournalTestPersistence();
        var index = new SqliteLibraryIndexService(persistence);
        var library = new LibraryService(
            new FakeMetadataService(), persistence, index, new FakeAuditTrail());

        var track = MakeTrack();
        track.Rating = 4;
        track.PlayCount = 11;
        persistence.LibraryTracks = new List<Track> { track };

        await library.LoadAsync();

        // JSON values kept in memory, and copied into the empty journal as baseline.
        Assert.Equal(4, library.Tracks[0].Rating);
        var state = await index.LoadUserStateAsync();
        Assert.Equal(4, state[track.Id].Rating);
        Assert.Equal(11, state[track.Id].PlayCount);
    }

    [Fact]
    public async Task SetTracksRating_WritesJournalRowNotFullJson()
    {
        using var persistence = new JournalTestPersistence();
        var index = new SqliteLibraryIndexService(persistence);
        var library = new LibraryService(
            new FakeMetadataService(), persistence, index, new FakeAuditTrail());

        var track = MakeTrack();
        persistence.LibraryTracks = new List<Track> { track };
        await library.LoadAsync();
        persistence.SaveLibraryCalls = 0;

        await library.SetTracksRatingAsync(new[] { library.Tracks[0] }, 5);

        Assert.Equal(0, persistence.SaveLibraryCalls);
        var state = await index.LoadUserStateAsync();
        Assert.Equal(5, state[track.Id].Rating);
    }

    [Fact]
    public async Task CorruptDb_FallsBackToFullJsonSave_NeverLosesRating()
    {
        using var persistence = new JournalTestPersistence();
        // A library.db that is not a SQLite database at all.
        await File.WriteAllTextAsync(
            Path.Combine(persistence.DataDirectory, "library.db"), "not a database");
        var index = new SqliteLibraryIndexService(persistence);
        var library = new LibraryService(
            new FakeMetadataService(), persistence, index, new FakeAuditTrail());

        var track = MakeTrack();
        track.Rating = 3;
        persistence.LibraryTracks = new List<Track> { track };

        // Load must not throw; the JSON values stay live.
        await library.LoadAsync();
        Assert.Equal(3, library.Tracks[0].Rating);
        persistence.SaveLibraryCalls = 0;

        // With the journal unusable, a rating change falls back to the full save.
        await library.SetTracksRatingAsync(new[] { library.Tracks[0] }, 5);
        Assert.Equal(5, library.Tracks[0].Rating);
        Assert.True(persistence.SaveLibraryCalls >= 1);
    }

    [Fact]
    public async Task EndToEnd_OldJsonLibrary_MigratesLyrics_SeedsJournal_SavesLyricFree()
    {
        // Combined-batch flow with the REAL PersistenceService (streaming load +
        // lyric migration) and the REAL journal: an old-format library.json with
        // inline lyrics and a rating must (1) migrate lyrics to the store and seed
        // the journal on first load, (2) journal — not full-save — a rating change,
        // and (3) emit lyric-free JSON on save while a fresh process still reads
        // both the lyrics (store) and the newer rating (journal overlay).
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var id = Guid.NewGuid();
            var libraryPath = Path.Combine(root, "library.json");
            await File.WriteAllTextAsync(libraryPath, System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["filePath"] = TestPaths.Primary("Music", "song.mp3"),
                    ["title"] = "Song",
                    ["rating"] = 4,
                    ["lyrics"] = "inline plain",
                    ["syncedLyrics"] = "[00:01.00]inline synced"
                }
            }));

            var persistence = new PersistenceService(root);
            // Pin the schema version so LoadAsync's background backfill stays inert.
            await persistence.SaveSettingsAsync(new AppSettings { MetadataSchemaVersion = int.MaxValue });
            var index = new SqliteLibraryIndexService(persistence);
            var library = new LibraryService(new FakeMetadataService(), persistence, index, new FakeAuditTrail());

            await library.LoadAsync();
            var track = Assert.Single(library.Tracks);
            Assert.Equal("inline plain", track.Lyrics);
            Assert.Equal(4, track.Rating);
            Assert.Equal(4, (await index.LoadUserStateAsync())[id].Rating); // seeded from JSON

            // Rating change: journal row only — library.json must stay untouched.
            var jsonBefore = await File.ReadAllTextAsync(libraryPath);
            await library.SetTracksRatingAsync(new[] { track }, 5);
            Assert.Equal(jsonBefore, await File.ReadAllTextAsync(libraryPath));
            Assert.Equal(5, (await index.LoadUserStateAsync())[id].Rating);

            // Structural save: lyric-free JSON; lyrics live in the store.
            await library.SaveAsync();
            var saved = await File.ReadAllTextAsync(libraryPath);
            Assert.DoesNotContain("inline plain", saved);
            Assert.DoesNotContain("\"lyrics\"", saved);

            // Fresh process: store-backed lyrics readable, journal rating wins.
            var persistence2 = new PersistenceService(root);
            var library2 = new LibraryService(
                new FakeMetadataService(), persistence2, new SqliteLibraryIndexService(persistence2), new FakeAuditTrail());
            await library2.LoadAsync();
            var reloaded = Assert.Single(library2.Tracks);
            Assert.Equal(5, reloaded.Rating);
            Assert.Equal("inline plain", reloaded.Lyrics);
            Assert.Equal("[00:01.00]inline synced", reloaded.SyncedLyrics);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* background init may still hold library.db */ }
        }
    }

    // ── Fakes ─────────────────────────────────────────────────

    private sealed class JournalTestPersistence : IPersistenceService, IDisposable
    {
        public string DataDirectory { get; }

        public List<Track> LibraryTracks { get; set; } = new();
        public int SaveLibraryCalls;

        public JournalTestPersistence()
        {
            DataDirectory = Path.Combine(
                Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DataDirectory);
        }

        public bool LibraryLoadFailed => false;
        public string? LastCorruptFilePath => null;
        public bool SettingsLoadFailed => false;

        // Schema already current — keeps LoadAsync's background backfills inert.
        public Task<AppSettings> LoadSettingsAsync()
            => Task.FromResult(new AppSettings { MetadataSchemaVersion = int.MaxValue });
        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
        public Task<List<Track>?> LoadLibraryAsync() => Task.FromResult<List<Track>?>(LibraryTracks);
        public Task SaveLibraryAsync(List<Track> tracks)
        {
            Interlocked.Increment(ref SaveLibraryCalls);
            return Task.CompletedTask;
        }
        public Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(new List<Playlist>());
        public Task SavePlaylistsAsync(List<Playlist> playlists) => Task.CompletedTask;
        public Task<QueueState?> LoadQueueStateAsync() => Task.FromResult<QueueState?>(null);
        public Task SaveQueueStateAsync(QueueState state) => Task.CompletedTask;
        public Task<LibraryIndexCache?> LoadIndexCacheAsync() => Task.FromResult<LibraryIndexCache?>(null);
        public Task SaveIndexCacheAsync(LibraryIndexCache cache) => Task.CompletedTask;
        public string GetArtworkPath(Guid albumId) => Path.Combine(DataDirectory, "artwork", $"{albumId}.jpg");
        public void SaveArtwork(Guid albumId, byte[] imageData) { }
        public string GetAnimatedCoverPath(Guid albumId, Guid? trackId, string extension)
            => Path.Combine(DataDirectory, "animated_covers", $"{albumId}.mp4");
        public void EnsureAnimatedCoverDir() { }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(DataDirectory))
                    Directory.Delete(DataDirectory, true);
            }
            catch
            {
                // Ignore cleanup race locks in tests.
            }
        }
    }

    private sealed class FakeMetadataService : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt)
        {
            embeddedArt = null;
            return null;
        }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => true;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => true;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => true;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => true;
        bool IMetadataService.WriteAdvancedFields(string filePath,
            AdvancedTagIO.AdvancedFields fields, AdvancedTagIO.AdvancedFields original) => true;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private sealed class FakeAuditTrail : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
