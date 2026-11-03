using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.AudioAnalysis;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Reset Everything (owner 11-03: "resetting the app should bring everything back to
/// default"). It cleared the library, playlists, queue and a few caches, but left the listening
/// history (Stats, Wrapped, Home), favorite artists, the ratings / play-count journal in
/// library.db (a rescan restored all of it), animated covers, artist info, lyrics, the avatar,
/// ledgers and progress files behind, and services holding some of them in memory wrote them back.
/// </summary>
public class ResetEverythingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", "reset-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>What stays after the reset (documented in SettingsViewModel.Reset.cs).</summary>
    private static readonly string[] KeptFiles =
    {
        "plugins/dev.example/plugin.json", "plugin-data/dev.example/state.json", "models/ggml-base.bin",
        "tools/yt-dlp-update.json", "server/users.db", "server/server.pfx", "server/server.pfx.key",
        "sync/sync.db", "app_written_sidecars.json", "local-api.json", "auto-update.json",
    };

    /// <summary>Written again, empty or with defaults, as a fresh install's first run writes them.</summary>
    private static readonly string[] RewrittenFiles =
    {
        "settings.json", "library.json", "playlists.json", "queue.json",
        "library.db", "library.db-wal", "library.db-shm",
    };

    private static readonly string[] SeededFiles =
    {
        "indexes.json", "crash.log", "crashlog-20261101-120000.log", "play_history.recent.json",
        "wrap_archive.json", "favorite_artists.json", "phone_handoff.json", "queue-position.json",
        "animated_artwork_bulk_ledger.json", "lyrics_bulk_ledger.json", "mp3_decoder_fallback.txt",
        "musixmatch_token", "replaygain-backfill.progress", "label-backfill.progress",
        "genre-backfill.progress", "library.json.corrupt-20261101-120000", "settings.json.bak",
        "artwork/album.jpg", "artwork/artists/a.jpg", "artwork/similar/s.jpg", "animated_covers/a.mp4",
        "playlist_covers/p.jpg", "cache/artwork_thumbs/t.png", "cache/waveforms/w.bin",
        "cache/lastfm_album_descriptions.json", "audit/2026-11.jsonl", "artist_info/a.json",
        "artist_info/a.mbid", "artist_bios/a.json", "lyrics_cache/a.lrc", "lyrics_store/a.json",
        "lyrics_studio_drafts/d.json", "lyrics_background/background.mp4", "profile/avatar.jpg",
        "organize_undo/u.json", "app-icons/ruby.ico", "server/covers/c.jpg", "server/transcode/t.flac",
    };

    private static void Touch(string root, string relative)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    private static long Count(string root, string table)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, "library.db"), Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)cmd.ExecuteScalar()!;
    }

    private static Track MakeTrack() => new()
    {
        Id = Guid.NewGuid(),
        FilePath = TestPaths.Primary("Music", "Song.flac"),
        Title = "Song", Artist = "Artist", Album = "Album", AlbumArtist = "Artist",
        Duration = TimeSpan.FromSeconds(100), SourceType = SourceType.Local,
        PlayCount = 12, Rating = 5, IsFavorite = true,
    };

    [AvaloniaFact]
    public async Task Reset_LeavesOnlyAFreshInstallPlusTheKeepList()
    {
        Directory.CreateDirectory(_root);
        var persistence = new PersistenceService(_root);
        var index = new SqliteLibraryIndexService(persistence);
        var library = new LibraryService(new MetadataService(), persistence, index, new AuditTrailService(persistence));
        var history = new PlayHistoryService(Path.Combine(_root, "play_history.json"));

        // Library user state, BPM/key analysis, play log, embedded-tag lyrics.
        var track = MakeTrack();
        await index.UpsertTracksAsync(new[] { track });
        await index.UpsertUserStateAsync(new[] { track });
        await new AudioAnalysisStore(persistence).UpsertAsync(new TrackAnalysisRecord(
            track.FilePath, 1, "2026-11-01T00:00:00Z", 120, 0.9, "8A", 0.8, "2026-11-01T00:00:00Z"), default);
        history.RecordPlay(track);
        await history.FlushAsync();
        persistence.LyricsStore.Write(track.Id, "plain", "[00:01.00]synced");
        Assert.False(persistence.LyricsStore.Read(track.Id).IsEmpty); // now in the LRU too

        foreach (var f in SeededFiles.Concat(KeptFiles)) Touch(_root, f);
        Assert.Equal(1, Count(_root, "track_user_state"));
        Assert.Equal(1, Count(_root, "track_analysis"));

        var vm = new SettingsViewModel(persistence, library, history);
        await vm.LoadAsync();

        // ConfirmResetLibrary without its OS side effects (launch at login).
        await vm.ClearSavedDataAsync();
        var defaults = new AppSettings { CommunityPluginsEnabled = false };
        await persistence.SaveSettingsAsync(defaults);
        vm.ResetSettingsToDefaults(defaults);
        await vm.ClearSavedDataAfterSettingsResetAsync();

        // Nothing in memory to write back.
        Assert.Empty(history.Events);
        Assert.Empty(library.Tracks);
        Assert.True(persistence.LyricsStore.Read(track.Id).IsEmpty);
        Assert.Equal(0, Count(_root, "tracks"));
        Assert.Equal(0, Count(_root, "track_user_state"));
        Assert.Equal(0, Count(_root, "track_analysis"));

        // On disk: the keep-list, the files a first run writes, and empty folders.
        var left = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(_root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();
        Assert.Empty(left.Except(KeptFiles).Except(RewrittenFiles));
        Assert.Empty(KeptFiles.Except(left));

        // Folders services wrote into without re-creating are back, empty.
        foreach (var dir in new[] { "artwork/artists", "artwork/similar", "artist_info", "animated_covers", "lyrics_cache", "cache" })
            Assert.True(Directory.Exists(Path.Combine(_root, dir)), dir);
    }

    [Fact]
    public async Task ClearUserState_EmptiesTheJournal_ThatScansKeep()
    {
        using var persistence = new TestPersistenceService();
        var index = new SqliteLibraryIndexService(persistence);
        await index.UpsertUserStateAsync(new[] { MakeTrack(), MakeTrack() });
        await index.ClearAsync(); // the scan mirror: leaves the journal alone on purpose
        Assert.Equal(2, (await index.LoadUserStateAsync()).Count);

        await index.ClearUserStateAsync();

        Assert.Empty(await index.LoadUserStateAsync());
    }

    [Fact]
    public void FavoriteArtists_Clear_ForgetsTheLoadedSetAndTheFile()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "favorite_artists.json");
        var favorites = new FavoriteArtistsService(path);
        favorites.SetFavorite("Artist", true);
        Assert.True(File.Exists(path));

        favorites.Clear();

        Assert.False(favorites.IsFavorite("Artist"));
        Assert.False(File.Exists(path));
        favorites.SetFavorite("Other", true); // the next heart writes only itself
        Assert.DoesNotContain("Artist", File.ReadAllText(path));
    }

    [Fact]
    public void WrapArchive_Clear_ForgetsLoadedYears()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "wrap_archive.json");
        var archive = new WrapArchiveService(path);
        var track = MakeTrack();
        var events = new List<PlayHistoryEvent>
        {
            new() { TrackId = track.Id, Title = track.Title, Artist = track.Artist, PlayedAtUtc = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc) },
        };
        archive.EnsureArchived(events, new Dictionary<Guid, Track> { [track.Id] = track }, currentYear: 2026);
        Assert.Contains(2024, archive.ArchivedYears);

        archive.Clear();

        Assert.Empty(archive.ArchivedYears);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void PhoneHandoff_Clear_ForgetsTheHeldHandoff()
    {
        Directory.CreateDirectory(_root);
        var inbox = new PhoneHandoffInbox(_root);
        inbox.Save(new PhoneHandoff { TrackIds = { Guid.NewGuid() }, SavedUtc = DateTime.UtcNow });
        inbox.NoteDesktopPlayback(DateTime.UtcNow.AddHours(-1));
        Assert.NotNull(inbox.Latest);

        inbox.Clear();

        Assert.Null(inbox.Latest);
        Assert.Equal(default, inbox.DesktopPlaybackUtc);
        Assert.False(File.Exists(Path.Combine(_root, "phone_handoff.json")));
        Assert.Null(new PhoneHandoffInbox(_root).Latest);
    }

    [Fact]
    public void Mp3DecoderFallbackList_Clear_ForgetsEntries()
    {
        Directory.CreateDirectory(_root);
        var store = Path.Combine(_root, "mp3_decoder_fallback.txt");
        var mp3 = Path.Combine(_root, "damaged.mp3");
        File.WriteAllText(mp3, "not really an mp3");
        var list = new Mp3DecoderFallbackList(store);
        Assert.True(list.Add(mp3));
        Assert.True(list.Contains(mp3));

        list.Clear();

        Assert.False(list.Contains(mp3));
        Assert.False(File.Exists(store));
        Assert.False(new Mp3DecoderFallbackList(store).Contains(mp3));
    }

    [Fact]
    public async Task AlbumDescriptions_Clear_DropsOverridesHeldInMemory()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "cache", "lastfm_album_descriptions.json");
        var descriptions = new LastFmAlbumDescriptions(new HttpClient(), path);
        await descriptions.SetOverrideAsync("Artist", "Old Album", "my own words");
        Assert.Contains("my own words", File.ReadAllText(path));

        await descriptions.ClearAsync();
        Assert.False(File.Exists(path));

        // The next write saves the whole dictionary: the old override must not come back.
        await descriptions.SetOverrideAsync("Artist", "New Album", "new words");
        Assert.DoesNotContain("my own words", File.ReadAllText(path));
    }

    [Fact]
    public async Task PinnedIconCopies_AreDeleted()
    {
        Touch(_root, "app-icons/ruby.ico");

        await PinnedShortcutIcon.ClearIconCopiesAsync(_root);

        Assert.False(Directory.Exists(Path.Combine(_root, "app-icons")));
    }
}
