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
    private EventHandler<bool>? _themeChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _playerPropertyChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _topBarPropertyChangedHandler;

    public MainWindow()
    {
        InitializeComponent();

        // Initialize the application once the window is fully loaded
        Loaded += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Wire up theme switching
                _themeChangedHandler = (_, isDark) =>
                {
                    if (Avalonia.Application.Current is App app)
                        app.SetTheme(isDark);
                };
                vm.Settings.ThemeChanged += _themeChangedHandler;

                await vm.InitializeAsync();
                RestoreWindowPlacement(vm.Settings.GetSettings());

                // Wire up albums view-mode toggle visuals
                _topBarPropertyChangedHandler = (_, e) =>
                {
                    if (e.PropertyName == nameof(TopBarViewModel.IsAlbumsCoverFlowMode))
                        UpdateAlbumsToggleVisuals(vm.TopBar.IsAlbumsCoverFlowMode);
                };
                vm.TopBar.PropertyChanged += _topBarPropertyChangedHandler;
                UpdateAlbumsToggleVisuals(vm.TopBar.IsAlbumsCoverFlowMode);

                // Initialize taskbar thumbnail buttons (Previous / Play-Pause / Next)
                InitializeTaskbarButtons(vm);
            }
        };

        // Close queue popup on outside click (tunnel so it fires before button commands)
        AddHandler(PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);

        // Volume control via mouse wheel and keyboard
        KeyDown += OnWindowKeyDown;

        // Accept dragging audio files/folders from Explorer into the app.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnWindowDrop, RoutingStrategies.Tunnel);

        Closing += (_, _) => CaptureWindowPlacement();
        Closed += OnWindowClosed;
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

        // Persist geometry immediately so next launch restores the latest size/state.
        try { Task.Run(vm.Settings.SaveAsync).GetAwaiter().GetResult(); } catch { }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _taskbar?.Dispose();

        // Unsubscribe from all event handlers to prevent memory leak
        if (DataContext is MainWindowViewModel vm)
        {
            if (_themeChangedHandler != null)
                vm.Settings.ThemeChanged -= _themeChangedHandler;

            if (_playerPropertyChangedHandler != null)
                vm.Player.PropertyChanged -= _playerPropertyChangedHandler;

            if (_topBarPropertyChangedHandler != null)
                vm.TopBar.PropertyChanged -= _topBarPropertyChangedHandler;
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

            // Update play/pause icon when playback state changes
            _playerPropertyChangedHandler = (_, e) =>
            {
                if (e.PropertyName == nameof(PlayerViewModel.State))
                {
                    _taskbar?.UpdatePlayPauseState(vm.Player.State == PlaybackState.Playing);
                }
            };
            vm.Player.PropertyChanged += _playerPropertyChangedHandler;
        }
        catch
        {
            // Non-critical — taskbar buttons are a nice-to-have
        }
    }

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        var paths = GetDroppedLocalPaths(e.Data);
        var hasImportable = paths.Any(IsImportablePath);
        e.DragEffects = hasImportable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnWindowDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
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

    private static List<string> GetDroppedLocalPaths(IDataObject data)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Preferred Avalonia API: handles both Files and FileNames payloads.
            foreach (var item in data.GetFiles() ?? Enumerable.Empty<IStorageItem>())
            {
                var uri = item.Path;
                if (uri.IsFile)
                    TryAddPath(uri.LocalPath);
                else
                    TryAddPath(uri.ToString());
            }

            // Defensive fallback for non-standard payloads.
            if (data.Contains(DataFormats.Files) && data.Get(DataFormats.Files) is IEnumerable<string> rawFiles)
            {
                foreach (var raw in rawFiles)
                    TryAddPath(raw);
            }

            // Some drag sources provide file names only.
            if (data.Contains(DataFormats.FileNames) && data.Get(DataFormats.FileNames) is IEnumerable<string> fileNames)
            {
                foreach (var fileName in fileNames)
                    TryAddPath(fileName);
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
        }
    }

    // ── Albums toggle visuals ──

    private void UpdateAlbumsToggleVisuals(bool isCoverFlow)
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
    }

    // ── Queue popup event handlers ──

    private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Clear search box focus when clicking outside it
        var searchBox = this.FindControl<TextBox>("SearchBox");
        if (searchBox is { IsFocused: true })
        {
            var pillBorder = (searchBox.Parent as Visual)?.GetVisualParent();
            bool insidePill = false;
            if (e.Source is Visual clickSource && pillBorder != null)
            {
                Visual? v = clickSource;
                while (v != null)
                {
                    if (ReferenceEquals(v, pillBorder)) { insidePill = true; break; }
                    v = v.GetVisualParent();
                }
            }
            if (!insidePill)
                this.Focus(NavigationMethod.Pointer);
        }

        if (DataContext is not MainWindowViewModel vm) return;
        if (!vm.Player.IsQueuePopupOpen) return;
        DebugLogger.Info(DebugLogger.Category.ContextMenu, "MainWindow.GlobalPointerPressed(Tunnel)",
            $"queueOpen=true, source={e.Source?.GetType().Name}, will close queue popup");

        // Don't close if click is inside the queue popup panel
        var panel = this.FindControl<Border>("QueuePopupPanel");
        if (panel is { IsVisible: true })
        {
            var pos = e.GetPosition(panel);
            if (pos.X >= 0 && pos.Y >= 0 &&
                pos.X <= panel.Bounds.Width && pos.Y <= panel.Bounds.Height)
                return;
        }

        // Don't close if click is on the queue toggle button (let its command handle toggle)
        var source = e.Source as Visual;
        while (source != null)
        {
            if (source is Button { Tag: "QueueToggle" })
                return;
            source = source.GetVisualParent();
        }

        vm.Player.IsQueuePopupOpen = false;
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

    // ── Queue drag-to-reorder ──

    private Point _queueDragStart;
    private bool _queueDragStarted;
    private Track? _queueDragTrack;
    private ListBoxItem? _dragSourceItem;

    private void OnQueueItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Grid grid) return;
        if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;

        _queueDragStart = e.GetPosition(grid);
        _queueDragStarted = false;
        _queueDragTrack = grid.Tag as Track;
    }

    private async void OnQueueItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Grid grid) return;
        if (_queueDragTrack == null || _queueDragStarted) return;
        if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;

        var pos = e.GetPosition(grid);
        if (Math.Abs(pos.X - _queueDragStart.X) < 8 && Math.Abs(pos.Y - _queueDragStart.Y) < 8)
            return;

        _queueDragStarted = true;

        // Apply drag visual: reduce opacity of the source item
        var listBox = this.FindControl<ListBox>("QueuePopupListBox");
        if (listBox != null)
        {
            var idx = (DataContext as MainWindowViewModel)?.Player.UpNext.IndexOf(_queueDragTrack);
            if (idx is >= 0)
            {
                _dragSourceItem = listBox.ContainerFromIndex(idx.Value) as ListBoxItem;
                if (_dragSourceItem != null)
                    _dragSourceItem.Opacity = 0.35;
            }
        }

        var data = new DataObject();
        data.Set("NoctisQueueTrack", _queueDragTrack);

        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        catch
        {
            // Drag cancelled or failed — non-critical
        }
        finally
        {
            ResetDragVisuals();
            _queueDragTrack = null;
            _queueDragStarted = false;
        }
    }

    private void ResetDragVisuals()
    {
        if (_dragSourceItem != null)
        {
            _dragSourceItem.Opacity = 1.0;
            _dragSourceItem = null;
        }

        var indicator = this.FindControl<Border>("QueueDropIndicator");
        if (indicator != null)
            indicator.IsVisible = false;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Wire up drag-drop handlers on the queue popup ListBox
        var listBox = this.FindControl<ListBox>("QueuePopupListBox");
        if (listBox != null)
        {
            listBox.AddHandler(DragDrop.DragOverEvent, OnQueueDragOver);
            listBox.AddHandler(DragDrop.DropEvent, OnQueueDrop);
            listBox.AddHandler(DragDrop.DragLeaveEvent, OnQueueDragLeave);
        }
    }

    private void OnQueueDragOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains("NoctisQueueTrack"))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;

        // Update drop indicator position
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

    private void OnQueueDragLeave(object? sender, DragEventArgs e)
    {
        var indicator = this.FindControl<Border>("QueueDropIndicator");
        if (indicator != null)
            indicator.IsVisible = false;
    }

    private void OnQueueDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (e.Data.Get("NoctisQueueTrack") is not Track draggedTrack) return;

        var fromIndex = vm.Player.UpNext.IndexOf(draggedTrack);
        if (fromIndex < 0) return;

        // Determine the drop target index from the pointer position
        var listBox = this.FindControl<ListBox>("QueuePopupListBox");
        if (listBox == null) return;

        var toIndex = GetDropTargetIndex(listBox, e);
        if (toIndex < 0) toIndex = vm.Player.UpNext.Count - 1;
        if (toIndex >= vm.Player.UpNext.Count) toIndex = vm.Player.UpNext.Count - 1;

        if (fromIndex != toIndex)
            vm.Player.MoveInQueue(fromIndex, toIndex);

        ResetDragVisuals();
        e.Handled = true;
    }

    private static int GetDropTargetIndex(ListBox listBox, DragEventArgs e)
    {
        var pos = e.GetPosition(listBox);

        // Walk visible ListBoxItems to find which one the pointer is over
        for (int i = 0; i < listBox.ItemCount; i++)
        {
            var container = listBox.ContainerFromIndex(i);
            if (container == null) continue;

            var itemBounds = container.Bounds;
            var itemPos = container.TranslatePoint(new Point(0, 0), listBox);
            if (itemPos == null) continue;

            var top = itemPos.Value.Y;
            var bottom = top + itemBounds.Height;
            var midpoint = top + itemBounds.Height / 2;

            if (pos.Y < midpoint && pos.Y >= top)
                return i;
            if (pos.Y >= midpoint && pos.Y < bottom)
                return i;
        }

        // Below all items — drop at the end
        return listBox.ItemCount - 1;
    }
}
