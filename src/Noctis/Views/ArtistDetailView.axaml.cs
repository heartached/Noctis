using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class ArtistDetailView : UserControl
{
    // One shared track menu for the song rows (TrackContextMenuBuilder pattern).
    private TrackContextMenuBuilder? _trackMenuBuilder;
    private Control? _menuOwner;

    public ArtistDetailView()
    {
        InitializeComponent();
        // "Read more" is only offered when the seven-line cap actually collapsed a line.
        // LayoutUpdated covers width changes and text swaps; the check is a few property
        // reads on the already-computed text layout, and the VM write is change-gated.
        BioText.LayoutUpdated += OnBioLayoutUpdated;
    }

    // ── Scroll position across navigation ──
    // The view-model survives in the navigation history while an album/track page is
    // open; the view is rebuilt on Back. Save the offset when leaving, restore it once
    // the sections have laid out to at least that height (tiles resize on the first
    // measure, so the extent grows over a few layout passes). Mirrors AlbumDetailView.

    private ArtistDetailViewModel? _trackedVm;
    private EventHandler? _pendingScrollRestore;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        TrackViewModel(DataContext as ArtistDetailViewModel);
        TryRestoreScroll();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelPendingScrollRestore();
        if (_trackedVm != null)
            _trackedVm.SavedScrollOffset = PageScrollViewer.Offset.Y;
        TrackViewModel(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // An in-place VM swap (same view reused for another artist) fires neither
        // attach nor detach: save the outgoing artist's offset, restore the incoming one's.
        if (_trackedVm != null && !ReferenceEquals(_trackedVm, DataContext))
            _trackedVm.SavedScrollOffset = PageScrollViewer.Offset.Y;
        TrackViewModel(DataContext as ArtistDetailViewModel);
        // The viewer's width doesn't change with the artist, so size the new VM's tiles here.
        UpdateWidthLayout(PageScrollViewer.Bounds.Width);
        if (this.IsAttachedToVisualTree())
            TryRestoreScroll();
    }

    private void TrackViewModel(ArtistDetailViewModel? vm)
    {
        if (ReferenceEquals(_trackedVm, vm)) return;
        if (_trackedVm != null) _trackedVm.TabChanged -= OnTabChanged;
        _trackedVm = vm;
        if (_trackedVm != null) _trackedVm.TabChanged += OnTabChanged;
    }

    /// <summary>A tab switch lands at the top of the tab strip: the hero stays above,
    /// the new tab's content starts right under the tabs.</summary>
    private void OnTabChanged(object? sender, EventArgs e)
    {
        CancelPendingScrollRestore();
        var sv = PageScrollViewer;
        var target = Math.Min(sv.Offset.Y, Math.Max(0, Hero.Bounds.Height - 8));
        sv.Offset = new Vector(0, target);
    }

    private void TryRestoreScroll()
    {
        CancelPendingScrollRestore();
        if (_trackedVm is not { SavedScrollOffset: > 0 } vm)
        {
            PageScrollViewer.Offset = new Vector(0, 0);
            return;
        }

        var target = vm.SavedScrollOffset;
        var attempts = 0;
        _pendingScrollRestore = (_, _) =>
        {
            attempts++;
            var sv = PageScrollViewer;
            var max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            // Wait for the page to be tall enough, but never forever: after a handful
            // of passes take whatever is scrollable (a shrunken library after a rescan).
            if (max < target && attempts < 12) return;
            sv.Offset = new Vector(0, Math.Min(target, max));
            CancelPendingScrollRestore();
        };
        PageScrollViewer.LayoutUpdated += _pendingScrollRestore;
    }

    private void CancelPendingScrollRestore()
    {
        if (_pendingScrollRestore == null) return;
        PageScrollViewer.LayoutUpdated -= _pendingScrollRestore;
        _pendingScrollRestore = null;
    }

    private void OnBioLayoutUpdated(object? sender, EventArgs e)
    {
        if (DataContext is not ArtistDetailViewModel vm) return;
        var lines = BioText.TextLayout?.TextLines;
        var overflows = lines != null && lines.Any(l => l.HasCollapsed);
        if (vm.BioOverflows != overflows)
            vm.BioOverflows = overflows;
    }

    /// <summary>Page width under which the Overview's About card stacks below (see above).</summary>
    internal const double OverviewStackBelow = 64 + 380 + 24 + 24 + 340 + 340;

    /// <summary>The vertical scrollbar's gutter (Styles.axaml ScrollBar:vertical, measured
    /// 18px), always held back so the page width doesn't depend on whether the bar shows.</summary>
    internal const double ScrollBarGutter = 18;

    private void OnPageScrollViewerSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) UpdateWidthLayout(e.NewSize.Width);
    }

    private void UpdateWidthLayout(double viewerWidth)
    {
        if (viewerWidth <= 0) return;
        // The viewer's width less the gutter, whether or not the bar is showing. Measuring
        // the content instead fed the bar back into the tile size: the bar appearing
        // shrank the tiles enough to hide it again, and they bounced (Discord, veil 10-07).
        var width = viewerWidth - ScrollBarGutter;
        // Popular needs ~340px beside Latest Release (380) and About (340) + gaps (48) +
        // margins (64); below that About drops under them (OverviewTopRow.stacked).
        OverviewTopRow.Classes.Set("stacked", width < OverviewStackBelow);
        if (DataContext is not ArtistDetailViewModel vm) return;

        // Tiles at the size Home and the Albums grid use (AlbumGridMetrics: five across in
        // Auto, else the cover-size setting) over the section width (margins 32+32, 2px
        // slack for layout rounding, as Home's row does).
        var usable = width - 64 - 2;
        var columns = AlbumGridMetrics.ComputeColumns(usable, vm.AlbumTileSizeAuto, vm.AlbumTileTargetSize);
        var newSize = AlbumGridMetrics.ComputeTileSize(usable, columns);
        if (columns == vm.GridColumns && Math.Abs(newSize - vm.TileArtworkSize) < 0.5) return;

        var savedY = PageScrollViewer.Offset.Y;
        vm.GridColumns = columns;
        vm.TileArtworkSize = newSize;
        if (savedY > 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                PageScrollViewer.Offset = new Vector(
                    0,
                    Math.Min(savedY, Math.Max(0, PageScrollViewer.Extent.Height - PageScrollViewer.Viewport.Height)));
            }, DispatcherPriority.Background);
        }
    }

    // ── Album tiles: one shared v2 menu (10-09 redesign), bound to the tile on open ──
    // Replaces the per-tile XAML ContextMenu: same commands (through the shared
    // LibraryAlbumsVm) and parameters, plus the entries the Albums page tile menu has.

    private AlbumContextMenuBuilder? _albumMenuBuilder;
    /// <summary>Culture each menu's strings were read in (they are read once, at Build).</summary>
    private string? _albumMenuCulture, _trackMenuCulture;

    private void OnAlbumTileContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Button tile && OpenAlbumMenu(tile))
            e.Handled = true;
    }

    private bool OpenAlbumMenu(Button tile)
    {
        if (tile.DataContext is not Album album) return false;
        if (DataContext is not ArtistDetailViewModel vm) return false;
        if (vm.LibraryAlbumsVm is not { } albumsVm) return false;

        // Single-album right-click on this page; clear any stale ctrl-selection on the shared VM.
        albumsVm.CtrlSelectedAlbums = new List<Album>();

        if (_albumMenuBuilder == null || _albumMenuCulture != Loc.Instance.Culture.Name)
        {
            _albumMenuCulture = Loc.Instance.Culture.Name;
            _albumMenuBuilder = new AlbumContextMenuBuilder();
            _albumMenuBuilder.Build(Loc.T("LibraryAlbums.RemoveFromLibrary"), this, v2: true, removeIsDanger: true);
        }

        _albumMenuBuilder.Bind(
            album,
            playCommand: albumsVm.PlayAlbumCommand,
            shuffleCommand: albumsVm.ShuffleAlbumCommand,
            playNextCommand: albumsVm.PlayNextCommand,
            addToQueueCommand: albumsVm.AddToQueueCommand,
            addToPlaylistCommand: albumsVm.AddToNewPlaylistCommand,
            toggleFavoritesCommand: albumsVm.ToggleAlbumFavoritesCommand,
            openMetadataCommand: albumsVm.OpenMetadataCommand,
            showInExplorerCommand: albumsVm.ShowInExplorerCommand,
            removeCommand: albumsVm.RemoveFromLibraryCommand,
            convertCommand: albumsVm.ConvertAlbumCommand,
            scanReplayGainCommand: albumsVm.ScanAlbumReplayGainCommand,
            searchLyricsCommand: albumsVm.SearchLyricsAlbumCommand);

        OpenMenu(_albumMenuBuilder.Menu, tile, PlacementMode.Pointer);
        return true;
    }

    /// <summary>A song row plays on DOUBLE click (user ask 09-14), like every flat track list
    /// in the app; a single click leaves the row alone. Taps that land on a button inside the
    /// row (the artwork Play overlay, the "…" glyph) belong to that button.</summary>
    private void OnSongRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control row || row.DataContext is not TopSongRow item) return;
        if (e.Source is Control source && source.FindAncestorOfType<Button>(true) is { } inner
            && !ReferenceEquals(inner, row)) return;
        if (DataContext is not ArtistDetailViewModel vm) return;
        vm.PlaySongCommand.Execute(item.Track);
    }

    private void OnPopularContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control owner) return;
        if (OpenTrackMenu(owner, PlacementMode.Pointer)) e.Handled = true;
    }

    /// <summary>The row's "…" glyph: the same track menu, dropped under the glyph.</summary>
    private void OnRowMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control glyph) return;
        OpenTrackMenu(glyph, PlacementMode.BottomEdgeAlignedRight);
        e.Handled = true;
    }

    private bool OpenTrackMenu(Control owner, PlacementMode placement)
    {
        if (owner.DataContext is not TopSongRow row) return false;
        if (DataContext is not ArtistDetailViewModel vm) return false;
        if (vm.LibraryAlbumsVm is not { } albumsVm) return false;

        // v2 layout (10-09 redesign); rebuilt after a live language switch so it follows it.
        if (_trackMenuBuilder == null || _trackMenuCulture != Loc.Instance.Culture.Name)
        {
            _trackMenuCulture = Loc.Instance.Culture.Name;
            _trackMenuBuilder = new TrackContextMenuBuilder();
            _trackMenuBuilder.Build(Loc.T("LibraryAlbums.RemoveFromLibrary"), null, this, v2: true, removeIsDanger: true);
        }

        _trackMenuBuilder.Bind(
            row.Track,
            playCommand: vm.PlaySongCommand,
            shuffleCommand: vm.ShufflePopularCommand,
            playNextCommand: albumsVm.PlayNextTrackCommand,
            addToQueueCommand: albumsVm.AddTrackToQueueCommand,
            addToPlaylistCommand: albumsVm.AddTrackToNewPlaylistCommand,
            toggleFavoriteCommand: albumsVm.ToggleTrackFavoriteCommand,
            openMetadataCommand: albumsVm.OpenTrackMetadataCommand,
            searchLyricsCommand: vm.SearchLyricsCommand,
            showInExplorerCommand: albumsVm.ShowInExplorerTrackCommand,
            removeCommand: albumsVm.RemoveTrackFromLibraryCommand);

        OpenMenu(_trackMenuBuilder.Menu, owner, placement);
        return true;
    }

    private void OpenMenu(ContextMenu menu, Control owner, PlacementMode placement)
    {
        ContextMenuCoordinator.NotifyOpening(menu);
        MenuOpenAnimation.CloseNow(menu);

        if (_menuOwner != null && !ReferenceEquals(_menuOwner, owner))
            _menuOwner.ContextMenu = null;
        if (menu.Parent is Control prev && !ReferenceEquals(prev, owner))
            prev.ContextMenu = null;

        _menuOwner = owner;
        owner.ContextMenu = menu;
        menu.Placement = placement;
        menu.Open(owner);
    }

    private async void OnChangePictureClick(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            if (DataContext is not ArtistDetailViewModel vm) return;
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Artist Picture",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Images")
                    {
                        Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp", "*.gif" }
                    }
                }
            });
            if (files.Count == 0) return;

            byte[] data;
            await using (var stream = await files[0].OpenReadAsync())
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms);
                data = ms.ToArray();
            }
            if (data.Length == 0) return;
            await vm.ChangePictureAsync(data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ArtistDetailView] Change picture failed: {ex.Message}");
        }
    }

    private async void OnSearchPictureClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is ArtistDetailViewModel vm)
                await vm.SearchPictureAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ArtistDetailView] Picture search failed: {ex.Message}");
        }
    }

    private void OnRemovePictureClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ArtistDetailViewModel vm)
            vm.RemovePicture();
    }

    /// <summary>Tile hover dots: the same menu a right-click on the tile opens, bound
    /// afresh (the tile may still hold the shared menu from an older language or album).</summary>
    private void OnTileMoreClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        for (var c = sender as Control; c != null; c = c.Parent as Control)
        {
            if (c is Button tile && tile.Classes.Contains("album-tile"))
            {
                OpenAlbumMenu(tile);
                break;
            }
        }
        e.Handled = true;
    }
}
