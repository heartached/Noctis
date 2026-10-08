using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Noctis.Models;
using Noctis.Services;
using Noctis.Views;
using Noctis.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace Noctis.ViewModels;

/// <summary>
/// Shared helper to open the Metadata window from any ViewModel.
/// </summary>
public static class MetadataHelper
{
    /// <summary>
    /// Shows a dialog as a modal owned by the main window (sized to the owner), or as a
    /// standalone window when no desktop lifetime is available.
    /// </summary>
    private static async Task ShowDialogOwned(Window window)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            DialogHelper.SizeToOwner(window, desktop.MainWindow);
            await window.ShowDialog(desktop.MainWindow);
        }
        else
        {
            window.Show();
        }
    }

    public static async Task OpenReplayGainScannerDialog(IReadOnlyList<Track> tracks)
    {
        if (tracks == null || tracks.Count == 0) return;
        var services = App.Services!;
        var service = services.GetRequiredService<IReplayGainScannerService>();
        var library = services.GetRequiredService<ILibraryService>();
        var metadata = services.GetRequiredService<IMetadataService>();
        var persistence = services.GetRequiredService<IPersistenceService>();
        var player = services.GetRequiredService<IAudioPlayer>();

        // "Add files…" resolves a picked path to its library entry when indexed, else
        // reads it the way a dropped external file is read. Snapshot the path index on
        // the UI thread: the resolver runs on the thread pool, and _library.Tracks is
        // mutated on the UI thread.
        var byPath = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in library.Tracks)
            byPath[t.FilePath] = t;
        Track? Resolve(string path) =>
            byPath.TryGetValue(path, out var known) ? known : ExternalTrackReader.Read(metadata, persistence, path);

        var vm = new ReplayGainScannerViewModel(tracks, service, library, Resolve);
        // The player caches the playing file's ReplayGain tags; re-read them so a queue
        // scanned mid-playback is levelled right away, not from the next track on.
        vm.ScanCompleted += (_, _) => player.ReloadReplayGainTags();
        var window = new ReplayGainScannerDialog(vm);
        await ShowDialogOwned(window);
    }

    /// <summary>Opens the auto-organize tool over all local library tracks.</summary>
    public static async Task OpenOrganizeFilesDialog(SettingsViewModel settings)
    {
        var library = App.Services!.GetRequiredService<ILibraryService>();
        var service = App.Services!.GetRequiredService<IFileOrganizerService>();
        var tracks = library.Tracks.Where(t => t.SourceType == SourceType.Local).ToList();
        var vm = new OrganizeFilesViewModel(tracks, service, settings);
        var window = new OrganizeFilesDialog(vm);
        await ShowDialogOwned(window);
    }

    /// <summary>Opens the duplicate-finder tool over the local library.</summary>
    public static async Task OpenDuplicateFinderDialog()
    {
        var service = App.Services!.GetRequiredService<IDuplicateFinderService>();
        var vm = new DuplicateFinderViewModel(service);
        var window = new DuplicateFinderDialog(vm);
        await ShowDialogOwned(window);
    }

    /// <summary>Opens the metadata finder over poorly-tagged local tracks.</summary>
    public static async Task OpenMetadataFinderDialog()
    {
        var library = App.Services!.GetRequiredService<ILibraryService>();
        var finder = App.Services!.GetRequiredService<IMetadataFinderService>();
        var metadata = App.Services!.GetRequiredService<IMetadataService>();
        var candidates = library.Tracks
            .Where(t => t.SourceType == SourceType.Local && IsPoorlyTagged(t))
            .ToList();
        var vm = new MetadataFinderViewModel(candidates, finder, metadata, library);
        var window = new MetadataFinderDialog(vm);
        await ShowDialogOwned(window);
    }

    private static bool IsPoorlyTagged(Track t) =>
        string.IsNullOrWhiteSpace(t.Title) ||
        string.IsNullOrWhiteSpace(t.Artist) || t.Artist == "Unknown Artist" ||
        string.IsNullOrWhiteSpace(t.Album) || t.Album == "Unknown Album";

    /// <summary>Opens the playlist-import tool (Exportify CSV / TuneMyMusic JSON).</summary>
    public static async Task OpenPlaylistImportDialog()
    {
        var service = App.Services!.GetRequiredService<IPlaylistImportService>();
        var vm = new PlaylistImportViewModel(service, App.Services!.GetRequiredService<ITidalAuthService>());
        var window = new PlaylistImportDialog(vm);
        await ShowDialogOwned(window);
    }

    /// <summary>Send to Folder (MusicBee-style copy) for a selection.</summary>
    public static async Task OpenSendToFolderDialog(IReadOnlyList<Track> tracks)
    {
        if (tracks == null || tracks.Count == 0) return;
        var service = App.Services!.GetRequiredService<ISendToFolderService>();
        var settings = App.Services!.GetService<MainWindowViewModel>()?.Settings.GetSettings();
        var vm = new SendToFolderViewModel(tracks, service, settings?.OrganizePattern ?? FileOrganizePlanner.DefaultPattern);
        await ShowDialogOwned(new SendToFolderDialog(vm));
    }

    /// <summary>Bulk lyrics: fetch from LRCLIB and save, or remove app-written lyrics.</summary>
    public static async Task OpenBulkLyricsDialog(IReadOnlyList<Track> tracks, bool remove)
    {
        if (tracks == null || tracks.Count == 0) return;
        var service = App.Services!.GetRequiredService<Services.Lyrics.ILyricsBulkService>();
        var vm = new BulkLyricsViewModel(tracks, service, remove);
        await ShowDialogOwned(new BulkLyricsDialog(vm));
    }

    /// <summary>Search Lyrics with a source picker (issue #113), for the lyrics page's track.</summary>
    public static Task OpenLyricsSearchDialog(LyricsSearchViewModel vm) =>
        ShowDialogOwned(new LyricsSearchDialog(vm));

    /// <summary>Lyrics Studio: time existing lyrics or transcribe, review, then save.</summary>
    public static async Task OpenLyricsStudio(IReadOnlyList<Track> tracks)
    {
        if (tracks == null || tracks.Count == 0) return;
        await ShowDialogOwned(new LyricsStudioDialog(CreateLyricsStudioViewModel(tracks)));
    }

    /// <summary>The Studio over a track list, wired to the app's engine, writer and prefs (dialog and sidebar page).</summary>
    public static LyricsStudioViewModel CreateLyricsStudioViewModel(IReadOnlyList<Track> tracks)
    {
        var main = App.Services!.GetService<MainWindowViewModel>();
        return new LyricsStudioViewModel(
            tracks,
            App.Services!.GetRequiredService<Services.LyricsStudio.ILyricsStudioEngine>(),
            App.Services!.GetRequiredService<Services.Lyrics.LyricsWriter>(),
            App.Services!.GetRequiredService<ILibraryService>(),
            main?.Player,
            () => main?.Settings.GetSettings() ?? new AppSettings(),
            s => { if (main is not null) main.Settings.ApplyLyricsStudioSettings(s); },
            new Services.LyricsStudio.LyricsStudioDraftStore(
                Path.Combine(App.Services!.GetRequiredService<IPersistenceService>().DataDirectory, "lyrics_studio_drafts")));
    }

    /// <summary>
    /// Lyrics Studio over the songs that lack the format the Studio is set to write: with
    /// word timings on, line-only LRC counts as missing (ELRC and LRC are different things);
    /// with it off, any timed lyrics count as done. First 40, so a run stays reviewable.
    /// </summary>
    public static Task OpenLyricsStudioForLibrary(MainWindowViewModel main) =>
        OpenLyricsStudioForLibrary(main.Settings.GetSettings().LyricsStudioWordTimings);

    public static async Task OpenLyricsStudioForLibrary(bool wordTimings)
    {
        var library = App.Services!.GetRequiredService<ILibraryService>();
        var local = library.Tracks.Where(t => t.SourceType == SourceType.Local).ToList();
        // Format detection reads disk per track — keep the library-wide pass off the UI thread.
        var tracks = await Task.Run(() =>
        {
            // Chunked so the scan stops once 40 are found instead of touching the whole library.
            const int chunk = 500;
            var picked = new List<Track>(40);
            for (var start = 0; start < local.Count && picked.Count < 40; start += chunk)
            {
                var slice = local.GetRange(start, Math.Min(chunk, local.Count - start));
                var formats = Services.LyricsStudio.ExistingLyricsLoader.DetectFormats(slice);
                for (var i = 0; i < slice.Count && picked.Count < 40; i++)
                    if (!Services.LyricsStudio.LyricsFormatDetector.AlreadyHas(formats[i], wordTimings)) picked.Add(slice[i]);
            }
            return picked;
        });
        if (tracks.Count > 0) await OpenLyricsStudio(tracks);
    }

    /// <summary>Search YouTube / paste a link and download into the library folder.</summary>
    public static async Task OpenYouTubeDownloadDialog(string? initialQuery = null)
    {
        var service = App.Services!.GetRequiredService<Services.YouTube.IYouTubeImportService>();
        var vm = new YouTubeDownloadViewModel(service, App.Services!.GetRequiredService<HttpClient>(), initialQuery);
        await ShowDialogOwned(new YouTubeDownloadDialog(vm));
    }

    /// <summary>Opens the Spek-style spectrogram window for one track (decodes via ffmpeg).</summary>
    public static async Task OpenSpectrogramWindow(Track track)
    {
        if (track == null) return;
        var converter = App.Services!.GetRequiredService<IAudioConverterService>();
        var vm = new SpectrogramViewModel(track, converter);
        var window = new SpectrogramWindow(vm);
        await ShowDialogOwned(window);
    }

    public static async Task OpenAudioConverterDialog(IReadOnlyList<Track> tracks)
    {
        if (tracks == null || tracks.Count == 0) return;
        var service = App.Services!.GetRequiredService<IAudioConverterService>();
        var library = App.Services!.GetRequiredService<ILibraryService>();
        var vm = new AudioConverterViewModel(tracks, service, library);
        var window = new AudioConverterDialog(vm);
        await ShowDialogOwned(window);
    }

    public static async Task OpenBatchMetadataWindow(IReadOnlyList<Track> tracks)
    {
        if (tracks == null || tracks.Count == 0) return;
        if (tracks.Count == 1) { await OpenMetadataWindow(tracks[0]); return; }
        await OpenMultiTrackMetadataWindow(tracks);
    }

    /// <summary>
    /// Opens the tabbed metadata editor in multi-select mode for an arbitrary set of
    /// tracks: blank artwork, "N artists / M songs selected" header, Mixed fields, and
    /// edits that fan out to every selected track.
    /// </summary>
    /// <summary>Lists the user's saved EQ presets in the Options tab (GitHub #95). Must run
    /// before the window binds: swapping a ComboBox's ItemsSource later nulls its selection.</summary>
    private static void AddUserEqPresets(MetadataViewModel vm)
    {
        if (App.Services!.GetService<MainWindowViewModel>() is { } main)
            vm.SetUserEqPresets(main.Settings.UserEqPresetNames);
    }

    /// <summary>
    /// Shows the metadata window straight away and loads its file-backed state behind it.
    /// It used to await InitializeAsync first, so the window appeared only after two TagLib
    /// parses and a cover decode (owner 10-08: metadata window froze 230–880 ms on lyric
    /// sidecar reads and waited 1–2 s for tag reads before showing). InitializeAsync runs up
    /// to its first await synchronously (IsLoading, baseline), then the dialog opens with
    /// the in-memory fields filled; Save stays disabled until the load lands.
    /// </summary>
    private static async Task ShowThenLoad(MetadataWindow window, MetadataViewModel vm)
    {
        var load = vm.InitializeAsync();
        await ShowDialogOwned(window);
        try { await load; }
        catch (Exception ex)
        {
            // Every read inside is guarded; should anything still throw, IsLoading has
            // already ended false (its finally), and logging keeps the fault observed.
            DebugLogger.Warn(DebugLogger.Category.UI, "Metadata.Load", ex.Message);
        }
    }

    public static async Task OpenMultiTrackMetadataWindow(IReadOnlyList<Track> tracks)
    {
        if (tracks == null || tracks.Count == 0) return;
        if (tracks.Count == 1) { await OpenMetadataWindow(tracks[0]); return; }

        var metadata = App.Services!.GetRequiredService<IMetadataService>();
        var library = App.Services!.GetRequiredService<ILibraryService>();
        var persistence = App.Services!.GetRequiredService<IPersistenceService>();
        var animatedCovers = new AnimatedCoverService(persistence);
        var itunes = App.Services!.GetService<ITunesArtworkService>();
        var lrcLib = App.Services!.GetService<ILrcLibService>();

        var vm = new MetadataViewModel(tracks[0], metadata, library, persistence, animatedCovers,
            albumScoped: true, albumTracks: tracks.ToList(), itunes: itunes, lrcLib: lrcLib, multiSelect: true,
            autoMatch: App.Services!.GetService<AutoMatchCoordinator>());
        AddUserEqPresets(vm);

        var window = new MetadataWindow(vm);
        await ShowThenLoad(window, vm);
    }

    public static async Task OpenMetadataWindow(Track track, bool albumScoped = false)
    {
        // The metadata editor is file-backed end to end (TagLib reads, atomic
        // file rewrites, sidecar renames) — nothing it does is meaningful for a
        // media-server stream, so opening it on one is a no-op.
        if (track.IsRemoteStream) return;

        var metadata = App.Services!.GetRequiredService<IMetadataService>();
        var library = App.Services!.GetRequiredService<ILibraryService>();
        var persistence = App.Services!.GetRequiredService<IPersistenceService>();

        List<Track>? albumTracks = null;
        if (albumScoped)
        {
            albumTracks = library.Tracks
                .Where(t => t.AlbumId == track.AlbumId)
                .ToList();
        }

        var animatedCovers = new AnimatedCoverService(persistence);
        var itunes = App.Services!.GetService<ITunesArtworkService>();
        var lrcLib = App.Services!.GetService<ILrcLibService>();
        var vm = new MetadataViewModel(track, metadata, library, persistence, animatedCovers, albumScoped, albumTracks, itunes, lrcLib, autoMatch: App.Services!.GetService<AutoMatchCoordinator>());
        AddUserEqPresets(vm);

        vm.AnimatedCoverChanging += (_, _) =>
        {
            // Release the playing loop's file handle before Save deletes/overwrites the
            // cover — LibVLC keeps it open and the file operation would silently fail.
            var main = App.Services!.GetService<MainWindowViewModel>();
            if (main?.Player.CurrentTrack?.AlbumId == track.AlbumId)
                main.Player.CurrentAnimatedCoverPath = null;
        };

        vm.ChangesSaved += (_, _) =>
        {
            var main = App.Services!.GetService<MainWindowViewModel>();
            if (main == null) return;

            // An animated cover may have been added/removed for this album — re-resolve so
            // surfaces bound to the player (album detail header, now playing, mini-art) update.
            if (main.Player.CurrentTrack?.AlbumId == track.AlbumId)
                main.Player.RefreshAnimatedCover();

            // Live-apply volume adjust and EQ preset when the edited track is currently playing.
            if (main.Player.CurrentTrack != track) return;
            var audio = App.Services!.GetRequiredService<IAudioPlayer>();
            // Unchanged saves (e.g. artwork-only) skip the write: the setter feeds the
            // volume machinery that a concurrent gapless handoff is contending with.
            if (audio.VolumeAdjust != track.VolumeAdjust)
                audio.VolumeAdjust = track.VolumeAdjust;
            main.Settings.ApplyEqPresetByName(
                string.IsNullOrEmpty(track.EqPreset) ? null : track.EqPreset);
        };

        var window = new MetadataWindow(vm);
        await ShowThenLoad(window, vm);
    }
}
