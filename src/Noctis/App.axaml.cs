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

    /// <summary>Audio files passed on this launch's command line ("Open with
    /// Noctis"), or opened through macOS's open-documents event before startup
    /// finished, consumed by the main window once the player is ready.</summary>
    public static IReadOnlyList<string> PendingOpenFiles { get; set; } = Array.Empty<string>();

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
            [typeof(LyricsViewModel)] = () => new LyricsView(),
            [typeof(ServerViewModel)] = () => new ServerView(),
            [typeof(AudioCdViewModel)] = () => new AudioCdView(),
            [typeof(VisualizerViewModel)] = () => new VisualizerView(),
            [typeof(LyricsStudioPageViewModel)] = () => new LyricsStudioView(),
        });
        DataTemplates.Insert(0, cachedLocator);
        CachedLocator = cachedLocator;

        // AppDomain.UnhandledException and TaskScheduler.UnobservedTaskException are
        // registered once, in Program.Main — which writes both crash.log and DebugLog.
        // Registering them here too logged every fault twice, to two different sinks,
        // with no correlation between the entries.

        // Dispatcher faults, however, had NO handler anywhere: an exception from a
        // UI-thread async continuation went straight to AppDomain.UnhandledException and
        // terminated the process. Log and mark handled — a fault in one continuation
        // should not take the whole app down.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            DebugLogger.Error(DebugLogger.Category.Error, "DispatcherUnhandledException",
                args.Exception.Message);
            Noctis.Services.DebugLog.Write("Dispatcher", args.Exception);
            args.Handled = true;
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
                    top.FocusManager.Focus(null); // 12: Focus(null) clears (ClearFocus removed)
                }
            },
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Every ComboBox drop-down eases open (fade + glide), matching the Settings folds.
        Noctis.Helpers.ComboBoxDropDownAnimator.Install();
    }

    /// <summary>
    /// For "scrolling is choppy" reports, which only reproduce on the reporter's machine.
    /// Scroll frames repaint the whole page, and on the software renderer that raster is
    /// the frame cost (a 5-column Albums page measured ~11 ms/frame on a fast core, far more
    /// with Liquid Glass on), so the session log (Developer Mode → Copy Logs) records which
    /// renderer this machine got. NOCTIS_RENDER_OVERLAY=1 draws Avalonia's own FPS, layout
    /// and render time graphs over the window to see the per-frame costs live.
    /// </summary>
    private static void AttachRenderDiagnostics(Window window)
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_RENDER_OVERLAY") == "1")
            window.RendererDiagnostics.DebugOverlays = Avalonia.Rendering.RendererDebugOverlays.Fps
                | Avalonia.Rendering.RendererDebugOverlays.LayoutTimeGraph
                | Avalonia.Rendering.RendererDebugOverlays.RenderTimeGraph;

        async void LogRenderer(object? sender, EventArgs e)
        {
            window.Opened -= LogRenderer;
            try
            {
                // GPU backends (ANGLE/D3D11, Vulkan) expose GPU interop; the software renderer does not.
                var compositor = Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(window)?.Compositor;
                var interop = compositor == null ? null : await compositor.TryGetCompositionGpuInterop();
                DebugLog.Write("Startup", interop != null
                    ? $"renderer: GPU ({interop.GetType().Name})"
                    : "renderer: software (no GPU interop) — scroll frames are rasterized on the CPU");
            }
            catch (Exception ex)
            {
                DebugLog.Write("Startup", $"renderer: unknown ({ex.Message})");
            }
        }
        window.Opened += LogRenderer;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // The core rewrites cover files without knowing about the UI's bitmap cache.
        global::Noctis.Services.LibraryService.ArtworkFileReplaced += global::Noctis.Services.ArtworkCache.Invalidate;
        // Downscaled cover decodes kept on disk: a 3000px cover is decoded at full size once.
        ArtworkThumbnailCache.Enable(System.IO.Path.Combine(Noctis.Helpers.AppPaths.DataRoot, "cache", "artwork_thumbs"));
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Noctis.Services.StartupTrace.Mark("avalonia-initialized");

            // This resolve builds the entire object graph, IAudioPlayer included — so it
            // blocks on the same singleton lock the Program.Main warm task is holding
            // while libvlc loads. The gap between the two marks is how much of that load
            // the warm task failed to hide.
            var mainVm = Services!.GetRequiredService<MainWindowViewModel>();
            Noctis.Services.StartupTrace.Mark("viewmodel-graph-resolved");

            // The view model rides the constructor so DataContext is set BEFORE
            // InitializeComponent. Assigning it afterwards (object initializer) let every
            // $parent[Window].DataContext.* chain in MainWindow.axaml evaluate once
            // against a null DataContext and log a binding warning at each startup.
            // Same total work between the surrounding marks — bindings now resolve in a
            // single pass instead of erroring first and re-resolving.
            var mainWindow = new MainWindow(mainVm);
            Noctis.Services.StartupTrace.Mark("main-window-constructed");

            // Decide "start minimized to tray" before the window is realized. Avalonia
            // shows MainWindow before Loaded fires, and the Hide() that honours this
            // setting used to be the last statement of that handler — after the settings
            // load, the whole library JSON, the index rebuild, playlists and the queue
            // restore. On a large library that was seconds of a fully painted window on
            // screen at every login, which is exactly what the setting exists to prevent.
            if (StartMinimizedAtLogin)
            {
                mainWindow.WindowState = Avalonia.Controls.WindowState.Minimized;
                mainWindow.ShowInTaskbar = false;
            }

            desktop.MainWindow = mainWindow;
            AttachRenderDiagnostics(mainWindow);

            // Background BPM/key analysis: kick a backfill pass after each library
            // update (initial scan, incremental rescans, imports), plus one now to
            // cover tracks already present from persisted JSON. StartBackfill is a
            // no-op when disabled, ffmpeg is unavailable, or a pass is already running,
            // and all heavy work runs off the UI thread (out-of-process ffmpeg + DSP).
            var analysisCoordinator = Services!.GetRequiredService<Noctis.Services.AudioAnalysis.AudioAnalysisCoordinator>();
            var library = Services!.GetRequiredService<ILibraryService>();
            library.LibraryUpdated += (_, _) => analysisCoordinator.StartBackfill();
            // No-op on Windows (the output chain taps the meters); elsewhere this is the
            // only thing that makes the visualizer / beat-reactive backdrops move.
            var sideFeed = Services!.GetRequiredService<Noctis.Services.AudioAnalysis.SideDecodeMeterFeed>();
            sideFeed.Start();

            // Clear temp export directories orphaned by a previous crash/kill.
            _ = Task.Run(Helpers.PngExportHelper.SweepStaleTempDirs);

            // No eager StartBackfill here: this runs before MainWindow.Loaded has awaited
            // Settings.LoadAsync(), so _settings() was still a default AppSettings and the
            // first pass ignored the user's BpmKeyAnalysisEnabled choice. The
            // LibraryUpdated subscription above covers it once the library is loaded, by
            // which point settings are real.

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
                // Await the backfill's unwind: TryWriteTags is a synchronous TagLib
                // rewrite of a whole audio file, and exiting mid-Save() truncates it.
                //
                // Every step is deadline-bounded. Avalonia's Win32 backend raises this
                // from WM_QUERYENDSESSION and reports a cancelled event back to Windows
                // as a shutdown veto, and ShutdownRequestedEventArgs (11.3.18) exposes no
                // way to tell an OS session-end from a normal quit. Unbounded, a slow save
                // left the user staring at "This app is preventing you from shutting
                // down" — or Windows force-terminated us and cut off the very save the
                // cancel exists to protect. Bounded, the veto lasts at most a few seconds
                // and we always exit under our own power.
                try { await analysisCoordinator.StopAsync(TimeSpan.FromSeconds(3)); } catch { }
                try { await mainVm.ShutdownAsync().WaitAsync(ShutdownSaveDeadline); }
                catch (TimeoutException)
                {
                    DebugLogger.Error(DebugLogger.Category.Error, "ShutdownSave",
                        $"did not finish within {ShutdownSaveDeadline.TotalSeconds:0}s — exiting anyway");
                }
                catch (Exception ex)
                {
                    DebugLogger.Error(DebugLogger.Category.Error, "ShutdownSave", ex.Message);
                }
                // The provider is never disposed: stop the feed thread and its ffmpeg child here.
                sideFeed.Dispose();
                desktop.Shutdown();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>How long the shutdown save may hold the exit before we quit regardless.</summary>
    private static readonly TimeSpan ShutdownSaveDeadline = TimeSpan.FromSeconds(4);

    // Names used for persistence and for picking the runtime overlay.
    public const string ThemeGray = "Gray";
    public const string ThemeDark = "Dark";
    public const string ThemeLight = "Light";
    public const string ThemeMidnight = "Midnight";
    public const string ThemeInk = "Ink";
    public const string ThemeSmoke = "Smoke";

    /// <summary>Built-in themes that run on the Light variant; every other name runs on Dark.</summary>
    private static readonly HashSet<string> LightVariantThemes = new(StringComparer.Ordinal)
    {
        ThemeLight,
    };

    public static bool IsLightVariantTheme(string? themeName) =>
        themeName != null && LightVariantThemes.Contains(themeName);

    private ResourceInclude? _activeThemeOverlay;
    private Avalonia.Controls.ResourceDictionary? _activeCustomOverlay;
    private string? _activeAccentHex;

    /// <summary>
    /// Callback the SettingsViewModel registers so App can resolve a Custom:<id> theme name
    /// to a concrete definition without taking a hard dependency on the settings service.
    /// </summary>
    public Func<string, Noctis.Models.CustomThemeDefinition?>? CustomThemeResolver { get; set; }

    /// <summary>Theme names from content packs: "Pack:&lt;pack id&gt;/&lt;theme id&gt;".</summary>
    public const string PackThemePrefix = "Pack:";

    /// <summary>Resolves the part after <see cref="PackThemePrefix"/> to a content-pack theme
    /// (registered by SettingsViewModel over the plugin host's content catalog).</summary>
    public Func<string, Noctis.Services.Plugins.PackTheme?>? PackThemeResolver { get; set; }

    /// <summary>
    /// Switches the application theme at runtime. Light-variant themes (see
    /// <see cref="IsLightVariantTheme"/>) run on the Light dictionary, every other theme on
    /// Dark, with an optional overlay merged on top (Gray / Light use the base dictionary as-is).
    /// </summary>
    public void SetTheme(string themeName)
    {
        var mainWindow = (ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        RunWithTransitionsSuppressed(mainWindow, () => SetThemeCore(themeName));
    }

    /// <summary>Class the main window carries while a theme switch is in flight. Styles that
    /// animate a themed brush for hover (Home chart rows / rail cards) drop their transitions
    /// under it, so the DynamicResource swap lands in one frame instead of lerping through a
    /// lighter semi-opaque grey (HomeTileThemeSwitchTests).</summary>
    public const string ThemeSwitchingClass = "theme-switching";

    /// <summary>Runs <paramref name="body"/> with <see cref="ThemeSwitchingClass"/> on
    /// <paramref name="root"/>; the class comes off once the resource change has been
    /// rendered, so the next hover animates again.</summary>
    public static void RunWithTransitionsSuppressed(StyledElement? root, Action body)
    {
        if (root == null || root.Classes.Contains(ThemeSwitchingClass))
        {
            body();
            return;
        }
        root.Classes.Add(ThemeSwitchingClass);
        try
        {
            body();
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => root.Classes.Remove(ThemeSwitchingClass),
                Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private void SetThemeCore(string themeName)
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

        // Content-pack themes: pure data (colours for whitelisted keys), expanded the same way
        // as custom themes. A pack that was switched off or removed falls back to Gray.
        if (themeName != null && themeName.StartsWith(PackThemePrefix, StringComparison.Ordinal))
        {
            var theme = PackThemeResolver?.Invoke(themeName.Substring(PackThemePrefix.Length));
            if (theme != null)
            {
                var resources = Noctis.Services.Plugins.PackThemeBuilder.Build(theme);
                RequestedThemeVariant = theme.IsLight ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
                var rd = new Avalonia.Controls.ResourceDictionary();
                foreach (var (key, value) in resources)
                {
                    if (key.StartsWith("__")) continue;
                    rd[key] = value;
                }
                Resources.MergedDictionaries.Add(rd);
                _activeCustomOverlay = rd;
                // The user's accent (seeded from the theme's accent when it was picked) wins.
                SetAccent(_activeAccentHex ?? theme.AccentHex);
                return;
            }
            themeName = ThemeGray;
        }

        RequestedThemeVariant = IsLightVariantTheme(themeName)
            ? Avalonia.Styling.ThemeVariant.Light
            : Avalonia.Styling.ThemeVariant.Dark;

        var overlayUri = themeName switch
        {
            ThemeDark => "avares://Noctis.UI/Assets/Themes/Dark.axaml",
            ThemeMidnight => "avares://Noctis.UI/Assets/Themes/Midnight.axaml",
            ThemeInk => "avares://Noctis.UI/Assets/Themes/Ink.axaml",
            ThemeSmoke => "avares://Noctis.UI/Assets/Themes/Smoke.axaml",
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
        new AccentPreset("Scarlet",    "#FF3B30"),
        new AccentPreset("Coral",      "#FF6F61"),
        new AccentPreset("Salmon",     "#FF8FA3"),
        new AccentPreset("Bubblegum",  "#FF7BAC"),
        new AccentPreset("Rose",       "#E754B5"),
        new AccentPreset("Magenta",    "#C724B1"),
        new AccentPreset("Orchid",     "#C45CE0"),
        new AccentPreset("Lilac",      "#B39DDB"),
        new AccentPreset("Violet",     "#874CF2"),
        new AccentPreset("Indigo",     "#4338CA"),
        new AccentPreset("Navy",       "#1F2A7A"),
        // Row 2 — blues, teals, greens
        new AccentPreset("Cobalt",     "#0D56B3"),
        new AccentPreset("Cerulean",   "#2A7FCF"),
        new AccentPreset("Sky",        "#39B5F0"),
        new AccentPreset("Arctic",     "#8ED6F8"),
        new AccentPreset("Teal",       "#0FA3B1"),
        new AccentPreset("Turquoise",  "#2DD4BF"),
        new AccentPreset("Jade",       "#00C49A"),
        new AccentPreset("Emerald",    "#12C76F"),
        new AccentPreset("Lime",       "#7ED957"),
        new AccentPreset("Olive",      "#8A9A2B"),
        new AccentPreset("Moss",       "#5C7A3A"),
        new AccentPreset("Lemon",      "#FFE45C"),
        // Row 3 — golds, oranges, earth tones, neutrals
        new AccentPreset("Gold",       "#F4D24B"),
        new AccentPreset("Amber",      "#FDB84D"),
        new AccentPreset("Tangerine",  "#FF8547"),
        new AccentPreset("Rust",       "#E2613B"),
        new AccentPreset("Terracotta", "#C8643E"),
        new AccentPreset("Brick",      "#B7412E"),
        new AccentPreset("Wine",       "#8E1B3A"),
        new AccentPreset("Rose Gold",  "#B76E79"),
        new AccentPreset("Mocha",      "#8B5E3C"),
        new AccentPreset("Sand",       "#D8B384"),
        new AccentPreset("Slate",      "#5B6C8F"),
        new AccentPreset("Silver",     "#B8BCC4"),
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
        // Row text / EQ bars / icons on the now-playing track box: white on dark themes,
        // black on light ones. Deliberately theme-driven rather than derived from the
        // accent's own luminance — flipping per-accent made the row read as mismatched
        // against the rest of the list, and a solid accent band with constant text is what
        // the design targets.
        //
        // The one thing that outranks that consistency is being able to read the row at all.
        // The constant was previously unconditional, which put white text on a near-white
        // band whenever the accent was pale (the Dark theme's silver, a white or pastel
        // custom accent) and left the row rendering as a blank bar. So the constant holds
        // only while it clears a 3:1 floor against the band; below that the row takes
        // whichever of black/white actually contrasts. Every accent that reads either way
        // keeps the theme colour, so this changes nothing for the common ones.
        var themeRowForeground = isLightTheme ? Colors.Black : Colors.White;
        // The now-playing row is the accent on every theme (Ink's pinned blue row read as
        // "the theme changes my accent", 09-17).
        var rowColor = color;
        var nowPlayingRowForeground = ContrastRatio(themeRowForeground, rowColor) >= 3.0
            ? themeRowForeground
            : HighestContrastForeground(rowColor);
        IBrush accentButtonBackground = new SolidColorBrush(color);
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
            // Fill for accent-filled action buttons. Identical to AccentColorBrush here;
            // it exists as its own key so MainWindow's Liquid Glass overlay can frost the
            // buttons without making every accent surface (sliders, now-playing row,
            // sidebar selection, drag preview) translucent too.
            ["AccentButtonBackground"]             = accentButtonBackground,
            ["AccentForegroundBrush"]              = new SolidColorBrush(accentForeground),
            ["AccentBorderBrush"]                  = new SolidColorBrush(accentBorder),
            ["AccentTextBrush"]                    = new SolidColorBrush(accentText),
            ["AccentTextExactBrush"]               = new SolidColorBrush(accentTextExact),
            ["AccentColorBrushLight1"]             = new SolidColorBrush(light1),
            ["AccentColorBrushDark1"]              = new SolidColorBrush(dark1),

            // Now-playing track row box. Previously retinted at runtime from the current
            // artwork's vibrant colour, which ignored the user's accent; it now follows the
            // accent like every other accent-filled surface.
            ["NowPlayingRowBrush"]           = new SolidColorBrush(rowColor),
            ["NowPlayingRowForegroundBrush"] = new SolidColorBrush(nowPlayingRowForeground),
            ["ToggleSwitchFillOn"]                 = new SolidColorBrush(color),
            ["ToggleSwitchFillOnPointerOver"]      = new SolidColorBrush(light1),
            ["ToggleSwitchFillOnPressed"]          = new SolidColorBrush(dark1),
            ["ToggleSwitchFillOnDragging"]         = new SolidColorBrush(light1),
            ["IslandIconAccent"]                   = new SolidColorBrush(color),

            // Fluent paints its accent-filled control states (checked ToggleButton,
            // CheckBox tick, RadioButton dot) with a hardcoded white foreground. The
            // fill above follows the accent, the foreground did not — so a white/very
            // light accent rendered white-on-white (invisible "Raw" pill label, tick,
            // radio dot). Re-point them at the same readable foreground the app's own
            // accent pills use.
            ["ToggleButtonForegroundChecked"]            = new SolidColorBrush(accentForeground),
            ["ToggleButtonForegroundCheckedPointerOver"] = new SolidColorBrush(accentForeground),
            ["ToggleButtonForegroundCheckedPressed"]     = new SolidColorBrush(accentForeground),
            ["CheckBoxCheckGlyphForegroundChecked"]            = new SolidColorBrush(accentForeground),
            ["CheckBoxCheckGlyphForegroundCheckedPointerOver"] = new SolidColorBrush(accentForeground),
            ["CheckBoxCheckGlyphForegroundCheckedPressed"]     = new SolidColorBrush(accentForeground),
            ["RadioButtonCheckGlyphFill"]            = new SolidColorBrush(accentForeground),
            ["RadioButtonCheckGlyphFillPointerOver"] = new SolidColorBrush(accentForeground),
            ["RadioButtonCheckGlyphFillPressed"]     = new SolidColorBrush(accentForeground),
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

    /// <summary>WCAG contrast ratio between two opaque colours (1.0 = identical, 21.0 = black on white).</summary>
    private static double ContrastRatio(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var hi = Math.Max(la, lb);
        var lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>
    /// Black or white, whichever is more legible on <paramref name="background"/>. Unlike
    /// GetReadableForeground — which is tuned for small glyphs and biases toward white — this
    /// makes no aesthetic choice; it is the last-resort pick for a large filled band.
    /// </summary>
    private static Color HighestContrastForeground(Color background) =>
        ContrastRatio(Colors.Black, background) >= ContrastRatio(Colors.White, background)
            ? Colors.Black
            : Colors.White;

    private static bool TryParseHex(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try { color = Color.Parse(hex.Trim()); return true; }
        catch { return false; }
    }
}
