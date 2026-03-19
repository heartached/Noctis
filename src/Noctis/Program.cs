using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;
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
            // Configure DI container
            var services = new ServiceCollection();
            ConfigureServices(services);
            var provider = services.BuildServiceProvider();

            // Make services available to the Avalonia App
            App.Services = provider;

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
        services.AddSingleton<ILrcLibService, LrcLibService>();
        services.AddSingleton<INetEaseService, NetEaseService>();

        // ViewModels — MainWindowViewModel is the root, created once
        services.AddSingleton<MainWindowViewModel>();
    }
}
