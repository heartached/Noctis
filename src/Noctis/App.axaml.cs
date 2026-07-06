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

    /// <summary>True when this process was launched by the OS autostart entry (its
    /// registered command carries a "--startup" arg), as opposed to a manual launch.</summary>
    public static bool LaunchedAtStartup { get; set; }

    /// <summary>True when the autostart entry additionally requested a minimized (tray)
    /// start ("--startup --minimized"). Read from args at process start so the decision
    /// needs no async settings load — the main window hides immediately if the tray is up.</summary>
    public static bool StartMinimizedAtLogin { get; set; }

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

        // Temporary diagnostic (no-op unless NOCTIS_MEMTRACE=1): localize the
        // reported runtime memory/CPU growth. Remove once diagnosed.
        MemoryTracer.StartIfEnabled();

        // Clicking anywhere outside a focused text box unfocuses it. Registered as
        // a class handler on TopLevel so it covers every window and dialog.
        Avalonia.Input.InputElement.PointerPressedEvent.AddClassHandler<TopLevel>(
            static (top, e) =>
            {
                if (top.FocusManager?.GetFocusedElement() is TextBox focused
                    && e.Source is Avalonia.Visual source
                    && source != focused
                    && !Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(focused, source))
                {
                    top.FocusManager.ClearFocus();
                }
            },
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
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

            // Background BPM/key analysis: kick a backfill pass after each library
            // update (initial scan, incremental rescans, imports), plus one now to
            // cover tracks already present from persisted JSON. StartBackfill is a
            // no-op when disabled, ffmpeg is unavailable, or a pass is already running,
            // and all heavy work runs off the UI thread (out-of-process ffmpeg + DSP).
            var analysisCoordinator = Services!.GetRequiredService<Noctis.Services.AudioAnalysis.AudioAnalysisCoordinator>();
            var library = Services!.GetRequiredService<ILibraryService>();
            library.LibraryUpdated += (_, _) => analysisCoordinator.StartBackfill();
            analysisCoordinator.StartBackfill();

            // Graceful shutdown: save state before exit. The handler must cancel the
            // request first — Avalonia proceeds with shutdown as soon as an async
            // handler hits its first await, which cut off the later saves (queue
            // snapshot, play-history flush, final scrobble). Cancel, finish the save,
            // then shut down for real; the flag makes the re-entrant call pass through.
            var shutdownSaveDone = false;
            desktop.ShutdownRequested += async (_, e) =>
            {
                if (shutdownSaveDone) return;
                shutdownSaveDone = true;
                e.Cancel = true;
                analysisCoordinator.Stop();
                try { await mainVm.ShutdownAsync(); }
                catch (Exception ex)
                {
                    DebugLogger.Error(DebugLogger.Category.Error, "ShutdownSave", ex.Message);
                }
                desktop.Shutdown();
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
        // Row 1 — reds, pinks, purples
        new AccentPreset("Crimson",    "#E74856"),
        new AccentPreset("Red",        "#FF4F57"),
        new AccentPreset("Coral",      "#FF6F61"),
        new AccentPreset("Salmon",     "#FF8FA3"),
        new AccentPreset("Pink",       "#FF7BAC"),
        new AccentPreset("Rose",       "#E754B5"),
        new AccentPreset("Magenta",    "#C724B1"),
        new AccentPreset("Plum",       "#9B59B6"),
        new AccentPreset("Orchid",     "#C45CE0"),
        new AccentPreset("Lavender",   "#D89BE8"),
        new AccentPreset("Violet",     "#874CF2"),
        new AccentPreset("Purple",     "#5917E8"),
        // Row 2 — blues, cyans, teals
        new AccentPreset("Navy",       "#1800A8"),
        new AccentPreset("Indigo",     "#4338CA"),
        new AccentPreset("Cobalt",     "#0D56B3"),
        new AccentPreset("Azure",      "#4C6EF5"),
        new AccentPreset("Periwinkle", "#7C83FD"),
        new AccentPreset("Ocean",      "#0E86D4"),
        new AccentPreset("Sky",        "#39B5F0"),
        new AccentPreset("Arctic",     "#8ED6F8"),
        new AccentPreset("Cyan",       "#19C2C2"),
        new AccentPreset("Turquoise",  "#2DD4BF"),
        new AccentPreset("Aqua",       "#55D4D9"),
        new AccentPreset("Teal",       "#0FA3B1"),
        // Row 3 — greens, yellows, oranges (last cell is the custom picker)
        new AccentPreset("Forest",     "#1F9D55"),
        new AccentPreset("Emerald",    "#12C76F"),
        new AccentPreset("Jade",       "#00C49A"),
        new AccentPreset("Lime",       "#7ED957"),
        new AccentPreset("Mint",       "#B8FF66"),
        new AccentPreset("Lemon",      "#FFE45C"),
        new AccentPreset("Gold",       "#F4D24B"),
        new AccentPreset("Amber",      "#FDB84D"),
        new AccentPreset("Peach",      "#FFA06B"),
        new AccentPreset("Tangerine",  "#FF8547"),
        new AccentPreset("Rust",       "#E2613B"),
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
        // Exact accent for text, adjusted only when the raw accent lacks contrast
        // against the current page background. Thresholds approximate a 3:1
        // contrast ratio vs the dark (#252525) and light page backgrounds.
        var lum = Luminance(color);
        var accentTextExact = isLightTheme
            ? (lum >= 0.28 ? Mix(color, Colors.Black, 0.45) : color)
            : (lum <= 0.15 ? Mix(color, Colors.White, 0.45) : color);

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
            ["AccentTextExactBrush"]               = new SolidColorBrush(accentTextExact),
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

    private static double Luminance(Color c)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.03928
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(c.R) +
               0.7152 * Linear(c.G) +
               0.0722 * Linear(c.B);
    }

    private static Color GetReadableForeground(Color background)
    {
        // Bias toward white: only switch to black when the accent is light enough
        // that white-on-accent would be unreadable (e.g. white, pale yellow, mint).
        return Luminance(background) >= 0.6 ? Colors.Black : Colors.White;
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
