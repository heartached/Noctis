using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using Avalonia.Controls.ApplicationLifetimes;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class MainWindow : Window, IPageKeyOverlayHost
{

    private TaskbarIntegrationService? _taskbar;
    private SmtcService? _smtc;
    private MprisService? _mpris;
    private LinuxResumeWatcher? _resumeWatcher;
    private LinuxTrayHost? _trayHost;

    /// <summary>
    /// Linux, XWayland on NVIDIA: after a suspend the window came back see-through until the
    /// app was restarted (Mistery, Discord 2026-09-22; see <see cref="LinuxResumeWatcher"/>).
    /// Each visible window gets a real size change and is put back: a size change gives a
    /// redirected window a new backing pixmap in the X server, XWayland drops its window
    /// buffers with it, and Avalonia rebuilds its render layer and redraws everything (an
    /// expose only redraws what it thinks is dirty). Normal windows grow 1px; maximized
    /// ones are restored and re-maximized, because KWin refuses client resizes of maximized
    /// windows. See <see cref="LinuxResumeWatcher.ChooseWindowRefresh"/> for what is skipped.
    /// The 1.5.3 Hide() + Show() remap is gone: Window.Hide() hides the owner's dialogs and
    /// completes their ShowDialog as if cancelled, and the loop then skipped them, hidden.
    /// </summary>
    private void RefreshWindowsAfterResume(int pass)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        foreach (var window in new List<Window>(desktop.Windows))
        {
            var name = window.GetType().Name;
            var refresh = LinuxResumeWatcher.ChooseWindowRefresh(window.IsVisible, window.WindowState, window.SizeToContent);
            try
            {
                switch (refresh)
                {
                    case LinuxResumeWatcher.WindowRefresh.NudgeSize:
                        NudgeWindowSizeAfterResume(window, name, pass);
                        break;
                    case LinuxResumeWatcher.WindowRefresh.Remaximize:
                        window.WindowState = WindowState.Normal;
                        DispatcherTimer.RunOnce(() =>
                        {
                            try
                            {
                                if (window.WindowState == WindowState.Normal)
                                    window.WindowState = WindowState.Maximized;
                                LinuxResumeWatcher.Log("ResumeWatch.Refreshed", $"pass {pass}: {name} restored and re-maximized");
                            }
                            catch (Exception ex)
                            {
                                LinuxResumeWatcher.Warn("ResumeWatch.Refresh", $"pass {pass}: {name}: {ex.Message}");
                            }
                        }, LinuxResumeWatcher.RefreshHold);
                        break;
                    default:
                        LinuxResumeWatcher.Log("ResumeWatch.Skipped", $"pass {pass}: {name} ({refresh})");
                        break;
                }
            }
            catch (Exception ex)
            {
                LinuxResumeWatcher.Warn("ResumeWatch.Refresh", $"pass {pass}: {name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Grows the window by 1px and puts it back after <see cref="LinuxResumeWatcher.RefreshHold"/>.
    /// The put-back waits for the window manager's answer: X11Window.Resize drops a request
    /// equal to the size it last heard back, so an immediate restore would be lost.
    /// </summary>
    private static void NudgeWindowSizeAfterResume(Window window, string name, int pass)
    {
        var before = window.ClientSize;
        var height = window.Height;
        window.Height = before.Height + 1;
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                var grew = window.ClientSize.Height > before.Height;
                window.Height = double.IsNaN(height) ? before.Height : height;
                LinuxResumeWatcher.Log("ResumeWatch.Refreshed",
                    $"pass {pass}: {name} {before.Width:0}x{before.Height:0} " +
                    (grew ? "grew 1px and was put back" : "did not grow (window manager kept its size, e.g. tiled)"));
            }
            catch (Exception ex)
            {
                LinuxResumeWatcher.Warn("ResumeWatch.Refresh", $"pass {pass}: {name}: {ex.Message}");
            }
        }, LinuxResumeWatcher.RefreshHold);
    }
    private MacNowPlayingService? _macNowPlaying;
    private TrayIcon? _trayIcon;
    private bool _exitRequestedFromTray;
    private EventHandler<string>? _themeChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _artworkAccentPlayerHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _artworkAccentSettingsHandler;
    private string? _artworkAccentPath;

    /// <summary>
    /// "Accent follows album art": recolours the accent from the playing cover's vibrant
    /// colour (path-cached; first decode runs off the UI thread), and restores the user's
    /// own accent when the toggle is off, nothing is playing, or the cover is too grey.
    /// </summary>
    private void ApplyArtworkAccent()
    {
        if (DataContext is not MainWindowViewModel vm || Avalonia.Application.Current is not App app) return;
        var path = vm.Settings.AccentFollowsArtwork ? vm.Player.CurrentArtPath : null;
        _artworkAccentPath = path;
        if (path == null)
        {
            app.SetAccent(vm.Settings.ActiveAccentHex);
            return;
        }
        Task.Run(() => ShareCardRenderer.GetVibrantColorHex(path)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            var hex = Noctis.Helpers.ArtworkAccent.TameForAccent(t.Result);
            Dispatcher.UIThread.Post(() =>
            {
                // A later track may have won the race; only the newest path paints.
                if (!ReferenceEquals(_artworkAccentPath, path) || !vm.Settings.AccentFollowsArtwork) return;
                app.SetAccent(hex ?? vm.Settings.ActiveAccentHex);
            });
        });
    }

    private EventHandler<string>? _accentChangedHandler;
    private EventHandler<bool>? _liquidGlassChangedHandler;
    private EventHandler<bool>? _sidebarAlwaysExpandedHandler;
    private EventHandler<Avalonia.Platform.PlatformColorValues>? _platformColorsChangedHandler;
    private ResourceDictionary? _liquidGlassOverlay;
    private bool _liquidGlassActive;
    private System.ComponentModel.PropertyChangedEventHandler? _playerPropertyChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _queuePopupStateHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _mainVmPropertyChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _currentTrackPropertyChangedHandler;
    private Track? _trackedFavoriteTrack;
    private Border? _sidebarWrapper;
    private Border? _lyricsPanelWrapper;
    private DockPanel? _contentDockPanel;
    private DockPanel? _rootPanel;
    private Border? _settingsOverlay;
    private Border? _settingsScrim;
    private Controls.GlassPanel? _settingsGlass;
    private Border? _settingsCard;
    private Border? _queuePopupPanel;
    private MiniPlayerWindow? _miniPlayer;
    private Action<IReadOnlyList<string>>? _singleInstanceActivationHandler;
    private Action? _detachFileActivation;
    private Action? _detachReopenActivation;

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

        RestoreMiniPlayerPlacement(_miniPlayer, miniVm, vm.Settings.GetSettings());

        _miniPlayer.Show();
        Hide();
    }

    /// <summary>
    /// Restores the mini player's last size and position. Falls back to the compact bar
    /// at the top-right of the screen the main window is on — for a first open, and for a
    /// stored position that no longer lands on a connected screen (the mini player has no
    /// title bar and cannot be dragged back from off-screen).
    /// </summary>
    private void RestoreMiniPlayerPlacement(
        MiniPlayerWindow mini, MiniPlayerViewModel miniVm, AppSettings settings)
    {
        var (width, height) = MiniPlayerViewModel.CanonicalSize(MiniPlayerForm.Bar);

        // The form follows the size, so a restored size also restores the layout the user
        // left it in (card, tall, lyrics split, …), not just the dimensions.
        if (settings.MiniPlayerWidth is { } savedW && settings.MiniPlayerHeight is { } savedH
            && double.IsFinite(savedW) && double.IsFinite(savedH))
        {
            width = Math.Clamp(savedW, mini.MinWidth, mini.MaxWidth);
            height = Math.Clamp(savedH, mini.MinHeight, mini.MaxHeight);
        }

        mini.Width = width;
        mini.Height = height;
        miniVm.UpdateFromSize(width, height);

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var scale = screen?.Scaling ?? 1.0;

        if (settings.MiniPlayerX is { } savedX && settings.MiniPlayerY is { } savedY
            && double.IsFinite(savedX) && double.IsFinite(savedY))
        {
            var restored = new PixelPoint((int)Math.Round(savedX), (int)Math.Round(savedY));
            if (IsPositionOnAScreen(restored, width * scale, height * scale))
            {
                // Pull it fully inside the work area of the screen it lands on (#75): a
                // placement saved half off an edge (or under a taskbar) opens whole.
                var size = MiniPlayerPlacement.ToPixels(width, height, scale);
                if (Screens.ScreenFromBounds(new PixelRect(restored, size)) is { } landed)
                    restored = MiniPlayerPlacement.Clamp(restored,
                        MiniPlayerPlacement.ToPixels(width, height, landed.Scaling), landed.WorkingArea);
                mini.Position = restored;
                return;
            }
        }

        if (screen == null) return;

        var area = screen.WorkingArea;
        mini.Position = new PixelPoint(
            area.X + area.Width - (int)(width * scale) - (int)(24 * scale),
            area.Y + (int)(24 * scale));
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

        // Windows 10: Avalonia leaves the native caption white under the dark theme.
        // The handle exists from construction, so this lands before the first paint.
        Win10DarkTitleBar.Apply(this);
        ActualThemeVariantChanged += (_, _) => Win10DarkTitleBar.Apply(this);

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
            // Every GlassPanel (sidebar, Settings sheet, island) drops back to its plain fill.
            AppGlass.Clear();
            return;
        }

        // Resolve the active theme's surface colors from Application-level resources
        // (the window-scoped glass overlay never shadows those), so every theme —
        // built-in or custom — keeps its own tint behind the glass.
        var main = ResolveThemeColor("AppMainBackground", Color.Parse("#252525"));
        var sidebar = ResolveThemeColor("AppSidebarBackground", Color.Parse("#141414"));

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
        // layers stack (≈73% net) — text always sits on a solid-enough frosted surface.
        // AppSidebarBackground stays the theme's own brush: every right-click menu,
        // flyout and tooltip paints with it, and at 55% they were see-through with no
        // blur behind them (GitHub #81). The sidebar pill frosts via AppGlass instead.
        _liquidGlassOverlay = new ResourceDictionary
        {
            ["AppMainBackground"] = new SolidColorBrush(main, 0.35),
            // The window root paints AppWindowBackgroundBrush (a gradient on some themes);
            // while glass is on it goes translucent with the content surface.
            ["AppWindowBackgroundBrush"] = new SolidColorBrush(main, 0.35),
            // Accent action buttons deliberately keep their solid accent fill: frosting
            // them (2026-08-06) read as washed-out, muddy buttons and was reverted 09-07.
        };
        Resources.MergedDictionaries.Add(_liquidGlassOverlay);

        // In-app frost: GlassPanel hosts (sidebar, Settings sheet, playback island) blur the
        // app content beneath them. Dialog windows are left alone: an AcrylicBlur hint on a
        // borderless transparent window painted the whole owner black on Win32 (09-07).
        AppGlass.Set(true, main, sidebar);
    }

    /// <summary>Card fade/scale plus the glass underlay's own Fade/scale, always together so the
    /// frost and the content it carries are never out of step (see SettingsOverlay in the XAML).</summary>
    private void SetSettingsSheet(bool shown)
    {
        var scale = Avalonia.Media.Transformation.TransformOperations.Parse(shown ? "scale(1)" : "scale(0.96)");
        if (_settingsCard != null)
        {
            _settingsCard.Opacity = shown ? 1 : 0;
            _settingsCard.RenderTransform = scale;
        }
        if (_settingsGlass != null)
        {
            _settingsGlass.Fade = shown ? 1 : 0;
            _settingsGlass.RenderTransform = scale;
        }
    }

    /// <summary>Mirrors the Settings overlay's visibility/opacity onto the sibling scrim
    /// (see SettingsScrim in the XAML for why it is not the overlay's Background).</summary>
    private void SetSettingsScrim(bool? visible, double? opacity)
    {
        if (_settingsScrim == null) return;
        if (visible is { } v) _settingsScrim.IsVisible = v;
        if (opacity is { } o) _settingsScrim.Opacity = o;
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
                    // While the accent follows the cover, a picker change is stored but the
                    // cover keeps the screen; it shows once playback stops or the toggle goes off.
                    if (vm.Settings.AccentFollowsArtwork && vm.Player.CurrentArtPath != null)
                        return;
                    if (Avalonia.Application.Current is App app)
                        app.SetAccent(hex);
                };
                vm.Settings.AccentChanged += _accentChangedHandler;

                _artworkAccentPlayerHandler = (_, e) =>
                {
                    if (e.PropertyName == nameof(PlayerViewModel.CurrentArtPath))
                        ApplyArtworkAccent();
                };
                vm.Player.PropertyChanged += _artworkAccentPlayerHandler;
                _artworkAccentSettingsHandler = (_, e) =>
                {
                    if (e.PropertyName == nameof(SettingsViewModel.AccentFollowsArtwork))
                        ApplyArtworkAccent();
                };
                vm.Settings.PropertyChanged += _artworkAccentSettingsHandler;

                _liquidGlassChangedHandler = (_, on) => ApplyLiquidGlass(on);
                vm.Settings.LiquidGlassChanged += _liquidGlassChangedHandler;

                // The 'System' theme tile resolved the OS light/dark mode once and
                // never tracked later switches. The VM no-ops unless System is the
                // active theme; Post guards against a non-UI-thread raise (SetTheme
                // touches Application.Resources, which is UI-thread-only).
                if (this.GetPlatformSettings() is { } platformSettings)
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
                _settingsScrim = this.FindControl<Border>("SettingsScrim");
                _settingsGlass = this.FindControl<Controls.GlassPanel>("SettingsGlass");
                _settingsCard = this.FindControl<Border>("SettingsCard");
                _queuePopupPanel = this.FindControl<Border>("QueuePopupPanel");

                InitializeQueuePopupBinding(vm);
                InitializeTaskbarButtons(vm);
                InitializeTrayIcon(vm);
                _trayHost = LinuxTrayHost.TryStart();
                _smtc = new SmtcService(vm.Player, TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
                _mpris = MprisService.TryStart(vm.Player);
                _resumeWatcher = LinuxResumeWatcher.TryStart(pass => Dispatcher.UIThread.Post(() => RefreshWindowsAfterResume(pass)));
                _macNowPlaying = MacNowPlayingService.TryStart(vm.Player);
                InitializeMacMenuBar(vm);
                Services.StartupTrace.Mark("tray-smtc-mpris-ready");

                // Launched at login with "start minimized to tray" on (encoded in the
                // autostart args, so it needs no async settings load). App already
                // minimized the window and took it off the taskbar before it was
                // realized; settle it into the tray, or back onto the taskbar when there
                // is no tray to get it back from.
                if (App.StartMinimizedAtLogin)
                    _ = SettleStartMinimizedAsync();

                await vm.InitializeAsync();
                Services.StartupTrace.Mark("initialize-async-done");
                Services.StartupTrace.Flush();

                // View-mode corner icons bind to TopBar.IsCoverFlowMode directly (09-13);
                // nothing to toggle by name any more.

                // Queue row position numbers: rows are virtualized and recycled, so
                // there is no per-item index to bind — stamp the 1-based position when
                // a container is prepared and re-stamp the visible rows whenever the
                // queue mutates (reorder / remove / insert).
                var queueList = this.FindControl<ListBox>("QueuePopupListBox");
                if (queueList != null)
                {
                    // GitHub #85: the row selection is keyed by index, so it is re-mapped on
                    // every queue change and re-applied to recycled containers as they prepare.
                    var queueSelection = _queueSelection = new QueueRowSelection<Track>(vm.Player.UpNext);
                    queueList.ContainerPrepared += (_, e) =>
                    {
                        SetQueueRowNumber(e.Container, e.Index);
                        e.Container.Classes.Set(QueueSelectedClass, queueSelection.Contains(e.Index));
                    };
                    var queueRowsSyncPending = false;
                    vm.Player.UpNext.CollectionChanged += (_, e) =>
                    {
                        queueSelection.Apply(e);
                        // One re-stamp per burst: a block remove / move raises an event per row (audit U03).
                        if (queueRowsSyncPending) return;
                        queueRowsSyncPending = true;
                        Dispatcher.UIThread.Post(() =>
                        {
                            queueRowsSyncPending = false;
                            RenumberQueueRows(queueList);
                            SyncQueueSelectionVisuals(queueList);
                        }, DispatcherPriority.Loaded);
                    };
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
                                EnsureLyricsPanelLoaded(mainVm2);
                                _lyricsPanelWrapper.IsVisible = true;
                                _lyricsPanelWrapper.Width = 356;
                                GetLyricsPanelView()?.SetShown(true);
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
                                    {
                                        _lyricsPanelWrapper.IsVisible = false;
                                        // Hiding does not detach the view, so un-register it
                                        // as a lyrics surface explicitly (parks the sync
                                        // timer, word clock and flowing backdrop).
                                        GetLyricsPanelView()?.SetShown(false);
                                    }
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
                                    var scrimTransitions = _settingsScrim?.Transitions;
                                    if (_settingsScrim != null) _settingsScrim.Transitions = null;
                                    SetSettingsScrim(visible: true, opacity: 1);
                                    _settingsOverlay.IsVisible = true;
                                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                    {
                                        if (_settingsScrim != null) _settingsScrim.Transitions = scrimTransitions;
                                        SetSettingsSheet(shown: true);
                                    }, Avalonia.Threading.DispatcherPriority.Render);
                                }
                                else
                                {
                                    // Backdrop fades in while the card scales up; the settle
                                    // happens on the next frame so the transitions animate it.
                                    _settingsOverlay.IsVisible = true;
                                    SetSettingsScrim(visible: true, opacity: null);
                                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                    {
                                        SetSettingsScrim(visible: null, opacity: 1);
                                        SetSettingsSheet(shown: true);
                                    }, Avalonia.Threading.DispatcherPriority.Render);
                                }
                            }
                            else
                            {
                                // Mirror of the open animation, then drop the overlay out
                                // of the tree once the 140ms transitions have played.
                                SetSettingsScrim(visible: null, opacity: 0);
                                SetSettingsSheet(shown: false);
                                Avalonia.Threading.DispatcherTimer.RunOnce(() =>
                                {
                                    if (_settingsOverlay != null &&
                                        DataContext is MainWindowViewModel m && !m.IsSettingsModalOpen)
                                    {
                                        _settingsOverlay.IsVisible = false;
                                        SetSettingsScrim(visible: false, opacity: null);
                                    }
                                }, TimeSpan.FromMilliseconds(150));
                            }
                        }
                    }
                    if (e.PropertyName == nameof(MainWindowViewModel.IsSidebarHidden))
                    {
                        // Un-hiding restores the pinned layout when "Keep sidebar expanded" is on.
                        var pinned = !mainVm2.IsSidebarHidden && mainVm2.Settings.SidebarAlwaysExpanded;
                        if (_sidebarWrapper != null)
                        {
                            _sidebarWrapper.Width = mainVm2.IsSidebarHidden ? 0 : (pinned ? 220 : 60);
                            _sidebarWrapper.IsVisible = !mainVm2.IsSidebarHidden;
                        }
                        if (_rootPanel != null)
                        {
                            _rootPanel.Margin = new Avalonia.Thickness(
                                mainVm2.IsSidebarHidden ? 0 : (pinned ? 236 : 76), 0, 0, 0);
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
                        if (e.Property == Border.IsPointerOverProperty && !vm.IsSidebarHidden
                            && !vm.Settings.SidebarAlwaysExpanded)
                        {
                            // Honor the "Hover to expand sidebar" preference: when disabled the
                            // rail stays icon-only and never expands (no slide animation).
                            // While "Keep sidebar expanded" is on the pin handler owns the
                            // layout and hovering must not touch it.
                            var expanded = _sidebarWrapper.IsPointerOver
                                           && vm.Settings.SidebarHoverExpand;
                            _sidebarWrapper.Width = expanded ? 220 : 60;
                            if (_rootPanel?.RenderTransform is TranslateTransform translate)
                                translate.X = expanded ? 160 : 0;
                            vm.Sidebar.IsExpanded = expanded;
                        }
                    };

                    // Pin/unpin immediately when the setting flips (and on startup once the
                    // persisted value lands) — no pointer event will fire to do it for us.
                    // Unlike the transient hover slide (a translate that pushes the right
                    // edge off-screen), pinning reflows the content into the remaining
                    // width via a real left margin so nothing gets cut off.
                    _sidebarAlwaysExpandedHandler = (_, pinned) =>
                    {
                        if (_sidebarWrapper == null || vm.IsSidebarHidden) return;
                        var expanded = pinned
                                       || (_sidebarWrapper.IsPointerOver
                                           && vm.Settings.SidebarHoverExpand);
                        _sidebarWrapper.Width = expanded ? 220 : 60;
                        if (_rootPanel != null)
                        {
                            _rootPanel.Margin = new Avalonia.Thickness(pinned ? 236 : 76, 0, 0, 0);
                            if (_rootPanel.RenderTransform is TranslateTransform translate)
                                translate.X = !pinned && expanded ? 160 : 0;
                        }
                        vm.Sidebar.IsExpanded = expanded;
                    };
                    vm.Settings.SidebarAlwaysExpandedChanged += _sidebarAlwaysExpandedHandler;
                    // The settings load may have finished before this subscription existed,
                    // so apply the current value once now (idempotent).
                    _sidebarAlwaysExpandedHandler(vm.Settings, vm.Settings.SidebarAlwaysExpanded);
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
    /// <summary>
    /// Builds the lyrics side panel the first time it opens. Same reasoning as
    /// <see cref="EnsureSettingsViewLoaded"/>: the view was instantiated inline in
    /// MainWindow.axaml, so its whole karaoke layout was templated inside
    /// InitializeComponent on every launch although the panel starts closed.
    /// Idempotent.
    /// </summary>
    internal void EnsureLyricsPanelLoaded(MainWindowViewModel vm)
    {
        var host = this.FindControl<ContentControl>("LyricsPanelHost");
        if (host is null || host.Content is not null) return;

        host.Content = new LyricsPanelView { DataContext = vm.Lyrics };
    }

    private LyricsPanelView? GetLyricsPanelView() =>
        this.FindControl<ContentControl>("LyricsPanelHost")?.Content as LyricsPanelView;

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
        // Every rebindable shortcut goes through the same tunnel pair, resolved against
        // ShortcutService so Settings › Shortcuts can change any of them at runtime.
        AddHandler(KeyDownEvent, OnGlobalShortcutKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnGlobalShortcutKeyUp, RoutingStrategies.Tunnel);
        // Queue-row keys (GitHub #85). Tunnel at the window. A page's WindowKeyForwarder is
        // added later and so runs first (newest first); it stands aside through
        // IsOverlayCapturingKeys, so Ctrl+A / Escape inside the queue don't hit the page.
        AddHandler(KeyDownEvent, OnQueueKeyDown, RoutingStrategies.Tunnel);

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

        // macOS delivers "Open With Noctis", Finder double-clicks and Dock-icon drops as
        // an open-documents event instead (see FileActivation) — at a cold launch too,
        // once the run loop starts, which is before InitializeAsync has restored the queue.
        _detachFileActivation = Helpers.FileActivation.Subscribe(
            Application.Current?.TryGetFeature<IActivatableLifetime>(),
            files => Dispatcher.UIThread.Post(() =>
            {
                ShowFromTray();
                if (DataContext is MainWindowViewModel vm)
                    vm.OpenExternalFilesWhenReady(files);
            }));

        // macOS: a Dock-icon click or relaunch only sends the running app a reopen, so a
        // window hidden into the menu-bar tray (close/minimize to tray, start minimized)
        // had no way back but the status item. With the mini player up there is a visible
        // window, and AppKit's convention then is to just activate — leave it.
        _detachReopenActivation = Helpers.FileActivation.SubscribeReopen(
            Application.Current?.TryGetFeature<IActivatableLifetime>(),
            () => Dispatcher.UIThread.Post(() =>
            {
                if (_miniPlayer == null)
                    ShowFromTray();
            }));

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
            if (IsTrayUsable
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

    // ── macOS menu bar ──

    /// <summary>
    /// Builds the macOS menu bar (issue #38). Without this Avalonia exports only its
    /// default app menu, so the bar showed a bare "Avalonia Application" entry with no
    /// options. App-level items land inside the bold app menu (Avalonia appends the
    /// standard Hide/Quit entries itself); the window-level menu contributes the
    /// Playback menu next to it. No-op off macOS — Windows/Linux use the in-app UI.
    /// </summary>
    private void InitializeMacMenuBar(MainWindowViewModel vm)
    {
        if (!OperatingSystem.IsMacOS()) return;

        try
        {
            var appMenu = new NativeMenu();
            var settings = new NativeMenuItem("Settings…")
            {
                Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta),
            };
            settings.Click += (_, _) => vm.OpenSettingsCommand.Execute(null);
            appMenu.Items.Add(settings);
            NativeMenu.SetMenu(Application.Current!, appMenu);

            // Gestures mirror Music.app (⌘→ next, ⌘← previous) by default, and follow
            // whatever the user rebinds in Settings › Shortcuts.
            var shortcuts = vm.Settings.ShortcutService;
            var playback = new NativeMenu();

            var playPause = new NativeMenuItem("Play / Pause");
            playPause.Click += (_, _) => vm.Player.PlayPauseCommand.Execute(null);
            playback.Items.Add(playPause);

            var next = new NativeMenuItem("Next Track")
            {
                Gesture = shortcuts.Get(ShortcutAction.NextTrack),
            };
            next.Click += (_, _) => vm.Player.NextCommand.Execute(null);
            playback.Items.Add(next);

            var previous = new NativeMenuItem("Previous Track")
            {
                Gesture = shortcuts.Get(ShortcutAction.PreviousTrack),
            };
            previous.Click += (_, _) => vm.Player.PreviousCommand.Execute(null);
            playback.Items.Add(previous);

            shortcuts.Changed += (_, _) =>
            {
                next.Gesture = shortcuts.Get(ShortcutAction.NextTrack);
                previous.Gesture = shortcuts.Get(ShortcutAction.PreviousTrack);
            };

            var windowMenu = new NativeMenu();
            windowMenu.Items.Add(new NativeMenuItem("Playback") { Menu = playback });
            NativeMenu.SetMenu(this, windowMenu);
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.UI, "MacMenu.Init", ex.Message);
        }
    }

    // ── System tray ──

    private void InitializeTrayIcon(MainWindowViewModel vm)
    {
        if (_trayIcon != null) return;

        try
        {
            var iconUri = new Uri("avares://Noctis.UI/Assets/Icons/Noctis.ico");
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

    /// <summary>
    /// Whether hiding into the tray leaves a way back. A TrayIcon object alone does not: on
    /// Linux Avalonia creates one even with nothing hosting it (stock GNOME), so every
    /// hide-to-tray path also needs a live StatusNotifierWatcher (audit P29).
    /// </summary>
    private bool IsTrayUsable => LinuxTrayHost.IsTrayUsable(
        _trayIcon != null, OperatingSystem.IsLinux(), _trayHost?.IsAvailable == true);

    private async Task SettleStartMinimizedAsync()
    {
        try
        {
            if (_trayHost != null)
            {
                // At login the panel may register its tray a moment after we start.
                await _trayHost.WaitForHostAsync(TimeSpan.FromSeconds(5));
                // Brought up meanwhile (second launch): ShowFromTray already settled it.
                if (ShowInTaskbar)
                    return;
            }
            SettleStartMinimized(this, IsTrayUsable);
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.UI, "TrayIcon.StartMinimized", ex.Message);
            ShowInTaskbar = true;
        }
    }

    /// <summary>
    /// A login launch arrives minimized with no taskbar button (see App). Into the tray when
    /// there is one; otherwise give the taskbar button back, or the app runs with no window,
    /// no taskbar entry and no tray icon. Internal for tests.
    /// </summary>
    internal static void SettleStartMinimized(Window window, bool trayUsable)
    {
        if (trayUsable)
            window.Hide();
        else
            window.ShowInTaskbar = true;
    }

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
            && IsTrayUsable
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
        _detachFileActivation?.Invoke();
        _detachFileActivation = null;
        _detachReopenActivation?.Invoke();
        _detachReopenActivation = null;

        _taskbar?.Dispose();
        _smtc?.Dispose();
        _smtc = null;
        _mpris?.Dispose();
        _mpris = null;
        _resumeWatcher?.Dispose();
        _resumeWatcher = null;
        _trayHost?.Dispose();
        _trayHost = null;
        _macNowPlaying?.Dispose();
        _macNowPlaying = null;
        if (_trayIcon != null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        if (_platformColorsChangedHandler != null && this.GetPlatformSettings() is { } platformSettings)
            platformSettings.ColorValuesChanged -= _platformColorsChangedHandler;

        // Unsubscribe from all event handlers to prevent memory leak
        if (DataContext is MainWindowViewModel vm)
        {
            if (_themeChangedHandler != null)
                vm.Settings.ThemeChanged -= _themeChangedHandler;

            if (_accentChangedHandler != null)
                vm.Settings.AccentChanged -= _accentChangedHandler;

            if (_artworkAccentPlayerHandler != null)
                vm.Player.PropertyChanged -= _artworkAccentPlayerHandler;
            if (_artworkAccentSettingsHandler != null)
                vm.Settings.PropertyChanged -= _artworkAccentSettingsHandler;

            if (_liquidGlassChangedHandler != null)
                vm.Settings.LiquidGlassChanged -= _liquidGlassChangedHandler;

            if (_sidebarAlwaysExpandedHandler != null)
                vm.Settings.SidebarAlwaysExpandedChanged -= _sidebarAlwaysExpandedHandler;

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
            else if (_queueSelection is { Count: > 0 } selection)
            {
                // A closed panel doesn't keep a selection to reappear with (GitHub #85).
                selection.Clear();
                RefreshQueueSelectionVisuals();
            }
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
                    UpdateTaskbarProgress(vm);
                }
                else if (e.PropertyName is nameof(PlayerViewModel.Position) or nameof(PlayerViewModel.Duration))
                {
                    UpdateTaskbarProgress(vm);
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

            // Taskbar progress (GitHub #53) follows its Settings toggle live: switching
            // it off must clear the overlay, not leave the last frame painted.
            vm.Settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.TaskbarProgressEnabled))
                    UpdateTaskbarProgress(vm);
            };
            UpdateTaskbarProgress(vm);
        }
        catch
        {
            // Non-critical — taskbar buttons are a nice-to-have
        }
    }

    /// <summary>Pushes the current song position onto the taskbar button, or clears it
    /// when the setting is off or nothing is playing. The service de-duplicates values,
    /// so calling this on every position tick is cheap.</summary>
    private void UpdateTaskbarProgress(MainWindowViewModel vm)
    {
        if (_taskbar == null) return;
        var player = vm.Player;
        if (!vm.Settings.TaskbarProgressEnabled || player.State == PlaybackState.Stopped || player.CurrentTrack == null)
        {
            _taskbar.ClearProgress();
            return;
        }
        _taskbar.SetProgress(player.Position.TotalSeconds, player.Duration.TotalSeconds,
            paused: player.State != PlaybackState.Playing);
    }

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        MoveDragChip(e);

        // Don't show import overlay for internal drags (album/track tiles dragged within the app)
        if (Helpers.DragFileBehavior.IsInternalDrag(e.DataTransfer))
            return;

        var paths = GetDroppedLocalPaths(e.DataTransfer);
        var hasImportable = paths.Any(IsImportablePath);
        e.DragEffects = hasImportable ? DragDropEffects.Copy : DragDropEffects.None;
        ShowDragOverlay(hasImportable);
        e.Handled = true;
    }

    private void OnWindowDragLeave(object? sender, DragEventArgs e)
    {
        ShowDragOverlay(false);
        // DragLeave also arrives for every element-to-element crossing inside the window;
        // only a real exit hides the chip (it comes back on the next DragOver in here).
        if (!new Rect(Bounds.Size).Contains(e.GetPosition(this)))
            SetDragChipShown(false);
    }

    // ── Drag chip (a picture of what is being dragged) ──

    private bool _dragChipActive;

    private void OnDragPreviewStarted(TopLevel top, Helpers.DragFileBehavior.DragPreview preview)
    {
        if (!ReferenceEquals(top, this)) return;
        // Set the title Run, not TextBlock.Text: Text would replace the inline E badge.
        if (this.FindControl<TextBlock>("DragChipTitle") is { Inlines: { } inlines }
            && inlines.OfType<Avalonia.Controls.Documents.Run>().FirstOrDefault() is { } titleRun)
            titleRun.Text = preview.Title;
        if (this.FindControl<Border>("DragChipExplicit") is { } explicitBadge)
            explicitBadge.IsVisible = preview.IsExplicit;
        if (this.FindControl<TextBlock>("DragChipSubtitle") is { } subtitle)
        {
            subtitle.Text = preview.Subtitle;
            subtitle.IsVisible = !string.IsNullOrWhiteSpace(preview.Subtitle);
        }
        if (this.FindControl<Controls.CachedImage>("DragChipArt") is { } art) art.SourcePath = preview.ArtworkPath;
        if (this.FindControl<Border>("DragChipCount") is { } badge) badge.IsVisible = preview.Count > 1;
        if (this.FindControl<TextBlock>("DragChipCountText") is { } count) count.Text = preview.Count.ToString();
        // Shown by the first DragOver, which is the first time the pointer position is known.
        _dragChipActive = true;
    }

    private void OnDragPreviewEnded(TopLevel top)
    {
        if (!ReferenceEquals(top, this)) return;
        _dragChipActive = false;
        SetDragChipShown(false);
    }

    /// <summary>Keeps the chip just below-right of the pointer, like a cursor label.</summary>
    private void MoveDragChip(DragEventArgs e)
    {
        if (!_dragChipActive || this.FindControl<Border>("DragChip") is not { } chip) return;
        if (chip.GetVisualParent() is not Visual parent) return;
        var p = e.GetPosition(parent);
        if (chip.RenderTransform is TranslateTransform tt)
        {
            tt.X = p.X + 16;
            tt.Y = p.Y + 18;
        }
        SetDragChipShown(true);
    }

    private void SetDragChipShown(bool shown)
    {
        if (this.FindControl<Border>("DragChip") is not { } chip) return;
        if (shown)
        {
            if (chip.IsVisible && chip.Opacity > 0) return;
            chip.IsVisible = true;
            // Next frame, so the opacity transition animates the fade-in.
            Dispatcher.UIThread.Post(() => { if (_dragChipActive) chip.Opacity = 1; }, DispatcherPriority.Render);
            return;
        }
        chip.Opacity = 0;
        DispatcherTimer.RunOnce(() =>
        {
            if (chip.Opacity == 0) chip.IsVisible = false;
        }, TimeSpan.FromMilliseconds(130));
    }

    private async void OnWindowDrop(object? sender, DragEventArgs e)
    {
        // Ignore internal drags (album/track tiles dragged within the app)
        if (Helpers.DragFileBehavior.IsInternalDrag(e.DataTransfer))
            return;

        e.Handled = true;
        ShowDragOverlay(false);
        if (DataContext is not MainWindowViewModel vm) return;

        var paths = GetDroppedLocalPaths(e.DataTransfer);
        if (paths.Count == 0) return;

        try
        {
            // GitHub #71: Settings → Library → "Import dropped files" off plays / queues
            // the drop from where it is instead of relocating it into Noctis Imports.
            if (vm.Settings.ImportDroppedMedia)
                await vm.ImportDroppedMediaAsync(paths);
            else
                await vm.QueueExternalMediaAsync(paths);
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

    private enum DropMode { Import, Play, Queue }

    /// <summary>The overlay's import arrow from the XAML, kept so the icon can switch back to it.</summary>
    private Avalonia.Media.Geometry? _dropImportIconData;

    /// <summary>"Add to queue": three list lines with a plus (GitHub #90).</summary>
    private static readonly Avalonia.Media.Geometry DropQueueIconData = Avalonia.Media.Geometry.Parse(
        "M3 5h13v2H3z M3 10h13v2H3z M3 15h8v2H3z M17 12h2v4h4v2h-4v4h-2v-4h-4v-2h4z");

    private void ShowDragOverlay(bool show)
    {
        var overlay = this.FindControl<Avalonia.Controls.Border>("DragDropOverlay");
        if (overlay == null) return;
        // GitHub #86 / #90: with "Import dropped files" off the drop plays / queues in place,
        // so "Drop files to import" promised something that would not happen. The overlay says
        // what this drop will do — play (nothing loaded) or add to the queue — with its own icon.
        if (show && DataContext is MainWindowViewModel vm
            && this.FindControl<TextBlock>("DragDropOverlayText") is { } text)
        {
            var mode = vm.Settings.ImportDroppedMedia ? DropMode.Import
                : vm.DropStartsPlayback ? DropMode.Play : DropMode.Queue;
            text.Text = Localization.Loc.T(mode switch
            {
                DropMode.Import => "Main.DropFilesImport",
                DropMode.Play => "Main.DropFilesPlay",
                _ => "Main.DropFilesQueue",
            });
            if (this.FindControl<PathIcon>("DragDropOverlayIcon") is { } icon)
            {
                _dropImportIconData ??= icon.Data;
                icon.Data = mode switch
                {
                    DropMode.Import => _dropImportIconData,
                    DropMode.Play => this.FindResource("PlayIcon") as Avalonia.Media.Geometry ?? _dropImportIconData,
                    _ => DropQueueIconData,
                };
            }
        }
        overlay.IsVisible = show;
        overlay.Opacity = show ? 1 : 0;
    }

    private static List<string> GetDroppedLocalPaths(IDataTransfer data)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Primary: Avalonia IStorageItem API (works for Explorer drops on most platforms).
            foreach (var item in data.TryGetFiles() ?? Enumerable.Empty<IStorageItem>())
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

            // Fallback: raw Text payload (some drag sources provide newline-separated paths).
            // Only accept lines that look like real file paths (drive letter or UNC prefix).
            if (paths.Count == 0 && data.Contains(DataFormat.Text))
            {
                var text = data.TryGetText();
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

    /// <summary>
    /// Set when the tunnelling KeyDown handler ran a shortcut, so the matching KeyUp is
    /// swallowed too. Avalonia's Button raises Click on key *up*, so swallowing only the
    /// press still let the focused button fire on the way back up.
    /// </summary>
    private ShortcutAction? _consumedShortcut;

    private ShortcutService? Shortcuts => (DataContext as MainWindowViewModel)?.Settings.ShortcutService;

    private void OnGlobalShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || Shortcuts is not { } shortcuts) return;
        // A Shortcuts-tab chip is waiting for a chord: the chip owns every key until it
        // is done, otherwise pressing the key you are trying to assign would run it.
        if (vm.Settings.Shortcuts.IsRecording) return;
        if (shortcuts.TryMatch(e) is not { } action) return;

        // An unmodified key (Space, or whatever the user bound) must still type in an
        // edit box: typing a space in the search box stays typing a space. Ctrl+←/→ in
        // the search box or Lyrics Studio moves the caret by a word, not the track.
        if (e.Source is TextBox && ShortcutDefaults.IsTextBoxKey(e.Key, e.KeyModifiers)) return;

        if (!ExecuteShortcut(vm, action)) return;
        _consumedShortcut = action;
        e.Handled = true;
    }

    private void OnGlobalShortcutKeyUp(object? sender, KeyEventArgs e)
    {
        if (_consumedShortcut is null) return;
        _consumedShortcut = null;
        e.Handled = true;
    }

    /// <summary>Runs the command behind a shortcut. Returns false for actions this window
    /// does not handle, so the key falls through untouched.</summary>
    private bool ExecuteShortcut(MainWindowViewModel vm, ShortcutAction action)
    {
        switch (action)
        {
            case ShortcutAction.PlayPause:
                vm.Player.PlayPauseCommand.Execute(null);
                return true;
            case ShortcutAction.NextTrack:
                vm.Player.NextCommand.Execute(null);
                return true;
            case ShortcutAction.PreviousTrack:
                vm.Player.PreviousCommand.Execute(null);
                return true;
            case ShortcutAction.VolumeUp:
                vm.Player.Volume = Math.Min(100, vm.Player.Volume + 5);
                return true;
            case ShortcutAction.VolumeDown:
                vm.Player.Volume = Math.Max(0, vm.Player.Volume - 5);
                return true;
            case ShortcutAction.ToggleFavorite:
                if (vm.Player.CurrentTrack == null) return false;
                vm.Player.ToggleCurrentTrackFavoriteCommand.Execute(null);
                return true;
            case ShortcutAction.ToggleFullscreen:
                ToggleFullScreen();
                return true;
            case ShortcutAction.SearchLibrary:
                // With Settings open the search key belongs to the settings search box.
                if (vm.IsSettingsModalOpen
                    && this.FindControl<ContentControl>("SettingsViewHost")?.Content is SettingsView settingsView)
                {
                    settingsView.FocusSearch();
                    return true;
                }
                vm.Sidebar.TopBar.ToggleSearchCommand.Execute(null);
                return true;
            case ShortcutAction.CommandPalette:
                _ = vm.OpenCommandPaletteAsync();
                return true;
            case ShortcutAction.NewPlaylist:
                vm.Sidebar.CreatePlaylistCommand.Execute(null);
                return true;
            case ShortcutAction.ToggleQueue:
                // GitHub #86: the island's Queue button is gone while nothing is loaded
                // (the bar unmounts), so this is the way in to an empty queue.
                vm.Player.ShowQueueCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // Escape is the one fixed key: it closes the topmost open surface, in z-order.
        // Everything rebindable is dispatched by OnGlobalShortcutKeyDown (tunnelling) so
        // a focused button can't swallow it first.
        if (e.Key != Key.Escape) return;

        // Escape used to check only the queue popup and otherwise unconditionally clear
        // the search box — so with the Settings modal or the lyrics side panel open it
        // silently wiped the user's search while the modal stayed up.
        if (WindowState == WindowState.FullScreen)
        {
            // Fullscreen counts as the topmost surface — leave it first, browser-style,
            // before closing any in-app overlay.
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

    private void OnQueueClearClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
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

        // The MenuItem's DataContext is the Track from the DataTemplate. The row it was
        // opened on was recorded by index (UpNext.IndexOf resolves a track queued twice to
        // its first copy); the IndexOf fallback covers a queue that shifted meanwhile.
        if (menuItem.DataContext is not Track track) return;
        var upNext = vm.Player.UpNext;
        var index = _queueContextRow >= 0 && _queueContextRow < upNext.Count
                    && ReferenceEquals(upNext[_queueContextRow], track)
            ? _queueContextRow
            : upNext.IndexOf(track);
        if (index < 0) return;

        // GitHub #85: on a selected row, the menu removes the whole selection.
        if (_queueSelection is { } selection && selection.Contains(index))
            vm.Player.RemoveManyFromQueue(selection.Snapshot());
        else
            vm.Player.RemoveFromQueue(index);
    }

    // ── Queue row selection (GitHub #85) ──
    //
    // Click selects one row, Ctrl+Click toggles, Shift+Click selects the range from the
    // anchor; Ctrl+A / Delete / Escape act while keyboard focus is inside the panel. The
    // selection is keyed by row index (QueueRowSelection), shown via the ctrl-selected
    // class on the row containers.

    private const string QueueSelectedClass = "ctrl-selected";
    private QueueRowSelection<Track>? _queueSelection;
    /// <summary>Row pressed without modifiers while part of a multi-selection: the
    /// selection collapses to it on release, unless the press became a block drag.</summary>
    private int _queuePendingCollapseRow = -1;
    /// <summary>Row whose context menu is open (set on ContextRequested).</summary>
    private int _queueContextRow = -1;

    private static int QueueRowIndex(ListBox listBox, Control rowControl) =>
        rowControl.FindAncestorOfType<ListBoxItem>() is { } item ? listBox.IndexFromContainer(item) : -1;

    /// <summary>Re-applies the selection class to the realized rows (they are recycled).</summary>
    private void SyncQueueSelectionVisuals(ListBox listBox)
    {
        foreach (var container in listBox.GetRealizedContainers())
            container.Classes.Set(QueueSelectedClass,
                _queueSelection?.Contains(listBox.IndexFromContainer(container)) == true);
    }

    private void RefreshQueueSelectionVisuals()
    {
        if (this.FindControl<ListBox>("QueuePopupListBox") is { } listBox)
            SyncQueueSelectionVisuals(listBox);
    }

    private void OnQueueRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        _queueContextRow = sender is Control row && this.FindControl<ListBox>("QueuePopupListBox") is { } listBox
            ? QueueRowIndex(listBox, row)
            : -1;
    }

    private bool IsFocusInQueuePanel() =>
        _queuePopupPanel is { IsVisible: true } panel
        && FocusManager?.GetFocusedElement() is Visual focused
        && (focused == panel || panel.IsVisualAncestorOf(focused));

    /// <summary>Page shortcuts (Ctrl+A, Escape) stay off the page while the Settings sheet
    /// covers it or the queue panel holds focus; the sheet and panel handle those keys.</summary>
    bool IPageKeyOverlayHost.IsOverlayCapturingKeys =>
        DataContext is MainWindowViewModel { IsSettingsModalOpen: true } || IsFocusInQueuePanel();

    private void OnQueueKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || _queueSelection is not { } selection) return;
        if (e.Source is TextBox || !IsFocusInQueuePanel()) return;

        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            // With a selection, Escape clears it; otherwise it falls through and closes the panel.
            if (selection.Count == 0) return;
            selection.Clear();
        }
        else if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control)
        {
            selection.SelectAll();
        }
        else if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None)
        {
            if (selection.Count == 0) return;
            vm.Player.RemoveManyFromQueue(selection.Snapshot());
            selection.Clear();
        }
        else
        {
            return;
        }
        RefreshQueueSelectionVisuals();
        e.Handled = true;
    }

    // ── Queue drag-to-reorder (pointer-tracked, Apple Music style) ──
    //
    // The dragged row is rendered as a floating preview (#QueueDragPreview) that lifts and
    // springs after the pointer. Its own slot stays reserved but empty, and the other rows
    // slide apart to open a gap where it will land. On release the card glides into the
    // gap and then Player.MoveInQueue commits the move.
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
    /// <summary>The pressed row, so the drag resolves ITS index: IndexOf(_queueDragTrack)
    /// finds the first copy of a track queued twice (GitHub #85).</summary>
    private Control? _queueDragRow;
    private double _queueDragRowOffsetY;
    private LiquidReorder? _queueLiquid;

    /// <summary>The dragged row's current queue index, read from its container (which
    /// follows a queue that shifted mid-drag); IndexOf only if the container moved on.</summary>
    private int QueueDragSourceIndex(PlayerViewModel player, ListBox listBox)
    {
        if (_queueDragTrack is not { } track) return -1;
        var upNext = player.UpNext;
        if (_queueDragRow is { } row && QueueRowIndex(listBox, row) is var i and >= 0
            && i < upNext.Count && ReferenceEquals(upNext[i], track))
            return i;
        return upNext.IndexOf(track);
    }

    private LiquidReorder? QueueLiquid
    {
        get
        {
            if (_queueLiquid != null) return _queueLiquid;
            var listBox = this.FindControl<ListBox>("QueuePopupListBox");
            var preview = this.FindControl<Border>("QueueDragPreview");
            if (listBox == null || preview == null) return null;
            return _queueLiquid = new LiquidReorder(this, listBox, preview, () => preview.DataContext = null);
        }
    }

    private void OnQueueItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control rowControl) return;
        if (rowControl.Tag is not Track track) return;
        if (!e.GetCurrentPoint(rowControl).Properties.IsLeftButtonPressed) return;
        if (DataContext is not MainWindowViewModel) return;

        // A new press lands before the last drop's glide finished: commit it now.
        QueueLiquid?.FinishNow();

        // GitHub #85: Ctrl / Shift presses only change the selection (no drag, and the
        // ListBox's own single selection is left alone). A plain press on a row of a
        // multi-selection keeps it, so the whole block can be dragged; release collapses it.
        _queuePendingCollapseRow = -1;
        if (this.FindControl<ListBox>("QueuePopupListBox") is { } listBox
            && _queueSelection is { } selection
            && QueueRowIndex(listBox, rowControl) is var row and >= 0)
        {
            var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (shift) selection.SelectRangeTo(row, additive: ctrl);
            else if (ctrl) selection.Toggle(row);
            else if (selection.Contains(row) && selection.Count > 1) _queuePendingCollapseRow = row;
            else selection.SelectOnly(row);
            SyncQueueSelectionVisuals(listBox);
            if (ctrl || shift)
            {
                e.Handled = true;
                return;
            }
        }

        _queueDragTrack = track;
        _queueDragRow = rowControl;
        _queueDragRowOffsetY = e.GetPosition(rowControl).Y;
        _queueDragStartPos = e.GetPosition(this);
        _queueDragActive = false;
    }

    private void OnQueueItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_queueDragTrack == null || QueueLiquid is not { IsSettling: false } liquid) return;
        if (sender is not Control rowControl) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (DataContext is not MainWindowViewModel vm) return;

        var pos = e.GetPosition(this);
        if (!_queueDragActive)
        {
            if (Math.Abs(pos.X - _queueDragStartPos.X) < QueueDragThreshold &&
                Math.Abs(pos.Y - _queueDragStartPos.Y) < QueueDragThreshold)
                return;

            StartQueueDrag(rowControl, e, liquid);
        }

        var wrapper = this.FindControl<Grid>("QueueListWrapper");
        var listBox = this.FindControl<ListBox>("QueuePopupListBox");
        if (wrapper == null || listBox == null) return;

        // Re-resolved every move: a track transition (UpNext.RemoveAt(0)) or a radio refill
        // can shift the dragged track mid-drag.
        liquid.SourceIndex = QueueDragSourceIndex(vm.Player, listBox);
        var cardTop = e.GetPosition(wrapper).Y - _queueDragRowOffsetY;
        liquid.MoveCardTo(cardTop);
        var target = LiquidReorder.NearestSlot(listBox, wrapper, cardTop + rowControl.Bounds.Height / 2);
        if (target >= 0) liquid.TargetIndex = target;
    }

    private void OnQueueItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_queueDragActive)
        {
            BeginQueueSettle();
        }
        else
        {
            ResetQueueDragState();
            // A plain click (no drag) on a row of a multi-selection selects just that row.
            if (_queuePendingCollapseRow >= 0 && _queueSelection is { } selection
                && sender is Control rowControl
                && this.FindControl<ListBox>("QueuePopupListBox") is { } listBox
                && QueueRowIndex(listBox, rowControl) is var row and >= 0)
            {
                selection.SelectOnly(row);
                SyncQueueSelectionVisuals(listBox);
            }
        }
        _queuePendingCollapseRow = -1;
        e.Pointer.Capture(null);
    }

    private void OnQueueItemPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Releasing the capture on drop raises this too; the glide owns cleanup then.
        if (QueueLiquid is { IsSettling: true }) return;
        // Otherwise lost capture is a cancel: restore visuals without performing the move.
        ResetQueueDragState();
    }

    private void StartQueueDrag(Control rowControl, PointerEventArgs e, LiquidReorder liquid)
    {
        _queueDragActive = true;

        // Capture the pointer so we keep receiving move/release events even if the cursor
        // leaves the row's hit area.
        e.Pointer.Capture(rowControl);

        var wrapper = this.FindControl<Grid>("QueueListWrapper");
        var listBox = this.FindControl<ListBox>("QueuePopupListBox");
        var preview = this.FindControl<Border>("QueueDragPreview");
        if (wrapper == null || listBox == null || preview == null || _queueDragTrack == null) return;

        preview.DataContext = _queueDragTrack;
        var first = listBox.GetRealizedContainers().FirstOrDefault();
        liquid.Pitch = first == null ? 0 : first.Bounds.Height + first.Margin.Top + first.Margin.Bottom;
        liquid.SourceIndex = liquid.TargetIndex =
            DataContext is MainWindowViewModel vm ? QueueDragSourceIndex(vm.Player, listBox) : -1;
        // Start exactly over the grabbed row so the lift reads as the row rising.
        var rowTop = rowControl.TranslatePoint(new Point(0, 0), wrapper)?.Y
                     ?? e.GetPosition(wrapper).Y - _queueDragRowOffsetY;
        liquid.Begin(rowTop);
    }

    /// <summary>Release: glide the card into the open gap; the move commits when it lands.</summary>
    private void BeginQueueSettle()
    {
        var wrapper = this.FindControl<Grid>("QueueListWrapper");
        var listBox = this.FindControl<ListBox>("QueuePopupListBox");
        if (DataContext is not MainWindowViewModel vm || wrapper == null || listBox == null
            || _queueDragTrack == null || QueueLiquid is not { } liquid)
        {
            ResetQueueDragState();
            return;
        }

        // Re-resolve the source index from the dragged row at drop time. The
        // press-time index goes stale: a drag lasts long enough for a track transition
        // (UpNext.RemoveAt(0)) or a queued radio refill to shift everything, and the
        // trusted index then moved the wrong track — the bounds checks prevented a crash
        // but not the wrong move.
        var track = _queueDragTrack;
        var from = QueueDragSourceIndex(vm.Player, listBox);
        if (from < 0)
        {
            ResetQueueDragState();
            return;
        }
        var to = Math.Clamp(liquid.TargetIndex, 0, Math.Max(0, vm.Player.UpNext.Count - 1));
        liquid.SourceIndex = from;
        liquid.TargetIndex = to;

        // GitHub #85: a row dragged out of a multi-selection carries the whole selection
        // (the card shows only the grabbed row, as on the playlist page). MoveBlockInQueue
        // takes an INSERTION index: past the source, one further down once the block is
        // lifted out. The rows' tracks are snapshotted so a queue that shifted during the
        // glide (a track advancing) skips the move, as the single-row path does.
        var selection = _queueSelection;
        var wasSelected = selection?.Contains(from) == true;
        var block = wasSelected && selection!.Count > 1 ? selection.Snapshot() : null;
        var blockTracks = block?.Select(i => vm.Player.UpNext[i]).ToList();
        var insertIndex = to > from ? to + 1 : to;

        // The landing spot is the target slot's layout position (its row is sliding away
        // from it), plus the row card's own 1px top margin inside the item.
        var landing = LiquidReorder.SlotTop(listBox, to, wrapper) is { } top ? top + 1 : liquid.CardY;
        _queueDragActive = false;
        _queueDragTrack = null;
        _queueDragRow = null;
        liquid.Settle(landing, () =>
        {
            var upNext = vm.Player.UpNext;
            if (block != null && blockTracks != null)
            {
                var unchanged = Enumerable.Range(0, block.Length)
                    .All(k => block[k] < upNext.Count && ReferenceEquals(upNext[block[k]], blockTracks[k]));
                if (!unchanged) return;
                var landAt = vm.Player.MoveBlockInQueue(block, insertIndex);
                if (landAt >= 0) selection?.Select(Enumerable.Range(landAt, block.Length));
            }
            // By reference at the row, not IndexOf: a track queued twice is two rows.
            else if (from != to && from < upNext.Count && ReferenceEquals(upNext[from], track))
            {
                vm.Player.MoveInQueue(from, to);
                // The move is a remove + insert, which drops the row from the selection.
                if (wasSelected) selection?.SelectOnly(to);
            }
            RefreshQueueSelectionVisuals();
        });
    }

    private void ResetQueueDragState()
    {
        QueueLiquid?.Cancel();
        _queueDragActive = false;
        _queueDragTrack = null;
        _queueDragRow = null;
    }

    // ── GitHub #88: rubber-band selection ────────────────────────────────────
    // A band starts from the popup's empty space (the row gutters, below the last row) or
    // from a Ctrl/Shift press on a row; a plain press on a row stays click / drag-to-reorder.
    // Rows are virtualized, so the band's start is kept in fractional ROW units measured off
    // a realized row (rows are a fixed height). That keeps it pinned to its rows while the
    // list auto-scrolls under a band held near the top or bottom edge.

    private const double QueueBandEdge = 28.0;
    private bool _queueBandPending;
    private bool _queueBandActive;
    private bool _queueBandClearOnClick;
    private Point _queueBandStartPos;
    private double _queueBandStartRow;
    private Point _queueBandLastPos;
    private int[]? _queueBandKeep;
    private DispatcherTimer? _queueBandScrollTimer;

    /// <summary>First on-screen realized row: its index, its top (margin included) in
    /// <paramref name="listBox"/> coordinates, and the row pitch.</summary>
    private static bool TryQueueRowMetrics(ListBox listBox, out int index, out double top, out double pitch)
    {
        index = -1; top = 0; pitch = 0;
        foreach (var container in listBox.GetRealizedContainers())
        {
            var i = listBox.IndexFromContainer(container);
            if (i < 0 || !container.IsVisible || (index >= 0 && i >= index)) continue;
            if (container.TranslatePoint(new Point(0, 0), listBox) is not { } p) continue;
            var h = container.Bounds.Height + container.Margin.Top + container.Margin.Bottom;
            // Skip a container kept realized far off-screen (e.g. the focused one).
            if (h <= 0 || p.Y + h < -h || p.Y > listBox.Bounds.Height + h) continue;
            index = i;
            top = p.Y - container.Margin.Top;
            pitch = h;
        }
        return index >= 0;
    }

    private void OnQueueBandPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox listBox || _queueSelection is null) return;
        if (e.Pointer.Type == PointerType.Touch) return;
        if (!e.GetCurrentPoint(listBox).Properties.IsLeftButtonPressed) return;
        if (QueueLiquid is { IsSettling: true }) return;

        var onRow = (e.Source as Visual)?.GetSelfAndVisualAncestors()
            .Any(v => v is Border b && b.Classes.Contains("queue-row")) == true;
        var modifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift);
        if (onRow && modifiers == KeyModifiers.None) return;

        var pos = e.GetPosition(listBox);
        if (!TryQueueRowMetrics(listBox, out var index, out var top, out var pitch)) return;

        _queueBandPending = true;
        _queueBandActive = false;
        _queueBandClearOnClick = !onRow && modifiers == KeyModifiers.None;
        _queueBandStartPos = _queueBandLastPos = pos;
        _queueBandStartRow = index + (pos.Y - top) / pitch;
        // The row press handler already ran (it sits deeper), so a Ctrl/Shift band keeps
        // what that click just selected.
        _queueBandKeep = modifiers != KeyModifiers.None ? _queueSelection.Snapshot() : null;
    }

    private void OnQueueBandMoved(object? sender, PointerEventArgs e)
    {
        if (!_queueBandPending || sender is not ListBox listBox) return;
        var pos = e.GetPosition(listBox);
        if (!_queueBandActive)
        {
            if (!e.GetCurrentPoint(listBox).Properties.IsLeftButtonPressed)
            {
                EndQueueBand();
                return;
            }
            if (Math.Abs(pos.X - _queueBandStartPos.X) < QueueDragThreshold &&
                Math.Abs(pos.Y - _queueBandStartPos.Y) < QueueDragThreshold)
                return;
            _queueBandActive = true;
            e.Pointer.Capture(listBox);
            _queueBandScrollTimer ??= CreateQueueBandScrollTimer();
            _queueBandScrollTimer.Start();
        }
        _queueBandLastPos = pos;
        UpdateQueueBand(listBox);
        e.Handled = true;
    }

    private void OnQueueBandReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_queueBandPending) return;
        var wasActive = _queueBandActive;
        // A plain click on empty space (no band) clears the selection, like a file list.
        if (!wasActive && _queueBandClearOnClick && _queueSelection is { } selection
            && sender is ListBox listBox)
        {
            selection.Clear();
            SyncQueueSelectionVisuals(listBox);
        }
        EndQueueBand();
        if (wasActive)
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnQueueBandCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender) || !_queueBandActive) return;
        EndQueueBand();
    }

    private void UpdateQueueBand(ListBox listBox)
    {
        if (_queueSelection is not { } selection
            || !TryQueueRowMetrics(listBox, out var index, out var top, out var pitch))
            return;

        var currentRow = index + (_queueBandLastPos.Y - top) / pitch;
        selection.SelectBand((int)Math.Floor(_queueBandStartRow), (int)Math.Floor(currentRow), _queueBandKeep);
        SyncQueueSelectionVisuals(listBox);

        if (this.FindControl<Grid>("QueueListWrapper") is not { } wrapper
            || this.FindControl<Border>("QueueMarquee") is not { } marquee)
            return;
        // The start row may have scrolled away: draw the band clipped to the list.
        var startY = top + (_queueBandStartRow - index) * pitch;
        var y1 = Math.Clamp(Math.Min(startY, _queueBandLastPos.Y), 0, listBox.Bounds.Height);
        var y2 = Math.Clamp(Math.Max(startY, _queueBandLastPos.Y), 0, listBox.Bounds.Height);
        var x1 = Math.Clamp(Math.Min(_queueBandStartPos.X, _queueBandLastPos.X), 0, listBox.Bounds.Width);
        var x2 = Math.Clamp(Math.Max(_queueBandStartPos.X, _queueBandLastPos.X), 0, listBox.Bounds.Width);
        if (listBox.TranslatePoint(new Point(x1, y1), wrapper) is not { } origin) return;
        marquee.Margin = new Thickness(origin.X, origin.Y, 0, 0);
        marquee.Width = x2 - x1;
        marquee.Height = y2 - y1;
        marquee.IsVisible = true;
    }

    private DispatcherTimer CreateQueueBandScrollTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            if (!_queueBandActive
                || this.FindControl<ListBox>("QueuePopupListBox") is not { } listBox
                || listBox.FindDescendantOfType<ScrollViewer>() is not { } scroller)
                return;
            var y = _queueBandLastPos.Y;
            var height = listBox.Bounds.Height;
            var depth = y < QueueBandEdge ? y - QueueBandEdge
                : y > height - QueueBandEdge ? y - (height - QueueBandEdge)
                : 0;
            if (depth == 0) return;
            var max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
            var next = Math.Clamp(scroller.Offset.Y + Math.Clamp(depth, -60, 60) * 0.5, 0, max);
            if (Math.Abs(next - scroller.Offset.Y) < 0.5) return;
            scroller.Offset = scroller.Offset.WithY(next);
            // Realize/arrange the rows at the new offset before measuring off them.
            listBox.UpdateLayout();
            UpdateQueueBand(listBox);
        };
        return timer;
    }

    private void EndQueueBand()
    {
        _queueBandPending = false;
        _queueBandActive = false;
        _queueBandKeep = null;
        _queueBandScrollTimer?.Stop();
        if (this.FindControl<Border>("QueueMarquee") is { } marquee)
            marquee.IsVisible = false;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        Helpers.DragFileBehavior.DragPreviewStarted += OnDragPreviewStarted;
        Helpers.DragFileBehavior.DragPreviewEnded += OnDragPreviewEnded;
        Closed += (_, _) =>
        {
            Helpers.DragFileBehavior.DragPreviewStarted -= OnDragPreviewStarted;
            Helpers.DragFileBehavior.DragPreviewEnded -= OnDragPreviewEnded;
        };

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

        // GitHub #71 (3): rows dragged from any library page drop onto the queue popup
        // to append them. The window handlers above leave in-app payloads alone, so this
        // list is the one place that reads them here.
        if (this.FindControl<ListBox>("QueuePopupListBox") is { } queueDropTarget)
        {
            DragDrop.SetAllowDrop(queueDropTarget, true);
            queueDropTarget.AddHandler(DragDrop.DragOverEvent, OnQueueDragOver);
            queueDropTarget.AddHandler(DragDrop.DropEvent, OnQueueDrop);
            // GitHub #88 rubber band. handledEventsToo: rows / ListBoxItems handle presses.
            queueDropTarget.AddHandler(PointerPressedEvent, OnQueueBandPressed, RoutingStrategies.Bubble, handledEventsToo: true);
            queueDropTarget.AddHandler(PointerMovedEvent, OnQueueBandMoved, RoutingStrategies.Bubble, handledEventsToo: true);
            queueDropTarget.AddHandler(PointerReleasedEvent, OnQueueBandReleased, RoutingStrategies.Bubble, handledEventsToo: true);
            queueDropTarget.AddHandler(PointerCaptureLostEvent, OnQueueBandCaptureLost, RoutingStrategies.Direct);
        }
    }

    private void OnQueueDragOver(object? sender, DragEventArgs e)
    {
        if (Helpers.DragFileBehavior.GetDraggedTracks(e.DataTransfer) is not { Count: > 0 }) return;
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnQueueDrop(object? sender, DragEventArgs e)
    {
        if (Helpers.DragFileBehavior.GetDraggedTracks(e.DataTransfer) is not { Count: > 0 } tracks) return;
        if (DataContext is not MainWindowViewModel vm) return;
        e.Handled = true;
        vm.Player.AddRangeToQueue(tracks.ToList());
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
