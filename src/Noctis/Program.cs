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
        try
        {
            // Explicit STA setup required for Windows OLE drag-and-drop from external apps
            if (OperatingSystem.IsWindows())
            {
                Thread.CurrentThread.SetApartmentState(ApartmentState.Unknown);
                Thread.CurrentThread.SetApartmentState(ApartmentState.STA);
            }

            // One instance per user: launching again (e.g. pinned taskbar icon while
            // the app sits in the tray) surfaces the running window instead of
            // starting a second player.
            if (!SingleInstanceGuard.TryAcquire())
            {
                SingleInstanceGuard.SignalFirstInstance();
                return;
            }

            // Configure DI container
            var services = new ServiceCollection();
            ConfigureServices(services);
            var provider = services.BuildServiceProvider();

            // Make services available to the Avalonia App
            App.Services = provider;

            // Mark login-launched runs (the autostart entry passes "--startup", plus
            // "--minimized" when the user wants it to start hidden in the tray) so the
            // main window can start minimized instead of popping up on boot.
            App.LaunchedAtStartup = Array.IndexOf(args, "--startup") >= 0;
            App.StartMinimizedAtLogin = App.LaunchedAtStartup && Array.IndexOf(args, "--minimized") >= 0;

            // Log unhandled exceptions to a crash file for post-mortem debugging
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                    LogCrash("AppDomain.UnhandledException", ex);
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
        }
        catch (Exception ex)
        {
            // Always log so we can post-mortem regardless of platform
            LogCrash("Program.Main", ex);

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
        services.AddSingleton<IOfflineCacheService, OfflineCacheService>();
        services.AddSingleton<ILibraryService, LibraryService>();
        // Continuous folder watching. Reads MusicFolders/WatchFoldersEnabled lazily
        // through the canonical SettingsViewModel so toggling in Settings takes effect
        // without a restart (same accessor pattern as AudioConverter below).
        services.AddSingleton<ILibraryWatcherService>(sp =>
            new LibraryWatcherService(
                sp.GetRequiredService<ILibraryService>(),
                () => App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings()
                      ?? new Noctis.Models.AppSettings()));
        services.AddSingleton<IUnifiedLibraryService, UnifiedLibraryService>();
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
        services.AddSingleton<IMediaSourceConnector, LocalMediaSourceConnector>();
        services.AddSingleton<IMediaSourceConnector, SmbMediaSourceConnector>();
        services.AddSingleton<IMediaSourceConnector, WebDavMediaSourceConnector>();
        services.AddSingleton<IMediaSourceConnector, NavidromeMediaSourceConnector>();
        services.AddSingleton<LoonClient>(sp =>
        {
            var persistence = sp.GetRequiredService<IPersistenceService>();
            var artworkDir = Path.Combine(persistence.DataDirectory, "artwork");
            return new LoonClient(artworkDir);
        });
        services.AddSingleton<IDiscordPresenceService, DiscordPresenceService>();
        services.AddSingleton<ILastFmService, LastFmService>();
        services.AddSingleton<IListenBrainzService, ListenBrainzService>();
        services.AddSingleton<ArtistImageService>();
        services.AddSingleton<ITunesArtworkService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<ILrcLibService, LrcLibService>();
        services.AddSingleton<INetEaseService, NetEaseService>();
        services.AddSingleton<IPlayHistoryService, PlayHistoryService>();
        services.AddSingleton<DeezerMetadataService>();
        services.AddSingleton<IAlbumArtworkSearch>(sp => sp.GetRequiredService<ITunesArtworkService>());
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

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            var crashDir = AppPaths.DataRoot;
            Directory.CreateDirectory(crashDir);
            var crashPath = Path.Combine(crashDir, "crash.log");
            var entry = $"[{DateTime.UtcNow:O}] {source}: {ex}\n---\n";
            File.AppendAllText(crashPath, entry);
        }
        catch
        {
            // Last-resort: don't let crash logging itself crash
        }
    }
}
