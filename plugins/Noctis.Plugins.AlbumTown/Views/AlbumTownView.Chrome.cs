using Avalonia;
using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Noctis.Plugins.AlbumTown.Town;

namespace Noctis.Plugins.AlbumTown.Views;

/// <summary>
/// The menu bar's place and size (Discord, Jafezy 11-02): along the top or down the side (the
/// "Menu bar" setting), a little triangle on its edge that folds it away and back, and full
/// screen. Also the lyric lines residents quote, read from Noctis in the background.
/// </summary>
public sealed partial class AlbumTownView
{
    public const string MenuTop = "Top (horizontal)";
    public const string MenuSide = "Side (vertical)";
    private const double SideBarWidth = 236;

    private Grid _headerGrid = null!;
    private StackPanel _titleBox = null!;
    private StackPanel _tabs = null!;
    private StackPanel _tools = null!;
    private DockPanel _root = null!;
    private Button _menuHandle = null!;
    private Path _menuArrow = null!;
    private Button _fullscreen = null!;
    private Window? _window;
    private WindowState? _stateBeforeFullScreen;
    private bool _menuVertical;
    private bool _menuCollapsed;

    /// <summary>The menu bar runs down the side (true) or along the top.</summary>
    public bool IsMenuVertical => _menuVertical;
    /// <summary>The menu bar is folded away (its triangle still shows).</summary>
    public bool IsMenuCollapsed => _menuCollapsed;
    public Border MenuBar => _header;
    public Button MenuHandle => _menuHandle;
    public bool IsFullScreen => TopLevel.GetTopLevel(this) is Window { WindowState: WindowState.FullScreen };

    /// <summary>"Top (horizontal)" or "Side (vertical)"; anything else is the top.</summary>
    public static bool IsSide(string? setting) => (setting ?? "").Trim().StartsWith("Side", StringComparison.OrdinalIgnoreCase);

    private Control BuildMenuHandle()
    {
        _menuArrow = new Path { Width = 12, Height = 7, Stretch = Stretch.Fill };
        _menuHandle = new Button
        {
            Content = _menuArrow,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        _menuHandle.Click += (_, _) => SetMenuCollapsed(!_menuCollapsed);
        PlaceMenuHandle();
        return _menuHandle;
    }

    /// <summary>Folds the menu bar away (or back). Only its triangle stays, on the edge it folded to.</summary>
    public void SetMenuCollapsed(bool collapsed)
    {
        _menuCollapsed = collapsed;
        _header.IsVisible = !collapsed;
        if (collapsed) _suggestionsBox.IsVisible = false;
        PlaceMenuHandle();
    }

    /// <summary>Puts the bar along the top or down the side.</summary>
    public void SetMenuVertical(bool vertical)
    {
        if (_headerGrid is null) return;
        _menuVertical = vertical;
        Detach(_titleBox);
        Detach(_tabs);
        Detach(_tools);
        _headerGrid.Children.Clear();
        var buttons = new Control[] { _newCrowd, _resetRooms, _fullscreen };
        if (vertical)
        {
            _tabs.Orientation = Orientation.Vertical;
            _tabs.HorizontalAlignment = HorizontalAlignment.Stretch;
            _tools.Orientation = Orientation.Vertical;
            _tools.Spacing = 10;
            _tools.VerticalAlignment = VerticalAlignment.Top;
            _gridBox.Orientation = Orientation.Vertical;
            _gridBox.Margin = default;
            _columns.HorizontalAlignment = _rows.HorizontalAlignment = HorizontalAlignment.Left;
            _zoomBox.Margin = default;
            _search.Width = SideBarWidth - 36;
            foreach (var b in buttons) { b.Margin = default; b.HorizontalAlignment = HorizontalAlignment.Left; }
            _header.Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = new StackPanel { Spacing = 16, Children = { _titleBox, _tabs, _tools } },
            };
            _header.Width = SideBarWidth;
            _header.Padding = new Thickness(18, 14);
            DockPanel.SetDock(_header, Dock.Left);
            _suggestionsBox.HorizontalAlignment = HorizontalAlignment.Left;
            _suggestionsBox.Margin = new Thickness(12, 12, 0, 0);
        }
        else
        {
            _tabs.Orientation = Orientation.Horizontal;
            _tabs.HorizontalAlignment = HorizontalAlignment.Center;
            _tools.Orientation = Orientation.Horizontal;
            _tools.Spacing = 0;
            _tools.VerticalAlignment = VerticalAlignment.Center;
            _gridBox.Orientation = Orientation.Horizontal;
            _gridBox.Margin = new Thickness(0, 0, 6, 0);
            _columns.HorizontalAlignment = _rows.HorizontalAlignment = HorizontalAlignment.Stretch;
            _zoomBox.Margin = new Thickness(0, 0, 14, 0);
            _search.Width = 240;
            foreach (var b in buttons) { b.Margin = new Thickness(8, 0, 0, 0); b.HorizontalAlignment = HorizontalAlignment.Stretch; }
            _headerGrid.Children.Add(_titleBox);
            _headerGrid.Children.Add(_tabs);
            _headerGrid.Children.Add(_tools);
            _header.Child = _headerGrid;
            _header.Width = double.NaN;
            if (!_fitHooked)
            {
                _fitHooked = true;
                _header.SizeChanged += (_, _) => FitTopBar();
            }
            FitTopBar();
            _header.Padding = new Thickness(18, 10);
            DockPanel.SetDock(_header, Dock.Top);
            _suggestionsBox.HorizontalAlignment = HorizontalAlignment.Right;
            _suggestionsBox.Margin = new Thickness(0, 8, 18, 0);
        }
        // DockPanel reads Dock when it arranges its children in order: put the bar back first.
        _root.Children.Remove(_header);
        _root.Children.Insert(0, _header);
        PlaceMenuHandle();
    }

    private bool _fitHooked;

    /// <summary>
    /// A top bar narrower than title + tabs + tools (Album Town inside Settings on a ~1700 px
    /// window, 11-03 live) used to draw them over each other: the Auto columns overflowed
    /// into the centre. The tools then drop to a second row, right-aligned.
    /// </summary>
    internal void FitTopBar()
    {
        if (_menuVertical || _headerGrid is null || _header.Child != _headerGrid) return;
        var room = _header.Bounds.Width - _header.Padding.Left - _header.Padding.Right;
        if (room <= 0) return;
        _titleBox.Measure(Size.Infinity);
        _tabs.Measure(Size.Infinity);
        _tools.Measure(Size.Infinity);
        var need = _titleBox.DesiredSize.Width + _tabs.DesiredSize.Width + _tools.DesiredSize.Width + 24;
        var wrap = need > room;
        if (wrap == (Grid.GetRow(_tools) == 1)) return;
        _headerGrid.RowDefinitions = wrap ? new RowDefinitions("Auto,Auto") : new RowDefinitions();
        Grid.SetRow(_tools, wrap ? 1 : 0);
        Grid.SetColumn(_tools, wrap ? 0 : 2);
        Grid.SetColumnSpan(_tools, wrap ? 3 : 1);
        _tools.HorizontalAlignment = wrap ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        _tools.Margin = wrap ? new Thickness(0, 8, 0, 0) : default;
    }

    private static void Detach(Control c)
    {
        if (c.Parent is Panel p) p.Children.Remove(c);
        else if (c.Parent is ContentControl cc) cc.Content = null;
        else if (c.Parent is Decorator d) d.Child = null;
    }

    /// <summary>The triangle: under a top bar (pointing up to fold it, down to bring it back),
    /// right of a side bar (pointing left, then right).</summary>
    private void PlaceMenuHandle()
    {
        var pointsBack = _menuCollapsed; // folded: the triangle points to where the bar comes from
        string data;
        if (_menuVertical)
        {
            _menuHandle.Width = 16;
            _menuHandle.Height = 44;
            _menuHandle.CornerRadius = new CornerRadius(0, 10, 10, 0);
            _menuHandle.HorizontalAlignment = HorizontalAlignment.Left;
            _menuHandle.VerticalAlignment = VerticalAlignment.Center;
            _menuArrow.Width = 7;
            _menuArrow.Height = 12;
            data = pointsBack ? "M0,0 L7,6 L0,12 Z" : "M7,0 L0,6 L7,12 Z";
        }
        else
        {
            _menuHandle.Width = 44;
            _menuHandle.Height = 16;
            _menuHandle.CornerRadius = new CornerRadius(0, 0, 10, 10);
            _menuHandle.HorizontalAlignment = HorizontalAlignment.Center;
            _menuHandle.VerticalAlignment = VerticalAlignment.Top;
            _menuArrow.Width = 12;
            _menuArrow.Height = 7;
            data = pointsBack ? "M0,0 L12,0 L6,7 Z" : "M0,7 L6,0 L12,7 Z";
        }
        _menuArrow.Data = Geometry.Parse(data);
        var name = _menuCollapsed ? "Show the menu bar" : "Hide the menu bar";
        AutomationName(_menuHandle, name);
        ToolTip.SetTip(_menuHandle, name);
    }

    private void ApplyChromeTheme(TownTheme theme)
    {
        if (_menuHandle is null) return;
        _menuHandle.Background = theme.Chrome;
        _menuArrow.Fill = theme.Muted;
    }

    // ── Full screen ──

    /// <summary>Full screen on, or back to how the window was.</summary>
    public void ToggleFullScreen()
    {
        if (TopLevel.GetTopLevel(this) is not Window w) return;
        if (w.WindowState == WindowState.FullScreen)
        {
            w.WindowState = _stateBeforeFullScreen is { } before and not WindowState.FullScreen ? before : WindowState.Normal;
            _stateBeforeFullScreen = null;
        }
        else
        {
            _stateBeforeFullScreen = w.WindowState;
            w.WindowState = WindowState.FullScreen;
        }
        UpdateFullScreenLabel();
    }

    private void UpdateFullScreenLabel()
    {
        var text = IsFullScreen ? "Exit full screen" : "Full screen";
        if (_fullscreen.Content is TextBlock label) label.Text = text;
        AutomationName(_fullscreen, text);
    }

    private void OnWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty) UpdateFullScreenLabel();
    }

    private void AttachChrome()
    {
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.PropertyChanged += OnWindowChanged;
        UpdateFullScreenLabel();
    }

    private void DetachChrome()
    {
        if (_window is not null)
        {
            _window.PropertyChanged -= OnWindowChanged;
            // Full screen was ours: leave the window as it was (the town closed or Settings moved on).
            if (_stateBeforeFullScreen is { } before && _window.WindowState == WindowState.FullScreen)
                _window.WindowState = before == WindowState.FullScreen ? WindowState.Normal : before;
        }
        _stateBeforeFullScreen = null;
        _window = null;
    }

    private void OnChromeKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 || (e.Key == Key.Escape && IsFullScreen && _stateBeforeFullScreen is not null))
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }

    // ── Lyrics the residents quote ──

    private readonly Dictionary<string, string[]> _lyrics = new(StringComparer.Ordinal);
    private readonly HashSet<string> _lyricsAsked = new(StringComparer.Ordinal);
    private bool _lyricsBusy;
    private const int MaxLyricAlbums = 400;

    /// <summary>
    /// A line from the lyrics of <paramref name="album"/>'s songs, or null. The first ask reads
    /// them from Noctis in the background (a few songs, lyrics Noctis already has: sidecar files
    /// from Lyrics Studio, stored lyrics, tags; never online), so the line comes next time.
    /// </summary>
    public string? LyricFor(TownAlbum album)
    {
        if (_lyrics.TryGetValue(album.Key, out var lines)) return lines.Length == 0 ? null : TalkLines.PickLyric(lines, _random);
        if (!_lyricsBusy && _lyrics.Count < MaxLyricAlbums && _lyricsAsked.Add(album.Key)) _ = LoadLyricsAsync(album);
        return null;
    }

    /// <summary>Lyric lines known for an album (tests).</summary>
    public IReadOnlyList<string>? LyricsOf(string key) => _lyrics.TryGetValue(key, out var l) ? l : null;

    private async Task LoadLyricsAsync(TownAlbum album)
    {
        _lyricsBusy = true;
        try
        {
            var ids = album.TrackIds;
            var start = ids.Count == 0 ? 0 : _random.Next(ids.Count);
            var lines = await Task.Run(() =>
            {
                var found = new List<string>();
                for (var i = 0; i < Math.Min(4, ids.Count) && found.Count < 60; i++)
                    found.AddRange(_host.LoadLyricLines(ids[(start + i) % ids.Count]));
                return found.ToArray();
            });
            _lyrics[album.Key] = lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _lyrics[album.Key] = Array.Empty<string>();
        }
        finally
        {
            _lyricsBusy = false;
        }
    }
}
