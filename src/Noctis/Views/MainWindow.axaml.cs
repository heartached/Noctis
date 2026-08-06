using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class MainWindow : Window
{
    private static readonly IBrush ActiveToggleBg = new SolidColorBrush(Color.Parse("#30FFFFFF"));
    private static readonly IBrush InactiveToggleBg = Brushes.Transparent;

    private TaskbarIntegrationService? _taskbar;
    private SmtcService? _smtc;
    private MprisService? _mpris;
    private TrayIcon? _trayIcon;
    private bool _exitRequestedFromTray;
    private EventHandler<string>? _themeChangedHandler;
    private EventHandler<string>? _accentChangedHandler;
    private EventHandler<bool>? _liquidGlassChangedHandler;
    private EventHandler<Avalonia.Platform.PlatformColorValues>? _platformColorsChangedHandler;
    private ResourceDictionary? _liquidGlassOverlay;
    private bool _liquidGlassActive;
    private System.ComponentModel.PropertyChangedEventHandler? _playerPropertyChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _queuePopupStateHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _topBarPropertyChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _mainVmPropertyChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _currentTrackPropertyChangedHandler;
    private Track? _trackedFavoriteTrack;
    private Border? _sidebarWrapper;
    private Border? _lyricsPanelWrapper;
    private DockPanel? _contentDockPanel;
    private DockPanel? _rootPanel;
    private Border? _settingsOverlay;
    private Border? _settingsCard;
    private Border? _queuePopupPanel;
    private MiniPlayerWindow? _miniPlayer;
    private Action<IReadOnlyList<string>>? _singleInstanceActivationHandler;

    /// <summary>
    /// Opens the compact always-on-top mini player (hiding the main window), or closes
    /// it if it's already open. Closing the mini player restores the main window.
    /// Triggered by clicking the album art in the bottom player bar.
    /// </summary>
    public void ToggleMiniPlayer()
    {
        if (_miniPlayer != null)
        {
            _miniPlayer.Close(); // Closed handler below restores the main window
            return;
        }

        if (DataContext is not MainWindowViewModel vm) return;

        var miniVm = vm.CreateMiniPlayerViewModel();
        _miniPlayer = new MiniPlayerWindow { DataContext = miniVm };
        _miniPlayer.Closed += OnMiniPlayerClosed;

        // Always open as the compact bar (Apple Music-style widget); resizing from
        // there morphs it into the other forms.
        var (barWidth, barHeight) = MiniPlayerViewModel.CanonicalSize(MiniPlayerForm.Bar);
        _miniPlayer.Width = barWidth;
        _miniPlayer.Height = barHeight;
        miniVm.UpdateFromSize(barWidth, barHeight);

        // Place it near the top-right of the screen the main window is on.
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen != null)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            var width = (int)(_miniPlayer.Width * scale);
            _miniPlayer.Position = new PixelPoint(
                area.X + area.Width - width - (int)(24 * scale),
                area.Y + (int)(24 * scale));
        }

        _miniPlayer.Show();
        Hide();
    }

    private void OnMiniPlayerClosed(object? sender, System.EventArgs e)
    {
        if (sender is MiniPlayerWindow mini)
            mini.Closed -= OnMiniPlayerClosed;
        _miniPlayer = null;

        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    // Parameterless overload kept for the XAML previewer/designer; the app always
    // passes the view model.
    public MainWindow() : this(null) { }

    public MainWindow(MainWindowViewModel? viewModel)
    {
        // Must land before InitializeComponent: bindings that walk
        // $parent[Window].DataContext then resolve on their first evaluation instead
        // of erroring against null (a logged warning per binding, every startup) and
        // re-resolving when the DataContext arrives afterwards.
        if (viewModel is not null)
            DataContext = viewModel;

        InitializeComponent();
        Services.StartupTrace.Mark("mainwindow-xaml-initialized");

        // Initialize the application once the window is fully loaded.
        //
        // The whole body is guarded. This is an async void handler running *inside*
        // StartWithClassicDesktopLifetime, so Program.Main's try never sees anything it
        // throws: a failure here (e.g. a corrupt or locked library.db surfacing as
        // SqliteException out of LoadAsync) went straight to AppDomain.UnhandledException
        // and killed the process behind an already-visible empty window, with no dialog
        // and no recovery. A half-initialized window the user can still quit and report
        // beats a silent death.
        Loaded += async (_, _) =>
        {
            try
            {
                await InitializeOnLoadedAsync();
            }
            catch (Exception ex)
            {
                DebugLog.Write("Startup", ex);
                await ShowStartupFailureAsync(ex);
            }
        };

        WireWindowLevelHandlers();
    }

    /// <summary>Shows a non-fatal "couldn't finish starting" notice. Never throws.</summary>
    private static async Task ShowStartupFailureAsync(Exception ex)
    {
        try
        {
            await ConfirmationDialog.ShowAsync(
                "Noctis couldn't finish starting, so some parts of the app may not work. " +
                "Details were written to the debug log.\n\n" + ex.Message);
        }
        catch { /* the dialog itself is best effort */ }
    }

    /// <summary>
    /// Applies or removes the Liquid Glass appearance (Settings → Appearance).
    ///
    /// On: the window asks the OS for blur-behind (AcrylicBlur, falling back to
    /// Mica/Blur where unavailable), shows the ExperimentalAcrylicBorder backdrop
    /// tinted with the active theme's surface color, and merges a window-scoped
    /// resource overlay that swaps the structural surface brushes (window/content
    /// background, sidebar) to translucent variants so the blur shows through.
    /// The overlay lives in <b>this window's</b> resources, never the Application's,
    /// so dialog windows and the mini player keep their opaque surfaces.
    ///
    /// Off: the overlay is removed (DynamicResource consumers snap back to the
    /// theme's opaque brushes), the transparency hint is cleared back to its
    /// default, and the backdrop is hidden — restoring the stock rendering.
    ///
    /// Fallback: if the platform grants no transparency (Linux without a
    /// compositor, headless), the material's FallbackColor paints an opaque
    /// theme-colored backdrop, so the translucent surfaces above it stay readable.
    /// </summary>
    private void ApplyLiquidGlass(bool on)
    {
        // Never applies on Linux: AcrylicBlur/Mica don't exist there, Blur is
        // KDE-only, and Avalonia's X11 backend doesn't track compositor changes
        // (AvaloniaUI/Avalonia#3300; #5333 "Transparency effect does not work on
        // Fedora GNOME") — the hint list would degrade to a plain see-through
        // window on most WMs, the exact "window turns transparent" artifact from
        // issue #26. The toggle is hidden in Settings on Linux
        // (IsLiquidGlassSupported); this gate also covers a settings file that
        // already carries LiquidGlassEnabled=true.
        if (OperatingSystem.IsLinux()) on = false;
        _liquidGlassActive = on;

        if (_liquidGlassOverlay != null)
        {
            Resources.MergedDictionaries.Remove(_liquidGlassOverlay);
            _liquidGlassOverlay = null;
        }

        var acrylic = this.FindControl<ExperimentalAcrylicBorder>("LiquidGlassAcrylic");

        if (!on)
        {
            ClearValue(TransparencyLevelHintProperty);
            if (acrylic != null) acrylic.IsVisible = false;
            return;
        }

        // Resolve the active theme's surface colors from Application-level resources
        // (the window-scoped glass overlay never shadows those), so every theme —
        // built-in or custom — keeps its own tint behind the glass.
        var main = ResolveThemeColor("AppMainBackground", Color.Parse("#252525"));
        var sidebar = ResolveThemeColor("AppSidebarBackground", Color.Parse("#141414"));
        var accent = ResolveThemeColor("AccentColorBrush", Color.Parse("#E74856"));

        TransparencyLevelHint = new[]
        {
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Mica,
            WindowTransparencyLevel.Blur,
            WindowTransparencyLevel.None,
        };

        if (acrylic != null)
        {
            // Fresh material instance: assigning the Material property is guaranteed
            // to invalidate, and the tint follows the active theme's surface color.
            acrylic.Material = new ExperimentalAcrylicMaterial
            {
                BackgroundSource = AcrylicBackgroundSource.Digger,
                TintColor = main,
                TintOpacity = 0.65,
                MaterialOpacity = 0.35,
                FallbackColor = main,
            };
            acrylic.IsVisible = true;
        }

        // Translucent surface variants. The acrylic tint underneath carries most of
        // the readability: in the content area the window, content-grid and page
        // layers stack (≈73% net), the sidebar pill lands at ≈71% — text always sits
        // on a solid-enough frosted surface.
        _liquidGlassOverlay = new ResourceDictionary
        {
            ["AppMainBackground"] = new SolidColorBrush(main, 0.35),
            ["AppSidebarBackground"] = new SolidColorBrush(sidebar, 0.55),

            // Accent-filled action buttons (accent-btn / accent-pill: Settings Close,
            // Save, Confirm, Create, Play All, the queue pills) frost along with the
            // surfaces they sit on, instead of staying the one opaque slab on a glass
            // panel. 0.55 keeps enough accent for AccentForegroundBrush to stay legible
            // against whatever the acrylic pulls through. AccentBorderBrush is consumed
            // only by these buttons, so re-pointing it here adds the glass edge without
            // touching anything else.
            ["AccentButtonBackground"] = new SolidColorBrush(accent, 0.55),
            ["AccentBorderBrush"] = new SolidColorBrush(Colors.White, 0.35),
        };
        Resources.MergedDictionaries.Add(_liquidGlassOverlay);
    }

    /// <summary>Reads a theme surface color from Application resources for the active
    /// theme variant; theme overlays (built-in and custom) win over the base palette.</summary>
    private static Color ResolveThemeColor(string key, Color fallback)
    {
        if (Avalonia.Application.Current is { } app
            && app.TryGetResource(key, app.ActualThemeVariant, out var value)
            && value is ISolidColorBrush brush)
            return brush.Color;
        return fallback;
    }

    private async Task InitializeOnLoadedAsync()
    {
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Wire up theme switching
                _themeChangedHandler = (_, themeKey) =>
                {
                    if (Avalonia.Application.Current is App app)
                        app.SetTheme(themeKey);
                    // Liquid Glass derives its tints from the theme's surface colors,
                    // so a theme switch while glass is on re-resolves them.
                    if (_liquidGlassActive)
                        ApplyLiquidGlass(true);
                };
                vm.Settings.ThemeChanged += _themeChangedHandler;

                _accentChangedHandler = (_, hex) =>
                {
                    if (Avalonia.Application.Current is App app)
                        app.SetAccent(hex);
                    // The frosted button fill is derived from the accent, so it has to be
                    // re-derived here too — same reason as the theme handler above.
                    if (_liquidGlassActive)
                        ApplyLiquidGlass(true);
                };
                vm.Settings.AccentChanged += _accentChangedHandler;

                _liquidGlassChangedHandler = (_, on) => ApplyLiquidGlass(on);
                vm.Settings.LiquidGlassChanged += _liquidGlassChangedHandler;

                // The 'System' theme tile resolved the OS light/dark mode once and
                // never tracked later switches. The VM no-ops unless System is the
                // active theme; Post guards against a non-UI-thread raise (SetTheme
                // touches Application.Resources, which is UI-thread-only).
                if (PlatformSettings is { } platformSettings)
                {
                    _platformColorsChangedHandler = (_, _) =>
                        Dispatcher.UIThread.Post(() => vm.Settings.NotifySystemColorsChanged());
                    platformSettings.ColorValuesChanged += _platformColorsChangedHandler;
                }

                // Load settings first so window placement is restored before the
                // rest of init runs (avoids a visible resize jump on startup).
                Services.StartupTrace.Mark("window-loaded-handler");
                await vm.Settings.LoadAsync();
                Services.StartupTrace.Mark("settings-loaded");
                RestoreWindowPlacement(vm.Settings.GetSettings());

                // Control-surface wiring runs BEFORE the library load. None of it needs
                // the library — only vm.Player and the window handle — and it used to sit
                // after InitializeAsync(), so for the whole of a large library's load
                // there was no tray icon, no Windows media-flyout entry, and dead
                // hardware media keys (on Linux the MPRIS bus name wasn't even claimed,
                // so playerctl reported no player at all). With queue-restore on, the
                // user could see a track sitting in the playbar while the media keys
                // did nothing.
                _sidebarWrapper = this.FindControl<Border>("SidebarWrapper");
                _lyricsPanelWrapper = this.FindControl<Border>("LyricsPanelWrapper");
                _contentDockPanel = this.FindControl<DockPanel>("ContentDockPanel");
                _rootPanel = this.FindControl<DockPanel>("RootPanel");
                _settingsOverlay = this.FindControl<Border>("SettingsOverlay");
                _settingsCard = this.FindControl<Border>("SettingsCard");
                _queuePopupPanel = this.FindControl<Border>("QueuePopupPanel");

                InitializeQueuePopupBinding(vm);
                InitializeTaskbarButtons(vm);
                InitializeTrayIcon(vm);
                _smtc = new SmtcService(vm.Player, TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
                _mpris = MprisService.TryStart(vm.Player);
                Services.StartupTrace.Mark("tray-smtc-mpris-ready");

                // Launched at login with "start minimized to tray" on (encoded in the
                // autostart args, so it needs no async settings load). App already
                // minimized the window before it was realized; drop it out of the
                // taskbar now that the tray icon exists to get it back. Guarded on
                // _trayIcon != null so a platform where the tray failed to initialize
                // never leaves the app running with no window AND no tray icon.
                if (App.StartMinimizedAtLogin && _trayIcon != null)
                {
                    Hide();
                }

                await vm.InitializeAsync();
                Services.StartupTrace.Mark("initialize-async-done");
                Services.StartupTrace.Flush();

                // Wire up albums view-mode toggle visuals
                _topBarPropertyChangedHandler = (_, e) =>
                {
                    if (e.PropertyName is nameof(TopBarViewModel.IsCoverFlowMode) or nameof(TopBarViewModel.IsCollageMode))
                        UpdateViewModeToggleVisuals(vm.TopBar.IsCoverFlowMode, vm.TopBar.IsCollageMode);
                };
                vm.TopBar.PropertyChanged += _topBarPropertyChangedHandler;
                UpdateViewModeToggleVisuals(vm.TopBar.IsCoverFlowMode, vm.TopBar.IsCollageMode);

                // Queue row position numbers: rows are virtualized and recycled, so
                // there is no per-item index to bind — stamp the 1-based position when
                // a container is prepared and re-stamp the visible rows whenever the
                // queue mutates (reorder / remove / insert).
                var queueList = this.FindControl<ListBox>("QueuePopupListBox");
                if (queueList != null)
                {
                    queueList.ContainerPrepared += (_, e) => SetQueueRowNumber(e.Container, e.Index);
                    vm.Player.UpNext.CollectionChanged += (_, _) =>
                        Dispatcher.UIThread.Post(() => RenumberQueueRows(queueList),
                            DispatcherPriority.Loaded);
                }
                _mainVmPropertyChangedHandler = (s, e) =>
                {
                    var mainVm2 = (MainWindowViewModel)s!;
                    if (e.PropertyName == nameof(MainWindowViewModel.IsLyricsPanelOpen))
                    {
                        if (_lyricsPanelWrapper != null)
                        {
                            if (mainVm2.IsLyricsPanelOpen)
                            {
                                _lyricsPanelWrapper.IsVisible = true;
                                _lyricsPanelWrapper.Width = 356;
                            }
                            else
                            {
                                // Slide shut, then drop the subtree out of layout/render —
                                // a hidden-but-visible panel re-laid-out its word cells on
                                // every lyrics load (the track-start UI stall).
                                _lyricsPanelWrapper.Width = 0;
                                Avalonia.Threading.DispatcherTimer.RunOnce(() =>
                                {
                                    if (_lyricsPanelWrapper != null &&
                                        DataContext is MainWindowViewModel m && !m.IsLyricsPanelOpen)
                                        _lyricsPanelWrapper.IsVisible = false;
                                }, TimeSpan.FromMilliseconds(240));
                            }
                        }
                    }
                    if (e.PropertyName == nameof(MainWindowViewModel.IsLyricsViewActive))
                    {
                        if (_contentDockPanel != null)
                        {
                            var lyricsActive = mainVm2.IsLyricsViewActive;
                            Grid.SetRow(_contentDockPanel, lyricsActive ? 0 : 1);
                            Grid.SetRowSpan(_contentDockPanel, lyricsActive ? 2 : 1);
                        }
                        // Fullscreen lyrics hide the sidebar; leaving the page restores
                        // it even windowed, since nothing else would bring it back.
                        UpdateImmersiveLyricsState();
                    }
                    if (e.PropertyName == nameof(MainWindowViewModel.IsSettingsModalOpen))
                    {
                        if (mainVm2.IsSettingsModalOpen)
                            EnsureSettingsViewLoaded();

                        if (_settingsOverlay != null && _settingsCard != null)
                        {
                            if (mainVm2.IsSettingsModalOpen)
                            {
                                if (mainVm2.SkipNextSettingsOpenAnimation)
                                {
                                    // Stats back-arrow reopen: the page underneath was just
                                    // swapped back, so the dark backdrop must reach full
                                    // strength in the same frame (a backdrop fade would flash
                                    // the restored section undimmed). The card itself still
                                    // plays the normal fade/scale entrance on top of it.
                                    mainVm2.SkipNextSettingsOpenAnimation = false;
                                    var overlayTransitions = _settingsOverlay.Transitions;
                                    var cardTransitions = _settingsCard.Transitions;
                                    _settingsOverlay.Transitions = null;
                                    _settingsCard.Transitions = null;
                                    _settingsOverlay.Opacity = 1;
                                    _settingsCard.Opacity = 0;
                                    _settingsCard.RenderTransform =
                                        Avalonia.Media.Transformation.TransformOperations.Parse("scale(0.96)");
                                    _settingsOverlay.IsVisible = true;
                                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                    {
                                        _settingsOverlay.Transitions = overlayTransitions;
                                        _settingsCard.Transitions = cardTransitions;
                                        _settingsCard.Opacity = 1;
                                        _settingsCard.RenderTransform =
                                            Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)");
                                    }, Avalonia.Threading.DispatcherPriority.Render);
                                }
                                else
                                {
                                    // Backdrop fades in while the card scales up; the settle
                                    // happens on the next frame so the transitions animate it.
                                    _settingsOverlay.IsVisible = true;
                                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                    {
                                        _settingsOverlay.Opacity = 1;
                                        _settingsCard.RenderTransform =
                                            Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)");
                                    }, Avalonia.Threading.DispatcherPriority.Render);
                                }
                            }
                            else
                            {
                                // Mirror of the open animation, then drop the overlay out
                                // of the tree once the 180ms transitions have played.
                                _settingsOverlay.Opacity = 0;
                                _settingsCard.RenderTransform =
                                    Avalonia.Media.Transformation.TransformOperations.Parse("scale(0.96)");
                                Avalonia.Threading.DispatcherTimer.RunOnce(() =>
                                {
                                    if (_settingsOverlay != null &&
                                        DataContext is MainWindowViewModel m && !m.IsSettingsModalOpen)
                                        _settingsOverlay.IsVisible = false;
                                }, TimeSpan.FromMilliseconds(200));
                            }
                        }
                    }
                    if (e.PropertyName == nameof(MainWindowViewModel.IsSidebarHidden))
                    {
                        if (_sidebarWrapper != null)
                        {
                            _sidebarWrapper.Width = mainVm2.IsSidebarHidden ? 0 : 60;
                            _sidebarWrapper.IsVisible = !mainVm2.IsSidebarHidden;
                        }
                        if (_rootPanel != null)
                        {
                            _rootPanel.Margin = new Avalonia.Thickness(mainVm2.IsSidebarHidden ? 0 : 76, 0, 0, 0);
                            if (_rootPanel.RenderTransform is TranslateTransform t)
                                t.X = 0;
                        }
                    }
                };
                vm.PropertyChanged += _mainVmPropertyChangedHandler;

                // Sidebar hover expand/collapse
                if (_sidebarWrapper != null)
                {
                    _sidebarWrapper.PropertyChanged += (_, e) =>
                    {
                        if (e.Property == Border.IsPointerOverProperty && !vm.IsSidebarHidden)
                        {
                            // Honor the "Hover to expand sidebar" preference: when disabled the
                            // rail stays icon-only and never expands (no slide animation).
                            var expanded = _sidebarWrapper.IsPointerOver
                                           && vm.Settings.SidebarHoverExpand;
                            _sidebarWrapper.Width = expanded ? 220 : 60;
                            if (_rootPanel?.RenderTransform is TranslateTransform translate)
                                translate.X = expanded ? 160 : 0;
                            vm.Sidebar.IsExpanded = expanded;
                        }
                    };
                }

            }
        }
    }

    /// <summary>
    /// Builds the Settings page the first time the modal opens. It is ~3,000 lines of
    /// XAML and used to be instantiated inline in MainWindow.axaml, which put all of that
    /// inside MainWindow's InitializeComponent — measured as the largest single block of
    /// the launch path, paid on every start whether or not the user opens Settings.
    /// Idempotent; cheap enough to call on every open.
    /// </summary>
    private void EnsureSettingsViewLoaded()
    {
        var host = this.FindControl<ContentControl>("SettingsViewHost");
        if (host is null || host.Content is not null) return;

        host.Content = new SettingsView { Background = Avalonia.Media.Brushes.Transparent };
    }

    private void WireWindowLevelHandlers()
    {
        // Close queue popup on outside click (tunnel so it fires before button commands)
        AddHandler(PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);

        // Space = play/pause has to beat whatever currently holds focus. Avalonia's
        // Button treats Space as its keyboard "click" and marks KeyDown handled, so the
        // bubbling handler below never saw the key once the user had clicked anything:
        // click a lyric line to seek and Space re-seeked to it, click the fullscreen
        // toggle and Space toggled fullscreen again. Tunnel it, same reasoning as the
        // queue-popup handler above.
        AddHandler(KeyDownEvent, OnGlobalPlayPauseKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnGlobalPlayPauseKeyUp, RoutingStrategies.Tunnel);

        // Volume control via mouse wheel and keyboard
        KeyDown += OnWindowKeyDown;

        // Drag-drop handlers are registered in OnLoaded (after visual tree is ready).

        Closing += OnMainWindowClosing;
        Closed += OnWindowClosed;

        // A second launch (taskbar/pinned icon while we sit in the tray) signals
        // the single-instance pipe — surface this window, and play any files
        // that launch was asked to open ("Open with Noctis" while running).
        _singleInstanceActivationHandler = files => Dispatcher.UIThread.Post(() =>
        {
            ShowFromTray();
            if (files.Count > 0 && DataContext is MainWindowViewModel vm)
                vm.OpenExternalFiles(files);
        });
        Helpers.SingleInstanceGuard.ActivationRequested += _singleInstanceActivationHandler;

        // Minimize-to-tray: hide the window when it minimizes and the setting is on.
        // Every WindowState change also re-evaluates the fullscreen-lyrics sidebar
        // rule here — F11, Escape and WM-initiated transitions all funnel through
        // this one observer.
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty)
                return;
            UpdateImmersiveLyricsState();
            if (WindowState != WindowState.Minimized)
                return;
            if (_trayIcon != null
                && DataContext is MainWindowViewModel trayVm
                && trayVm.Settings.MinimizeToTray
                && _miniPlayer == null)
            {
                Hide();
            }
        };

        // If the main window goes down (OS shutdown, etc.) take the mini player with it
        // so it can't outlive the app shell as an orphaned topmost window.
        Closed += (_, _) =>
        {
            if (_miniPlayer is { } mini)
            {
                mini.Closed -= OnMiniPlayerClosed;
                _miniPlayer = null;
                mini.Close();
            }
        };
    }

    private void RestoreWindowPlacement(AppSettings settings)
    {
        var width = settings.WindowWidth;
        var height = settings.WindowHeight;
        if (double.IsFinite(width) && double.IsFinite(height)
            && width >= MinWidth && height >= MinHeight)
        {
            Width = width;
            Height = height;
        }

        if (double.IsFinite(settings.WindowX) && double.IsFinite(settings.WindowY))
        {
            var restored = new PixelPoint(
                (int)Math.Round(settings.WindowX), (int)Math.Round(settings.WindowY));

            // Only restore a position that still lands on a connected screen. A window
            // last closed on a secondary monitor persists negative or large-offset
            // coordinates; with that monitor gone it was restored fully off-screen, and
            // because close-to-tray keeps the process alive the user had no way back
            // short of editing settings.json.
            if (IsPositionOnAScreen(restored, width, height))
                Position = restored;
            else
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        // Don't undo the pre-realize minimize when the app was launched to start hidden:
        // restoring the saved state here would flash the window open for the rest of
        // startup, which is the thing that minimize was for.
        if (!App.StartMinimizedAtLogin
            && Enum.TryParse<WindowState>(settings.MainWindowState, out var savedState))
        {
            WindowState = savedState is WindowState.Minimized or WindowState.FullScreen
                ? WindowState.Normal
                : savedState;
        }
    }

    /// <summary>
    /// True when a meaningful part of the restored window — specifically its title-bar
    /// strip, the part the user needs to drag it back — overlaps a connected screen.
    /// </summary>
    private bool IsPositionOnAScreen(PixelPoint position, double width, double height)
    {
        try
        {
            var all = Screens?.All;
            if (all == null || all.Count == 0) return true; // can't tell — don't fight it

            var w = (int)Math.Max(1, double.IsFinite(width) ? width : MinWidth);
            var h = (int)Math.Max(1, double.IsFinite(height) ? height : MinHeight);
            // Title-bar strip rather than the whole window: a window whose body spills
            // off the edge is fine, one whose title bar is gone is not.
            var titleBar = new PixelRect(position.X, position.Y, w, Math.Min(h, 48));

            foreach (var screen in all)
                if (screen.Bounds.Intersects(titleBar))
                    return true;

            return false;
        }
        catch
        {
            return true;
        }
    }

    // ── System tray ──

    private void InitializeTrayIcon(MainWindowViewModel vm)
    {
        if (_trayIcon != null) return;

        try
        {
            var iconUri = new Uri("avares://Noctis/Assets/Icons/Noctis.ico");
            var icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(iconUri));

            var menu = new NativeMenu();

            var open = new NativeMenuItem("Open Noctis");
            open.Click += (_, _) => ShowFromTray();
            menu.Items.Add(open);

            menu.Items.Add(new NativeMenuItemSeparator());

            // Basic playback control without leaving the tray.
            var playPause = new NativeMenuItem("Play / Pause");
            playPause.Click += (_, _) => vm.Player.PlayPauseCommand.Execute(null);
            menu.Items.Add(playPause);

            var next = new NativeMenuItem("Next Track");
            next.Click += (_, _) => vm.Player.NextCommand.Execute(null);
            menu.Items.Add(next);

            var previous = new NativeMenuItem("Previous Track");
            previous.Click += (_, _) => vm.Player.PreviousCommand.Execute(null);
            menu.Items.Add(previous);

            menu.Items.Add(new NativeMenuItemSeparator());

            var quit = new NativeMenuItem("Quit");
            quit.Click += (_, _) =>
            {
                _exitRequestedFromTray = true;
                Close();
            };
            menu.Items.Add(quit);

            _trayIcon = new TrayIcon
            {
                Icon = icon,
                ToolTipText = "Noctis",
                Menu = menu,
                IsVisible = true,
            };
            _trayIcon.Clicked += (_, _) => ShowFromTray();
            TrayIcon.SetIcons(Application.Current!, new TrayIcons { _trayIcon });

            // Keep the tooltip and the play/pause label current. Both were set once and
            // never updated, so hovering the tray icon of a player deliberately running
            // headless told the user nothing about what was playing, and the menu item
            // never showed which action it would perform.
            _trayStateHandler = (_, e) =>
            {
                if (e.PropertyName is not (nameof(PlayerViewModel.CurrentTrack)
                    or nameof(PlayerViewModel.State))) return;

                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        var track = vm.Player.CurrentTrack;
                        if (_trayIcon != null)
                        {
                            _trayIcon.ToolTipText = track == null
                                ? "Noctis"
                                : Truncate($"{track.Title} — {track.Artist}", 120);
                        }
                        playPause.Header = vm.Player.State == PlaybackState.Playing ? "Pause" : "Play";
                    }
                    catch { /* tray backends vary; never let this bubble */ }
                });
            };
            vm.Player.PropertyChanged += _trayStateHandler;
        }
        catch (Exception ex)
        {
            // Tray support is best-effort (e.g. some Linux DEs have no tray).
            DebugLogger.Error(DebugLogger.Category.UI, "TrayIcon.Init", ex.Message);
        }
    }

    private System.ComponentModel.PropertyChangedEventHandler? _trayStateHandler;

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];

    private void ShowFromTray()
    {
        // Close the mini player first. ToggleMiniPlayer hides the main window when the
        // mini player opens, so surfacing the main window without closing it left the
        // user with both on screen — with a Topmost mini player floating over the app.
        // Its Closed handler restores and activates the main window, so returning here
        // is correct.
        if (_miniPlayer != null)
        {
            try { _miniPlayer.Close(); return; }
            catch { /* fall through and show the main window directly */ }
        }

        // A login launch starts minimized with no taskbar button (see App); the first
        // trip out of the tray is where it earns them back.
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Close-to-tray: intercept user-initiated closes only. OS shutdown and
        // explicit app shutdown (tray Exit) always pass through.
        if (!_exitRequestedFromTray
            && e.CloseReason == WindowCloseReason.WindowClosing
            && _trayIcon != null
            && _miniPlayer == null
            && DataContext is MainWindowViewModel vm
            && vm.Settings.CloseToTray)
        {
            e.Cancel = true;
            // Session boundary: the process may be killed later without a
            // graceful shutdown (OS shutdown while in tray), so snapshot the
            // queue now for next launch's restore.
            vm.Player.SaveQueueStateInBackground();
            Hide();
            return;
        }

        CaptureWindowPlacement();
    }

    private void CaptureWindowPlacement()
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var settings = vm.Settings.GetSettings();
        if (WindowState == WindowState.Normal)
        {
            settings.WindowWidth = Math.Max(MinWidth, Bounds.Width);
            settings.WindowHeight = Math.Max(MinHeight, Bounds.Height);
            settings.WindowX = Position.X;
            settings.WindowY = Position.Y;
        }

        // Never restore into Minimized; fullscreen (F11) is a transient view state,
        // so persist the state it would restore to instead.
        settings.MainWindowState = WindowState switch
        {
            WindowState.Minimized => WindowState.Normal.ToString(),
            WindowState.FullScreen => _preFullScreenState.ToString(),
            _ => WindowState.ToString(),
        };

        // Snapshot the UI-bound collections here, on the UI thread, before handing the
        // save to a worker. SyncToSettings enumerates CustomThemes / MusicFolders /
        // FolderRules / EqBands — all ObservableCollections mutated from the UI thread —
        // so a concurrent add/remove during shutdown threw InvalidOperationException
        // inside SaveAsync's catch and silently dropped the final write, including the
        // window geometry this method exists to persist.
        vm.Settings.SnapshotCollectionsForSave();

        // Persist geometry in the background so window close isn't blocked on disk I/O.
        // Closing is cooperative — this fires before the window tears down, and the
        // write is atomic (temp file + Move) so a crash during shutdown is safe.
        // FlushPendingSaveAsync also cancels any in-flight debounce timer so the last
        // slider drag or keystroke isn't lost to a save that never fires.
        _ = Task.Run(async () =>
        {
            try { await vm.Settings.FlushPendingSaveAsync(); } catch { }
        });
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_singleInstanceActivationHandler != null)
        {
            Helpers.SingleInstanceGuard.ActivationRequested -= _singleInstanceActivationHandler;
            _singleInstanceActivationHandler = null;
        }

        _taskbar?.Dispose();
        _smtc?.Dispose();
        _smtc = null;
        _mpris?.Dispose();
        _mpris = null;
        if (_trayIcon != null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        if (_platformColorsChangedHandler != null && PlatformSettings is { } platformSettings)
            platformSettings.ColorValuesChanged -= _platformColorsChangedHandler;

        // Unsubscribe from all event handlers to prevent memory leak
        if (DataContext is MainWindowViewModel vm)
        {
            if (_themeChangedHandler != null)
                vm.Settings.ThemeChanged -= _themeChangedHandler;

            if (_accentChangedHandler != null)
                vm.Settings.AccentChanged -= _accentChangedHandler;

            if (_liquidGlassChangedHandler != null)
                vm.Settings.LiquidGlassChanged -= _liquidGlassChangedHandler;

            if (_playerPropertyChangedHandler != null)
                vm.Player.PropertyChanged -= _playerPropertyChangedHandler;

            if (_queuePopupStateHandler != null)
                vm.Player.PropertyChanged -= _queuePopupStateHandler;

            if (_trayStateHandler != null)
                vm.Player.PropertyChanged -= _trayStateHandler;

            if (_trackedFavoriteTrack != null && _currentTrackPropertyChangedHandler != null)
            {
                _trackedFavoriteTrack.PropertyChanged -= _currentTrackPropertyChangedHandler;
                _trackedFavoriteTrack = null;
            }

            if (_topBarPropertyChangedHandler != null)
                vm.TopBar.PropertyChanged -= _topBarPropertyChangedHandler;

            if (_mainVmPropertyChangedHandler != null)
                vm.PropertyChanged -= _mainVmPropertyChangedHandler;
        }
    }

    /// <summary>
    /// Subscribes the player-state handling that the *window* needs, independent of the
    /// Windows taskbar integration.
    ///
    /// This used to live entirely inside <see cref="InitializeTaskbarButtons"/>, which
    /// returns early off Windows and swallows its own exceptions. The queue popup is
    /// declared IsVisible="False" in XAML and is only ever shown from the
    /// IsQueuePopupOpen branch below — so on macOS and Linux (and on Windows whenever
    /// TryGetPlatformHandle returned null or the taskbar COM init threw) the Queue
    /// button, the Songs-page Queue action and Escape-to-close all silently did nothing,
    /// and the queue↔lyrics-panel mutual exclusion was dead too.
    /// </summary>
    private void InitializeQueuePopupBinding(MainWindowViewModel vm)
    {
        _queuePopupStateHandler = (_, e) =>
        {
            if (e.PropertyName != nameof(PlayerViewModel.IsQueuePopupOpen)) return;

            // Queue popup and lyrics panel share the right edge — mutual exclusion.
            if (vm.Player.IsQueuePopupOpen)
                vm.IsLyricsPanelOpen = false;
            AnimateSidePanel(_queuePopupPanel, vm.Player.IsQueuePopupOpen,
                () => DataContext is MainWindowViewModel m && !m.Player.IsQueuePopupOpen);
        };
        vm.Player.PropertyChanged += _queuePopupStateHandler;
    }

    private void InitializeTaskbarButtons(MainWindowViewModel vm)
    {
        if (!Helpers.PlatformHelper.IsWindows) return;

        try
        {
            var handle = TryGetPlatformHandle();
            if (handle == null) return;

            _taskbar = new TaskbarIntegrationService();
            _taskbar.Initialize(handle.Handle);

            // Wire button clicks to player commands (dispatched to UI thread)
            _taskbar.PreviousClicked += () =>
                Dispatcher.UIThread.Post(() => vm.Player.PreviousCommand.Execute(null));
            _taskbar.PlayPauseClicked += () =>
                Dispatcher.UIThread.Post(() => vm.Player.PlayPauseCommand.Execute(null));
            _taskbar.NextClicked += () =>
                Dispatcher.UIThread.Post(() => vm.Player.NextCommand.Execute(null));
            _taskbar.FavoriteClicked += () =>
                Dispatcher.UIThread.Post(() => vm.Player.ToggleCurrentTrackFavoriteCommand.Execute(null));

            // Tracks IsFavorite changes on the *current* track so we can swap the heart icon.
            _currentTrackPropertyChangedHandler = (_, e) =>
            {
                if (e.PropertyName == nameof(Track.IsFavorite))
                    _taskbar?.UpdateFavoriteState(vm.Player.CurrentTrack?.IsFavorite == true);
            };

            void RebindCurrentTrack()
            {
                if (_trackedFavoriteTrack != null && _currentTrackPropertyChangedHandler != null)
                    _trackedFavoriteTrack.PropertyChanged -= _currentTrackPropertyChangedHandler;

                _trackedFavoriteTrack = vm.Player.CurrentTrack;

                if (_trackedFavoriteTrack != null && _currentTrackPropertyChangedHandler != null)
                    _trackedFavoriteTrack.PropertyChanged += _currentTrackPropertyChangedHandler;

                _taskbar?.UpdateFavoriteState(_trackedFavoriteTrack?.IsFavorite == true);
            }

            // Update play/pause icon when playback state changes
            _playerPropertyChangedHandler = (_, e) =>
            {
                if (e.PropertyName == nameof(PlayerViewModel.State))
                {
                    _taskbar?.UpdatePlayPauseState(vm.Player.State == PlaybackState.Playing);
                }
                // IsQueuePopupOpen is handled by InitializeQueuePopupBinding, which runs
                // on every platform — it must not depend on the taskbar being available.
                else if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack))
                {
                    RebindCurrentTrack();
                }
            };
            vm.Player.PropertyChanged += _playerPropertyChangedHandler;

            // Seed initial state so icons reflect reality on first paint.
            RebindCurrentTrack();
        }
        catch
        {
            // Non-critical — taskbar buttons are a nice-to-have
        }
    }

    // The file-import drag-drop below uses Avalonia's pre-11.3 IDataObject/DataFormats
    // API. The newer DataTransfer API isn't adopted yet, so suppress the obsolete-usage
    // warnings for this self-contained region rather than rewriting working code.
#pragma warning disable CS0618 // Type or member is obsolete
    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        // Don't show import overlay for internal drags (album/track tiles dragged within the app)
        if (e.Data.Contains(Helpers.DragFileBehavior.InternalDragFormat))
            return;

        var paths = GetDroppedLocalPaths(e.Data);
        var hasImportable = paths.Any(IsImportablePath);
        e.DragEffects = hasImportable ? DragDropEffects.Copy : DragDropEffects.None;
        ShowDragOverlay(hasImportable);
        e.Handled = true;
    }

    private void OnWindowDragLeave(object? sender, DragEventArgs e)
    {
        ShowDragOverlay(false);
    }

    private async void OnWindowDrop(object? sender, DragEventArgs e)
    {
        // Ignore internal drags (album/track tiles dragged within the app)
        if (e.Data.Contains(Helpers.DragFileBehavior.InternalDragFormat))
            return;

        e.Handled = true;
        ShowDragOverlay(false);
        if (DataContext is not MainWindowViewModel vm) return;

        var paths = GetDroppedLocalPaths(e.Data);
        if (paths.Count == 0) return;

        try
        {
            await vm.ImportDroppedMediaAsync(paths);
        }
        catch (OperationCanceledException)
        {
            // Drop import was cancelled; no action needed.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Drop import failed: {ex.Message}");
        }
    }

    private void ShowDragOverlay(bool show)
    {
        var overlay = this.FindControl<Avalonia.Controls.Border>("DragDropOverlay");
        if (overlay == null) return;
        overlay.IsVisible = show;
        overlay.Opacity = show ? 1 : 0;
    }

    private static List<string> GetDroppedLocalPaths(IDataObject data)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Primary: Avalonia IStorageItem API (works for Explorer drops on most platforms).
            foreach (var item in data.GetFiles() ?? Enumerable.Empty<IStorageItem>())
            {
                try
                {
                    var uri = item.Path;
                    if (uri is { IsFile: true })
                        TryAddPath(uri.LocalPath);
                    else if (item.Name is { } name && !string.IsNullOrWhiteSpace(name))
                        TryAddPath(name);
                }
                catch
                {
                    // Skip items with inaccessible Path property.
                }
            }

            // Fallback: DataFormats.Files may contain IStorageItem or string collections.
            if (paths.Count == 0 && data.Contains(DataFormats.Files))
            {
                var raw = data.Get(DataFormats.Files);
                if (raw is IEnumerable<IStorageItem> storageItems)
                {
                    foreach (var si in storageItems)
                    {
                        try { TryAddPath(si.Path?.LocalPath); } catch { }
                    }
                }
                else if (raw is IEnumerable<string> stringPaths)
                {
                    foreach (var s in stringPaths)
                        TryAddPath(s);
                }
            }

            // Fallback: raw Text payload (some drag sources provide newline-separated paths).
            // Only accept lines that look like real file paths (drive letter or UNC prefix).
            if (paths.Count == 0 && data.Contains(DataFormats.Text))
            {
                var text = data.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    foreach (var line in text.Split('\n', '\r'))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length >= 2 &&
                            ((char.IsLetter(trimmed[0]) && trimmed[1] == ':') ||
                             trimmed.StartsWith(@"\\") ||
                             trimmed.StartsWith("/")))
                        {
                            TryAddPath(trimmed);
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore malformed drag payloads.
        }

        return paths.ToList();

        void TryAddPath(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return;
            var candidate = rawPath.Trim();

            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.IsFile)
                candidate = uri.LocalPath;

            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (!string.IsNullOrWhiteSpace(fullPath))
                    paths.Add(fullPath);
            }
            catch
            {
                // Ignore invalid path entries in drag payload.
            }
        }
    }

    private static bool IsImportablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Directory.Exists(path)) return true;
        if (!File.Exists(path)) return false;
        return MetadataService.SupportedExtensions.Contains(Path.GetExtension(path));
    }
#pragma warning restore CS0618 // Type or member is obsolete

    /// <summary>
    /// True between the Space press we consumed as play/pause and its release. Button
    /// raises Click on key *up*, so swallowing only the press still let the focused
    /// button fire on the way back up.
    /// </summary>
    private bool _spaceShortcutConsumed;

    private void OnGlobalPlayPauseKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None) return;
        if (DataContext is not MainWindowViewModel vm) return;

        // Typing a space in a search/edit box stays typing a space.
        if (e.Source is TextBox) return;

        vm.Player.PlayPauseCommand.Execute(null);
        _spaceShortcutConsumed = true;
        e.Handled = true;
    }

    private void OnGlobalPlayPauseKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !_spaceShortcutConsumed) return;
        _spaceShortcutConsumed = false;
        e.Handled = true;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        switch (e.Key)
        {
            case Key.Up when e.KeyModifiers == KeyModifiers.Control:
                vm.Player.Volume = Math.Min(100, vm.Player.Volume + 5);
                e.Handled = true;
                break;
            case Key.Down when e.KeyModifiers == KeyModifiers.Control:
                vm.Player.Volume = Math.Max(0, vm.Player.Volume - 5);
                e.Handled = true;
                break;
            case Key.Escape:
                // Close the topmost open surface, in z-order. Escape used to check only
                // the queue popup and otherwise unconditionally clear the search box —
                // so with the Settings modal or the lyrics side panel open it silently
                // wiped the user's search while the modal stayed up.
                if (WindowState == WindowState.FullScreen)
                {
                    // Fullscreen (F11) counts as the topmost surface — leave it first,
                    // browser-style, before closing any in-app overlay.
                    ToggleFullScreen();
                }
                else if (vm.IsSettingsModalOpen)
                {
                    vm.CloseSettingsCommand.Execute(null);
                }
                else if (vm.IsLyricsPanelOpen)
                {
                    vm.IsLyricsPanelOpen = false;
                }
                else if (vm.Player.IsQueuePopupOpen)
                {
                    vm.Player.IsQueuePopupOpen = false;
                }
                else
                {
                    vm.TopBar.ClearSearchCommand.Execute(null);
                }
                e.Handled = true;
                break;
            // Space is handled by OnGlobalPlayPauseKeyDown (tunneling) so a focused
            // button can't swallow it first.
            case Key.D when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                vm.ToggleDebugPanel();
                e.Handled = true;
                break;
            case Key.K when e.KeyModifiers == KeyModifiers.Control:
                _ = vm.OpenCommandPaletteAsync();
                e.Handled = true;
                break;
            case Key.F11:
                ToggleFullScreen();
                e.Handled = true;
                break;
        }
    }

    // ── Fullscreen toggle (issue #22) ──

    /// <summary>State to restore when leaving fullscreen, so a Maximized window comes
    /// back Maximized instead of Normal.</summary>
    private WindowState _preFullScreenState = WindowState.Normal;

    private void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _preFullScreenState;
        }
        else
        {
            _preFullScreenState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            WindowState = WindowState.FullScreen;
        }
    }

    /// <summary>
    /// Fullscreen lyrics own the whole screen: the sidebar hides while the window is
    /// FullScreen with the lyrics page up, and comes back the moment either condition
    /// ends — leaving fullscreen, or navigating off the page (windowed included, since
    /// nothing else would un-hide it). The same condition feeds the lyrics VM's
    /// fullscreen flag, which gates the opt-in focus dimming. Runs off both the
    /// WindowState observer and the IsLyricsViewActive handler so every path lands on
    /// the same answer.
    /// </summary>
    private void UpdateImmersiveLyricsState()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var immersive = WindowState == WindowState.FullScreen && vm.IsLyricsViewActive;
        if (vm.IsSidebarHidden != immersive)
            vm.IsSidebarHidden = immersive;
        if (vm.Lyrics.IsFullScreenPageActive != immersive)
            vm.Lyrics.IsFullScreenPageActive = immersive;
    }

    // ── Albums toggle visuals ──

    private void UpdateViewModeToggleVisuals(bool isCoverFlow, bool isCollage = false)
    {
        if (AlbumsLibraryModeBtn != null)
        {
            AlbumsLibraryModeBtn.Background = isCoverFlow ? InactiveToggleBg : ActiveToggleBg;
            AlbumsLibraryModeBtn.Opacity = isCoverFlow ? 0.5 : 1.0;
        }
        if (AlbumsUpNextModeBtn != null)
        {
            AlbumsUpNextModeBtn.Background = isCoverFlow ? ActiveToggleBg : InactiveToggleBg;
            AlbumsUpNextModeBtn.Opacity = isCoverFlow ? 1.0 : 0.5;
        }
        if (AlbumsCollageModeBtn != null)
        {
            AlbumsCollageModeBtn.Background = isCollage ? ActiveToggleBg : InactiveToggleBg;
            AlbumsCollageModeBtn.Opacity = isCollage ? 1.0 : 0.5;
        }
    }

    // ── Queue popup event handlers ──

    private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Mouse back/forward buttons drive in-app navigation (browser-style).
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsXButton1Pressed || props.IsXButton2Pressed)
        {
            if (DataContext is MainWindowViewModel navVm)
            {
                if (props.IsXButton1Pressed)
                    navVm.GoBackInHistoryCommand.Execute(null);
                else
                    navVm.GoForwardInHistoryCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }

        // Queue popup is now sticky — it only closes via the Queue toggle button or Escape.
        // Clicks elsewhere in the app (player controls, sidebar, content area) do not dismiss it.
    }

    private async void OnQueueClearClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // A queue curated over many sessions with Play Next / Add to Queue used to be
        // destroyed by one click on a small ghost button — and PlayTrack overwrites
        // queue.json immediately afterwards, so it couldn't be recovered from disk either.
        var count = vm.Player.UpNext.Count;
        if (count >= 5)
        {
            var confirmed = await Views.ConfirmationDialog.ShowAsync(
                $"Clear all {count} tracks from the queue? This cannot be undone.");
            if (!confirmed) return;
        }

        vm.Player.ClearQueue();
    }

    private void OnQueueItemDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not ListBox listBox) return;

        var index = listBox.SelectedIndex;
        if (index < 0) return;

        // Play the tapped track but keep the popup open, so the user can keep
        // browsing/queuing without it dismissing out from under them.
        vm.Player.PlayFromUpNextAt(index);
    }

    private void OnQueueRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not MenuItem menuItem) return;

        // The MenuItem's DataContext is the Track from the DataTemplate
        if (menuItem.DataContext is not Track track) return;
        var index = vm.Player.UpNext.IndexOf(track);
        if (index >= 0)
            vm.Player.RemoveFromQueue(index);
    }

    // ── Queue drag-to-reorder (pointer-tracked, Apple Music style) ──
    //
    // The dragged row is rendered as a floating preview (#QueueDragPreview) that follows
    // the pointer's Y position. The original ListBoxItem is hidden via Opacity=0 while the
    // drag is active so its slot in the list stays reserved (no surrounding shift).
    // On release we compute the target index and call Player.MoveInQueue.
    //
    // Notes:
    // - No DragDrop.DoDragDrop. All tracking is via PointerPressed/Moved/Released on the row Border.
    // - Pointer capture is taken only AFTER the user crosses the movement threshold, so single
    //   clicks and double-taps continue to work normally for selection/play.

    private const double QueueDragThreshold = 6.0;

    /// <summary>
    /// Open/close animation for the queue popup, mirroring the Settings modal:
    /// fade + slide/scale settle on open, the reverse on close, then the closed
    /// panel drops out of the tree so it stops participating in layout/render.
    /// <paramref name="stillClosed"/> re-checks the state when the close timer
    /// fires, so a quick re-open never hides an open panel.
    /// (The lyrics panel intentionally keeps its own width-slide animation.)
    /// </summary>
    private static void AnimateSidePanel(Border? panel, bool open, Func<bool> stillClosed)
    {
        if (panel == null) return;
        if (open)
        {
            // Show first; the settle runs on the next frame so the transitions animate it.
            panel.IsVisible = true;
            Dispatcher.UIThread.Post(() =>
            {
                panel.Opacity = 1;
                panel.RenderTransform =
                    Avalonia.Media.Transformation.TransformOperations.Parse("translateX(0px) scale(1)");
            }, DispatcherPriority.Render);
        }
        else
        {
            panel.Opacity = 0;
            panel.RenderTransform =
                Avalonia.Media.Transformation.TransformOperations.Parse("translateX(16px) scale(0.97)");
            DispatcherTimer.RunOnce(() =>
            {
                if (stillClosed())
                    panel.IsVisible = false;
            }, TimeSpan.FromMilliseconds(200));
        }
    }

    /// <summary>Stamps the 1-based queue position into a (possibly recycled) row container.</summary>
    private static void SetQueueRowNumber(Control container, int index)
    {
        // Deferred: the row's template may not be applied yet when the container is prepared.
        Dispatcher.UIThread.Post(() =>
        {
            var tb = container.GetVisualDescendants().OfType<TextBlock>()
                              .FirstOrDefault(t => t.Name == "QueueRowNumber");
            if (tb != null) tb.Text = (index + 1).ToString();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Re-stamps positions on the realized rows after the queue mutates.</summary>
    private static void RenumberQueueRows(ListBox listBox)
    {
        foreach (var container in listBox.GetRealizedContainers())
        {
            var i = listBox.IndexFromContainer(container);
            if (i < 0) continue;
            var tb = container.GetVisualDescendants().OfType<TextBlock>()
                              .FirstOrDefault(t => t.Name == "QueueRowNumber");
            if (tb != null) tb.Text = (i + 1).ToString();
        }
    }

    private Point _queueDragStartPos;
    private bool _queueDragActive;
    private Track? _queueDragTrack;
    private int _queueDragSourceIndex = -1;
    private double _queueDragRowOffsetY;
    private ListBoxItem? _queueDragHiddenItem;

    private void OnQueueItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control rowControl) return;
        if (rowControl.Tag is not Track track) return;
        if (!e.GetCurrentPoint(rowControl).Properties.IsLeftButtonPressed) return;
        if (DataContext is not MainWindowViewModel vm) return;

        _queueDragTrack = track;
        _queueDragSourceIndex = vm.Player.UpNext.IndexOf(track);
        _queueDragRowOffsetY = e.GetPosition(rowControl).Y;
        _queueDragStartPos = e.GetPosition(this);
        _queueDragActive = false;
    }

    private void OnPageSortByMenuItemPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is MenuItem item)
            item.IsSubMenuOpen = true;
    }

    private void OnQueueItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_queueDragTrack == null) return;
        if (sender is not Control rowControl) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var pos = e.GetPosition(this);
        if (!_queueDragActive)
        {
            if (Math.Abs(pos.X - _queueDragStartPos.X) < QueueDragThreshold &&
                Math.Abs(pos.Y - _queueDragStartPos.Y) < QueueDragThreshold)
                return;

            StartQueueDrag(rowControl, e);
        }

        UpdateQueueDragPreviewPosition(e);
        UpdateQueueDropIndicator(e);
    }

    private void OnQueueItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_queueDragActive)
        {
            CommitQueueDrop(e);
        }
        ResetQueueDragState();
        if (sender is Control rowControl)
            e.Pointer.Capture(null);
    }

    private void OnQueueItemPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Treat lost capture as a cancel — restore visuals without performing the move.
        ResetQueueDragState();
    }

    private void StartQueueDrag(Control rowControl, PointerEventArgs e)
    {
        _queueDragActive = true;

        // Capture the pointer so we keep receiving move/release events even if the cursor
        // leaves the row's hit area.
        e.Pointer.Capture(rowControl);

        // Populate the floating preview with the dragged track and show it.
        var preview = this.FindControl<Border>("QueueDragPreview");
        if (preview != null && _queueDragTrack != null)
        {
            preview.DataContext = _queueDragTrack;
            preview.IsVisible = true;
        }

        // Hide the original row container so its slot stays reserved without showing
        // a duplicate of the dragged track.
        var listBox = this.FindControl<ListBox>("QueuePopupListBox");
        if (listBox != null && _queueDragSourceIndex >= 0)
        {
            _queueDragHiddenItem = listBox.ContainerFromIndex(_queueDragSourceIndex) as ListBoxItem;
            if (_queueDragHiddenItem != null)
                _queueDragHiddenItem.Opacity = 0;
        }
    }

    private void UpdateQueueDragPreviewPosition(PointerEventArgs e)
    {
        var wrapper = this.FindControl<Grid>("QueueListWrapper");
        var preview = this.FindControl<Border>("QueueDragPreview");
        if (wrapper == null || preview == null) return;
        if (preview.RenderTransform is not TranslateTransform tt) return;

        // Track the same point inside the row that the user initially grabbed.
        var pointerInWrapper = e.GetPosition(wrapper).Y;
        tt.Y = pointerInWrapper - _queueDragRowOffsetY;
    }

    private void UpdateQueueDropIndicator(PointerEventArgs e)
    {
        var listBox = this.FindControl<ListBox>("QueuePopupListBox");
        var indicator = this.FindControl<Border>("QueueDropIndicator");
        var wrapper = this.FindControl<Grid>("QueueListWrapper");
        if (listBox == null || indicator == null || wrapper == null) return;

        var pointerInWrapper = e.GetPosition(wrapper);
        double? indicatorY = null;

        for (int i = 0; i < listBox.ItemCount; i++)
        {
            var container = listBox.ContainerFromIndex(i);
            if (container == null) continue;

            var itemPos = container.TranslatePoint(new Point(0, 0), wrapper);
            if (itemPos == null) continue;

            var top = itemPos.Value.Y;
            var bottom = top + container.Bounds.Height;
            var mid = (top + bottom) / 2;

            if (pointerInWrapper.Y >= top && pointerInWrapper.Y < mid)
            {
                indicatorY = top;
                break;
            }
            if (pointerInWrapper.Y >= mid && pointerInWrapper.Y < bottom)
            {
                indicatorY = bottom;
                break;
            }
        }

        if (indicatorY != null)
        {
            if (indicator.RenderTransform is TranslateTransform transform)
                transform.Y = indicatorY.Value;
            indicator.IsVisible = true;
        }
        else
        {
            indicator.IsVisible = false;
        }
    }

    private void CommitQueueDrop(PointerEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var listBox = this.FindControl<ListBox>("QueuePopupListBox");
        if (listBox == null) return;
        if (_queueDragTrack == null) return;

        // Re-resolve the source index from the tracked object at drop time. The
        // press-time index goes stale: a drag lasts long enough for a track transition
        // (UpNext.RemoveAt(0)) or a queued radio refill to shift everything, and the
        // trusted index then moved the wrong track — the bounds checks prevented a crash
        // but not the wrong move.
        var fromIndex = vm.Player.UpNext.IndexOf(_queueDragTrack);
        if (fromIndex < 0) return;

        var posInListBox = e.GetPosition(listBox);
        var toIndex = GetQueueDropTargetIndex(listBox, posInListBox);
        if (toIndex < 0) toIndex = vm.Player.UpNext.Count - 1;
        if (toIndex >= vm.Player.UpNext.Count) toIndex = vm.Player.UpNext.Count - 1;

        if (fromIndex != toIndex)
            vm.Player.MoveInQueue(fromIndex, toIndex);
    }

    private void ResetQueueDragState()
    {
        var preview = this.FindControl<Border>("QueueDragPreview");
        if (preview != null)
        {
            preview.IsVisible = false;
            preview.DataContext = null;
        }

        if (_queueDragHiddenItem != null)
        {
            _queueDragHiddenItem.Opacity = 1.0;
            _queueDragHiddenItem = null;
        }

        var indicator = this.FindControl<Border>("QueueDropIndicator");
        if (indicator != null)
            indicator.IsVisible = false;

        _queueDragActive = false;
        _queueDragTrack = null;
        _queueDragSourceIndex = -1;
    }

    private static int GetQueueDropTargetIndex(ListBox listBox, Point posInListBox)
    {
        for (int i = 0; i < listBox.ItemCount; i++)
        {
            var container = listBox.ContainerFromIndex(i);
            if (container == null) continue;

            var itemPos = container.TranslatePoint(new Point(0, 0), listBox);
            if (itemPos == null) continue;

            var top = itemPos.Value.Y;
            var bottom = top + container.Bounds.Height;
            var midpoint = top + container.Bounds.Height / 2;

            if (posInListBox.Y < midpoint && posInListBox.Y >= top)
                return i;
            if (posInListBox.Y >= midpoint && posInListBox.Y < bottom)
                return i;
        }
        return listBox.ItemCount - 1;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Register drag-drop for file import on both the Window and root Panel.
        // AllowDrop must be set on the actual hit-test target, not just the Window.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnWindowDrop, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnWindowDragLeave, RoutingStrategies.Tunnel, handledEventsToo: true);

        var rootPanel = this.FindControl<Panel>("RootPanel")?.Parent as Panel;
        if (rootPanel != null)
        {
            DragDrop.SetAllowDrop(rootPanel, true);
            rootPanel.AddHandler(DragDrop.DragOverEvent, OnWindowDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
            rootPanel.AddHandler(DragDrop.DropEvent, OnWindowDrop, RoutingStrategies.Bubble, handledEventsToo: true);
            rootPanel.AddHandler(DragDrop.DragLeaveEvent, OnWindowDragLeave, RoutingStrategies.Bubble, handledEventsToo: true);
        }

    }

    // Backdrop click closes the Settings modal; clicks inside the card are swallowed.
    private void OnSettingsBackdropTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.IsSettingsModalOpen = false;
    }

    private void OnSettingsCardTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
    }
}
