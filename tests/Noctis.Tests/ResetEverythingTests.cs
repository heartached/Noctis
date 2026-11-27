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
        "settings.json", "computers.json", "library.json", "playlists.json", "queue.json",
        "library.db", "library.db-wal", "library.db-shm",
    };

    private static readonly string[] SeededFiles =
    {
        "indexes.json", "crash.log", "crashlog-20261101-120000.log", "play_history.recent.json",
        "wrap_archive.json", "favorite_artists.json", "phone_handoff.json", "queue-position.json",
        "animated_artwork_bulk_ledger.json", "lyrics_bulk_ledger.json", "mp3_decoder_fallback.txt", "whats_new_cache.json",
        "animated_artwork_no_autofetch.json", "animated_artwork_autofetch_ledger.json",
        "artist_video_no_autofetch.json", "artist_video_autofetch_ledger.json", "artist_video_index.json",
        "artist_video_shrink_tried.json", "music_video_choices.json", "musixmatch_token", "computers.json", "replaygain-backfill.progress", "label-backfill.progress",
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

    /// <summary>
    /// The library reads the hidden folders and linked albums once at load. The reset wrote
    /// none to settings.json, but the library kept hiding / linking the old ones after the next
    /// scan, and its next hide wrote the old list back into the file.
    /// </summary>
    [AvaloniaFact]
    public async Task LibraryClear_ForgetsTheHiddenFoldersAndAlbumLinks_ItReadAtLoad()
    {
        Directory.CreateDirectory(_root);
        var persistence = new PersistenceService(_root);
        var old = Path.Combine(_root, "Music", "Old");
        await persistence.SaveSettingsAsync(new AppSettings
        {
            HiddenLibraryFolders = new() { old },
            AlbumLinks = new() { new AlbumLink { AlbumId = Guid.NewGuid(), SourceAlbumId = Guid.NewGuid() } },
        });
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new AuditTrailService(persistence));
        await library.LoadAsync();
        Assert.Single(library.HiddenFolders);
        Assert.Single(library.AlbumLinks);

        await library.ClearAsync();
        await persistence.SaveSettingsAsync(new AppSettings()); // the reset's default settings
        await library.SetFolderHiddenAsync(Path.Combine(_root, "Music", "New"), hidden: true);

        Assert.Empty(library.AlbumLinks);
        Assert.Equal(new[] { Path.Combine(_root, "Music", "New") }, library.HiddenFolders);
        Assert.Equal(new[] { Path.Combine(_root, "Music", "New") }, (await persistence.LoadSettingsAsync()).HiddenLibraryFolders);
    }

    /// <summary>
    /// Every file name the desktop app writes, as a literal in its source, is classified for
    /// Reset Everything (owner 11-27: the reset fell behind the features added after it). A new
    /// data file fails here until it is put in SettingsViewModel.ResetClearedFiles (and cleared
    /// by its owner if one holds it in memory) or listed below with the reason it stays.
    /// </summary>
    [Fact]
    public void EveryDataFileName_InTheSource_IsClassifiedForTheReset()
    {
        var otherwise = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Written again by the reset itself, empty or with defaults.
            ["settings.json"] = "rewritten", ["library.json"] = "rewritten", ["library.db"] = "rewritten",
            ["playlists.json"] = "rewritten", ["queue.json"] = "rewritten",
            // Emptied by their owners during the reset.
            ["tidal-auth.json"] = "TidalService.Disconnect", ["pending_scrobbles.json"] = "ScrobbleQueue.Forget",
            // Inside folders the reset deletes.
            ["lastfm_album_descriptions.json"] = "cache/", ["lastfm_taste.json"] = "cache/",
            ["lastfm_stats.json"] = "cache/", ["events.jsonl"] = "audit/",
            // Kept on purpose (SettingsViewModel.Reset.cs, KEPT).
            [".instance.lock"] = "kept", ["session.log"] = "kept", ["local-api.json"] = "kept",
            ["auto-update.json"] = "kept", ["app_written_sidecars.json"] = "kept", ["users.db"] = "kept",
            ["server.pfx"] = "kept", ["server.pfx.key"] = "kept", ["sync.db"] = "kept",
            ["yt-dlp-update.json"] = "kept (tools/)", ["plugin.json"] = "kept (plugins/)",
            ["ggml-tiny.bin"] = "kept (models/)", ["ggml-base.bin"] = "kept (models/)",
            ["ggml-small.bin"] = "kept (models/)", ["ggml-medium.bin"] = "kept (models/)", ["lullaby.bin"] = "kept (models/)",
            ["data-location.json"] = "kept: the data folder's location, in the home folder",
            // Not in the data folder.
            ["Noctis.Plugins.index.json"] = "embedded resource", ["Thumbs.db"] = "music folder junk",
            ["checksums.txt"] = "release asset name", ["plugins.dat"] = "libvlc's folder in the app",
            ["noctis_wasapi.log"] = "Desktop", ["noctis_vlc_diag.log"] = "Desktop", ["noctis_memtrace.log"] = "Desktop",
        };
        var cleared = new HashSet<string>(SettingsViewModel.ResetClearedFiles, StringComparer.Ordinal);
        bool MatchesPattern(string name) => SettingsViewModel.ResetClearedPatterns.Any(p =>
            System.Text.RegularExpressions.Regex.IsMatch(name,
                "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace("\\*", ".*") + "$"));

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Noctis.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var literal = new System.Text.RegularExpressions.Regex("\"([A-Za-z0-9_.-]+\\.(?:json|jsonl|db|txt|progress|log|dat|bin|key|pfx|lock))\"");
        var names = new[] { "Noctis", "Noctis.Core", "Noctis.UI", "Noctis.Core.Server" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root!.FullName, "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .SelectMany(f => literal.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        var unclassified = names.Where(n => !cleared.Contains(n) && !MatchesPattern(n) && !otherwise.ContainsKey(n)).OrderBy(n => n).ToList();
        Assert.True(unclassified.Count == 0, "Not classified for Reset Everything: " + string.Join(", ", unclassified));
        Assert.Contains("music_video_choices.json", names); // the scan itself works
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

    /// <summary>Settings › Lyrics › Music videos: each song's video choice, held in memory by
    /// the stream service, which saved them straight back after the reset deleted the file.</summary>
    [Fact]
    public void MusicVideoChoices_Clear_ForgetsTheLoadedChoicesAndTheFile()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "music_video_choices.json");
        var store = new Noctis.Services.YouTube.MusicVideoChoiceStore(path);
        var old = Guid.NewGuid();
        store.Set(new Noctis.Services.YouTube.MusicVideoChoice
            { TrackKey = Noctis.Services.YouTube.MusicVideoChoiceStore.KeyFor(old), VideoId = "old" });
        Assert.True(File.Exists(path));

        store.Clear();

        Assert.Null(store.Get(old));
        Assert.False(File.Exists(path));
        var other = Guid.NewGuid();
        store.Set(new Noctis.Services.YouTube.MusicVideoChoice
            { TrackKey = Noctis.Services.YouTube.MusicVideoChoiceStore.KeyFor(other), VideoId = "new" });
        Assert.DoesNotContain("\"old\"", File.ReadAllText(path));
        Assert.Null(new Noctis.Services.YouTube.MusicVideoChoiceStore(path).Get(old));
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
