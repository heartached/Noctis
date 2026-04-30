using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Noctis.Controls;
using Noctis.Services;
using Noctis.Views;
using Noctis.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Noctis;

public partial class App : Application
{
    /// <summary>Global service provider, configured in Program.cs.</summary>
    public static IServiceProvider? Services { get; set; }

    /// <summary>Cached view locator for pre-warming heavy views.</summary>
    public static CachedViewLocator? CachedLocator { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Cache heavy views so they aren't recreated on every navigation.
        // Views with complex templates (virtualized lists, context menus)
        // take ~1s to build from scratch. Caching eliminates this lag.
        var cachedLocator = new CachedViewLocator(new Dictionary<Type, Func<Avalonia.Controls.Control>>
        {
            [typeof(LibrarySongsViewModel)] = () => new LibrarySongsView(),
            [typeof(LibraryAlbumsViewModel)] = () => new LibraryAlbumsView(),
            [typeof(LibraryArtistsViewModel)] = () => new LibraryArtistsView(),
            [typeof(CoverFlowViewModel)] = () => new CoverFlowView(),
            [typeof(HomeViewModel)] = () => new HomeView(),
            [typeof(FavoritesViewModel)] = () => new FavoritesView(),
            [typeof(LibraryPlaylistsViewModel)] = () => new LibraryPlaylistsView(),
            [typeof(StatisticsViewModel)] = () => new StatisticsView(),
            [typeof(QueueViewModel)] = () => new QueueView(),
            [typeof(SettingsViewModel)] = () => new SettingsView(),
        });
        DataTemplates.Insert(0, cachedLocator);
        CachedLocator = cachedLocator;

        // Global error capture — logs to DebugLogger when enabled
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            DebugLogger.Error(DebugLogger.Category.Error, "UnhandledException",
                $"terminating={args.IsTerminating}, msg={ex?.Message}");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DebugLogger.Error(DebugLogger.Category.Error, "UnobservedTaskException",
                $"msg={args.Exception?.InnerException?.Message ?? args.Exception?.Message}");
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainVm = Services!.GetRequiredService<MainWindowViewModel>();

            desktop.MainWindow = new MainWindow
            {
                DataContext = mainVm
            };

            // Graceful shutdown: save state before exit
            desktop.ShutdownRequested += async (_, _) =>
            {
                await mainVm.ShutdownAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Names used for persistence and for picking the runtime overlay.
    public const string ThemeGray = "Gray";
    public const string ThemeDark = "Dark";
    public const string ThemeLight = "Light";
    public const string ThemeMidnight = "Midnight";
    public const string ThemePaper = "Paper";

    private ResourceInclude? _activeThemeOverlay;

    /// <summary>
    /// Switches the application theme at runtime. Light maps to the Light variant;
    /// every other theme runs on the Dark variant with an optional overlay merged on top
    /// (Gray uses the base Dark dictionary as-is).
    /// </summary>
    public void SetTheme(string themeName)
    {
        // Drop any prior overlay before swapping.
        if (_activeThemeOverlay != null)
        {
            Resources.MergedDictionaries.Remove(_activeThemeOverlay);
            _activeThemeOverlay = null;
        }

        RequestedThemeVariant = (themeName == ThemeLight || themeName == ThemePaper)
            ? Avalonia.Styling.ThemeVariant.Light
            : Avalonia.Styling.ThemeVariant.Dark;

        var overlayUri = themeName switch
        {
            ThemeDark => "avares://Noctis/Assets/Themes/Dark.axaml",
            ThemeMidnight => "avares://Noctis/Assets/Themes/Midnight.axaml",
            ThemePaper => "avares://Noctis/Assets/Themes/Paper.axaml",
            _ => null
        };

        if (overlayUri != null)
        {
            var include = new ResourceInclude((Uri?)null) { Source = new Uri(overlayUri) };
            Resources.MergedDictionaries.Add(include);
            _activeThemeOverlay = include;
        }
    }
}
