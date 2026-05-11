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

public partial class LibrarySongsView : UserControl
{
    private LibrarySongsViewModel? _vm;
    private EventHandler? _pendingScrollRestore;
    private readonly HashSet<ListBoxItem> _selectedTrackItems = new();
    private TrackContextMenuBuilder? _menuBuilder;
    private ListBoxItem? _menuOwnerItem;

    public LibrarySongsView()
    {
        InitializeComponent();

        // Double-click to play from here
        TrackList.DoubleTapped += OnTrackDoubleTapped;
        TrackList.AddHandler(PointerPressedEvent, OnTrackPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Attach context menu to every ListBoxItem so right-click works across the full row
        TrackList.ContainerPrepared += OnTrackContainerPrepared;
        TrackList.ContainerClearing += OnTrackContainerClearing;

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

        MultiSelectHelper.HandleTrackRowClick(item, e, _selectedTrackItems);
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        MultiSelectHelper.HandleTrackSelectAll(e, TrackList, _selectedTrackItems);
    }

    private ContextMenu GetOrCreateContextMenu()
    {
        if (_menuBuilder != null) return _menuBuilder.Menu;

        if (DataContext is not LibrarySongsViewModel) return new ContextMenu();

        _menuBuilder = new TrackContextMenuBuilder();
        return _menuBuilder.Build("Remove from Library", null, this);
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
            addToNewPlaylistCommand: vm.AddToNewPlaylistCommand,
            toggleFavoriteCommand: vm.ToggleFavoriteCommand,
            openMetadataCommand: vm.OpenMetadataCommand,
            searchLyricsCommand: vm.SearchLyricsCommand,
            showInExplorerCommand: vm.ShowInExplorerCommand,
            removeCommand: vm.RemoveFromLibraryCommand,
            playlists: vm.Playlists,
            addToExistingPlaylistCommand: vm.AddToExistingPlaylistCommand);
    }

    private void OnTrackContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is ListBoxItem item)
            item.ContextRequested += OnTrackItemContextRequested;
    }

    private void OnTrackContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container is ListBoxItem item)
        {
            item.ContextRequested -= OnTrackItemContextRequested;
            item.ContextMenu = null;
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
            vm.CtrlSelectedTracks = MultiSelectHelper.GetSelectedTrackData<Track>(_selectedTrackItems);

        BindContextMenuToTrack(track);
        var menu = GetOrCreateContextMenu();
        if (menu.IsOpen)
            menu.Close();

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
            vm.CtrlSelectedTracks = MultiSelectHelper.GetSelectedTrackData<Track>(_selectedTrackItems);

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
            _vm.FilteredTracks.CollectionChanged -= OnFilteredTracksChanged;

        _vm = DataContext as LibrarySongsViewModel;
        if (_vm != null)
            _vm.FilteredTracks.CollectionChanged += OnFilteredTracksChanged;

        // Reset shared menu so it picks up new VM commands
        _menuBuilder?.Reset();
        _menuBuilder = null;
    }

    private void OnFilteredTracksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_vm?.HasActiveFilter != true)
            return;
        if (_pendingScrollRestore != null || (_vm != null && _vm.SavedScrollOffset > 0))
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

    private static void OnTitleCellLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is not Grid titleCell)
            return;

        var title = titleCell.Children.OfType<TextBlock>().FirstOrDefault();
        if (title == null)
            return;

        var explicitBadge = titleCell.Children.OfType<Border>().FirstOrDefault();
        var reservedBadgeWidth = 0.0;

        if (explicitBadge?.IsVisible == true)
        {
            var badgeMargin = explicitBadge.Margin;
            var badgeWidth = explicitBadge.Bounds.Width > 0
                ? explicitBadge.Bounds.Width
                : explicitBadge.DesiredSize.Width;
            reservedBadgeWidth = badgeWidth + badgeMargin.Left + badgeMargin.Right;
        }

        var maxTitleWidth = Math.Max(0, titleCell.Bounds.Width - reservedBadgeWidth);
        if (Math.Abs(title.MaxWidth - maxTitleWidth) > 0.5)
            title.MaxWidth = maxTitleWidth;
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

        if (DataContext is LibrarySongsViewModel vm)
        {
            var sv = TrackList.FindDescendantOfType<ScrollViewer>();
            if (sv != null)
                vm.SavedScrollOffset = sv.Offset.Y;
        }
        if (_vm != null)
            _vm.FilteredTracks.CollectionChanged -= OnFilteredTracksChanged;
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

        if (_vm != null)
        {
            _vm.FilteredTracks.CollectionChanged -= OnFilteredTracksChanged;
            _vm.FilteredTracks.CollectionChanged += OnFilteredTracksChanged;
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
