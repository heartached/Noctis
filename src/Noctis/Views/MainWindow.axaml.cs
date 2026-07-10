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
    private TrayIcon? _trayIcon;
    private bool _exitRequestedFromTray;
    private EventHandler<string>? _themeChangedHandler;
    private EventHandler<string>? _accentChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _playerPropertyChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _topBarPropertyChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _mainVmPropertyChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _currentTrackPropertyChangedHandler;
    private Track? _trackedFavoriteTrack;
    private Border? _sidebarWrapper;
    private Border? _lyricsPanelWrapper;
    private DockPanel? _contentDockPanel;
    private DockPanel? _rootPanel;
    private MiniPlayerWindow? _miniPlayer;
    private Action? _singleInstanceActivationHandler;

    /// <summary>
    /// Opens the compact always-on-top mini player (hiding the main window), or closes
    /// it if it's already open. Closing the mini player restores the main window.
    /// Triggered by clicking the cover art in the bottom player bar.
    /// </summary>
    public void ToggleMiniPlayer()
    {
        if (_miniPlayer != null)
        {
            _miniPlayer.Close(); // Closed handler below restores the main window
            return;
        }

        if (DataContext is not MainWindowViewModel vm) return;

        _miniPlayer = new MiniPlayerWindow { DataContext = vm.Player };
        // The mini player's DataContext is the PlayerViewModel, which has no view of
        // Settings, so the animated-cover gate is bound here (live, so toggling the
        // setting while the mini player is open takes effect immediately).
        _miniPlayer.AnimatedArt.Bind(
            Noctis.Controls.AnimatedCoverImage.IsActiveProperty,
            new Avalonia.Data.Binding(nameof(SettingsViewModel.EnableAnimatedCovers)) { Source = vm.Settings });
        _miniPlayer.Closed += OnMiniPlayerClosed;

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

    public MainWindow()
    {
        InitializeComponent();

        // Initialize the application once the window is fully loaded
        Loaded += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Wire up theme switching
                _themeChangedHandler = (_, themeKey) =>
                {
                    if (Avalonia.Application.Current is App app)
                        app.SetTheme(themeKey);
                };
                vm.Settings.ThemeChanged += _themeChangedHandler;

                _accentChangedHandler = (_, hex) =>
                {
                    if (Avalonia.Application.Current is App app)
                        app.SetAccent(hex);
                };
                vm.Settings.AccentChanged += _accentChangedHandler;

                // Load settings first so window placement is restored before the
                // rest of init runs (avoids a visible resize jump on startup).
                await vm.Settings.LoadAsync();
                RestoreWindowPlacement(vm.Settings.GetSettings());

                await vm.InitializeAsync();

                // Wire up albums view-mode toggle visuals
                _topBarPropertyChangedHandler = (_, e) =>
                {
                    if (e.PropertyName is nameof(TopBarViewModel.IsCoverFlowMode) or nameof(TopBarViewModel.IsCollageMode))
                        UpdateViewModeToggleVisuals(vm.TopBar.IsCoverFlowMode, vm.TopBar.IsCollageMode);
                };
                vm.TopBar.PropertyChanged += _topBarPropertyChangedHandler;
                UpdateViewModeToggleVisuals(vm.TopBar.IsCoverFlowMode, vm.TopBar.IsCollageMode);

                // Wire lyrics panel + sidebar hover
                _sidebarWrapper = this.FindControl<Border>("SidebarWrapper");
                _lyricsPanelWrapper = this.FindControl<Border>("LyricsPanelWrapper");
                _contentDockPanel = this.FindControl<DockPanel>("ContentDockPanel");
                _rootPanel = this.FindControl<DockPanel>("RootPanel");
                _mainVmPropertyChangedHandler = (s, e) =>
                {
                    var mainVm2 = (MainWindowViewModel)s!;
                    if (e.PropertyName == nameof(MainWindowViewModel.IsLyricsPanelOpen))
                    {
                        if (_lyricsPanelWrapper != null) _lyricsPanelWrapper.Width = mainVm2.IsLyricsPanelOpen ? 356 : 0;
                    }
                    if (e.PropertyName == nameof(MainWindowViewModel.IsLyricsViewActive))
                    {
                        if (_contentDockPanel != null)
                        {
                            var lyricsActive = mainVm2.IsLyricsViewActive;
                            Grid.SetRow(_contentDockPanel, lyricsActive ? 0 : 1);
                            Grid.SetRowSpan(_contentDockPanel, lyricsActive ? 2 : 1);
                        }
                        // Restore sidebar when leaving lyrics view
                        if (!mainVm2.IsLyricsViewActive && mainVm2.IsSidebarHidden)
                        {
                            mainVm2.IsSidebarHidden = false;
                            if (_sidebarWrapper != null) _sidebarWrapper.Width = 60;
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

                // Initialize taskbar thumbnail buttons (Previous / Play-Pause / Next)
                InitializeTaskbarButtons(vm);

                // System tray icon (minimize/close-to-tray + playback controls)
                InitializeTrayIcon(vm);

                // Windows media overlay (SMTC): now-playing card on the media-key
                // flyout/lock screen + media-key control (no-op off Windows).
                _smtc = new SmtcService(vm.Player, TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);

                // Launched at login with "start minimized to tray" on (encoded in the
                // autostart args, so it needs no async settings load) → hide to the tray
                // instead of showing the window. Guarded on _trayIcon != null so a
                // platform where the tray failed to initialize never leaves the app
                // running with no window AND no tray icon (i.e. invisible).
                if (App.StartMinimizedAtLogin && _trayIcon != null)
                {
                    Hide();
                }
            }
        };

        // Close queue popup on outside click (tunnel so it fires before button commands)
        AddHandler(PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);

        // Volume control via mouse wheel and keyboard
        KeyDown += OnWindowKeyDown;

        // Drag-drop handlers are registered in OnLoaded (after visual tree is ready).

        Closing += OnMainWindowClosing;
        Closed += OnWindowClosed;

        // A second launch (taskbar/pinned icon while we sit in the tray) signals
        // the single-instance pipe — surface this window instead.
        _singleInstanceActivationHandler = () => Dispatcher.UIThread.Post(ShowFromTray);
        Helpers.SingleInstanceGuard.ActivationRequested += _singleInstanceActivationHandler;

        // Minimize-to-tray: hide the window when it minimizes and the setting is on.
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty || WindowState != WindowState.Minimized)
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
            Position = new PixelPoint((int)Math.Round(settings.WindowX), (int)Math.Round(settings.WindowY));
        }

        if (Enum.TryParse<WindowState>(settings.MainWindowState, out var savedState))
        {
            WindowState = savedState == WindowState.Minimized ? WindowState.Normal : savedState;
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
        }
        catch (Exception ex)
        {
            // Tray support is best-effort (e.g. some Linux DEs have no tray).
            DebugLogger.Error(DebugLogger.Category.UI, "TrayIcon.Init", ex.Message);
        }
    }

    private void ShowFromTray()
    {
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

        settings.MainWindowState = WindowState == WindowState.Minimized
            ? WindowState.Normal.ToString()
            : WindowState.ToString();

        // Persist geometry in the background so window close isn't blocked on disk I/O.
        // Closing is cooperative — this fires before the window tears down, and the
        // write is atomic (temp file + Move) so a crash during shutdown is safe.
        _ = Task.Run(async () =>
        {
            try { await vm.Settings.SaveAsync(); } catch { }
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
        if (_trayIcon != null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        // Unsubscribe from all event handlers to prevent memory leak
        if (DataContext is MainWindowViewModel vm)
        {
            if (_themeChangedHandler != null)
                vm.Settings.ThemeChanged -= _themeChangedHandler;

            if (_accentChangedHandler != null)
                vm.Settings.AccentChanged -= _accentChangedHandler;

            if (_playerPropertyChangedHandler != null)
                vm.Player.PropertyChanged -= _playerPropertyChangedHandler;

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
                else if (e.PropertyName == nameof(PlayerViewModel.IsQueuePopupOpen))
                {
                    // Queue popup and lyrics panel share the right edge — mutual exclusion.
                    if (vm.Player.IsQueuePopupOpen)
                        vm.IsLyricsPanelOpen = false;
                }
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
                // Close queue popup if open, otherwise clear search
                if (vm.Player.IsQueuePopupOpen)
                {
                    vm.Player.IsQueuePopupOpen = false;
                    e.Handled = true;
                }
                else
                {
                    vm.TopBar.ClearSearchCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.Space when e.KeyModifiers == KeyModifiers.None:
                // Only toggle play/pause if the focused element is NOT a TextBox
                if (FocusManager?.GetFocusedElement() is not TextBox)
                {
                    vm.Player.PlayPauseCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.D when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                vm.ToggleDebugPanel();
                e.Handled = true;
                break;
            case Key.K when e.KeyModifiers == KeyModifiers.Control:
                _ = vm.OpenCommandPaletteAsync();
                e.Handled = true;
                break;
        }
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

    private void OnQueueClearClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.Player.ClearQueue();
    }

    private void OnQueueItemDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not ListBox listBox) return;

        var index = listBox.SelectedIndex;
        if (index < 0) return;

        vm.Player.PlayFromUpNextAt(index);
        vm.Player.IsQueuePopupOpen = false;
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
        if (_queueDragSourceIndex < 0) return;

        var posInListBox = e.GetPosition(listBox);
        var toIndex = GetQueueDropTargetIndex(listBox, posInListBox);
        if (toIndex < 0) toIndex = vm.Player.UpNext.Count - 1;
        if (toIndex >= vm.Player.UpNext.Count) toIndex = vm.Player.UpNext.Count - 1;

        if (_queueDragSourceIndex != toIndex)
            vm.Player.MoveInQueue(_queueDragSourceIndex, toIndex);
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
