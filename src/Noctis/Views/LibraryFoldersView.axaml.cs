using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LibraryFoldersView : UserControl
{
    private TrackContextMenuBuilder? _menuBuilder;
    private ListBoxItem? _menuOwnerItem;
    private EventHandler? _pendingScrollRestore;

    public LibraryFoldersView()
    {
        InitializeComponent();

        // Double-click a row to play it within the folder's track list.
        TrackList.DoubleTapped += OnTrackDoubleTapped;

        // Attach context menu to every ListBoxItem so right-click works across the full row
        TrackList.ContainerPrepared += OnTrackContainerPrepared;
        TrackList.ContainerClearing += OnTrackContainerClearing;

        // Wire any containers that were realized before this subscription.
        foreach (var container in TrackList.GetRealizedContainers())
        {
            if (container is ListBoxItem item)
            {
                item.ContextRequested -= OnTrackItemContextRequested;
                item.ContextRequested += OnTrackItemContextRequested;
            }
        }

        // Reset shared menu so it picks up new VM commands
        DataContextChanged += (_, _) =>
        {
            _menuBuilder?.Reset();
            _menuBuilder = null;
        };
    }

    private void OnTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Ignore double-taps on the title/artist link buttons — they navigate instead
        if (e.Source is Control source && source.FindAncestorOfType<Button>() != null)
            return;

        if (DataContext is not LibraryFoldersViewModel vm) return;
        if (TrackList.SelectedItem is Track track)
            vm.PlayTrackCommand.Execute(track);
    }

    private ContextMenu GetOrCreateContextMenu()
    {
        if (_menuBuilder != null) return _menuBuilder.Menu;

        if (DataContext is not LibraryFoldersViewModel) return new ContextMenu();

        _menuBuilder = new TrackContextMenuBuilder();
        return _menuBuilder.Build("Remove from Library", null, this);
    }

    private void BindContextMenuToTrack(Track track)
    {
        GetOrCreateContextMenu();
        var vm = DataContext as LibraryFoldersViewModel;
        if (vm == null || _menuBuilder == null) return;

        _menuBuilder.Bind(
            track,
            playCommand: vm.PlayTrackCommand,
            shuffleCommand: vm.ShuffleFolderCommand,
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
            snoozeCommand: vm.SnoozeForMonthCommand);
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
        if (_menuBuilder?.Menu?.Parent is Control parent)
            parent.ContextMenu = null;
    }

    private void OnTrackItemContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        if (item.DataContext is not Track track) return;

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

    // ── Track-list scroll persistence (same pattern as Songs/Albums views) ──
    // The Folders view is rebuilt on every visit, so leaving it (e.g. clicking a
    // row's artist/title link) and coming Back reset the list to the top. Save
    // the offset on detach and restore it once layout has realized the rows.

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelPendingScrollRestore();

        if (DataContext is LibraryFoldersViewModel vm)
        {
            var sv = TrackList.FindDescendantOfType<ScrollViewer>();
            if (sv != null)
                vm.SavedScrollOffset = sv.Offset.Y;
        }
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

        if (DataContext is LibraryFoldersViewModel vm && vm.SavedScrollOffset > 0)
        {
            var targetOffset = vm.SavedScrollOffset;
            var attempts = 0;

            _pendingScrollRestore = (s, args) =>
            {
                attempts++;
                var sv = TrackList.FindDescendantOfType<ScrollViewer>();
                if (sv == null) return;

                // Virtualized rows grow the extent over the first layout passes —
                // wait for it to cover the target before clamping (bounded).
                if (sv.Extent.Height < targetOffset && attempts < 10)
                    return;

                var clampedOffset = Math.Min(targetOffset, Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
                sv.Offset = new Vector(0, clampedOffset);
                CancelPendingScrollRestore();
            };

            TrackList.LayoutUpdated += _pendingScrollRestore;
        }
    }
}
