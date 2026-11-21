using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LibraryAlbumsView : UserControl, ITrackSelectionSource
{
    private LibraryAlbumsViewModel? _vm;
    private EventHandler? _pendingScrollRestore;
    /// <summary>The VM FilterKey the grid last reset its scroll for (see OnFilteredRowsChanged).</summary>
    private string? _scrollResetFilterKey;
    // Track selection by the Album itself (not the tile Button) so that row
    // virtualization recycling the tile Buttons on scroll doesn't drop the
    // ctrl-selected highlight. See MultiSelectHelper's "Data-tracked album-tile variants".
    private readonly HashSet<Album> _selectedAlbums = new();
    /// <summary>The album a Shift+click spans from: the last one clicked (GitHub #226).</summary>
    private Album? _rangeAnchor;

    /// <summary>The Ctrl-selection, for window shortcuts (GitHub #131 ReplayGain).</summary>
    public IReadOnlyList<Track> GetSelectedTracks() => TrackSelection.FromAlbums(_selectedAlbums);

    public LibraryAlbumsView()
    {
        InitializeComponent();
        AlbumCarousel.AlbumContextRequested += OnCarouselAlbumContextRequested;
        MasonryPanel.ElementPrepared += OnMasonryElementPrepared;

        DataContextChanged += OnDataContextChanged;
        AddHandler(PointerPressedEvent, OnTilePointerPressed, RoutingStrategies.Tunnel);
        // GitHub #226: rubber-band selection from empty space (see "Rubber band" below).
        AddHandler(PointerMovedEvent, OnBandPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnBandPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnBandCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        // Forward Ctrl+A from the window so it works without first clicking a tile.
        _ = new WindowKeyForwarder(this, OnViewKeyDown);
        MultiSelectHelper.KeepRowsUnselected(AlbumListBox);
    }

    private void OnTilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var source = e.Source as Control;
        while (source != null && !(source is Button b && b.Classes.Contains("album-tile")))
            source = source.Parent as Control;
        if (source is not Button tile)
        {
            // GitHub #209: a click beside the tiles clears the selection, as in the queue.
            if (_selectedAlbums.Count > 0 && MultiSelectHelper.IsPlainBackgroundPress(e, rowsAreLayout: true))
                ClearSelection();
            ArmBand(e, paddingTile: null);
            return;
        }
        if (tile.DataContext is not Album album) return;

        // GitHub #226: Shift+click spans from the last clicked album, in grid order.
        if (MultiSelectHelper.IsRangePress(e) && _vm != null)
        {
            e.Handled = true;
            _rangeAnchor = MultiSelectHelper.SelectRange(_vm.CarouselAlbums, _rangeAnchor, album,
                e.KeyModifiers.HasFlag(PlatformHelper.PrimaryModifier), _selectedAlbums);
            foreach (var t in CollectAlbumTiles())
                MultiSelectHelper.SyncAlbumTileVisual(t, _selectedAlbums, animate: true);
            Focus();
            return;
        }

        // A drag from the gap between covers draws a rubber band (GitHub #226).
        var inPadding = IsInTilePadding(tile, e);
        if (inPadding)
            ArmBand(e, tile);

        // GitHub #209: the gap between two covers is the tiles' own padding. With a selection
        // up, a plain press there is a click beside the albums: it clears the selection and
        // does not open the tile it happens to belong to.
        if (_selectedAlbums.Count > 0 && inPadding
            && e.GetCurrentPoint(null).Properties.IsLeftButtonPressed
            && !e.KeyModifiers.HasFlag(PlatformHelper.PrimaryModifier)
            && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            ClearSelection();
            e.Handled = true;
            return;
        }

        if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            _rangeAnchor = album;

        if (!MultiSelectHelper.HandleAlbumTileClickByData(tile, album, e, _selectedAlbums)
            && !e.KeyModifiers.HasFlag(PlatformHelper.PrimaryModifier)
            && e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            // Plain left-click clears any existing selection (mirrors prior behavior).
            MultiSelectHelper.ClearAlbumSelectionsByData(_selectedAlbums, CollectAlbumTiles());
        }

        // Ensure this view has focus so Ctrl+A reaches OnViewKeyDown
        if (_selectedAlbums.Count > 0)
            Focus();
    }

    /// <summary>The press landed in the tile's padding (the gap around its cover and caption).
    /// With one row height (1.6.9 spacing) a one-line caption leaves room under it inside the
    /// tile: that counts as gap too, so the press is tested against the cover + caption.</summary>
    private static bool IsInTilePadding(Button tile, PointerPressedEventArgs e)
    {
        if (tile.Content is Control content && content.Bounds.Width > 0 && content.Bounds.Height > 0)
        {
            var q = e.GetPosition(content);
            return q.X < 0 || q.Y < 0 || q.X > content.Bounds.Width || q.Y > content.Bounds.Height;
        }
        var p = e.GetPosition(tile);
        var pad = tile.Padding;
        return p.X < pad.Left || p.Y < pad.Top
            || p.X > tile.Bounds.Width - pad.Right || p.Y > tile.Bounds.Height - pad.Bottom;
    }

    /// <summary>Re-apply the ctrl-selected visual as tiles are (re)realized on scroll.</summary>
    private void OnAlbumTileLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button tile) return;
        MultiSelectHelper.SyncAlbumTileVisual(tile, _selectedAlbums);
        // Album Hover Zoom (GitHub #218): Loaded can fire again on re-attach, so re-subscribe.
        tile.PointerEntered -= OnTileHoverEntered;
        tile.PointerEntered += OnTileHoverEntered;
        // A grid row re-used for other albums (TileRow) re-binds its tiles instead of loading them.
        tile.DataContextChanged -= OnAlbumTileRebound;
        tile.DataContextChanged += OnAlbumTileRebound;
    }

    private void OnAlbumTileRebound(object? sender, EventArgs e)
    {
        if (sender is Button tile)
            MultiSelectHelper.SyncAlbumTileVisual(tile, _selectedAlbums);
    }

    // ── Album Hover Zoom (GitHub #218) ──
    // The hovered tile's grow + glow is pure style (Styles.axaml, .hover-zoom); no other tile
    // moves (GitHub #250). Two things the style cannot do are done here when a tile is entered:
    // - Lift it above its neighbours. Its .hover-zoom ZIndex sits on the Button, but in the row
    //   grid each Button is the only child of a TileRow ContentPresenter, so that ZIndex sorted
    //   nothing: the tiles to its right and the row below drew over its grown edge and glow.
    //   The presenter and the row's ListBoxItem are lifted instead (left up until the next
    //   hover, so the shrink back also plays on top).
    // - Grow away from the grid's edges. Grown from its centre, a tile in the first row or an
    //   edge column ran past the scroller's viewport and was clipped (5px of cover at 2400px).

    private const double HoverZoomScale = 1.04;
    private readonly List<Visual> _hoverLifted = new();

    private void OnTileHoverEntered(object? sender, PointerEventArgs e)
    {
        if (_vm?.IsHoverZoom != true || sender is not Button tile) return;
        LiftHoveredTile(tile);
        AnchorHoveredTile(tile);
    }

    private void LiftHoveredTile(Button tile)
    {
        foreach (var v in _hoverLifted) v.ZIndex = 0;
        _hoverLifted.Clear();
        if (tile.GetVisualParent() is ContentPresenter { Parent: TileRow } presenter)
            _hoverLifted.Add(presenter);
        if (tile.FindAncestorOfType<ListBoxItem>() is { } row)
            _hoverLifted.Add(row);
        foreach (var v in _hoverLifted) v.ZIndex = 1;
    }

    private void AnchorHoveredTile(Button tile)
    {
        if (ActiveScroller()?.Content is not Visual content || tile.GetVisualParent() is not Visual parent) return;
        if (parent.TranslatePoint(tile.Bounds.Position, content) is not { } at) return;
        var growX = tile.Bounds.Width * (HoverZoomScale - 1) / 2;
        var growY = tile.Bounds.Height * (HoverZoomScale - 1) / 2;
        var left = at.X < growX;
        var right = content.Bounds.Width - (at.X + tile.Bounds.Width) < growX;
        var x = left == right ? 0.5 : left ? 0 : 1;
        var y = at.Y < growY ? 0 : 0.5;
        tile.RenderTransformOrigin = new RelativePoint(x, y, RelativeUnit.Relative);
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _selectedAlbums.Count > 0)
        {
            ClearSelection();
            e.Handled = true;
            return;
        }

        var allAlbums = _vm?.FilteredAlbumRows.OfType<AlbumRow>().SelectMany(r => r.Albums) ?? Enumerable.Empty<Album>();
        MultiSelectHelper.HandleAlbumSelectAllByData(e, allAlbums, CollectAlbumTiles(), _selectedAlbums);
    }

    private void ClearSelection()
    {
        MultiSelectHelper.ClearAlbumSelectionsByData(_selectedAlbums, CollectAlbumTiles());
        if (DataContext is LibraryAlbumsViewModel vm) vm.CtrlSelectedAlbums = new List<Album>();
    }

    private List<Button> CollectAlbumTiles()
    {
        var allTiles = new List<Button>();
        foreach (var desc in AlbumListBox.GetVisualDescendants())
        {
            if (desc is Button b && b.Classes.Contains("album-tile"))
                allTiles.Add(b);
        }
        foreach (var c in MasonryPanel.RealizedElements)
            if (c is Button b) allTiles.Add(b);
        return allTiles;
    }

    /// <summary>The scroller of the layout on screen: the row grid's or the Masonry one (#213).</summary>
    private ScrollViewer? ActiveScroller() => _vm?.IsMasonryView == true
        ? MasonryScroller
        : AlbumListBox.FindDescendantOfType<ScrollViewer>();

    /// <summary>A Masonry tile recycled onto another album takes that album's selection at once.</summary>
    private void OnMasonryElementPrepared(object? sender, Control element)
    {
        if (element is Button tile)
            MultiSelectHelper.SyncAlbumTileVisual(tile, _selectedAlbums);
    }

    // ── Artist page Songs section: shared track context menu (same pattern as HomeView) ──

    private TrackContextMenuBuilder? _trackMenuBuilder;
    /// <summary>Culture the track menu's strings were read in (they are read once, at Build).</summary>
    private string? _trackMenuCulture;
    private Control? _menuOwner;

    private void OnArtistSongContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control owner) return;
        // Song pills wrap their Track in a TopSongRow for rank display.
        if (owner.DataContext is not TopSongRow row) return;
        if (DataContext is not LibraryAlbumsViewModel vm) return;

        // v2 layout (10-09 redesign); rebuilt after a live language switch so it follows it.
        if (_trackMenuBuilder == null || _trackMenuCulture != Loc.Instance.Culture.Name)
        {
            _trackMenuCulture = Loc.Instance.Culture.Name;
            _trackMenuBuilder = new TrackContextMenuBuilder();
            _trackMenuBuilder.Build(Loc.T("LibraryAlbums.RemoveFromLibrary"), null, this, v2: true, removeIsDanger: true);
        }

        _trackMenuBuilder.Bind(
            row.Track,
            playCommand: vm.PlayArtistSongCommand,
            shuffleCommand: vm.ShuffleArtistSongsCommand,
            playNextCommand: vm.PlayNextTrackCommand,
            addToQueueCommand: vm.AddTrackToQueueCommand,
            addToPlaylistCommand: vm.AddTrackToNewPlaylistCommand,
            toggleFavoriteCommand: vm.ToggleTrackFavoriteCommand,
            openMetadataCommand: vm.OpenTrackMetadataCommand,
            searchLyricsCommand: vm.SearchLyricsTrackCommand,
            showInExplorerCommand: vm.ShowInExplorerTrackCommand,
            removeCommand: vm.RemoveTrackFromLibraryCommand);

        OpenMenu(_trackMenuBuilder.Menu, owner);
        e.Handled = true;
    }

    private void OpenMenu(ContextMenu menu, Control owner)
    {
        // Close any menu still open from a previous rapid right-click so menus
        // don't stack on top of each other.
        ContextMenuCoordinator.NotifyOpening(menu);
        MenuOpenAnimation.CloseNow(menu);

        // Detach from the previous owner so Open() doesn't throw
        // "Cannot show ContextMenu on a different control".
        if (_menuOwner != null && !ReferenceEquals(_menuOwner, owner))
            _menuOwner.ContextMenu = null;
        if (menu.Parent is Control prev && !ReferenceEquals(prev, owner))
            prev.ContextMenu = null;

        _menuOwner = owner;
        owner.ContextMenu = menu;
        menu.Placement = PlacementMode.Pointer;
        menu.Open(owner);
    }

    // ── Album tiles: one shared v2 menu (10-09 redesign), bound to the tile on open ──
    // Replaces the per-tile XAML ContextMenu: same commands and parameters, plus the
    // album's Lyrics Background Video ▸ and Search Lyrics the XAML menu lacked.

    private AlbumContextMenuBuilder? _albumMenuBuilder;
    /// <summary>Culture the menu's strings were read in (they are read once, at Build).</summary>
    private string? _albumMenuCulture;

    private void OnAlbumTileContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Button tile && OpenAlbumMenu(tile,
                e.TryGetPosition(tile, out var at) ? tile.PointToScreen(at) : null))
            e.Handled = true;
    }

    /// <summary>GitHub #178: the carousel's cards get the grid tile's album menu.</summary>
    private void OnCarouselAlbumContextRequested(object? sender, Album album)
    {
        if (sender is Control owner)
            OpenAlbumMenu(album, owner, null);
    }

    /// <param name="anchor">Screen point the menu grows out of (the click), or null.</param>
    private bool OpenAlbumMenu(Button tile, PixelPoint? anchor) =>
        tile.DataContext is Album album && OpenAlbumMenu(album, tile, anchor);

    private bool OpenAlbumMenu(Album album, Control tile, PixelPoint? anchor)
    {
        if (DataContext is not LibraryAlbumsViewModel vm) return false;

        // Push ctrl-selected albums to ViewModel so commands can operate on all of them
        vm.CtrlSelectedAlbums = _selectedAlbums.ToList();

        // Rebuilt after a live language switch so the menu follows it.
        if (_albumMenuBuilder == null || _albumMenuCulture != Loc.Instance.Culture.Name)
        {
            _albumMenuCulture = Loc.Instance.Culture.Name;
            _albumMenuBuilder = new AlbumContextMenuBuilder();
            _albumMenuBuilder.Build(Loc.T("LibraryAlbums.RemoveFromLibrary"), this, v2: true, removeIsDanger: true);
            // Trial (10-09): this menu alone opens with the "pop" motion instead of the rise-up.
            MenuOpenAnimation.SetPop(_albumMenuBuilder.Menu, true);
        }
        MenuOpenAnimation.SetPopAnchor(_albumMenuBuilder.Menu, anchor);

        _albumMenuBuilder.Bind(
            album,
            playCommand: vm.PlayAlbumCommand,
            shuffleCommand: vm.ShuffleAlbumCommand,
            playNextCommand: vm.PlayNextCommand,
            addToQueueCommand: vm.AddToQueueCommand,
            addToPlaylistCommand: vm.AddToNewPlaylistCommand,
            toggleFavoritesCommand: vm.ToggleAlbumFavoritesCommand,
            openMetadataCommand: vm.OpenMetadataCommand,
            showInExplorerCommand: vm.ShowInExplorerCommand,
            removeCommand: vm.RemoveFromLibraryCommand,
            convertCommand: vm.ConvertAlbumCommand,
            scanReplayGainCommand: vm.ScanAlbumReplayGainCommand,
            snoozeCommand: vm.SnoozeAlbumForMonthCommand);
        _albumMenuBuilder.BindSendToFolder(album, vm.SendAlbumToFolderCommand); // GitHub #121
        _albumMenuBuilder.BindFindLyrics(album, vm.FindLyricsAlbumCommand); // GitHub #195
        _albumMenuBuilder.BindGroupAlbums(album, vm.GroupIntoOneAlbumCommand); // GitHub #178
        _albumMenuBuilder.BindCarouselStyle(album, AlbumCarouselStyleOverrides.For(album), vm.IsCarouselAvailable); // GitHub #233

        OpenMenu(_albumMenuBuilder.Menu, tile);
        return true;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.FilteredAlbumRows.CollectionChanged -= OnFilteredRowsChanged;
            _vm.CtrlSelectionConsumed -= OnCtrlSelectionConsumed;
        }

        _vm = DataContext as LibraryAlbumsViewModel;
        if (_vm != null)
        {
            _vm.FilteredAlbumRows.CollectionChanged += OnFilteredRowsChanged;
            _vm.CtrlSelectionConsumed += OnCtrlSelectionConsumed;
            _scrollResetFilterKey = _vm.FilterKey;
        }
    }

    /// <summary>A menu command used the selection: drop it here too (GitHub #178).</summary>
    private void OnCtrlSelectionConsumed(object? sender, EventArgs e) =>
        MultiSelectHelper.ClearAlbumSelectionsByData(_selectedAlbums, CollectAlbumTiles());

    private void OnFilteredRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // A rebuild can replace the Album objects. Selected ones no longer shown can't be seen
        // or unselected, so they must not ride along into the next command (GitHub #178); the
        // ones still shown stay selected under their new objects, matched by album id (GitHub
        // #225: each library update after launch rebuilt the albums and lost the selection).
        if (_vm != null && (_selectedAlbums.Count > 0 || _rangeAnchor != null))
        {
            var shown = new Dictionary<Guid, Album>();
            foreach (var a in _vm.FilteredAlbumRows.OfType<AlbumRow>().SelectMany(r => r.Albums))
                shown.TryAdd(a.Id, a);
            var kept = _selectedAlbums.Select(a => shown.GetValueOrDefault(a.Id)).OfType<Album>().ToList();
            _selectedAlbums.Clear();
            _selectedAlbums.UnionWith(kept);
            _rangeAnchor = _rangeAnchor == null ? null : shown.GetValueOrDefault(_rangeAnchor.Id);
        }

        // Scroll to top when an active filter (search text, chips or artist) changes, not on
        // every rebuild: a library reload or column-count change re-fills the same results
        // and keeps the user's place.
        // BUT skip if a scroll restore is pending (returning from album detail)
        if (_vm == null || _vm.FilterKey == _scrollResetFilterKey)
            return;
        _scrollResetFilterKey = _vm.FilterKey;

        if (!_vm.HasActiveFilter && !_vm.IsPageFiltered)
            return;
        if (_pendingScrollRestore != null)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            var sv = ActiveScroller();
            if (sv != null)
                sv.Offset = new Vector(0, 0);
        }, DispatcherPriority.Background);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        if (e.NewSize.Width <= 0 || DataContext is not LibraryAlbumsViewModel vm)
            return;

        // Column count and tile size (AlbumGridMetrics.TileChrome around each cover) are
        // computed in the view model, which folds in the cover-size setting.
        var usable = GridUsableWidth(e.NewSize.Width);
        // The scroll bar's width is only known once the list has been laid out: settle on
        // the measured value after this pass (a no-op when the guess was right).
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is LibraryAlbumsViewModel v && Bounds.Width > 0)
                v.UpdateGridMetrics(GridUsableWidth(Bounds.Width));
        }, DispatcherPriority.Background);

        // Save and restore scroll position so sidebar hover doesn't reset scroll
        var sv = ActiveScroller();
        var savedY = sv?.Offset.Y ?? 0;

        if (!vm.UpdateGridMetrics(usable))
            return;

        if (sv != null && savedY > 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                sv.Offset = new Vector(0, Math.Min(savedY, Math.Max(0, sv.Extent.Height - sv.Viewport.Height)));
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// The width the tile rows really get: the view less the DockPanel's side margins (12 + 2),
    /// the rows' 1px side margins and the always-shown vertical scroll bar. A flat "- 24" guessed the bar's share and came
    /// out 15px wide at 1400px, so every cover was 3px wider than its slot and ate into the
    /// gap between covers (GitHub #210).
    /// </summary>
    private double GridUsableWidth(double viewWidth)
    {
        var sv = ActiveScroller();
        var bar = sv is { Bounds.Width: > 0, Viewport.Width: > 0 } ? sv.Bounds.Width - sv.Viewport.Width : 10;
        return viewWidth - 16 - bar;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelPendingScrollRestore();

        if (DataContext is LibraryAlbumsViewModel vm)
        {
            var sv = ActiveScroller();
            if (sv != null)
                vm.SavedScrollOffset = sv.Offset.Y;
        }
        if (_vm != null)
        {
            _vm.FilteredAlbumRows.CollectionChanged -= OnFilteredRowsChanged;
            _vm.CtrlSelectionConsumed -= OnCtrlSelectionConsumed;
        }

        // Reset multi-selection so it doesn't leak back when the view is revisited.
        EndBand();
        _bandPress = null;
        MultiSelectHelper.ClearAlbumSelectionsByData(_selectedAlbums, CollectAlbumTiles());
        _rangeAnchor = null;
        if (DataContext is LibraryAlbumsViewModel selVm) selVm.CtrlSelectedAlbums = new List<Album>();

        base.OnDetachedFromVisualTree(e);
    }

    private void CancelPendingScrollRestore()
    {
        if (_pendingScrollRestore != null)
        {
            LayoutUpdated -= _pendingScrollRestore;
            _pendingScrollRestore = null;
            AlbumListBox.Opacity = 1;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is LibraryAlbumsViewModel vm && vm.SavedScrollOffset > 0)
        {
            var targetOffset = vm.SavedScrollOffset;

            // Restore scroll position after layout without hiding the view.
            // Using a single-shot LayoutUpdated avoids the Opacity=0 blank flash.
            _pendingScrollRestore = (s, args) =>
            {
                var sv = ActiveScroller();
                if (sv == null) return;

                var clampedOffset = Math.Min(targetOffset, Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
                sv.Offset = new Vector(0, clampedOffset);
                CancelPendingScrollRestore();
            };

            LayoutUpdated += _pendingScrollRestore;
        }
        else if (DataContext is LibraryAlbumsViewModel activeVm
                 && (activeVm.IsPageFiltered || activeVm.HasActiveFilter))
        {
            _pendingScrollRestore = (s, args) =>
            {
                var sv = ActiveScroller();
                if (sv == null) return;

                sv.Offset = new Vector(0, 0);
                CancelPendingScrollRestore();
            };

            LayoutUpdated += _pendingScrollRestore;
        }
    }

    // ── Rubber band (GitHub #226) ──
    // A left mouse press on empty space (page margins, the gaps between rows, a tile's own
    // padding) and a drag past BandThreshold draws a translucent accent rectangle; the albums
    // it meets become the selection (Ctrl held: added to it). Near the top or bottom edge the
    // grid scrolls on its own. Hits are worked out by data, not by realized tiles: the row
    // grid by row and column index between the band's two ends, the Masonry panel from its
    // placed stack, so albums swept past while scrolling stay selected. Positions are taken
    // in the scroller's content coordinates, so the band's start stays put while it scrolls.
    // Touch and pen presses never start one (they scroll), nor does a press on a cover or
    // caption (that is a click or a file drag) or anything with Shift.

    private const double BandThreshold = 4;
    private const double BandEdge = 48;

    private sealed record BandPress(Point Start, Button? PaddingTile, bool Additive);

    private sealed class BandState
    {
        public required ScrollViewer Scroller;
        public required Visual Content;
        public required Point Start;              // content coordinates
        public required HashSet<Album> Base;
        public required bool Masonry;
        public required DispatcherTimer Timer;
        public (int Floor, int Ceil) StartRow, StartCol;
    }

    private BandPress? _bandPress;
    private BandState? _band;
    private Point _bandLast;

    private void ArmBand(PointerPressedEventArgs e, Button? paddingTile)
    {
        _bandPress = null;
        if (_vm == null || !(_vm.IsRowGridView || _vm.IsMasonryView)) return;
        if (e.Pointer.Type != PointerType.Mouse) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        if (paddingTile == null && !MultiSelectHelper.HitsNoControl(e.Source, rowsAreLayout: true)) return;
        _bandPress = new BandPress(e.GetPosition(this), paddingTile, e.KeyModifiers.HasFlag(PlatformHelper.PrimaryModifier));
    }

    private void OnBandPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_bandPress is not { } press) return;
        var pos = e.GetPosition(this);
        if (_band == null)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _bandPress = null; return; }
            if (Math.Abs(pos.X - press.Start.X) < BandThreshold && Math.Abs(pos.Y - press.Start.Y) < BandThreshold) return;
            if (!StartBand(press, e.Pointer)) { _bandPress = null; return; }
        }
        _bandLast = pos;
        UpdateBand();
    }

    private void OnBandPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _bandPress = null;
        if (_band == null) return;
        _bandLast = e.GetPosition(this);
        UpdateBand();
        EndBand();
        if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnBandCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_band != null) EndBand();
        _bandPress = null;
    }

    private bool StartBand(BandPress press, IPointer pointer)
    {
        if (_vm == null) return false;
        var masonry = _vm.IsMasonryView;
        var sv = masonry ? MasonryScroller : AlbumListBox.FindDescendantOfType<ScrollViewer>();
        if (sv?.Content is not Visual content) return false;
        if (this.TranslatePoint(press.Start, content) is not { } start) return false;

        // The press was on a tile's padding: the drag is the band's, not a file drag.
        if (press.PaddingTile != null) DragFileBehavior.CancelGesture(press.PaddingTile);

        var band = new BandState
        {
            Scroller = sv,
            Content = content,
            Start = start,
            Base = press.Additive ? new HashSet<Album>(_selectedAlbums) : new HashSet<Album>(),
            Masonry = masonry,
            Timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) },
        };
        if (!masonry)
        {
            var (rows, cols) = RowGridGeometry(band);
            band.StartRow = Span(rows, start.Y);
            band.StartCol = Span(cols, start.X);
        }
        _band = band;
        pointer.Capture(this);
        band.Timer.Tick += (_, _) => OnBandTick();
        band.Timer.Start();
        return true;
    }

    private void EndBand()
    {
        if (_band is not { } band) return;
        _band = null;
        band.Timer.Stop();
        BandRect.IsVisible = false;
        if (_selectedAlbums.Count > 0) Focus();
    }

    /// <summary>Near the top or bottom edge the grid scrolls under the band, faster the
    /// further out the pointer is (up to 30 px a tick).</summary>
    private void OnBandTick()
    {
        if (_band is not { } band) return;
        var sv = band.Scroller;
        if (this.TranslatePoint(_bandLast, sv) is not { } p) return;
        var height = sv.Viewport.Height;
        var dy = p.Y < BandEdge ? -Math.Min(30, (BandEdge - p.Y) / 2 + 2)
            : p.Y > height - BandEdge ? Math.Min(30, (p.Y - (height - BandEdge)) / 2 + 2)
            : 0;
        if (dy == 0) return;
        var max = Math.Max(0, sv.Extent.Height - height);
        var y = Math.Clamp(sv.Offset.Y + dy, 0, max);
        if (Math.Abs(y - sv.Offset.Y) < 0.01) return;
        sv.Offset = new Vector(sv.Offset.X, y);
        // Hits are read after the grid has laid out at the new offset.
        Dispatcher.UIThread.Post(UpdateBand, DispatcherPriority.Background);
    }

    private void UpdateBand()
    {
        if (_band is not { } band || _vm == null) return;
        var sv = band.Scroller;
        if (this.TranslatePoint(_bandLast, sv) is not { } inViewport) return;
        // The band's moving end stays inside the visible grid; beyond it the grid scrolls.
        var clamped = new Point(Math.Clamp(inViewport.X, 0, sv.Viewport.Width), Math.Clamp(inViewport.Y, 0, sv.Viewport.Height));
        if (sv.TranslatePoint(clamped, band.Content) is not { } end) return;

        var hits = band.Masonry ? MasonryPanel.AlbumsIn(Between(band.Start, end).Inflate(0.5)) : RowGridHits(band, end);
        _selectedAlbums.Clear();
        _selectedAlbums.UnionWith(band.Base);
        _selectedAlbums.UnionWith(hits);
        foreach (var tile in CollectAlbumTiles())
            MultiSelectHelper.SyncAlbumTileVisual(tile, _selectedAlbums, animate: true);

        // Draw it: both ends through the content, which carries the scroll offset.
        if (band.Content.TranslatePoint(band.Start, BandLayer) is { } a && band.Content.TranslatePoint(end, BandLayer) is { } b)
        {
            var r = Between(a, b);
            Canvas.SetLeft(BandRect, r.X);
            Canvas.SetTop(BandRect, r.Y);
            BandRect.Width = Math.Max(1, r.Width);
            BandRect.Height = Math.Max(1, r.Height);
            BandRect.IsVisible = true;
        }
    }

    /// <summary>Albums of the rows and columns between the band's two ends. The start's row and
    /// column were found when the band began (that row may be scrolled away since); the end is
    /// on screen, so its row is realized.</summary>
    private List<Album> RowGridHits(BandState band, Point end)
    {
        var hits = new List<Album>();
        var (rows, cols) = RowGridGeometry(band);
        var endRow = Span(rows, end.Y);
        var endCol = Span(cols, end.X);
        var (r0, r1) = end.Y >= band.Start.Y ? (band.StartRow.Ceil, endRow.Floor) : (endRow.Ceil, band.StartRow.Floor);
        var (c0, c1) = end.X >= band.Start.X ? (band.StartCol.Ceil, endCol.Floor) : (endCol.Ceil, band.StartCol.Floor);
        if (_vm == null || r0 > r1 || c0 > c1) return hits;
        var all = _vm.FilteredAlbumRows;
        for (var r = Math.Max(0, r0); r <= r1 && r < all.Count; r++)
        {
            if (all[r] is not AlbumRow row) continue;
            for (var c = Math.Max(0, c0); c <= c1 && c < row.Albums.Count; c++)
                hits.Add(row.Albums[c]);
        }
        return hits;
    }

    /// <summary>The realized rows' vertical extents (an album row by its tiles, so the gap
    /// between rows is no row) and the columns' horizontal extents, in content coordinates,
    /// each with its index.</summary>
    private (List<(int Index, double From, double To)> Rows, List<(int Index, double From, double To)> Cols) RowGridGeometry(BandState band)
    {
        var rows = new List<(int, double, double)>();
        var cols = new List<(int, double, double)>();
        foreach (var container in AlbumListBox.GetRealizedContainers())
        {
            if (!container.IsVisible) continue;
            var index = AlbumListBox.IndexFromContainer(container);
            if (index < 0) continue;
            var tiles = container.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile")).ToList();
            if (container.DataContext is AlbumRow && tiles.Count > 0)
            {
                double top = double.MaxValue, bottom = double.MinValue;
                var rowCols = new List<(int, double, double)>();
                for (var c = 0; c < tiles.Count; c++)
                {
                    if (tiles[c].TranslatePoint(default, band.Content) is not { } o) continue;
                    top = Math.Min(top, o.Y);
                    bottom = Math.Max(bottom, o.Y + tiles[c].Bounds.Height);
                    rowCols.Add((c, o.X, o.X + tiles[c].Bounds.Width));
                }
                if (top <= bottom) rows.Add((index, top, bottom));
                if (rowCols.Count > cols.Count) cols = rowCols;
            }
            else if (container.TranslatePoint(default, band.Content) is { } o)
            {
                rows.Add((index, o.Y, o.Y + container.Bounds.Height));
            }
        }
        return (rows, cols);
    }

    private static Rect Between(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>For a coordinate: the last item that starts at or before it (Floor) and the
    /// first that ends at or after it (Ceil). Inside an item both are that item; in a gap they
    /// are the items on either side.</summary>
    private static (int Floor, int Ceil) Span(List<(int Index, double From, double To)> items, double at)
    {
        var floor = -1;
        var ceil = int.MaxValue;
        foreach (var (index, from, to) in items)
        {
            if (from <= at) floor = Math.Max(floor, index);
            if (to >= at) ceil = Math.Min(ceil, index);
        }
        return (floor, ceil);
    }

    /// <summary>Tile hover dots: the same menu a right-click on the tile opens.</summary>
    private void OnTileMoreClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Always (re)bind from the tile: a recycled tile may still hold the shared menu
        // bound to the album it showed before. OpenAlbumMenu also pushes the selection.
        for (var c = sender as Control; c != null; c = c.Parent as Control)
        {
            if (c is Button tile && tile.Classes.Contains("album-tile"))
            {
                // Grow out of the dots button's centre.
                var dots = sender as Control;
                OpenAlbumMenu(tile, dots?.PointToScreen(new Point(dots.Bounds.Width / 2, dots.Bounds.Height / 2)));
                break;
            }
        }
        e.Handled = true;
    }
}
