using Android.Content;
using Android.Content.PM;
using AndroidX.Core.View;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Android.Services;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Mobile.Services;
using Noctis.Mobile.Services.Account;
using Noctis.Mobile.Services.Scrobbling;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Services;
using AApplication = Android.App.Application;
using AResources = Android.Content.Res.Resources;
using AUiMode = Android.Content.Res.UiMode;
using ALog = Android.Util.Log;
using Build = Android.OS.Build;

namespace Noctis.Android;

public partial class AndroidApp : Avalonia.Application, IThemeHost
{
    /// <summary>logcat tag for the mirrored <see cref="DebugLog"/>: `adb logcat -s Noctis`.</summary>
    private const string LogTag = "Noctis";

    private readonly MobileTheme _theme = new();
    private (string Appearance, string DarkTheme, string Accent, bool AccentFollowsTheme) _themeChoice = ("System", "Ink", MobileTheme.DefaultAccent, true);
    private PlatformThemeVariant? _appliedSystem;
    private ShellViewModel? _shell;
    private Media3AudioPlayer? _player;
    private AndroidVolumeControl? _volume;
    private readonly object _accountGate = new();

    /// <summary>Completes when StartAsync has loaded the library (faulted if that failed); the
    /// account service's syncs wait for it.</summary>
    private readonly TaskCompletionSource _libraryLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The running app, for the activity's lifecycle hooks. Declared <c>new</c> on purpose:
    /// the inherited <see cref="Avalonia.Application.Current"/> is typed <c>Application?</c>,
    /// so without this MainActivity's <c>AndroidApp.Current?.OnBackgrounded()</c> would bind
    /// to the base member and fail to compile.
    /// </summary>
    public static new AndroidApp? Current { get; private set; }

    public override void Initialize()
    {
        // First thing after the platform set the dispatcher up: until then every timer due
        // within ~30 ms spins the main thread at a full core (see DispatcherClock).
        if (!DispatcherClock.Align(Dispatcher.UIThread))
            ALog.Warn(LogTag, "Dispatcher clock not aligned: this Avalonia renamed its fields");
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Current = this;
        var context = AApplication.Context;

        // Every Core service files its data under AppPaths.DataRoot; on Android that is the
        // app's private files dir. DataRoot resolves lazily and permanently on first read and
        // a later override with a different root throws, so this must precede the first
        // service construction — nothing above may touch PersistenceService, PlayHistoryService
        // or AppWrittenSidecarRegistry.
        AppPaths.OverrideDataRoot(context.FilesDir!.AbsolutePath);

        // What the desktop App wires at the same point. Without the first, a cover the scan
        // replaced (a re-tagged album) kept showing its old picture in every list until a
        // restart: CachedImage reloads only on ArtworkCache.Invalidate. Without the second,
        // every cold decode of a cover read the full-size file: a 3000 px PNG (most real
        // covers) cannot shrink while decoding, so each grid tile that scrolled in past the
        // bitmap budget cost a full decode again. The thumbnails live in the cache dir, which
        // Android may clear; a missing one is simply decoded from the source once more.
        LibraryService.ArtworkFileReplaced += ArtworkCache.Invalidate;
        ArtworkThumbnailCache.Enable(Path.Combine((context.CacheDir ?? context.FilesDir!).AbsolutePath, "artwork_thumbs"));
        // Two cover decodes at a time: mid-range phones have two fast cores, and a grid fling
        // otherwise ran a full-size decode per tile at once (see CachedImage.MaxConcurrentDecodes).
        Noctis.Controls.CachedImage.MaxConcurrentDecodes = 2;
        // No GC memory pressure per cover: on Android it forces full collections, each one an
        // explicit ART GC on the UI thread, a scroll hitch per burst of covers (ArtworkCache).
        Noctis.Services.ArtworkCache.UseMemoryPressure = false;

        // DebugLog is an in-memory ring with no output of its own, so every handled error the
        // app logs — startup, library scan, playback, SAF listing — would be invisible on a
        // phone. Mirror it to logcat, where `adb logcat -s Noctis` can read it. AttachSink
        // replays what is already buffered, so attaching here loses nothing logged earlier;
        // the reset callback exists for the desktop's disk mirror and has no analogue here.
        // Redacted on the way out: logcat is readable over adb, and account errors can carry
        // a device key or password text (LogRedactor). The crash hooks log through here too.
        // The header's default names the entry assembly, which Android does not have ("Noctis ?").
        DebugLog.DescribeBuild = () => DescribeVersion(context);
        DebugLog.AttachSink(line => ALog.Info(LogTag, LogRedactor.Redact(line)), static () => { });
        HookUnhandledExceptions();

        var persistence = new PersistenceService();
        var metadata = new MetadataService();
        var index = new SqliteLibraryIndexService(persistence);
        var audit = new AuditTrailService(persistence);
        // Favourite/rating changes to desktop songs go through this to the account service,
        // which queues them for the next sync (it attaches itself once constructed).
        var stateRecorder = new NoctisStateRecorder();
        // A folder that is out of reach (a card taken out, a folder renamed) keeps its songs and
        // no longer holds up the others: removing or adding a folder still applies.
        var library = new LibraryService(metadata, persistence, index, audit, syncRecorder: stateRecorder,
            fileSystem: new AndroidFileSystemSource(context)) { ScanPastUnavailableFolders = true };
        var history = new PlayHistoryService();
        // The application context, never the activity: the player outlives the activity
        // (it keeps playing in the background service) and holding the activity leaks it.
        _player = new Media3AudioPlayer(context, library, persistence);

        _volume = new AndroidVolumeControl(context);
        var nowPlaying = new NowPlayingViewModel(_player, library, persistence, history, volume: _volume);
        var account = CreateAccountService(context, library, persistence, stateRecorder, _libraryLoaded.Task);
        // Desktop songs' lyrics come from the desktop (saved on the phone for offline use); every
        // other song reads its sidecars through SAF as before.
        var remoteLyrics = account is IRemoteLyricsSource source
            ? new RemoteLyricsFileAccess(new SafTrackFileAccess(context), source)
            : null;
        var lyrics = new LyricsPageViewModel(_player, nowPlaying, (ITrackFileAccess?)remoteLyrics ?? new SafTrackFileAccess(context), persistence)
        {
            // Avalonia sizes by density only; the lyrics page applies the system font scale itself.
            FontScale = context.Resources?.Configuration?.FontScale ?? 1f,
            PrepareTrack = remoteLyrics is null ? null : remoteLyrics.ForLoad,
            // The desktop's automatic lookup for a song with no lyrics of its own: LRCLIB, asked
            // by title, artist and length only (Settings → Lyrics → Find Lyrics Online).
            Online = LrcLibOnlineLyricsSource.Create(persistence.DataDirectory, new HttpClient()),
        };
        var shell = new ShellViewModel(
            // Settings › Music Folders › Find All Music Automatically (MediaStore, opt-in).
            new LibraryViewModel(library, persistence, new AndroidFolderPicker(), history) { DeviceMusic = AndroidDeviceMusic.Create(context) },
            nowPlaying,
            lyrics)
        {
            Outputs = new AndroidOutputSwitcher(context),
            BatterySaver = new AndroidBatterySaver(context),
            FrameRate = AndroidFrameRate.Instance,
            Theme = this,
            Logs = new AndroidLogExporter(context),
            Links = new AndroidUrlLauncher(context),
            // Account → Scan QR Code: Google's code scanner (no camera permission, see the class).
            QrScanner = new AndroidQrScanner(),
            // Playlists ⋯ › Import Playlist: .m3u / .m3u8 files from the system document picker.
            PlaylistFiles = new AndroidPlaylistFilePicker(context),
            VersionText = DescribeVersion(context),
            Account = account,
            // Plays sent from the phone itself to Last.fm / ListenBrainz (Settings → Scrobbling).
            // Without an account there is no computer to send plays through: the phone sends them.
            Scrobbler = new PhoneScrobbler(persistence, new HttpClient()) { HasComputerRoute = account != null },
            // The desktop's album descriptions: Last.fm, asked by artist and album name only
            // when an album page opens (Settings → About says so).
            AlbumDescriptions = LastFmAlbumDescriptionSource.Create(persistence.DataDirectory, new HttpClient()),
            // Home's From Your Last.fm and Discover on Last.fm (GitHub #177): the charts of the
            // account connected in Settings → Scrobbling, read by its name only, cached for 12 h.
            LastFmTaste = new LastFmTasteService(new HttpClient(), Path.Combine(persistence.DataDirectory, "cache", "lastfm_taste.json")),
            // The desktop's artist photos: Deezer, asked by artist name only when an artist page
            // opens or artist circles come into view (Settings → About says so).
            ArtistPhotos = DeezerArtistPhotoSource.Create(persistence.DataDirectory, new HttpClient(), library),
            // Artists' own logos on the artist page (MusicBrainz for the artist, then fanart.tv /
            // TheAudioDB), asked by name only when an artist page opens; only in a build that
            // carries a key (Noctis.Android.csproj), else the page shows the name.
            ArtistLogos = ArtistLogoKeys.FromAssembly(typeof(AndroidApp).Assembly) is { IsEmpty: false } logoKeys
                ? ArtistLogoSource.Create(persistence.DataDirectory, new HttpClient(), library, logoKeys)
                : null,
        };
        _shell = shell;
        // A fast word is one search, not one per letter (SearchPageViewModel.TypingDebounce).
        shell.Search.TypingDebounce = TimeSpan.FromMilliseconds(150);

        if (account != null)
        {
            // Desktop songs play from their download or the desktop's stream; the stream needs
            // the pinned certificate trusted and the device key sent, for as long as signed in.
            _player.ResolveRemote = account.ResolvePlaybackUri;
            // The songs that stream from the computer: when it does not answer, the player passes
            // over them to the downloaded ones instead of trying each in silence.
            nowPlaying.NeedsComputer = t => account.IsRemote(t) && !account.IsDownloaded(t);
            account.StateChanged += (_, _) => ApplyAccountState(account);
            ApplyAccountState(account);
        }

        // Notification / lock screen / Bluetooth / headset transport. The session player raises
        // these instead of seeking ExoPlayer's own item list, so every transport path runs
        // through PlaybackQueue; they arrive on a Java binder thread, hence the dispatcher hop.
        _player.SessionNextRequested += (_, _) => Dispatcher.UIThread.Post(() => shell.Player.NextCommand.Execute(null));
        _player.SessionPreviousRequested += (_, _) => Dispatcher.UIThread.Post(() => shell.Player.PreviousCommand.Execute(null));
        // Whether those buttons are drawn enabled. Read synchronously from the same Java
        // thread, so they stay plain field reads on the ViewModel (see NowPlayingViewModel.
        // HasNext/HasPrevious) — ExoPlayer's own item list is the wrong source because it
        // holds at most the current track plus one prepared successor.
        _player.HasNextInQueue = () => shell.Player.HasNext;
        _player.HasPreviousInQueue = () => shell.Player.HasPrevious;

        if (ApplicationLifetime is IActivityApplicationLifetime activity)
            activity.MainViewFactory = () =>
            {
                var view = new ShellView { DataContext = shell };
                // Scroll-freeze diagnostic, off unless `adb shell setprop debug.noctis.stalls 1`.
                if (ReadSystemProperty("debug.noctis.stalls") == "1")
                    view.AttachedToVisualTree += (_, _) =>
                    {
                        if (Avalonia.Controls.TopLevel.GetTopLevel(view) is { } top) FrameStallMonitor.Start(top);
                    };
                return view;
            };

        _ = StartAsync(persistence, library, history);

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The phone's link to the owner's Noctis desktop (Settings → Account). Null while the
    /// feature is parked (<see cref="PhoneAccountFeatures"/>) or if it cannot be built: Settings
    /// then shows no Account section, the sheet no download actions, and the player never streams.
    /// </summary>
    private static INoctisAccountService? CreateAccountService(Context context, ILibraryService library,
        IPersistenceService persistence, NoctisStateRecorder stateRecorder, Task libraryReady)
    {
        if (!PhoneAccountFeatures.Enabled) return null;
        // The service's TLS: its pin check wired into AndroidMessageHandler.
        NoctisHandlerFactory handlerFactory = AndroidNoctisHttp.CreateHandler;
        // Both directories under NoBackupFilesDir, so neither the device key nor the
        // downloads are ever backed up.
        try
        {
            var noBackup = context.NoBackupFilesDir!.AbsolutePath;
            return new NoctisAccountService(library, persistence, handlerFactory,
                accountDir: Path.Combine(noBackup, "account"),
                offlineDir: Path.Combine(noBackup, "offline"),
                deviceName: $"{Build.Manufacturer} {Build.Model}".Trim(),
                stateRecorder: stateRecorder)
            {
                LibraryReady = libraryReady,
                // Settings → Downloads → Wi-Fi only: a download on mobile data is refused.
                IsOnWifi = () => AndroidNetwork.IsUnmetered(context),
                // No ALAC decoder, neither the phone's nor the bundled FFmpeg (a build without
                // Libs/media3-decoder-ffmpeg): the desktop sends its ALAC songs as FLAC.
                PreferFlacForAlac = !Media3AudioPlayer.HasDecoder("audio/alac"),
                // "Send phone songs to computer" reads the phone's songs as playback does: the SAF
                // document through its file descriptor, streamed, never copied.
                LocalFiles = new SafTrackFileAccess(context),
            };
        }
        catch (Exception ex)
        {
            DebugLog.Write("Account", $"Account service unavailable: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// Signed in: ExoPlayer trusts the pinned desktop certificate and sends the device key with
    /// that desktop's stream requests (header only, see Media3AudioPlayer.SetStreamAuth). Signed out: both undone. At start and on every account change, which
    /// the service raises on any thread — hence the lock, so two changes cannot interleave.
    /// </summary>
    private void ApplyAccountState(INoctisAccountService account)
    {
        lock (_accountGate)
        {
            try
            {
                var linked = account.IsSignedIn ? account.Account : null;
                if (linked != null)
                {
                    AndroidStreamTrust.Install(linked.Fingerprint, new Uri(linked.ServerUrl).Host);
                    _player?.SetStreamAuth(linked.ServerUrl, linked.DeviceKey);
                }
                else
                {
                    _player?.SetStreamAuth(null, null);
                    AndroidStreamTrust.Clear();
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write("Account", $"Stream trust update failed: {ex.Message}");
            }
        }
    }

    /// <summary>An Android system property (getprop), empty when unset or unreadable.</summary>
    private static string ReadSystemProperty(string name)
    {
        try
        {
            using var cls = Java.Lang.Class.ForName("android.os.SystemProperties");
            using var get = cls.GetMethod("get", Java.Lang.Class.FromType(typeof(Java.Lang.String)));
            return get.Invoke(null, new Java.Lang.String(name))?.ToString() ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Last-chance logging. AsyncRelayCommand rethrows on the UI thread, so an exception that
    /// escapes any async command or event handler would otherwise end the process with nothing
    /// in `adb logcat -s Noctis`. A UI-thread exception is logged and marked handled: the app
    /// stays up in whatever state the failed action left, which beats vanishing mid-song.
    /// Exceptions on other threads cannot be survived; they are only logged on the way down.
    /// </summary>
    private static void HookUnhandledExceptions()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            DebugLog.Write("Crash", $"Unhandled UI-thread exception (kept running): {e.Exception}");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DebugLog.Write("Crash", $"Unhandled exception (terminating: {e.IsTerminating}): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            DebugLog.Write("Crash", $"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };
    }

    /// <summary>Settings → language/theme, then the library and the saved queue. The shell is
    /// already on screen; it fills in as these land.</summary>
    private async Task StartAsync(IPersistenceService persistence, ILibraryService library, IPlayHistoryService history)
    {
        try
        {
            var settings = await persistence.LoadSettingsAsync();
            // The shell is already on screen in the phone's language: a saved choice relabels it.
            var cultureBefore = Loc.Instance.Culture.Name;
            Loc.Instance.SetCulture(settings.Language);
            if (Loc.Instance.Culture.Name != cultureBefore) _shell!.Relabel();
            // Themes came in after the dark styles: the first start migrates Theme + Background once.
            if (await PhoneThemes.MigrateAsync(persistence, settings))
                DebugLog.Write("Theme", $"Migrated to the {settings.MobileThemeName} theme (Match Theme {settings.MobileAccentFollowsTheme})");
            ApplyTheme(settings.MobileAppearance, PhoneThemes.DarkThemeFor(settings), settings.AccentColorHex, settings.MobileAccentFollowsTheme);
            // Settings › Appearance › Smooth Motion: up to 120 Hz unless the user chose Standard.
            AndroidFrameRate.Instance.Request(settings.MobileSmoothMotion);
            // Settings › Background, before the first page paints rather than after the library loads.
            _shell!.Background.ApplySettings(settings);
            // The Library rows need only the settings: on screen now, not after the loads below.
            _shell!.Library.ApplySettings(settings);
            // No SetGapless(settings.GaplessPlaybackEnabled): the phone is always gapless
            // (NowPlayingViewModel turns it on), whatever a desktop-written settings.json says.
            await history.PreloadAsync();
            await library.LoadAsync();
            _libraryLoaded.TrySetResult();
            await _shell!.InitializeAsync();
            // Scan on Startup (desktop parity): new songs in the folders show up on their own.
            _ = _shell.Library.ScanAtStartupAsync();
        }
        catch (Exception ex)
        {
            // Failed before the library loaded: syncs fail rather than save a library.json without the local songs.
            if (_libraryLoaded.TrySetException(ex)) _ = _libraryLoaded.Task.Exception; // logged below, not "unobserved"
            DebugLog.Write("Startup", $"Android startup failed: {ex}");
        }
    }

    /// <summary>
    /// Activity paused: checkpoint the queue and position. Best-effort only — this is
    /// fire-and-forget and the process can be killed before the write lands. What actually
    /// bounds the loss is NowPlayingViewModel's five-second save cadence plus its save on
    /// pause; this call just shortens the window on the common home/screen-off path.
    /// </summary>
    public void OnBackgrounded()
    {
        if (_shell != null)
        {
            _shell.Background.Suspended = true;   // Slow Motion stands still
            _shell.IsSuspended = true;            // and so does Now Playing's record
        }
        var save = _shell?.SaveStateAsync();
        // Matches NowPlayingViewModel.SaveStateNow: a background save that throws would
        // otherwise be an unobserved exception nobody ever sees.
        _ = save?.ContinueWith(
            t => DebugLog.Write("Queue", $"Background save failed: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>MainActivity, on resume: Slow Motion drifts again, the record turns again.</summary>
    public void OnForegrounded()
    {
        if (_shell == null) return;
        _shell.Background.Suspended = false;
        _shell.IsSuspended = false;
        // Back from the system settings: the audio permission may have been allowed or taken away.
        _shell.Library.RecheckDeviceMusic();
    }

    /// <summary>Activity Back: give the shell first refusal so a full-screen overlay closes
    /// instead of the activity finishing. See <see cref="ShellViewModel.TryHandleSystemBack"/>.</summary>
    public bool TryHandleSystemBack() => _shell?.TryHandleSystemBack() ?? false;

    /// <summary>The system font size changed (MainActivity.OnConfigurationChanged).</summary>
    public void ApplyFontScale(float scale)
    {
        if (_shell != null) _shell.Lyrics.FontScale = scale;
    }

    /// <summary>A volume key or a resume (MainActivity): the media volume may have moved.</summary>
    public void OnVolumeKey() => _volume?.NotifyChanged();

    /// <summary>Settings (and startup, and a system dark-mode switch) re-theme through here.</summary>
    public void ApplyTheme(string appearance, string darkTheme, string accentHex) =>
        ApplyTheme(appearance, darkTheme, accentHex, _themeChoice.AccentFollowsTheme);

    /// <summary>A colour theme's own accent wins while <paramref name="accentFollowsTheme"/> (Match Theme).</summary>
    public void ApplyTheme(string appearance, string darkTheme, string accentHex, bool accentFollowsTheme)
    {
        _themeChoice = (appearance, darkTheme, accentHex, accentFollowsTheme);
        var system = SystemVariant();
        _appliedSystem = system;
        var resolved = MobileTheme.Resolve(appearance, darkTheme, system);
        var accent = PhoneThemes.EffectiveAccent(resolved, accentHex, accentFollowsTheme);
        DebugLog.Write("Theme", $"{appearance}/{darkTheme} on a {system} system -> {resolved}, accent {accent}");
        _theme.Apply(this, resolved, accent);
        // The theme rebuilt its palette: the background mesh goes back on top, its glass in the theme's colours.
        _shell?.Background.Reassert(PhoneThemes.DarkOrInk(darkTheme).Id);
        // Avalonia resets the status bar icons to the new theme; a page's own choice wins again.
        Dispatcher.UIThread.Post(ReassertStatusBarIcons, DispatcherPriority.Background);
    }

    /// <summary>The status bar icons a page asked for (<see cref="SetStatusBarIcons"/>); null = the theme's.</summary>
    private bool? _statusBarIconsDark;

    /// <summary>
    /// A page drawn under the status bar (the album cover) sets the icon colour from what is
    /// under it. Avalonia 12.1.3 has no public way to: IInsetsManager.SystemBarColor only colours
    /// the bars (and returns early when edge-to-edge), and AndroidInsetsManager.SystemBarTheme,
    /// which sets AppearanceLightStatusBars, is internal and driven by the app's theme variant
    /// (TopLevelImpl.SetFrameThemeVariant). So this sets the AndroidX flag itself; null puts
    /// back the theme's value (dark icons on Light), the same one Avalonia sets.
    /// </summary>
    public void SetStatusBarIcons(bool? dark)
    {
        _statusBarIconsDark = dark;
        ApplyStatusBarIcons();
    }

    /// <summary>MainActivity, on regaining focus: Avalonia re-applies its theme value whenever its
    /// view becomes visible again (AvaloniaView.OnVisibilityChanged, after the app comes back from
    /// the background), so a page's own choice is put back. Nothing to do without one.</summary>
    public void ReassertStatusBarIcons()
    {
        if (_statusBarIconsDark != null) ApplyStatusBarIcons();
    }

    private void ApplyStatusBarIcons()
    {
        try
        {
            if (MainActivity.Current?.Window is not { } window || window.DecorView is not { } decor) return;
            var dark = _statusBarIconsDark ?? ActualThemeVariant == ThemeVariant.Light;
            new WindowInsetsControllerCompat(window, decor).AppearanceLightStatusBars = dark;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Theme", $"Status bar icons failed: {ex.Message}");
        }
    }

    /// <summary>
    /// "System" appearance: Android's dark-mode switch arrives as a configuration change
    /// (UiMode is in MainActivity's ConfigurationChanges, so the activity is kept and this runs
    /// from its OnConfigurationChanged); re-theme when the device's night mode moved. Avalonia's
    /// ColorValuesChanged is not used: its CONFIGURATION_CHANGED receiver never fired on the
    /// API 35 emulator (09-23).
    /// </summary>
    public void OnConfigurationChanged()
    {
        if (SystemVariant() == _appliedSystem) return;   // rotation, font scale, our own night-mode echo
        ApplyTheme(_themeChoice.Appearance, _themeChoice.DarkTheme, _themeChoice.Accent, _themeChoice.AccentFollowsTheme);
    }

    /// <summary>
    /// The device's night mode, from the system resources. Not the activity's configuration
    /// (nor PlatformSettings, which reads a context): Avalonia's TopLevelImpl.SetFrameThemeVariant
    /// pushes the app's own theme into the activity with AppCompat SetLocalNightMode, so after
    /// picking Light on a dark phone the activity reports "not night" and "System" would stay Light.
    /// </summary>
    private static PlatformThemeVariant SystemVariant() =>
        AResources.System?.Configuration is { } config && (config.UiMode & AUiMode.NightMask) == AUiMode.NightNo
            ? PlatformThemeVariant.Light
            : PlatformThemeVariant.Dark;

    private static string DescribeVersion(Context context)
    {
        try
        {
            var info = context.PackageManager!.GetPackageInfo(context.PackageName!, (PackageInfoFlags)0)!;
            return $"Noctis {info.VersionName}";
        }
        catch (Exception ex)
        {
            DebugLog.Write("Android", $"Version lookup failed: {ex.Message}");
            return "Noctis";
        }
    }
}
