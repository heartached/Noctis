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
        try
        {
            // Explicit STA setup required for Windows OLE drag-and-drop from external apps
            if (OperatingSystem.IsWindows())
            {
                Thread.CurrentThread.SetApartmentState(ApartmentState.Unknown);
                Thread.CurrentThread.SetApartmentState(ApartmentState.STA);
            }

            // Audio files passed on the command line ("Open with Noctis" /
            // double-clicked track): forwarded to the running instance, or
            // played once this instance finishes starting.
            var filesToOpen = args
                .Where(a => !a.StartsWith('-') && File.Exists(a))
                .ToArray();

            // One instance per user: launching again (e.g. pinned taskbar icon while
            // the app sits in the tray) surfaces the running window instead of
            // starting a second player.
            if (!SingleInstanceGuard.TryAcquire())
            {
                if (SingleInstanceGuard.SignalFirstInstance(filesToOpen))
                    return;

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

            // Warm the LibVLC-backed audio player while Avalonia initializes.
            // Its constructor (native libvlc load + plugin scan + audio device
            // warm-up) is the heaviest single service; resolved lazily it runs
            // synchronously on the UI thread inside the MainWindowViewModel
            // resolve and delays first paint. DI serializes singleton creation,
            // so the UI-thread resolve either finds it ready or waits exactly as
            // it does today — the instance is never built twice.
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
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // Skia keeps decoded bitmaps as GPU textures in a bounded cache.
            // The default (~64 MB) is small for an image-heavy music library —
            // when album-art textures exceed it during scroll, the GPU evicts
            // older textures and we re-upload them on the next frame, which is
            // what causes scroll stutter on the album grid. 256 MB comfortably
            // holds the visible+nearby cover textures for a 10K-track library.
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 256L * 1024 * 1024 })
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
            new Services.AudioCd.AudioCdService(new Services.AudioCd.SystemDriveProbe(), new Services.AudioCd.LibVlcAudioCdReader()));
        services.AddSingleton<LoonClient>(sp =>
        {
            var persistence = sp.GetRequiredService<IPersistenceService>();
            var artworkDir = Path.Combine(persistence.DataDirectory, "artwork");
            return new LoonClient(artworkDir, sp.GetRequiredService<HttpClient>());
        });
        services.AddSingleton<IDiscordPresenceService, DiscordPresenceService>();
        services.AddSingleton<ILastFmService, LastFmService>();
        services.AddSingleton<IListenBrainzService, ListenBrainzService>();
        services.AddSingleton<ArtistImageService>();
        services.AddSingleton<ArtistInfoService>();
        services.AddSingleton<ITunesArtworkService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<ShortcutService>();
        services.AddSingleton<ILrcLibService, LrcLibService>();
        services.AddSingleton<INetEaseService, NetEaseService>();
        services.AddSingleton<IPlayHistoryService, PlayHistoryService>();
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
        services.AddSingleton<IPlaylistImportService, PlaylistImportService>();

        // Background BPM/key analysis pipeline. Decodes via ffmpeg out-of-process
        // (reusing AudioConverterService for ffmpeg discovery) and runs managed DSP;
        // results cache in library.db and fill Track.Bpm/MusicalKey when missing.
        services.AddSingleton<IAudioAnalysisService>(sp =>
            new AudioAnalysisService(sp.GetRequiredService<IAudioConverterService>()));
        services.AddSingleton<IAudioAnalysisStore>(sp =>
            new AudioAnalysisStore(sp.GetRequiredService<IPersistenceService>()));
        services.AddSingleton<AudioAnalysisCoordinator>(sp =>
            new AudioAnalysisCoordinator(
                sp.GetRequiredService<IAudioAnalysisService>(),
                sp.GetRequiredService<IAudioAnalysisStore>(),
                sp.GetRequiredService<ILibraryService>(),
                () => App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings()
                      ?? new Noctis.Models.AppSettings()));

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
