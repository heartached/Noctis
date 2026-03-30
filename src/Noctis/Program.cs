using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;
using System.Threading;
using Noctis.Services;
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

            // Configure DI container
            var services = new ServiceCollection();
            ConfigureServices(services);
            var provider = services.BuildServiceProvider();

            // Make services available to the Avalonia App
            App.Services = provider;

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
            // Show a native Win32 message box so users see why the app failed to start
            // (e.g. missing libvlc native DLLs, .NET host issues after extraction)
            MessageBox(IntPtr.Zero,
                $"Noctis failed to start:\n\n{ex.Message}",
                "Noctis — Startup Error", 0x10 /* MB_ICONERROR */);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

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
        services.AddSingleton<ArtistImageService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<ILrcLibService, LrcLibService>();
        services.AddSingleton<INetEaseService, NetEaseService>();

        // ViewModels — MainWindowViewModel is the root, created once
        services.AddSingleton<MainWindowViewModel>();
    }

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var crashDir = Path.Combine(appData, "Noctis");
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
