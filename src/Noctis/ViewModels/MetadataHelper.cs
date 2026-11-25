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

    /// <summary>Playlist from a Genre or Artist (all its songs, or a mix), opened once made.</summary>
    public static async Task OpenQuickPlaylistDialog()
    {
        var main = App.Services!.GetService<MainWindowViewModel>();
        if (main is null) return;
        var library = App.Services!.GetRequiredService<ILibraryService>().Tracks.ToList();
        var vm = new QuickPlaylistViewModel(library, main.CreatePlaylistAndOpenAsync);
        await ShowDialogOwned(new QuickPlaylistDialog(vm));
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

    /// <summary>GitHub #239: Move to Album for a selection (or the playing song).</summary>
    public static Task OpenMoveToAlbumDialog(IReadOnlyList<Track> tracks)
    {
        if (tracks == null || tracks.Count == 0) return Task.CompletedTask;
        var library = App.Services!.GetRequiredService<ILibraryService>();
        return MoveToAlbumFlow.RunAsync(tracks, library,
            vm => ShowDialogOwned(new MoveToAlbumDialog(vm)),
            ConfirmationDialog.ShowAsync);
    }

    /// <summary>
    /// Bulk lyrics: fetch from the enabled sources and save, or remove app-written lyrics.
    /// GitHub #214: a Find Lyrics run the user hid (or whose results they haven't closed yet)
    /// comes back instead of a new dialog, as the library run does.
    /// </summary>
    public static async Task OpenBulkLyricsDialog(IReadOnlyList<Track> tracks, bool remove)
    {
        if (tracks == null || tracks.Count == 0) return;
        if (ReusableSelectionRun(_selectionLyricsRun, remove) is { } pending)
        {
            await ShowDialogOwned(new BulkLyricsDialog(pending));
            return;
        }
        var service = App.Services!.GetRequiredService<Services.Lyrics.ILyricsBulkService>();
        var vm = new BulkLyricsViewModel(tracks, service, remove, openStudio: OpenLyricsStudio,
            prefs: LyricsOptionPrefs(), savePrefs: SaveLyricsOptionPrefs);
        if (!remove) _selectionLyricsRun = vm;
        await ShowDialogOwned(new BulkLyricsDialog(vm));
    }

    /// <summary>GitHub #214: the last album / selection Find Lyrics run, kept while the user isn't done with it.</summary>
    private static BulkLyricsViewModel? _selectionLyricsRun;

    /// <summary>The run a fetch should bring back instead of starting anew; null for Remove or when there is none.</summary>
    internal static BulkLyricsViewModel? ReusableSelectionRun(BulkLyricsViewModel? last, bool remove) =>
        !remove && last is { NeedsReopen: true } ? last : null;

    /// <summary>
    /// GitHub #214: the label the song and album menus' Find Lyrics entry shows while a hidden
    /// run is pending ("…in progress" / "…results"), so the way back to it is visible; null
    /// when there is none and the entry keeps its own label.
    /// </summary>
    internal static string? PendingFindLyricsMenuHeader() => ReusableSelectionRun(_selectionLyricsRun, remove: false) switch
    {
        { IsRunning: true } => Noctis.Localization.Loc.T("LibraryAlbums.FindLyricsRunning"),
        { } => Noctis.Localization.Loc.T("LibraryAlbums.FindLyricsResults"),
        null => null,
    };

    /// <summary>GitHub #217: the Find Lyrics options as last ticked.</summary>
    private static BulkLyricsOptionPrefs? LyricsOptionPrefs()
    {
        var s = App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings();
        return s is null ? null : new BulkLyricsOptionPrefs(s.BulkLyricsUpgrade, s.BulkLyricsRecheck, s.BulkLyricsReviewFirst);
    }

    /// <summary>GitHub #217: keeps the ticked options, in the live settings and on disk.</summary>
    private static void SaveLyricsOptionPrefs(BulkLyricsOptionPrefs prefs)
    {
        static void Apply(AppSettings s, BulkLyricsOptionPrefs p)
        {
            s.BulkLyricsUpgrade = p.Upgrade;
            s.BulkLyricsRecheck = p.Recheck;
            s.BulkLyricsReviewFirst = p.ReviewFirst;
        }
        if (App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings() is { } live) Apply(live, prefs);
        if (App.Services?.GetService<IPersistenceService>() is { } persistence)
            _ = persistence.UpdateSettingsAsync(s => Apply(s, prefs)).ContinueWith(
                t => DebugLogger.Warn(DebugLogger.Category.Lyrics, "Bulk.SavePrefsFailed", t.Exception?.GetBaseException().Message ?? string.Empty),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    // The whole-library runs live here, not in their dialogs: Hide closes the dialog and the
    // run goes on; opening the tool again shows the same run instead of starting a second one.
    private static BulkLyricsViewModel? _libraryLyricsRun;
    private static BulkAnimatedArtworkViewModel? _libraryArtworkRun;
    private static AnimatedArtworkAutoFetcher? _albumArtworkFetcher;

    /// <summary>
    /// GitHub #167: the one fetcher behind every album page's "fetch animated artwork on
    /// open", so lookups from pages opened in quick succession queue and space out together.
    /// Null without the app's services (tests).
    /// </summary>
    internal static AnimatedArtworkAutoFetcher? AlbumArtworkFetcher
    {
        get
        {
            if (_albumArtworkFetcher != null) return _albumArtworkFetcher;
            var services = App.Services;
            if (services?.GetService<ITunesArtworkService>() is not { } itunes ||
                services.GetService<IPersistenceService>() is not { } persistence)
                return null;
            return _albumArtworkFetcher = AnimatedArtworkAutoFetcher.For(itunes, persistence);
        }
    }

    /// <summary>
    /// Settings › Reset Everything: stops both whole-library runs and waits for them to end, so
    /// neither saves its ledger (or a cover / lyric file) after the data folder is emptied, and
    /// drops them so the tools start over on the new library.
    /// </summary>
    internal static async Task StopLibraryRunsAsync()
    {
        var lyrics = _libraryLyricsRun;
        var artwork = _libraryArtworkRun;
        var selection = _selectionLyricsRun;
        var genres = _libraryGenresRun;
        _libraryGenresRun = null;
        _selectionLyricsRun = null;
        _libraryLyricsRun = null;
        _libraryArtworkRun = null;
        // Its block list and "no match" memory belong to the old data; the next page reloads them.
        _albumArtworkFetcher = null;
        // GitHub #195: the background lookup of new songs stops too.
        if (App.Services?.GetService<Services.Lyrics.LyricsAutoFindService>() is { } autoLyrics)
        {
            try { await autoLyrics.ClearAsync(); } catch { }
        }
        if (lyrics is { IsRunning: true })
        {
            lyrics.StopCommand.Execute(null);
            try { await (lyrics.StartCommand.ExecutionTask ?? Task.CompletedTask); } catch { }
        }
        // GitHub #214: an album / selection Find Lyrics run is kept now, so it stops too.
        if (selection is { IsRunning: true })
        {
            selection.StopCommand.Execute(null);
            try { await (selection.StartCommand.ExecutionTask ?? Task.CompletedTask); } catch { }
            try { await (selection.ApplyCommand.ExecutionTask ?? Task.CompletedTask); } catch { }
        }
        if (artwork is { IsRunning: true })
        {
            artwork.StopCommand.Execute(null);
            try { await (artwork.StartCommand.ExecutionTask ?? Task.CompletedTask); } catch { }
        }
        // GitHub #267: the library genre lookup writes nothing until Apply, but must not apply to the new library.
        if (genres is not null)
        {
            genres.StopCommand.Execute(null);
            try { await (genres.StartCommand.ExecutionTask ?? Task.CompletedTask); } catch { }
            try { await (genres.ApplyCommand.ExecutionTask ?? Task.CompletedTask); } catch { }
        }
    }

    /// <summary>
    /// Settings ▸ Tools ▸ Find Lyrics (Discord, Cosmo 11-02): every local song through the bulk
    /// fetch, which skips songs that already have the preferred format and resumes a stopped run.
    /// </summary>
    public static async Task OpenLibraryLyricsDialog()
    {
        if (_libraryLyricsRun is not { IsRunning: true })
        {
            var tracks = App.Services!.GetRequiredService<ILibraryService>().Tracks
                .Where(t => t.SourceType == SourceType.Local).ToList();
            if (tracks.Count == 0) return;
            var service = App.Services!.GetRequiredService<Services.Lyrics.ILyricsBulkService>();
            _libraryLyricsRun = new BulkLyricsViewModel(tracks, service, remove: false, library: true, openStudio: OpenLyricsStudio,
                prefs: LyricsOptionPrefs(), savePrefs: SaveLyricsOptionPrefs);
        }
        await ShowDialogOwned(new BulkLyricsDialog(_libraryLyricsRun));
    }

    /// <summary>Settings ▸ Tools ▸ Find Animated Artwork: every album without a loop, clear matches only.</summary>
    public static async Task OpenLibraryAnimatedArtworkDialog()
    {
        if (_libraryArtworkRun is not { IsRunning: true })
        {
            var services = App.Services!;
            var persistence = services.GetRequiredService<IPersistenceService>();
            // GitHub #185: the quality Settings › Artwork names, read per album.
            var settings = services.GetService<MainWindowViewModel>()?.Settings;
            var service = AnimatedArtworkBulkService.For(
                services.GetRequiredService<ITunesArtworkService>(),
                new AnimatedCoverService(persistence),
                Path.Combine(persistence.DataDirectory, "animated_artwork_bulk_ledger.json"),
                () => settings?.AnimatedArtworkPrefersMax == true);
            var albums = AnimatedArtworkBulkService.GroupAlbums(services.GetRequiredService<ILibraryService>().Tracks);
            _libraryArtworkRun = new BulkAnimatedArtworkViewModel(albums, service);
        }
        await ShowDialogOwned(new BulkAnimatedArtworkDialog(_libraryArtworkRun));
    }

    /// <summary>GitHub #267: the whole-library Get Genres from Discogs run, kept while hidden like the other library runs.</summary>
    private static BulkDiscogsGenresViewModel? _libraryGenresRun;

    /// <summary>
    /// GitHub #267: Get Genres from Discogs for the songs' albums (an album menu, a selection of
    /// albums or songs). Songs are grouped by album; only local songs of named albums take part.
    /// </summary>
    public static Task OpenDiscogsGenresDialog(IReadOnlyList<Track> tracks)
    {
        if (tracks == null || tracks.Count == 0) return Task.CompletedTask;
        var albums = AnimatedArtworkBulkService.GroupAlbums(tracks.Distinct());
        if (albums.Count == 0) return Task.CompletedTask;
        return ShowDialogOwned(new BulkDiscogsGenresDialog(CreateDiscogsGenresViewModel(albums, library: false)));
    }

    /// <summary>GitHub #267: Settings ▸ Tools ▸ Get Genres from Discogs: every local album.</summary>
    public static async Task OpenLibraryDiscogsGenresDialog()
    {
        // A running lookup, or finds not yet applied, come back instead of a new run.
        if (_libraryGenresRun is not ({ IsRunning: true } or { HasUnsavedFinds: true }))
        {
            var albums = AnimatedArtworkBulkService.GroupAlbums(App.Services!.GetRequiredService<ILibraryService>().Tracks);
            _libraryGenresRun = CreateDiscogsGenresViewModel(albums, library: true);
        }
        await ShowDialogOwned(new BulkDiscogsGenresDialog(_libraryGenresRun));
    }

    private static BulkDiscogsGenresViewModel CreateDiscogsGenresViewModel(IReadOnlyList<IReadOnlyList<Track>> albums, bool library)
    {
        var services = App.Services!;
        return new BulkDiscogsGenresViewModel(albums,
            services.GetRequiredService<DiscogsGenreService>(),
            services.GetRequiredService<IMetadataService>(),
            services.GetRequiredService<ILibraryService>(),
            isLibraryRun: library,
            prefs: DiscogsGenrePrefs(),
            savePrefs: SaveDiscogsGenrePrefs);
    }

    /// <summary>GitHub #267: the dialog's options as last set.</summary>
    internal static DiscogsGenreOptions? DiscogsGenrePrefs()
    {
        var s = App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings();
        return s is null ? null : DiscogsGenrePrefsFrom(s);
    }

    internal static DiscogsGenreOptions DiscogsGenrePrefsFrom(AppSettings s) => new(
        Enum.TryParse<DiscogsGenreSource>(s.DiscogsGenreSource, ignoreCase: true, out var source) ? source : DiscogsGenreSource.Styles,
        Math.Max(0, s.DiscogsMaxGenres), s.DiscogsUseRelease, s.DiscogsAddToExisting);

    internal static void ApplyDiscogsGenrePrefs(AppSettings s, DiscogsGenreOptions o)
    {
        s.DiscogsGenreSource = o.Source.ToString();
        s.DiscogsMaxGenres = o.MaxGenres;
        s.DiscogsUseRelease = o.UseRelease;
        s.DiscogsAddToExisting = o.AddToExisting;
    }

    /// <summary>GitHub #267: keeps the options, in the live settings and on disk.</summary>
    private static void SaveDiscogsGenrePrefs(DiscogsGenreOptions options)
    {
        if (App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings() is { } live) ApplyDiscogsGenrePrefs(live, options);
        if (App.Services?.GetService<IPersistenceService>() is { } persistence)
            _ = persistence.UpdateSettingsAsync(s => ApplyDiscogsGenrePrefs(s, options)).ContinueWith(
                t => DebugLog.Write(DiscogsGenreService.LogSource, "saving options failed: " + (t.Exception?.GetBaseException().Message ?? string.Empty)),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Search Lyrics with a source picker (issue #113), for the lyrics page's track.</summary>
    public static Task OpenLyricsSearchDialog(LyricsSearchViewModel vm) =>
        ShowDialogOwned(new LyricsSearchDialog(vm));

    /// <summary>Search Artist Info (GitHub #141): pick the artist page's Deezer account.</summary>
    public static Task OpenArtistInfoSearchDialog(ArtistInfoSearchViewModel vm) =>
        ShowDialogOwned(new ArtistInfoSearchDialog(vm));

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
                Path.Combine(App.Services!.GetRequiredService<IPersistenceService>().DataDirectory, "lyrics_studio_drafts")))
        {
            // "Sync with lrc.red's model" beside Lullaby (offered while lrc.red is on in Settings).
            LrcRed = App.Services!.GetService<ILrcRedLyricsService>(),
        };
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
        var vm = new AudioConverterViewModel(tracks, service, library, ConverterPrefs(), SaveConverterPrefs);
        var window = new AudioConverterDialog(vm);
        await ShowDialogOwned(window);
    }

    /// <summary>The Convert dialog's options as of the last conversion started.</summary>
    private static AudioConverterPrefs? ConverterPrefs()
    {
        var s = App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings();
        return s is null ? null : ConverterPrefsFrom(s);
    }

    internal static AudioConverterPrefs ConverterPrefsFrom(AppSettings s) => new(
        s.ConverterFormat, s.ConverterBitrateKbps, s.ConverterBitDepth, s.ConverterPcmBitDepth,
        s.ConverterDsdRate, s.ConverterOutputFolder, s.ConverterFilenamePattern,
        s.ConverterCopyTags, s.ConverterEmbedArtwork, s.ConverterAddToLibrary);

    internal static void ApplyConverterPrefs(AppSettings s, AudioConverterPrefs p)
    {
        s.ConverterFormat = p.Format;
        s.ConverterBitrateKbps = p.BitrateKbps;
        s.ConverterBitDepth = p.BitDepth;
        s.ConverterPcmBitDepth = p.PcmBitDepth;
        s.ConverterDsdRate = p.DsdRate;
        s.ConverterOutputFolder = p.OutputFolder;
        s.ConverterFilenamePattern = p.FilenamePattern;
        s.ConverterCopyTags = p.CopyTags;
        s.ConverterEmbedArtwork = p.EmbedArtwork;
        s.ConverterAddToLibrary = p.AddToLibrary;
    }

    /// <summary>Keeps the options, in the live settings and on disk (as the Find Lyrics ones, #217).</summary>
    private static void SaveConverterPrefs(AudioConverterPrefs prefs)
    {
        if (App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings() is { } live) ApplyConverterPrefs(live, prefs);
        if (App.Services?.GetService<IPersistenceService>() is { } persistence)
            _ = persistence.UpdateSettingsAsync(s => ApplyConverterPrefs(s, prefs)).ContinueWith(
                t => DebugLog.Write("AudioConverter", $"Saving the options failed: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
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
        // Save closes the window at once and writes the files behind it (Discord 11-23).
        if (vm.SaveCommand.ExecutionTask is { IsCompleted: false } save)
            _ = ReportBackgroundSaveAsync(vm, save);
        else if (vm.HasSaveError)
            ShowNotice(vm.SaveErrorMessage, TimeSpan.FromSeconds(8));
        try { await load; }
        catch (Exception ex)
        {
            // Every read inside is guarded; should anything still throw, IsLoading has
            // already ended false (its finally), and logging keeps the fault observed.
            DebugLogger.Warn(DebugLogger.Category.UI, "Metadata.Load", ex.Message);
        }
    }

    /// <summary>How long a background save runs quietly before the notice pill says it is
    /// still saving. Most saves (a title, a rating) finish well inside it and say nothing.</summary>
    internal static readonly TimeSpan SlowSaveNoticeDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Reports an Edit Info save that is still writing after its window closed: nothing for a
    /// quick success, "Saving changes…" once it runs longer than <see cref="SlowSaveNoticeDelay"/>
    /// (then "Changes saved."), and the failure text (logged by the save) when a file could
    /// not be written — the inline error strip the dialog used to keep open for it is gone
    /// with the window.
    /// </summary>
    private static async Task ReportBackgroundSaveAsync(MetadataViewModel vm, Task save)
    {
        var saidSaving = false;
        if (await Task.WhenAny(save, Task.Delay(SlowSaveNoticeDelay)) != save)
        {
            saidSaving = true;
            ShowNotice(Noctis.Localization.Loc.T("Metadata.SavingInBackground"), TimeSpan.FromMinutes(10));
        }
        try { await save; }
        catch { /* Save catches and reports its own faults in SaveErrorMessage */ }
        if (vm.HasSaveError)
        {
            DebugLog.Write("Metadata", $"Background save: {vm.SaveErrorMessage}");
            ShowNotice(vm.SaveErrorMessage, TimeSpan.FromSeconds(8));
        }
        else if (saidSaving)
            ShowNotice(Noctis.Localization.Loc.T("Metadata.SavedInBackground"));
    }

    private static void ShowNotice(string text, TimeSpan? duration = null) =>
        App.Services?.GetService<MainWindowViewModel>()?.ShowNotice(text, duration);

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

        var opened = tracks;
        var edited = tracks.Select(t => EditedInstance(t, library)).ToList();
        var vm = new MetadataViewModel(edited[0], metadata, library, persistence, animatedCovers,
            albumScoped: true, albumTracks: edited, itunes: itunes, lrcLib: lrcLib, multiSelect: true,
            metadataSearch: App.Services!.GetService<Services.MetadataSearch.IMetadataSearchService>());
        AddUserEqPresets(vm);

        vm.ChangesSaved += (_, _) =>
        {
            for (var i = 0; i < opened.Count; i++) MirrorSavedEdit(opened[i], edited[i]);
            if (App.Services!.GetService<MainWindowViewModel>() is { } main)
                LiveApplyPlayingOptions(main, edited);
        };

        var window = new MetadataWindow(vm);
        await ShowThenLoad(window, vm);
    }

    /// <summary>
    /// Live-applies volume adjust and EQ preset when one of the edited tracks is playing.
    /// Only the exact track the dialog was opened from used to count: an album or
    /// multi-select save fans Options out to every track, yet the playing one kept its old
    /// volume and EQ until it was played again (the multi-select dialog had no hook at all).
    /// </summary>
    private static void LiveApplyPlayingOptions(MainWindowViewModel main, IReadOnlyCollection<Track> edited)
    {
        var playing = main.Player.CurrentTrack;
        if (playing == null) return;
        // By id, and the values from the edited instance: the queue can hold a pre-reload
        // Track object for the same song (see SyncQueueFavoritesFromLibrary), which neither
        // equals the edited one nor carries the values the save just wrote.
        var saved = edited.FirstOrDefault(t => t.Id == playing.Id);
        if (saved == null) return;
        var audio = App.Services!.GetRequiredService<IAudioPlayer>();
        // Unchanged saves (e.g. artwork-only) skip the write: the setter feeds the
        // volume machinery that a concurrent gapless handoff is contending with.
        if (audio.VolumeAdjust != saved.VolumeAdjust)
            audio.VolumeAdjust = saved.VolumeAdjust;
        main.Settings.ApplyEqPresetByName(
            string.IsNullOrEmpty(saved.EqPreset) ? null : saved.EqPreset);
    }

    /// <summary>
    /// GitHub #212: the lyrics page's Add Lyrics / Edit Lyrics. Edit Info opened on the song's
    /// lyrics (<see cref="MetadataViewModel.OpenOnLyrics"/>): type or paste plain text or LRC,
    /// fix timed lyrics in their raw text. Save writes them the way every Edit Info save does
    /// (Settings ▸ Lyrics ▸ Save lyrics to); <paramref name="saved"/> then runs, so the page
    /// can show them at once.
    /// </summary>
    public static Task OpenLyricsEditor(Track track, Action? saved = null) =>
        OpenMetadataWindow(track, albumScoped: false, openOnLyrics: true, saved: saved);

    /// <summary>
    /// The song's library instance, which Edit Info edits. The player bar, the queue and the
    /// lyrics page hand over the queue's Track, and after any rewrite of the file (every tag
    /// save: the folder watcher re-imports it as a new Track) that is a stale copy: the editor
    /// showed its old tags and Save wrote them all back into the file, undoing an edit made
    /// from the Songs list meanwhile (Discord, NJZ: "the metadata edits itself back"); with no
    /// re-import after the save, the library's row never saw the edit at all.
    /// </summary>
    internal static Track EditedInstance(Track track, ILibraryService library) =>
        !track.IsExternal && library.GetTrackById(track.Id) is { } own ? own : track;

    /// <summary>After a save, the copy Edit Info was opened on shows what was saved.</summary>
    internal static void MirrorSavedEdit(Track opened, Track edited)
    {
        if (opened.CopyTagsFrom(edited)) opened.NotifyMetadataUpdated();
    }

    public static async Task OpenMetadataWindow(Track track, bool albumScoped = false, bool openOnLyrics = false, Action? saved = null)
    {
        // The metadata editor is file-backed end to end (TagLib reads, atomic
        // file rewrites, sidecar renames) — nothing it does is meaningful for a
        // media-server stream, so opening it on one is a no-op.
        if (track.IsRemoteStream) return;

        var metadata = App.Services!.GetRequiredService<IMetadataService>();
        var library = App.Services!.GetRequiredService<ILibraryService>();
        var persistence = App.Services!.GetRequiredService<IPersistenceService>();
        var opened = track;
        track = EditedInstance(track, library);

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
        var vm = new MetadataViewModel(track, metadata, library, persistence, animatedCovers, albumScoped, albumTracks, itunes, lrcLib, metadataSearch: App.Services!.GetService<Services.MetadataSearch.IMetadataSearchService>());
        AddUserEqPresets(vm);

        vm.AnimatedCoverChanging += (_, _) =>
        {
            // Release the playing loop's file handle before Save deletes/overwrites the
            // cover — LibVLC keeps it open and the file operation would silently fail.
            var main = App.Services!.GetService<MainWindowViewModel>();
            if (main?.Player.CurrentTrack?.AlbumId == track.AlbumId)
                main.Player.CurrentAnimatedCoverPath = null;
        };

        // First: the handlers below and `saved` (the lyrics page) read the copy they hold.
        vm.ChangesSaved += (_, _) => MirrorSavedEdit(opened, track);
        vm.ChangesSaved += (_, _) =>
        {
            var main = App.Services!.GetService<MainWindowViewModel>();
            if (main == null) return;

            // An animated cover may have been added/removed for this album — re-resolve so
            // surfaces bound to the player (album detail header, now playing, mini-art) update.
            if (main.Player.CurrentTrack?.AlbumId == track.AlbumId)
                main.Player.RefreshAnimatedCover();

            LiveApplyPlayingOptions(main, albumTracks ?? new List<Track> { track });
        };
        if (saved != null) vm.ChangesSaved += (_, _) => saved();
        if (openOnLyrics) vm.OpenOnLyrics();

        var window = new MetadataWindow(vm);
        await ShowThenLoad(window, vm);
    }
}
