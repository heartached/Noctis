using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LibrarySongsView : UserControl, ITrackSelectionSource
{
    private LibrarySongsViewModel? _vm;
    private EventHandler? _pendingScrollRestore;
    /// <summary>The VM FilterKey the list last reset its scroll for (see OnFilteredTracksChanged).</summary>
    private string? _scrollResetFilterKey;
    // Track selection by the Track itself (not the ListBoxItem container) so
    // virtualization-driven container recycling doesn't drag the visual
    // ctrl-selected class onto a different track when the user scrolls or
    // tabs back to the page. See MultiSelectHelper's "Data-tracked variants".
    private readonly HashSet<Track> _selectedTracks = new();
    private DateTime _lastScrollUtc;

    /// <summary>Rows selected or a scroll in the last moment: the list must hold still.</summary>
    private bool IsUserBusy() =>
        _selectedTracks.Count > 0 || DateTime.UtcNow - _lastScrollUtc < TimeSpan.FromSeconds(1.5);

    /// <summary>The Ctrl-selection, for window shortcuts (GitHub #131 ReplayGain).</summary>
    public IReadOnlyList<Track> GetSelectedTracks() => _selectedTracks.ToList();
    private TrackContextMenuBuilder? _menuBuilder;
    /// <summary>Culture the menu's strings were read in (they are read once, at Build).</summary>
    private string? _menuCulture;
    private ListBoxItem? _menuOwnerItem;

    /// <summary>The list's columns (GitHub #151), shared by the header and every row.</summary>
    internal ColumnLayout Columns { get; } = LibraryColumns.CreateSongs();
    private IDisposable? _columnsBinding;

    public LibrarySongsView()
    {
        InitializeComponent();
        // Inherited by the header and every row's ColumnsPanel.
        ColumnsPanel.SetLayout(this, Columns);

        // Double-click to play from here
        TrackList.DoubleTapped += OnTrackDoubleTapped;
        TrackList.AddHandler(PointerPressedEvent, OnTrackPointerPressed, RoutingStrategies.Tunnel);
        // Forward Ctrl+A from the window so it works without first clicking a row.
        _ = new WindowKeyForwarder(this, OnViewKeyDown);
        // A Plays re-rank waits while the user scrolls (see LibrarySongsViewModel.IsUserBusy).
        TrackList.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            if (e.OffsetDelta.Y != 0) _lastScrollUtc = DateTime.UtcNow;
        });

        // Attach context menu to every ListBoxItem so right-click works across the full row
        TrackList.ContainerPrepared += OnTrackContainerPrepared;
        TrackList.ContainerClearing += OnTrackContainerClearing;
        // GitHub #158: shown, the ReplayGain column asks for the rows on screen.
        Columns.Changed += (_, _) => ReadReplayGainOf(TrackList.GetRealizedContainers().Select(c => c.DataContext).OfType<Track>());

        // Wire any containers that were realized before this subscription so
        // right-click works on row padding/edges, not just the inner Grid.
        foreach (var container in TrackList.GetRealizedContainers())
        {
            if (container is ListBoxItem item)
            {
                item.ContextRequested -= OnTrackItemContextRequested;
                item.ContextRequested += OnTrackItemContextRequested;
            }
        }

        DataContextChanged += OnDataContextChanged;
    }

    private void OnTrackPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var source = e.Source as Control;
        while (source != null && source is not ListBoxItem)
            source = source.Parent as Control;
        if (source is not ListBoxItem item) return;
        if (item.DataContext is not Track track) return;

        MultiSelectHelper.HandleTrackRowClickByData(item, track, e, _selectedTracks);

        if (_selectedTracks.Count > 0)
            Focus();
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _selectedTracks.Count > 0)
        {
            MultiSelectHelper.ClearTrackSelectionsByData(_selectedTracks, TrackList);
            if (DataContext is LibrarySongsViewModel vm) vm.CtrlSelectedTracks = new List<Track>();
            e.Handled = true;
            return;
        }

        MultiSelectHelper.HandleTrackSelectAllByData(e, TrackList, _selectedTracks);
    }

    private ContextMenu GetOrCreateContextMenu()
    {
        var culture = Noctis.Localization.Loc.Instance.Culture.Name;
        if (_menuBuilder != null && _menuCulture == culture) return _menuBuilder.Menu;
        // A live language switch while this page is open: rebuild so the menu follows it.
        if (_menuBuilder != null)
        {
            DetachMenuFromOwner();
            _menuBuilder = null;
        }

        if (DataContext is not LibrarySongsViewModel) return new ContextMenu();

        // v2 layout (10-09 redesign): quick tiles, grouped rows, Tools ▸.
        _menuBuilder = new TrackContextMenuBuilder();
        _menuCulture = culture;
        return _menuBuilder.Build(Noctis.Localization.Loc.T("LibraryAlbums.RemoveFromLibrary"), null, this, v2: true, removeIsDanger: true);
    }

    private void BindContextMenuToTrack(Track track)
    {
        GetOrCreateContextMenu();
        var vm = DataContext as LibrarySongsViewModel;
        if (vm == null || _menuBuilder == null) return;

        _menuBuilder.Bind(
            track,
            playCommand: vm.PlayFromHereCommand,
            shuffleCommand: vm.ShuffleAllCommand,
            playNextCommand: vm.PlayNextCommand,
            addToQueueCommand: vm.AddToQueueCommand,
            addToPlaylistCommand: vm.AddToNewPlaylistCommand,
            toggleFavoriteCommand: vm.ToggleFavoriteCommand,
            openMetadataCommand: vm.OpenMetadataCommand,
            searchLyricsCommand: vm.SearchLyricsCommand,
            showInExplorerCommand: vm.ShowInExplorerCommand,
            removeCommand: vm.RemoveFromLibraryCommand,
            convertCommand: vm.ConvertTracksCommand,
            scanReplayGainCommand: vm.ScanReplayGainCommand,
            startRadioCommand: vm.StartRadioCommand,
            snoozeCommand: vm.SnoozeForMonthCommand,
            rateCommand: vm.RateTrackCommand,
            fetchLyricsCommand: vm.FetchLyricsCommand,
            lyricsStudioCommand: vm.OpenLyricsStudioCommand,
            removeLyricsCommand: vm.RemoveLyricsCommand,
            sendToFolderCommand: vm.SendToFolderCommand);
    }

    /// <summary>Click on a row's stars: rate that track (or the whole Ctrl-selection it belongs to).</summary>
    private void OnRowRated(object? sender, int stars)
    {
        if (sender is RatingStars stars1 && stars1.DataContext is Track track && DataContext is LibrarySongsViewModel vm)
            _ = vm.RateAsync(track, stars);
    }

    private void OnTrackContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is ListBoxItem item)
        {
            item.ContextRequested += OnTrackItemContextRequested;
            // Re-apply ctrl-selected visual based on the data set so recycled
            // containers reflect the right state after a scroll.
            MultiSelectHelper.SyncContainerVisual(item, _selectedTracks);
            if (item.DataContext is Track track) ReadReplayGainOf(new[] { track });
        }
    }

    /// <summary>
    /// GitHub #158: a song whose ReplayGain tags were never read (a library from before the
    /// column) showed a blank cell until the startup pass reached its file, minutes later on
    /// a big library. While the column is shown, rows coming on screen are read right away.
    /// </summary>
    private void ReadReplayGainOf(IEnumerable<Track> tracks)
    {
        if (_vm != null && Columns.IsVisible("ReplayGain"))
            _vm.ReadReplayGainSoon(tracks);
    }

    private void OnTrackContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container is ListBoxItem item)
        {
            item.ContextRequested -= OnTrackItemContextRequested;
            item.ContextMenu = null;
            // Strip the visual class so the recycled container doesn't display
            // a stale selection on its next track.
            item.Classes.Remove("ctrl-selected");
        }
    }

    private void DetachMenuFromOwner()
    {
        if (_menuOwnerItem != null)
        {
            _menuOwnerItem.ContextMenu = null;
            _menuOwnerItem = null;
        }
        // Also detach from any button that previously owned the menu
        if (_menuBuilder?.Menu?.Parent is Control parent)
        {
            parent.ContextMenu = null;
        }
    }

    private void OnTrackItemContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        if (item.DataContext is not Track track) return;
        if (DataContext is LibrarySongsViewModel vm)
            vm.CtrlSelectedTracks = _selectedTracks.ToList();

        BindContextMenuToTrack(track);
        var menu = GetOrCreateContextMenu();
        MenuOpenAnimation.CloseNow(menu);

        DetachMenuFromOwner();
        _menuOwnerItem = item;
        item.ContextMenu = menu;
        menu.Placement = PlacementMode.Pointer;
        menu.Open(item);
        e.Handled = true;
    }

    private void OnOptionsButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not Track track) return;
        if (DataContext is LibrarySongsViewModel vm)
            vm.CtrlSelectedTracks = _selectedTracks.ToList();

        BindContextMenuToTrack(track);
        var menu = GetOrCreateContextMenu();

        if (menu.IsOpen) { menu.Close(); return; }

        // Detach from previous owner and attach to the button so Open() doesn't
        // throw "Cannot show ContextMenu on a different control".
        DetachMenuFromOwner();
        btn.ContextMenu = menu;
        _menuOwnerItem = null;

        menu.Placement = PlacementMode.BottomEdgeAlignedRight;
        menu.Open(btn);
        e.Handled = true;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.FilteredTracks.CollectionChanged -= OnFilteredTracksChanged;
            _vm.IsUserBusy = null;
        }

        _vm = DataContext as LibrarySongsViewModel;
        if (_vm != null)
        {
            _vm.FilteredTracks.CollectionChanged += OnFilteredTracksChanged;
            _scrollResetFilterKey = _vm.FilterKey;
            _vm.IsUserBusy = IsUserBusy;
        }

        // Reset shared menu so it picks up new VM commands
        _menuBuilder?.Reset();
        _menuBuilder = null;
    }

    private void OnFilteredTracksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Scroll to top only when the filter itself changed: a library reload (metadata
        // save, scan tick, analysis pass) re-fills the same results and keeps the user's place.
        if (_vm == null || _vm.FilterKey == _scrollResetFilterKey)
            return;
        _scrollResetFilterKey = _vm.FilterKey;

        if (!_vm.HasActiveFilter)
            return;
        if (_pendingScrollRestore != null)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            var sv = TrackList.FindDescendantOfType<ScrollViewer>();
            if (sv != null)
                sv.Offset = new Vector(0, 0);
        }, DispatcherPriority.Background);
    }

    private void OnTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Ignore double-taps on the 3-dot options button — it should only open the menu
        if (e.Source is Control source && source.FindAncestorOfType<Button>() != null)
            return;
        if (DataContext is not LibrarySongsViewModel vm) return;
        if (TrackList.SelectedItem is Track track)
        {
            vm.PlayFromHereCommand.Execute(track);
        }
    }

    /// <summary>Child lookups for <see cref="OnTitleCellLayoutUpdated"/>, resolved once
    /// per cell and stashed in Tag: LayoutUpdated fires after EVERY window layout pass,
    /// and a template cell's children never change (recycling reuses the same Grid).</summary>
    private sealed record TitleCellChildren(TextBlock Title, Border? ExplicitBadge, Border? ArtThumb, TextBlock? LyricSnippet);

    private static void OnTitleCellLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is not Grid titleCell)
            return;

        if (titleCell.Tag is not TitleCellChildren children)
        {
            var title = titleCell.Children.OfType<TextBlock>().FirstOrDefault();
            if (title == null)
                return;

            // Select by class: the cell holds two Borders (leading artwork thumb, trailing
            // explicit badge) and each reserves its own width from the title's budget.
            var explicitBadge = titleCell.Children.OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("explicit-badge"));
            var artThumb = titleCell.Children.OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("row-art"));
            var snippet = titleCell.Children.OfType<TextBlock>()
                .FirstOrDefault(t => t.Classes.Contains("lyric-snippet"));
            children = new TitleCellChildren(title, explicitBadge, artThumb, snippet);
            titleCell.Tag = children;
        }

        var reservedWidth = 0.0;

        if (children.ExplicitBadge?.IsVisible == true)
            reservedWidth += ReservedWidth(children.ExplicitBadge);
        if (children.ArtThumb?.IsVisible == true)
            reservedWidth += ReservedWidth(children.ArtThumb);

        var maxTitleWidth = Math.Max(0, titleCell.Bounds.Width - reservedWidth);
        if (Math.Abs(children.Title.MaxWidth - maxTitleWidth) > 0.5)
            children.Title.MaxWidth = maxTitleWidth;
        // The lyric snippet sits under the title and the badge: only the artwork reserves room.
        if (children.LyricSnippet is { } lyricSnippet)
        {
            var art = children.ArtThumb?.IsVisible == true ? ReservedWidth(children.ArtThumb) : 0;
            var maxSnippetWidth = Math.Max(0, titleCell.Bounds.Width - art);
            if (Math.Abs(lyricSnippet.MaxWidth - maxSnippetWidth) > 0.5)
                lyricSnippet.MaxWidth = maxSnippetWidth;
        }

        static double ReservedWidth(Border element)
        {
            var width = element.Bounds.Width > 0
                ? element.Bounds.Width
                : element.DesiredSize.Width;
            return width + element.Margin.Left + element.Margin.Right;
        }
    }

    private void OnQueueButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var mainWindow = this.FindLogicalAncestorOfType<MainWindow>();
        if (mainWindow?.DataContext is MainWindowViewModel mainVm)
        {
            mainVm.NavigateCommand.Execute("queue");
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelPendingScrollRestore();
        _columnsBinding?.Dispose();
        _columnsBinding = null;

        if (DataContext is LibrarySongsViewModel vm)
        {
            var sv = TrackList.FindDescendantOfType<ScrollViewer>();
            if (sv != null)
                vm.SavedScrollOffset = sv.Offset.Y;
        }
        if (_vm != null)
            _vm.FilteredTracks.CollectionChanged -= OnFilteredTracksChanged;

        // Reset multi-selection so it doesn't leak back when the view is revisited.
        _selectedTracks.Clear();
        foreach (var child in TrackList.GetVisualDescendants())
            if (child is ListBoxItem li) li.Classes.Remove("ctrl-selected");
        if (DataContext is LibrarySongsViewModel selVm) selVm.CtrlSelectedTracks = new List<Track>();

        base.OnDetachedFromVisualTree(e);
    }

    private void CancelPendingScrollRestore()
    {
        if (_pendingScrollRestore != null)
        {
            TrackList.LayoutUpdated -= _pendingScrollRestore;
            _pendingScrollRestore = null;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Column visibility, order and widths live in Settings (no window VM in tests: defaults).
        _columnsBinding?.Dispose();
        _columnsBinding = TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel { Settings: { } settings }
            ? LibraryColumns.BindSongs(Columns, settings)
            : null;

        if (_vm != null)
        {
            _vm.FilteredTracks.CollectionChanged -= OnFilteredTracksChanged;
            _vm.FilteredTracks.CollectionChanged += OnFilteredTracksChanged;
            _scrollResetFilterKey = _vm.FilterKey;
        }

        if (DataContext is LibrarySongsViewModel vm && vm.SavedScrollOffset > 0)
        {
            var targetOffset = vm.SavedScrollOffset;

            _pendingScrollRestore = (s, args) =>
            {
                var sv = TrackList.FindDescendantOfType<ScrollViewer>();
                if (sv == null) return;

                var clampedOffset = Math.Min(targetOffset, Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
                sv.Offset = new Vector(0, clampedOffset);
                CancelPendingScrollRestore();
            };

            TrackList.LayoutUpdated += _pendingScrollRestore;
        }
    }

}
