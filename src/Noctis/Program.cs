using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;
using System.Threading;
using Noctis.Helpers;
using Noctis.Services;
using Noctis.Services.AudioAnalysis;
using Noctis.Services.Loon;
using Noctis.ViewModels;

namespace Noctis;

/// <summary>
/// Application entry point. Configures dependency injection and launches the Avalonia app.
/// </summary>
internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Services.StartupTrace.Begin();
        // Before anything can spawn a host tool: children must not inherit the AppImage's libraries.
        Helpers.PlatformHelper.ScrubAppImageLoaderPathsForChildren();
        // The core's log header must name this app, not the core assembly.
        Services.DebugLog.DescribeBuild = Services.UpdateService.DescribeBuild;
        try
        {
            // Explicit STA setup required for Windows OLE drag-and-drop from external apps
            if (OperatingSystem.IsWindows())
            {
                Thread.CurrentThread.SetApartmentState(ApartmentState.Unknown);
                Thread.CurrentThread.SetApartmentState(ApartmentState.STA);
            }

            // A restart from Settings (Data Folder, GitHub #165) starts this process before the
            // old one has exited: wait for it, or the single-instance guard below would hand
            // this launch to the exiting window and quit.
            var waitForPid = WaitForPreviousInstance(args);

            // Audio files passed on the command line ("Open with Noctis" /
            // double-clicked track): forwarded to the running instance, or
            // played once this instance finishes starting.
            var filesToOpen = args
                .Where(a => !a.StartsWith('-') && a != waitForPid && File.Exists(a))
                .ToArray();

            // One instance per user: launching again (e.g. pinned taskbar icon while
            // the app sits in the tray) surfaces the running window instead of
            // starting a second player.
            var ownsInstance = SingleInstanceGuard.TryAcquire();
            if (!ownsInstance && SingleInstanceGuard.SignalFirstInstance(filesToOpen))
            {
                // This exit is silent by design, and from a launcher it looks like the app
                // opening and closing (GitHub #192). Say what happened for terminal runs.
                if (!OperatingSystem.IsWindows())
                    Console.Error.WriteLine(HandedToRunningInstanceText(AppPaths.HomeRoot));
                return;
            }

            // Where the data lives (GitHub #165), settled before anything reads the data root:
            // finish a data folder move the last session copied, and make sure a folder the
            // user moved the data to is really there. An unplugged drive must not turn into a
            // fresh, empty library. Before the fall-through below logs: that resolves the data root.
            var dataStart = DataLocation.PrepareAtStartup(AppPaths.HomeRoot, finishPendingMove: ownsInstance);
            if (dataStart.Status == DataLocation.StartupStatus.Unavailable)
            {
                if (!AskToStartWithDefaultDataFolder(dataStart.ConfiguredRoot))
                    return;
                DataLocation.SessionFallbackFrom = dataStart.ConfiguredRoot;
                AppPaths.OverrideDataRoot(AppPaths.HomeRoot);
            }

            if (!ownsInstance)
            {
                // Nobody answered the activation pipe, so there is no live instance to
                // surface — only something holding the instance guard. That is either a
                // hung Noctis or (Windows) an unrelated process that got there first: the
                // mutex name is a bare, guessable, un-ACL'd string, so any process in the
                // session can claim it and permanently stop Noctis from launching. Exiting
                // here meant the user double-clicked Noctis, waited two seconds, and
                // nothing happened at all — no window, no error, no tray flash. Launch anyway.
                LogCrash("SingleInstance",
                    new InvalidOperationException(
                        "Single-instance guard is held but the activation pipe did not " +
                        "answer — starting anyway."));
                SingleInstanceGuard.StartActivationListener();
            }

            // Settle whether the previous run died (preserving its log for
            // Settings → About) and start mirroring this session's log to disk
            // so a crash can't take it along. Deliberately AFTER the
            // single-instance guard: a duplicate launch exits above without ever
            // touching the live instance's journal — on Linux/macOS File.Move
            // ignores advisory locks, so an early Initialize in the duplicate
            // would steal the running session's log as a bogus crash file.
            // Everything logged before this point reaches the file anyway: the
            // journal replays the in-memory ring when it attaches.
            Services.CrashJournal.Initialize(AppPaths.DataRoot);

            App.PendingOpenFiles = filesToOpen;

            // Configure DI container
            var services = new ServiceCollection();
            ConfigureServices(services);
            var provider = services.BuildServiceProvider();
            Services.StartupTrace.Mark("di-container-built");

            // Make services available to the Avalonia App
            App.Services = provider;

            // A finished data folder move set the old copy aside; delete it off the UI path.
            if (ownsInstance)
            {
                _ = Task.Run(() =>
                {
                    try { DataLocation.CleanupOldData(AppPaths.HomeRoot, AppPaths.DataRoot); }
                    catch (Exception ex) { Services.DebugLog.Write("DataLocation", $"old data cleanup: {ex.Message}"); }
                });
            }

            // Auto-update: a verified release downloaded in an earlier session installs now, before the
            // audio engine or any window exists, so it never interrupts playback. Only the instance that
            // owns the guard may do it, and not when files were passed (the installer's relaunch drops them).
            if (ownsInstance && provider.GetRequiredService<UpdateService>().TryInstallPendingUpdateAtLaunch(filesToOpen.Length > 0))
            {
                provider.Dispose();
                Services.CrashJournal.MarkCleanShutdown();
                return;
            }

            // Warm the LibVLC-backed audio player while Avalonia initializes.
            // Its constructor (native libvlc load + plugin scan + audio device
            // warm-up) is the heaviest single service; resolved lazily it runs
            // synchronously on the UI thread inside the MainWindowViewModel
            // resolve and delays first paint. DI serializes singleton creation,
            // so the UI-thread resolve either finds it ready or waits exactly as
            // it does today — the instance is never built twice.
            // Headphone surround is a LibVLC instance option, and the output device
            // pins both the LibVLC instance arg and every sink the player opens, so
            // both have to be known before the player exists — ahead of the settings
            // load.
            VlcAudioPlayer.HeadphoneSurroundAtLaunch = PersistenceService.ReadBoolSettingAtLaunch(
                AppPaths.DataRoot, nameof(Noctis.Models.AppSettings.HeadphoneSurround));
            VlcAudioPlayer.OutputDeviceAtLaunch = PersistenceService.ReadStringSettingAtLaunch(
                AppPaths.DataRoot, nameof(Noctis.Models.AppSettings.AudioOutputDeviceId));
            VlcAudioPlayer.DecoderFallbackListPathAtLaunch = Path.Combine(AppPaths.DataRoot, "mp3_decoder_fallback.txt");
            // The interface size (GitHub #145) before the main window is built, so it opens at
            // its size instead of jumping there when the settings load lands.
            Noctis.Helpers.UiScale.SetFactor(Noctis.Models.AppSettings.SnapUiScale(
                PersistenceService.ReadIntSettingAtLaunch(AppPaths.DataRoot,
                    nameof(Noctis.Models.AppSettings.UiScalePercent), 100)) / 100.0);
            _ = Task.Run(() =>
            {
                try { provider.GetRequiredService<IAudioPlayer>(); }
                catch { /* a broken libvlc install surfaces the same error on the UI-thread resolve */ }
                Services.StartupTrace.Mark("libvlc-warm-done");
            });

            // Mark login-launched runs (the autostart entry passes "--startup", plus
            // "--minimized" when the user wants it to start hidden in the tray) so the
            // main window can start minimized instead of popping up on boot.
            App.LaunchedAtStartup = Array.IndexOf(args, "--startup") >= 0;
            App.StartMinimizedAtLogin = App.LaunchedAtStartup && Array.IndexOf(args, "--minimized") >= 0;

            // Log unhandled exceptions to a crash file for post-mortem debugging
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                    LogCrash("AppDomain.UnhandledException", ex, fatal: args.IsTerminating);
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                LogCrash("TaskScheduler.UnobservedTaskException", args.Exception);
                args.SetObserved(); // prevent process termination
            };

            // Launch the Avalonia application
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

            // Cleanup
            provider.Dispose();

            // The lifetime exited under its own power (window close, tray quit,
            // OS session end after the shutdown save) — the only clean-exit path
            // in the app, so the journal must not survive into the next launch.
            Services.CrashJournal.MarkCleanShutdown();
        }
        catch (Exception ex)
        {
            // Always log so we can post-mortem regardless of platform
            LogCrash("Program.Main", ex, fatal: true);

            // On Windows, surface a native message box (libvlc DLLs missing etc.).
            // On macOS/Linux, the crash log + stderr is the post-mortem path.
            if (OperatingSystem.IsWindows())
            {
                MessageBox(IntPtr.Zero,
                    $"Noctis failed to start:\n\n{ex.Message}",
                    "Noctis — Startup Error", 0x10 /* MB_ICONERROR */);
            }
            else
            {
                Console.Error.WriteLine($"Noctis failed to start: {ex}");

                // A Finder/Dock/login launch has no terminal, so stderr alone was a
                // bounce and a silent exit — and the next launch fails the same way,
                // so the crash-journal banner never gets a chance to show. Put the
                // message (libvlc install guidance etc.) up in a native dialog too.
                ShowStartupErrorAlert(ex.Message, Path.Combine(AppPaths.DataRoot, "crash.log"));
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>What a launch that handed itself to the running instance prints. Internal for tests.</summary>
    internal static string HandedToRunningInstanceText(string homeRoot) =>
        "Noctis is already running for this data folder (" + homeRoot + "), so this launch asked it to show " +
        "its window and quit. If no window appears, that copy is stuck: end it (e.g. `pkill -x Noctis`) and start Noctis again.";

    /// <summary>The argument a Settings restart passes: the exiting process to wait for.</summary>
    internal const string WaitForPidArgument = "--wait-for-pid";

    /// <summary>Waits (up to 30 s) for the process named by <see cref="WaitForPidArgument"/> to
    /// exit. Returns the pid argument so it is not mistaken for a file to open.</summary>
    private static string? WaitForPreviousInstance(string[] args)
    {
        var i = Array.IndexOf(args, WaitForPidArgument);
        if (i < 0 || i + 1 >= args.Length) return null;
        if (int.TryParse(args[i + 1], out var pid) && pid != Environment.ProcessId)
        {
            try
            {
                using var previous = System.Diagnostics.Process.GetProcessById(pid);
                previous.WaitForExit(30_000);
            }
            catch { /* already gone */ }
        }
        return args[i + 1];
    }

    /// <summary>
    /// The data folder the user chose can't be reached (unplugged USB drive, offline share).
    /// Asks whether to run this session on the default folder; false = quit. The chosen folder
    /// stays the setting, so the next launch with the drive connected uses it again.
    /// </summary>
    private static bool AskToStartWithDefaultDataFolder(string configuredRoot)
    {
        var text = DataFolderUnavailableText(configuredRoot);
        if (OperatingSystem.IsWindows())
        {
            const uint MB_YESNO = 0x04, MB_ICONWARNING = 0x30;
            const int IDYES = 6;
            return MessageBox(IntPtr.Zero, text, "Noctis — Data folder not found", MB_YESNO | MB_ICONWARNING) == IDYES;
        }

        Console.Error.WriteLine(text);
        foreach (var (fileName, args) in DataFolderQuestionCommands(text, OperatingSystem.IsMacOS()))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(fileName) { UseShellExecute = false };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) continue;
                p.WaitForExit();
                // Every tool here exits 0 for "use the default folder" and non-zero for quit or
                // a closed dialog; one that can't open a window exits at once, non-zero: quit.
                return p.ExitCode == 0;
            }
            catch { /* not installed — try the next tool */ }
        }
        // No way to ask: quit rather than start on another folder unasked.
        return false;
    }

    internal static string DataFolderUnavailableText(string configuredRoot) =>
        "Noctis keeps its data in:\n" + configuredRoot + "\n\n" +
        "That folder can't be reached right now. If it is on a USB or network drive, connect it and start Noctis again.\n\n" +
        "Start with the default data folder for now? Your data in that folder stays as it is, " +
        "and Noctis looks for it again at the next start. (To go back to the default folder for good, " +
        "use Settings › Advanced › Data Folder › Use Default.)";

    /// <summary>The yes/no dialog tools for <see cref="AskToStartWithDefaultDataFolder"/> on
    /// macOS/Linux, tried in order. Exit code 0 = start with the default folder. Internal for tests.</summary>
    internal static (string FileName, string[] Args)[] DataFolderQuestionCommands(string text, bool macOS)
    {
        if (macOS)
        {
            return new[]
            {
                ("/usr/bin/osascript", new[]
                {
                    "-e", "on run argv",
                    "-e", "display dialog (item 1 of argv) with title \"Noctis\" buttons {\"Quit\", \"Use Default Folder\"} default button 2 cancel button 1 with icon caution",
                    "-e", "end run",
                    text,
                }),
            };
        }

        // zenity parses --text as Pango markup (see StartupErrorAlertCommands).
        var markup = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        return new[]
        {
            ("zenity", new[] { "--question", "--title=Noctis — Data folder not found", "--ok-label=Use Default Folder", "--cancel-label=Quit", "--text=" + markup }),
            ("kdialog", new[] { "--title", "Noctis — Data folder not found", "--yes-label", "Use Default Folder", "--no-label", "Quit", "--yesno", text }),
        };
    }

    /// <summary>
    /// macOS/Linux counterpart of the Windows startup MessageBox: the desktop's own
    /// dialog tools, tried in order until one launches. The message travels as an
    /// argument, never spliced into script source. Internal for tests
    /// (InternalsVisibleTo Noctis.Tests).
    /// </summary>
    internal static (string FileName, string[] Args)[] StartupErrorAlertCommands(
        string error, string crashLogPath, bool macOS)
    {
        var body = $"{error}\n\nDetails: {crashLogPath}";
        if (macOS)
        {
            return new[]
            {
                ("/usr/bin/osascript", new[]
                {
                    "-e", "on run argv",
                    "-e", "display alert \"Noctis failed to start\" message (item 1 of argv) as critical",
                    "-e", "end run",
                    body,
                }),
            };
        }

        // zenity parses --text as Pango markup: an unescaped "<" or "&" in the
        // exception text would fail the parse and show an empty dialog.
        var text = $"Noctis failed to start:\n\n{body}";
        var markup = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        return new[]
        {
            ("zenity", new[] { "--error", "--title=Noctis — Startup Error", "--text=" + markup }),
            ("kdialog", new[] { "--title", "Noctis — Startup Error", "--error", text }),
        };
    }

    private static void ShowStartupErrorAlert(string error, string crashLogPath)
    {
        foreach (var (fileName, args) in StartupErrorAlertCommands(error, crashLogPath, OperatingSystem.IsMacOS()))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(fileName) { UseShellExecute = false };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) continue;
                // Blocks like the Windows MessageBox. A tool that launched but has no
                // display to open exits at once; the next one would fail the same way.
                p.WaitForExit();
                return;
            }
            catch { /* not installed — try the next tool */ }
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // Skia keeps decoded bitmaps as GPU textures in a bounded cache.
            // The default (~64 MB) is small for an image-heavy music library —
            // when album-art textures exceed it during scroll, the GPU evicts
            // older textures and we re-upload them on the next frame, which is
            // what causes scroll stutter on the album grid. Covers are now decoded
            // at the size they draw at (a 2x album tile is ~0.6 MB, not 2.4 MB), so
            // 128 MB holds the visible+nearby textures for a 10K-track library with
            // room to spare; on integrated GPUs and the software path this cache is
            // host RAM, which is why it is not larger.
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 128L * 1024 * 1024 })
            .LogToTrace();

        // LogToTrace installed its sink; wrap it so Avalonia's own warnings and
        // errors (binding failures, layout complaints — the usual trail behind
        // "weird UI renders") also land in the session log, deduplicated and
        // capped so recycled-container binding noise can't flood the ring.
        Avalonia.Logging.Logger.Sink = new Services.AvaloniaLogBridge(Avalonia.Logging.Logger.Sink);

        // The app's default font is the embedded Inter, which carries no
        // CJK/Hangul glyphs. Windows resolves missing glyphs through the system
        // font manager automatically, but on macOS/Linux that lookup doesn't
        // reliably engage for embedded fonts, so Korean/Japanese/Chinese lyrics
        // rendered as "?" boxes. Provide an explicit fallback chain of each
        // platform's stock CJK-capable fonts.
        if (OperatingSystem.IsMacOS())
        {
            builder = builder.With(new Avalonia.Media.FontManagerOptions
            {
                FontFallbacks = new[]
                {
                    new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("PingFang SC") },
                    new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Hiragino Sans") },
                    new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Apple SD Gothic Neo") },
                    new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Apple Color Emoji") },
                }
            });
        }
        else if (OperatingSystem.IsLinux())
        {
            builder = builder.With(new Avalonia.Media.FontManagerOptions
            {
                FontFallbacks = new[]
                {
                    new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Noto Sans CJK SC") },
                    new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Noto Sans CJK KR") },
                    new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Noto Sans CJK JP") },
                    new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Noto Color Emoji") },
                }
            });

            // Escape hatch for GPU/driver rendering trouble on Linux (issue #26:
            // heavy stutter + windows flashing transparent on Arch/X11).
            // NOCTIS_SOFTWARE_RENDER=1 forces Avalonia's X11 software renderer —
            // on a number of Linux setups the GL paths render far slower than
            // software (AvaloniaUI/Avalonia discussion #18807), and a stalled GPU
            // swapchain presents as see-through window content. Opt-in only; the
            // default renderer selection is unchanged.
            if (Environment.GetEnvironmentVariable("NOCTIS_SOFTWARE_RENDER") == "1")
            {
                builder = builder.With(new X11PlatformOptions
                {
                    RenderingMode = new[] { X11RenderingMode.Software }
                });
            }
        }

        return builder;
    }

    /// <summary>
    /// Registers all services and ViewModels in the DI container.
    /// </summary>
    private static void ConfigureServices(IServiceCollection services)
    {
        // Services — registered as singletons (one instance for the app lifetime)
        services.AddSingleton<IPersistenceService, PersistenceService>();
        services.AddSingleton<IMetadataService, MetadataService>();
        services.AddSingleton<ISqliteLibraryIndexService, SqliteLibraryIndexService>();
        services.AddSingleton<IAuditTrailService, AuditTrailService>();
        services.AddSingleton<IPlaylistInteropService, PlaylistInteropService>();
        services.AddSingleton<ILibraryService, LibraryService>();
        // Continuous folder watching. Reads MusicFolders/WatchFoldersEnabled lazily
        // through the canonical SettingsViewModel so toggling in Settings takes effect
        // without a restart (same accessor pattern as AudioConverter below).
        services.AddSingleton<ILibraryWatcherService>(sp =>
            new LibraryWatcherService(
                sp.GetRequiredService<ILibraryService>(),
                () => App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings()
                      ?? new Noctis.Models.AppSettings()));
        services.AddSingleton<ISyncService, NavidromeSyncService>();
        services.AddSingleton<IAudioPlayer, VlcAudioPlayer>();

        // External integrations
        services.AddSingleton<HttpClient>(_ =>
        {
            var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Noctis/1.0");
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return http;
        });
        services.AddSingleton<IMediaSourceConnector, NavidromeMediaSourceConnector>();
        // On-demand media-server browsing/streaming (the "Server" section).
        services.AddSingleton<Services.MediaServer.IMediaServerService, Services.MediaServer.MediaServerService>();
        services.AddSingleton<Services.AudioCd.IAudioCdService>(_ =>
            new Services.AudioCd.AudioCdService(new Services.AudioCd.SystemDriveProbe(), new Services.AudioCd.LibVlcAudioCdReader(),
                probeOnConstruct: false));
        services.AddSingleton<LoonClient>(sp =>
        {
            var persistence = sp.GetRequiredService<IPersistenceService>();
            var artworkDir = Path.Combine(persistence.DataDirectory, "artwork");
            return new LoonClient(artworkDir, sp.GetRequiredService<HttpClient>());
        });
        services.AddSingleton<IDiscordPresenceService, DiscordPresenceService>();
        services.AddSingleton<ILastFmService, LastFmService>();
        // GitHub #177: Home's Last.fm mixes and Discover (read-only account charts, cached 12 h).
        services.AddSingleton(sp => new LastFmTasteService(sp.GetRequiredService<HttpClient>(),
            Path.Combine(AppPaths.DataRoot, "cache", "lastfm_taste.json")));
        services.AddSingleton<IListenBrainzService, ListenBrainzService>();
        services.AddSingleton<ArtistImageService>();
        services.AddSingleton<ArtistInfoService>();
        services.AddSingleton<SimilarArtistsService>();
        services.AddSingleton<ITunesArtworkService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<ShortcutService>();
        services.AddSingleton<ILrcLibService, LrcLibService>();
        services.AddSingleton<INetEaseService, NetEaseService>();
        services.AddSingleton<IMusixmatchService, MusixmatchService>();
        services.AddSingleton<IKugouLyricsService, KugouLyricsService>();
        // The model sync gets a client of its own: the shared one gives up after 15 s.
        services.AddSingleton<ILrcRedLyricsService>(sp =>
            new LrcRedLyricsService(sp.GetRequiredService<HttpClient>(), LrcRedLyricsService.CreateModelHttpClient()));
        services.AddSingleton<IUnisonLyricsService, UnisonLyricsService>();
        services.AddSingleton<IYouTubeMusicLyricsService, YouTubeMusicLyricsService>();
        services.AddSingleton<IPlayHistoryService, PlayHistoryService>();
        services.AddSingleton<PhoneHandoffInbox>();
        // Singleton so the startup archive pass and the Wrap dialog share one instance
        // (and one lock over wrap_archive.json).
        services.AddSingleton<IWrapArchiveService, WrapArchiveService>();
        services.AddSingleton<DeezerMetadataService>();
        services.AddSingleton<AutoMatchCoordinator>(sp =>
            new AutoMatchCoordinator(
                sp.GetRequiredService<IMetadataFinderService>(),
                sp.GetRequiredService<DeezerMetadataService>(),
                () => App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings()
                      ?? new Noctis.Models.AppSettings()));
        // The metadata editor's Search Metadata engine (Deezer + Apple Music + MusicBrainz/CAA,
        // owner 10-08 revamp). Singleton: its session caches and the shared request pacers
        // must outlive any one editor window. Provider toggles are read per search.
        services.AddSingleton<Services.MetadataSearch.IMetadataSearchService>(sp =>
            new Services.MetadataSearch.MetadataSearchService(
                sp.GetRequiredService<HttpClient>(),
                () => App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings()
                      ?? new Noctis.Models.AppSettings()));
        // AudioConverter resolves the ffmpeg path lazily, so the user can change
        // it in Settings without restarting. Read through MainWindowViewModel —
        // it's the canonical owner of the SettingsViewModel instance.
        services.AddSingleton<IAudioConverterService>(sp =>
            new AudioConverterService(
                () => App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings().FfmpegPath ?? string.Empty,
                sp.GetRequiredService<IMetadataService>()));
        services.AddSingleton<IReplayGainScannerService, ReplayGainScannerService>();

        // Library tools
        services.AddSingleton<IFileOrganizerService, FileOrganizerService>();
        services.AddSingleton<IDuplicateFinderService, DuplicateFinderService>();
        services.AddSingleton<IMetadataFinderService>(sp =>
            new MetadataFinderService(
                sp.GetRequiredService<HttpClient>(),
                () => App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings()
                      ?? new Noctis.Models.AppSettings(),
                sp.GetRequiredService<DeezerMetadataService>()));
        services.AddSingleton<ITidalAuthService>(sp => new TidalAuthService(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<IPersistenceService>().DataDirectory));
        services.AddSingleton<IPlaylistImportService, PlaylistImportService>();

        // Deferred file-tag writes: star clicks and bulk lyrics saves touch each file once,
        // after the user goes quiet and never while that file is playing. Flushed at shutdown.
        services.AddSingleton<IDeferredTagWriter>(_ => new DeferredTagWriter());

        // Account & Sync ledger (state-based, last-writer-wins) served by the Noctis server.
        // Not wired while the account features are parked: no ledger writes, and the server's
        // sync endpoints answer "turned off" whatever SyncEnabled the settings file carries.
        if (AccountFeatures.Enabled)
        {
            services.AddSingleton<Services.Sync.ILibrarySyncService>(sp =>
                new Services.Sync.LibrarySyncService(
                    () => App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings()
                          ?? new Noctis.Models.AppSettings(),
                    sp.GetRequiredService<IPersistenceService>()));
            services.AddSingleton<Services.Sync.ITrackStateRecorder>(sp => sp.GetRequiredService<Services.Sync.ILibrarySyncService>());
        }

        // Bulk lyrics (fetch / remove) and Lyrics Studio share one writer so every path
        // follows the lyrics page's sidecar + registry rules.
        services.AddSingleton<Services.Lyrics.LyricsWriter>(sp =>
            new Services.Lyrics.LyricsWriter(sp.GetRequiredService<IMetadataService>(), sp.GetRequiredService<IDeferredTagWriter>()));
        services.AddSingleton<Services.Lyrics.ILyricsBulkService>(sp =>
            new Services.Lyrics.LyricsBulkService(
                sp.GetRequiredService<ILrcLibService>(),
                sp.GetRequiredService<Services.Lyrics.LyricsWriter>(),
                sp.GetRequiredService<ILibraryService>(),
                () => App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings()
                      ?? new Noctis.Models.AppSettings(),
                // The lyrics page's Auto sources, in its order; each asked only while on in Settings.
                () =>
                {
                    var lrcLib = sp.GetRequiredService<ILrcLibService>();
                    var netEase = sp.GetRequiredService<INetEaseService>();
                    var list = new List<Services.Lyrics.LyricsBulkSource>
                    {
                        new("LRCLIB", s => s.LrcLibEnabled, (a, t, d, c) => LrcLibLookup.FetchAsync(lrcLib, a, t, d, c)),
                        new("NetEase", s => s.NetEaseEnabled, netEase.SearchLyricsAsync),
                    };
                    list.AddRange(Services.Lyrics.OnlineLyricsSource.BuiltIns(
                            sp.GetRequiredService<IMusixmatchService>(), sp.GetRequiredService<IKugouLyricsService>(),
                            sp.GetRequiredService<IYouTubeMusicLyricsService>(), sp.GetRequiredService<ILrcRedLyricsService>(),
                            sp.GetRequiredService<IUnisonLyricsService>())
                        .Select(x => new Services.Lyrics.LyricsBulkSource(x.Name, x.IsEnabled, x.SearchAsync)));
                    return list;
                },
                Path.Combine(sp.GetRequiredService<IPersistenceService>().DataDirectory, "lyrics_bulk_ledger.json")));
        services.AddSingleton<Services.LyricsStudio.ILyricsStudioEngine>(sp =>
            new Services.LyricsStudio.LyricsStudioEngine(
                sp.GetRequiredService<IAudioConverterService>(),
                sp.GetRequiredService<ILrcLibService>(),
                sp.GetRequiredService<IPersistenceService>()));

        // Send to Folder (MusicBee-style copy to a drive/folder). With the library and the
        // watcher, a Move relocates the tracks like Organize Files (GitHub #121).
        services.AddSingleton<ISendToFolderService>(sp => new SendToFolderService(
            sp.GetRequiredService<ILibraryService>(), sp.GetService<ILibraryWatcherService>()));

        // yt-dlp as an external tool, for lyrics background videos from YouTube. (Adding songs from
        // YouTube to the library is the official "Add from YouTube" plugin since 10-29.)
        services.AddSingleton<Services.YouTube.YtDlpTool>(sp =>
            new Services.YouTube.YtDlpTool(
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<IPersistenceService>().DataDirectory,
                () => App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings().YtDlpPath ?? string.Empty));

        // Background BPM/key analysis pipeline. Decodes via ffmpeg out-of-process
        // (reusing AudioConverterService for ffmpeg discovery) and runs managed DSP;
        // results cache in library.db and fill Track.Bpm/MusicalKey when missing.
        services.AddSingleton<IAudioAnalysisService>(sp =>
            new AudioAnalysisService(sp.GetRequiredService<IAudioConverterService>()));
        services.AddSingleton<IAudioAnalysisStore>(sp =>
            new AudioAnalysisStore(sp.GetRequiredService<IPersistenceService>()));
        // Off-Windows visualizer feed: LibVLC owns the output there (no render-chain
        // tap), so the spectrum/beat meters are fed from an ffmpeg side decode of the
        // playing track instead. Started from App once the player exists.
        services.AddSingleton<SideDecodeMeterFeed>(sp =>
            new SideDecodeMeterFeed(sp.GetRequiredService<IAudioPlayer>(), sp.GetRequiredService<IAudioConverterService>()));
        // Waveform seek bar (GitHub #93): ffmpeg decode on one low-priority worker, cached
        // under cache/waveforms. Inert until Settings → Player → Waveform seek bar is on.
        services.AddSingleton<Services.Waveform.WaveformService>(sp =>
            new Services.Waveform.WaveformService(
                new Services.Waveform.FfmpegWaveformDecoder(sp.GetRequiredService<IAudioConverterService>()),
                new Services.Waveform.WaveformCache(Services.Waveform.WaveformCache.DefaultDirectory)));
        services.AddSingleton<AudioAnalysisCoordinator>(sp =>
            new AudioAnalysisCoordinator(
                sp.GetRequiredService<IAudioAnalysisService>(),
                sp.GetRequiredService<IAudioAnalysisStore>(),
                sp.GetRequiredService<ILibraryService>(),
                () => App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings()
                      ?? new Noctis.Models.AppSettings()));

        // GitHub #148: measures and tags new files once their album has finished copying.
        // Inert until Settings → Audio → Output → ReplayGain → "Scan new files automatically" is on.
        services.AddSingleton<ReplayGainAutoScanService>(sp =>
        {
            var player = sp.GetRequiredService<IAudioPlayer>();
            return new ReplayGainAutoScanService(
                sp.GetRequiredService<IReplayGainScannerService>(),
                sp.GetRequiredService<ILibraryService>(),
                () => App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings()
                      ?? new Noctis.Models.AppSettings(),
                playingPath: () => player.CurrentMediaPath,
                // The player caches the playing file's tags (see OpenReplayGainScannerDialog).
                onTagsWritten: () => Avalonia.Threading.Dispatcher.UIThread.Post(player.ReloadReplayGainTags));
        });

        // ViewModels — MainWindowViewModel is the root, created once
        services.AddSingleton<MainWindowViewModel>();
    }

    private static void LogCrash(string source, Exception ex, bool fatal = false)
    {
        // Fatal faults stamp the session journal first, so the preserved file
        // reads as a crash (marker, then the stack via the DebugLog write below)
        // instead of an anonymous kill. Non-fatal callers (unobserved tasks, the
        // single-instance fallback) must NOT stamp — the app carries on, and a
        // false stamp would put a scary "crashed" banner on the next launch.
        if (fatal)
            Services.CrashJournal.MarkFatal(source);

        try
        {
            var crashDir = AppPaths.DataRoot;
            Directory.CreateDirectory(crashDir);
            var crashPath = Path.Combine(crashDir, "crash.log");

            // Roll at ~1 MB. Every crash appends a full stack trace, and a crash loop
            // (e.g. the libvlc-missing case, which fires on every launch) grew this file
            // without bound.
            const long MaxCrashLogBytes = 1024 * 1024;
            try
            {
                var info = new FileInfo(crashPath);
                if (info.Exists && info.Length > MaxCrashLogBytes)
                    File.Move(crashPath, crashPath + ".1", overwrite: true);
            }
            catch { /* rotation is best effort */ }

            var entry = $"[{DateTime.UtcNow:O}] {source}: {ex}\n---\n";
            // Scrub before the sink — crash.log is shared in bug reports like every
            // other log, and exception text can echo auth-bearing URLs. A scrubber
            // failure must not lose the crash report itself.
            try { entry = Services.LogRedaction.Scrub(entry); } catch { }
            File.AppendAllText(crashPath, entry);
        }
        catch
        {
            // Last-resort: don't let crash logging itself crash
        }

        // Also surface it in the in-app session log (Settings → About → Developer Mode).
        try { Services.DebugLog.Write(source, ex); } catch { }
    }
}
