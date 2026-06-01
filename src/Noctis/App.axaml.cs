using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
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

    private ResourceInclude? _activeThemeOverlay;
    private Avalonia.Controls.ResourceDictionary? _activeCustomOverlay;
    private string? _activeAccentHex;

    /// <summary>
    /// Callback the SettingsViewModel registers so App can resolve a Custom:<id> theme name
    /// to a concrete definition without taking a hard dependency on the settings service.
    /// </summary>
    public Func<string, Noctis.Models.CustomThemeDefinition?>? CustomThemeResolver { get; set; }

    /// <summary>
    /// Switches the application theme at runtime. Light maps to the Light variant;
    /// every other theme runs on the Dark variant with an optional overlay merged on top
    /// (Gray uses the base Dark dictionary as-is).
    /// </summary>
    public void SetTheme(string themeName)
    {
        if (_activeThemeOverlay != null)
        {
            Resources.MergedDictionaries.Remove(_activeThemeOverlay);
            _activeThemeOverlay = null;
        }
        if (_activeCustomOverlay != null)
        {
            Resources.MergedDictionaries.Remove(_activeCustomOverlay);
            _activeCustomOverlay = null;
        }

        // Custom themes
        if (themeName != null && themeName.StartsWith("Custom:", StringComparison.Ordinal))
        {
            var id = themeName.Substring("Custom:".Length);
            var def = CustomThemeResolver?.Invoke(id);
            if (def != null)
            {
                var derived = Noctis.Services.ThemeDerivation.Derive(def);

                RequestedThemeVariant =
                    derived.TryGetValue("__BaseVariant", out var v) && (string)v == "Light"
                        ? Avalonia.Styling.ThemeVariant.Light
                        : Avalonia.Styling.ThemeVariant.Dark;

                var rd = new Avalonia.Controls.ResourceDictionary();
                foreach (var (key, value) in derived)
                {
                    if (key.StartsWith("__")) continue;
                    rd[key] = value;
                }
                Resources.MergedDictionaries.Add(rd);
                _activeCustomOverlay = rd;

                // Custom themes drive their own accent — push it through the SetAccent
                // path so the accent palette (Dark1/2/3, Light1/2/3, MenuFlyout hovers,
                // etc.) is generated and overlaid the same way as for built-in themes.
                SetAccent(def.AccentHex);
                return;
            }
            // Unknown id falls through to default handling (Gray).
            themeName = ThemeGray;
        }

        RequestedThemeVariant = themeName == ThemeLight
            ? Avalonia.Styling.ThemeVariant.Light
            : Avalonia.Styling.ThemeVariant.Dark;

        var overlayUri = themeName switch
        {
            ThemeDark => "avares://Noctis/Assets/Themes/Dark.axaml",
            ThemeMidnight => "avares://Noctis/Assets/Themes/Midnight.axaml",
            _ => null
        };

        if (overlayUri != null)
        {
            var include = new ResourceInclude((Uri?)null) { Source = new Uri(overlayUri) };
            Resources.MergedDictionaries.Add(include);
            _activeThemeOverlay = include;
        }

        // Theme overlays redefine AccentColorBrush et al. — re-apply the user's accent
        // last so it always wins, regardless of theme.
        if (_activeAccentHex != null)
            SetAccent(_activeAccentHex);
    }

    // ── Accent palette ────────────────────────────────────────────

    public sealed record AccentPreset(string Name, string Hex);

    /// <summary>
    /// Curated accent presets. Order is meaningful — this is the order shown in Settings.
    /// </summary>
    public static readonly IReadOnlyList<AccentPreset> AccentPresets = new[]
    {
        new AccentPreset("Crimson",   "#E74856"),
        new AccentPreset("Red",       "#FF4F57"),
        new AccentPreset("Rose",      "#E754B5"),
        new AccentPreset("Lavender",  "#D89BE8"),
        new AccentPreset("Orchid",    "#C45CE0"),
        new AccentPreset("Violet",    "#874CF2"),
        new AccentPreset("Purple",    "#5917E8"),
        new AccentPreset("Teal",      "#0FA3B1"),
        new AccentPreset("Cyan",      "#19C2C2"),
        new AccentPreset("Aqua",      "#55D4D9"),
        new AccentPreset("Sky",       "#39B5F0"),
        new AccentPreset("Azure",     "#4C6EF5"),
        new AccentPreset("Cobalt",    "#0D56B3"),
        new AccentPreset("Navy",      "#1800A8"),
        new AccentPreset("Emerald",   "#12C76F"),
        new AccentPreset("Lime",      "#7ED957"),
        new AccentPreset("Mint",      "#B8FF66"),
        new AccentPreset("Gold",      "#F4D24B"),
        new AccentPreset("Amber",     "#FDB84D"),
        new AccentPreset("Tangerine", "#FF8547"),
    };

    private ResourceDictionary? _activeAccentOverlay;

    /// <summary>
    /// Raised after the accent overlay is swapped, so .cs-driven controls that captured
    /// the previous accent brush (e.g. LottieToggle) can re-resolve and update in place.
    /// </summary>
    public static event EventHandler? AccentApplied;

    /// <summary>
    /// Applies an accent colour at runtime by merging an override dictionary on top of
    /// Application.Resources. Replaces SystemAccentColor (+ Dark1-3 / Light1-3 shades) and
    /// every brush that fans out from it (toggle on-states, island accent, accent brushes).
    /// </summary>
    public void SetAccent(string hex)
    {
        if (!TryParseHex(hex, out var color))
            color = Color.Parse("#E74856");

        _activeAccentHex = hex;

        if (_activeAccentOverlay != null)
        {
            Resources.MergedDictionaries.Remove(_activeAccentOverlay);
            _activeAccentOverlay = null;
        }

        var dark1  = Mix(color, Colors.Black, 0.15);
        var dark2  = Mix(color, Colors.Black, 0.30);
        var dark3  = Mix(color, Colors.Black, 0.45);
        var light1 = Mix(color, Colors.White, 0.15);
        var light2 = Mix(color, Colors.White, 0.30);
        var light3 = Mix(color, Colors.White, 0.45);
        var accentForeground = GetReadableForeground(color);
        var isLightTheme = RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Light;
        // Outline around accent-filled pills. Only meaningful when the accent fill
        // would be indistinguishable from the page background — in practice that's
        // a white / very-light accent on the Light theme. In every other case the
        // outline is visual noise, so make it fully transparent.
        var accentBorder = (isLightTheme && IsLight(color))
            ? Mix(color, Colors.Black, 0.25)
            : Color.FromArgb(0, 0, 0, 0);
        // Page-bg-aware accent for *text* / icon foregrounds drawn on the main surface.
        // Falls back to a darkened/lightened accent when the accent itself would blend
        // into the current page background.
        var accentText = isLightTheme
            ? (IsLight(color) ? Mix(color, Colors.Black, 0.55) : color)
            : (IsLight(color) ? color : Mix(color, Colors.White, 0.55));

        var rd = new ResourceDictionary
        {
            ["SystemAccentColor"] = color,
            ["SystemAccentColorDark1"] = dark1,
            ["SystemAccentColorDark2"] = dark2,
            ["SystemAccentColorDark3"] = dark3,
            ["SystemAccentColorLight1"] = light1,
            ["SystemAccentColorLight2"] = light2,
            ["SystemAccentColorLight3"] = light3,
            ["SystemControlHighlightAccentBrush"]  = new SolidColorBrush(color),
            ["SystemControlHighlightAccentBrush2"] = new SolidColorBrush(light1),
            ["AccentColorBrush"]                   = new SolidColorBrush(color),
            ["AccentForegroundBrush"]              = new SolidColorBrush(accentForeground),
            ["AccentBorderBrush"]                  = new SolidColorBrush(accentBorder),
            ["AccentTextBrush"]                    = new SolidColorBrush(accentText),
            ["AccentColorBrushLight1"]             = new SolidColorBrush(light1),
            ["AccentColorBrushDark1"]              = new SolidColorBrush(dark1),
            ["ToggleSwitchFillOn"]                 = new SolidColorBrush(color),
            ["ToggleSwitchFillOnPointerOver"]      = new SolidColorBrush(light1),
            ["ToggleSwitchFillOnPressed"]          = new SolidColorBrush(dark1),
            ["ToggleSwitchFillOnDragging"]         = new SolidColorBrush(light1),
            ["IslandIconAccent"]                   = new SolidColorBrush(color),
            // Slider fill/thumb tracks the accent so every slider (seek, volume,
            // volume-adjust, pre-amp, island) is uniformly accent-coloured.
            ["IslandSliderFilled"]                 = new SolidColorBrush(color),

            // Accent-tinted hover/press for menu items so dropdowns match the active accent
            ["MenuFlyoutItemBackgroundPointerOver"]    = new SolidColorBrush(color, 0.22),
            ["MenuFlyoutItemBackgroundPressed"]       = new SolidColorBrush(color, 0.35),
            ["MenuFlyoutSubItemBackgroundPointerOver"] = new SolidColorBrush(color, 0.22),
            ["MenuFlyoutSubItemBackgroundPressed"]    = new SolidColorBrush(color, 0.35),
            ["MenuFlyoutSubItemBackgroundSubMenuOpened"] = new SolidColorBrush(color, 0.22),
            ["MenuBarItemBackgroundPointerOver"]      = new SolidColorBrush(color, 0.22),
            ["MenuBarItemBackgroundPressed"]          = new SolidColorBrush(color, 0.35),
            ["MenuBarItemBackgroundSelected"]         = new SolidColorBrush(color, 0.22),
        };

        Resources.MergedDictionaries.Add(rd);
        _activeAccentOverlay = rd;

        AccentApplied?.Invoke(this, EventArgs.Empty);
    }

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte r = (byte)(a.R + (b.R - a.R) * t);
        byte g = (byte)(a.G + (b.G - a.G) * t);
        byte bl = (byte)(a.B + (b.B - a.B) * t);
        return Color.FromRgb(r, g, bl);
    }

    private static Color GetReadableForeground(Color background)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.03928
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        var luminance =
            0.2126 * Linear(background.R) +
            0.7152 * Linear(background.G) +
            0.0722 * Linear(background.B);

        // Bias toward white: only switch to black when the accent is light enough
        // that white-on-accent would be unreadable (e.g. white, pale yellow, mint).
        return luminance >= 0.6 ? Colors.Black : Colors.White;
    }

    private static bool IsLight(Color c) => GetReadableForeground(c) == Colors.Black;

    private static bool TryParseHex(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try { color = Color.Parse(hex.Trim()); return true; }
        catch { return false; }
    }
}
