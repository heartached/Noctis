using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.AudioAnalysis;

namespace Noctis.ViewModels;

/// <summary>
/// Settings › Advanced › Reset Everything, the data half: the app comes back as a fresh install
/// would start (owner 11-03: "resetting the app should bring everything back to default").
/// Everything in the data folder, and what it does:
///
/// CLEARED (user data, caches, resume ledgers)
///   library.json, library.db: tracks, track_user_state (ratings, favorites, play counts,
///     snoozes, badges, saved positions: a rescan restored them all), track_analysis (BPM/key)
///   playlists.json, queue.json, queue-position.json (dropped by the empty queue save)
///   play_history.json + play_history.recent.json (Stats, Wrapped, Home), wrap_archive.json
///   favorite_artists.json, phone_handoff.json
///   artwork/ (artists/, similar/), animated_covers/, playlist_covers/, artist_info/ (incl.
///     pinned MusicBrainz ids), artist_bios/ (legacy), cache/ (artwork_thumbs/, waveforms/,
///     lastfm_album_descriptions.json with the user's description overrides,
///     lastfm_taste.json, the Last.fm mixes' account charts)
///   lyrics_cache/, lyrics_store/ (lyrics read from tags; a rescan reads them again),
///     lyrics_studio_drafts/, lyrics_background/ (its settings reset with the rest)
///   profile/ (avatar; its setting resets), app-icons/ (after the classic icon is back)
///   audit/, crash.log, crashlog-*.log, organize_undo/ (the moved files stay where they are;
///     only the way back is forgotten), *.corrupt-* quarantines, settings.json.bak (old
///     settings, credentials included)
///   animated_artwork_bulk_ledger.json, lyrics_bulk_ledger.json, *-backfill.progress,
///     mp3_decoder_fallback.txt, musixmatch_token (anonymous token, fetched again)
///   tidal-auth.json (signed out, as the other services' sign-ins live in settings.json)
///   server/covers/, server/transcode/ (caches; cleared once the server is off)
///
/// KEPT
///   settings.json: rewritten with defaults (the reset's other half).
///   plugins/, plugin-data/: installed plugins stay installed (see CommunityPluginsEnabled in
///     ConfirmResetLibrary); their data is deleted only with the plugin, because the plugins
///     stay loaded and would write into, or fail on, a folder removed under them.
///   models/: Lyrics Studio speech models, hundreds of MB to download again; not settings.
///   tools/: the yt-dlp binary and its update state.
///   server/ (users.db, server.pfx, server.pfx.key), sync/sync.db: the phone pairing,
///     accounts and sync ledger; deleting them unpairs every phone. (The account features
///     are parked, AccountFeatures.Enabled = false, but the pairing is live.)
///   app_written_sidecars.json: which files next to the user's music Noctis wrote, so they
///     can still be cleaned up later.
///   local-api.json, auto-update.json, .instance.lock, session.log: runtime state of this
///     session (the Local API token, the updater's queue, already settled by the reset's
///     AutoInstallUpdates = false, the single-instance lock, the crash journal).
/// </summary>
public partial class SettingsViewModel
{
    /// <summary>Files deleted outright (relative to the data folder).</summary>
    private static readonly string[] ResetClearedFiles =
    {
        "indexes.json", "crash.log", "play_history.json", "play_history.recent.json",
        "wrap_archive.json", "favorite_artists.json", "phone_handoff.json",
        "queue-position.json", "queue-position.json.tmp",
        "animated_artwork_bulk_ledger.json", "lyrics_bulk_ledger.json",
        "mp3_decoder_fallback.txt", "musixmatch_token",
    };

    /// <summary>File patterns deleted in the data folder itself (not below it).</summary>
    private static readonly string[] ResetClearedPatterns = { "*-backfill.progress", "*.corrupt-*", "crashlog-*.log" };

    /// <summary>Folders deleted; <c>Recreate</c> when a service made it once at startup and
    /// writes into it without creating it again.</summary>
    private static readonly (string Path, bool Recreate)[] ResetClearedFolders =
    {
        ("artwork", true), (Path.Combine("artwork", "artists"), true), (Path.Combine("artwork", "similar"), true),
        ("animated_covers", true), ("lyrics_cache", true), ("playlist_covers", true), ("cache", true),
        ("audit", true), ("artist_info", true), ("artist_bios", false), ("lyrics_store", false),
        ("lyrics_studio_drafts", false), ("organize_undo", false),
    };

    /// <summary>Cleared after the settings reset: their owners let go of them only then.</summary>
    private static readonly string[] ResetClearedFoldersAfterSettings =
    {
        "profile", "lyrics_background", Path.Combine("server", "covers"), Path.Combine("server", "transcode"),
    };

    /// <summary>
    /// Stops the background work that writes into the data folder, empties every in-memory
    /// owner (or it saves its copy straight back) and then the folder itself. One failure
    /// never stops the rest. Internal for tests.
    /// </summary>
    internal async Task ClearSavedDataAsync()
    {
        var root = _persistence.DataDirectory;

        async Task Step(string what, Func<Task> clear)
        {
            try { await clear(); }
            catch (Exception ex) { Debug.WriteLine($"[Settings] Reset: failed to clear {what}: {ex.Message}"); }
        }
        Task StepSync(string what, Action clear) => Step(what, () => { clear(); return Task.CompletedTask; });

        // A play count import would otherwise apply old counts and journal them after the reset.
        CancelPlayCountImport();
        // The whole-library lyrics / animated artwork tools save a ledger as they go.
        await Step("bulk runs", MetadataHelper.StopLibraryRunsAsync);
        // The Artists tab's portrait sweep walks the old artist list for minutes.
        await Step("artist pictures", () => App.Services?.GetService<ArtistImageService>()?.StopFetchesAsync() ?? Task.CompletedTask);

        // Stop playback now, and write the play counts / positions the player still holds, so
        // they land in the journal before it is emptied rather than in the new one after it.
        if (_player is { } player)
        {
            await StepSync("playback", () => player.StopAndClear("reset everything"));
            await Step("pending play state", player.FlushPendingPlayStateAsync);
        }

        // Also stops the tag backfills and the artwork heal, and empties the user-state journal.
        await Step("library", () => _library.ClearAsync());
        await Step("playlists", () => _persistence.SavePlaylistsAsync(new List<Playlist>()));
        await Step("queue state", () => _persistence.SaveQueueStateAsync(new QueueState()));

        // In-memory owners first: each holds what it loaded and would write it back.
        await Step("play history", () => _playHistory.ClearAsync());
        var services = App.Services;
        await StepSync("wrap archive", () => services?.GetService<IWrapArchiveService>()?.Clear());
        await StepSync("phone handoff", () => services?.GetService<PhoneHandoffInbox>()?.Clear());
        // A table in library.db, so emptied whether or not the analysis service ever started.
        await Step("audio analysis", () => (services?.GetService<IAudioAnalysisStore>() ?? new AudioAnalysisStore(_persistence)).ClearAsync());
        await Step("album descriptions", () => _lastFm?.ClearAlbumDescriptionsAsync() ?? Task.CompletedTask);
        await Step("Last.fm mixes", () => services?.GetService<LastFmTasteService>()?.ClearAsync() ?? Task.CompletedTask);
        await StepSync("MP3 decoder list", () => (_audioPlayer as VlcAudioPlayer)?.ForgetDecoderFallbacks());
        await StepSync("lyrics store", () => _persistence.ClearLyricsStore());
        await StepSync("TIDAL sign-in", () => { _tidal?.Disconnect(); IsTidalConnected = false; });
        // Favorite artists are held by the Artists page: MainWindowViewModel clears them on SettingsReset.

        // Then the folder. Task.Run: recursive deletes scale with the library (one cover per
        // album) and blocked the UI thread for their whole duration on slow disks.
        await Task.Run(() =>
        {
            foreach (var name in ResetClearedFiles)
                DeleteForReset(Path.Combine(root, name), isFolder: false);
            foreach (var pattern in ResetClearedPatterns)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(root, pattern))
                        DeleteForReset(file, isFolder: false);
                }
                catch (Exception ex) { Debug.WriteLine($"[Settings] Reset: failed to list {pattern}: {ex.Message}"); }
            }
            foreach (var (name, _) in ResetClearedFolders)
                DeleteForReset(Path.Combine(root, name), isFolder: true);
            foreach (var (name, recreate) in ResetClearedFolders)
            {
                if (!recreate) continue;
                try { Directory.CreateDirectory(Path.Combine(root, name)); }
                catch (Exception ex) { Debug.WriteLine($"[Settings] Reset: failed to recreate {name}: {ex.Message}"); }
            }
        });
        _dirSizeCache.Clear();

        // Preserved crash-session logs (crashlog-*.log) go with crash.log.
        await StepSync("crash logs", CrashJournal.ClearPreserved);
    }

    /// <summary>
    /// The reset's last step, once the settings are back to defaults: the server is off, the
    /// avatar and lyrics clip settings no longer point into their folders, and the pinned
    /// shortcut is back on the classic icon. Internal for tests.
    /// </summary>
    internal async Task ClearSavedDataAfterSettingsResetAsync()
    {
        var root = _persistence.DataDirectory;
        await Task.Run(() =>
        {
            foreach (var name in ResetClearedFoldersAfterSettings)
                DeleteForReset(Path.Combine(root, name), isFolder: true);
            // The default settings save copied the old settings.json (credentials and all) here.
            DeleteForReset(Path.Combine(root, "settings.json.bak"), isFolder: false);
        });
        try { await PinnedShortcutIcon.ClearIconCopiesAsync(root); }
        catch (Exception ex) { Debug.WriteLine($"[Settings] Reset: failed to clear app-icons: {ex.Message}"); }
    }

    private static void DeleteForReset(string path, bool isFolder)
    {
        try
        {
            if (isFolder)
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Reset: failed to delete {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}
