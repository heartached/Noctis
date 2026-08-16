using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class AlbumDetailView : UserControl
{
    private EventHandler? _pendingScrollRestore;
    private System.ComponentModel.PropertyChangedEventHandler? _bgHandler;
    private AlbumDetailViewModel? _trackedVm;
    // Multi-select tracked by Track (data) so it survives container recycling.
    private readonly HashSet<Track> _selectedTracks = new();

    // One shared track menu for the whole page, bound to a row on open — the
    // per-row XAML menus (context menu + 3-dot flyout, ~35 items and ~14 bitmap
    // decodes per row) were the dominant cost of realizing this non-virtualized
    // list: the shared Unknown-Album bucket froze the app for minutes and ran
    // it out of memory at WAV-rip library scale. Same pattern as LibrarySongsView.
    private TrackContextMenuBuilder? _menuBuilder;
    private ListBoxItem? _menuOwnerItem;

    public AlbumDetailView()
    {
        InitializeComponent();

        DiscGroupList.ContainerPrepared += OnDiscGroupContainerPrepared;
        DiscGroupList.ContainerClearing += OnDiscGroupContainerClearing;

        DataContextChanged += OnAlbumDataContextChanged;

        OtherVersionsScroll.ScrollChanged += (_, _) => UpdateHScrollArrows(OtherVersionsScroll, OtherVersionsLeft, OtherVersionsRight);
        OtherVersionsScroll.LayoutUpdated += (_, _) => UpdateHScrollArrows(OtherVersionsScroll, OtherVersionsLeft, OtherVersionsRight);
        MoreByArtistScroll.ScrollChanged += (_, _) => UpdateHScrollArrows(MoreByArtistScroll, MoreByArtistLeft, MoreByArtistRight);
        MoreByArtistScroll.LayoutUpdated += (_, _) => UpdateHScrollArrows(MoreByArtistScroll, MoreByArtistLeft, MoreByArtistRight);

        AddHandler(InputElement.PointerPressedEvent, OnOptionsFlyoutButtonPointerPressed, RoutingStrategies.Tunnel);
        // Forward Ctrl+A from the window so it works without first clicking a row.
        _ = new WindowKeyForwarder(this, OnViewKeyDown);
    }

    /// <summary>Ctrl+Click toggles a track row's selection; a plain click clears it.</summary>
    private void OnTrackRowPointerPressed(PointerPressedEventArgs e)
    {
        var src = e.Source as Control;
        while (src != null && src is not ListBoxItem)
            src = src.Parent as Control;
        if (src is not ListBoxItem item) return;
        if (item.DataContext is not Track track) return;

        MultiSelectHelper.HandleTrackRowClickByData(item, track, e, _selectedTracks);
        if (_selectedTracks.Count > 0)
            Focus();
    }

    /// <summary>Ctrl+A selects all album tracks (toggles to deselect when all are selected).</summary>
    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _selectedTracks.Count > 0)
        {
            ClearTrackSelection();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.A || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (DataContext is not AlbumDetailViewModel vm) return;
        e.Handled = true;

        var all = vm.Tracks.ToList();
        var allSelected = all.Count > 0 && all.All(t => _selectedTracks.Contains(t));
        _selectedTracks.Clear();
        if (!allSelected)
            foreach (var t in all) _selectedTracks.Add(t);

        foreach (var li in DiscGroupList.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (li.DataContext is Track t && _selectedTracks.Contains(t))
                li.Classes.Add("ctrl-selected");
            else
                li.Classes.Remove("ctrl-selected");
        }
    }

    private void ClearTrackSelection()
    {
        _selectedTracks.Clear();
        foreach (var li in DiscGroupList.GetVisualDescendants().OfType<ListBoxItem>())
            li.Classes.Remove("ctrl-selected");
        if (DataContext is AlbumDetailViewModel vm)
            vm.CtrlSelectedTracks = new List<Track>();
    }

    private static void UpdateHScrollArrows(ScrollViewer sv, Button left, Button right)
    {
        var maxOffset = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
        left.IsVisible = sv.Offset.X > 1;
        right.IsVisible = sv.Offset.X < maxOffset - 1;
    }

    private static void ScrollHorizontal(ScrollViewer sv, double delta)
    {
        var maxOffset = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
        var target = Math.Clamp(sv.Offset.X + delta, 0, maxOffset);
        sv.Offset = new Vector(target, sv.Offset.Y);
    }

    private void OnOtherVersionsLeftClick(object? sender, RoutedEventArgs e)
        => ScrollHorizontal(OtherVersionsScroll, -OtherVersionsScroll.Viewport.Width * 0.9);

    private void OnOtherVersionsRightClick(object? sender, RoutedEventArgs e)
        => ScrollHorizontal(OtherVersionsScroll, OtherVersionsScroll.Viewport.Width * 0.9);

    private void OnMoreByArtistLeftClick(object? sender, RoutedEventArgs e)
        => ScrollHorizontal(MoreByArtistScroll, -MoreByArtistScroll.Viewport.Width * 0.9);

    private void OnMoreByArtistRightClick(object? sender, RoutedEventArgs e)
        => ScrollHorizontal(MoreByArtistScroll, MoreByArtistScroll.Viewport.Width * 0.9);

    private void OnDiscGroupContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is not ContentPresenter cp) return;
        cp.Loaded += OnDiscGroupPresenterLoaded;
    }

    private void OnDiscGroupContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container is not ContentPresenter cp) return;
        cp.Loaded -= OnDiscGroupPresenterLoaded;
        UnwireListBox(cp);
    }

    private void OnDiscGroupPresenterLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContentPresenter cp) return;
        cp.Loaded -= OnDiscGroupPresenterLoaded;
        WireListBox(cp);
    }

    private void WireListBox(ContentPresenter cp)
    {
        var lb = cp.FindDescendantOfType<ListBox>();
        if (lb == null) return;
        lb.DoubleTapped += OnTrackDoubleTapped;
        lb.ContainerPrepared += OnTrackContainerPrepared;
        lb.ContainerClearing += OnTrackContainerClearing;

        // Non-virtualizing StackPanel realizes all items before this Loaded handler
        // runs, so ContainerPrepared has already fired for existing rows. Wire the
        // ContextRequested handler on those existing containers so right-click works
        // anywhere on the row (including ListBoxItem padding outside the inner Grid).
        foreach (var container in lb.GetRealizedContainers())
        {
            if (container is ListBoxItem item)
            {
                item.ContextRequested -= OnTrackItemContextRequested;
                item.ContextRequested += OnTrackItemContextRequested;
            }
        }
    }

    private void UnwireListBox(ContentPresenter cp)
    {
        var lb = cp.FindDescendantOfType<ListBox>();
        if (lb == null) return;
        lb.DoubleTapped -= OnTrackDoubleTapped;
        lb.ContainerPrepared -= OnTrackContainerPrepared;
        lb.ContainerClearing -= OnTrackContainerClearing;

        foreach (var container in lb.GetRealizedContainers())
        {
            if (container is ListBoxItem item)
                item.ContextRequested -= OnTrackItemContextRequested;
        }
    }

    private void OnTrackContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is ListBoxItem item)
        {
            item.ContextRequested += OnTrackItemContextRequested;
            MultiSelectHelper.SyncContainerVisual(item, _selectedTracks);
        }
    }

    private void OnTrackContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container is ListBoxItem item)
        {
            item.ContextRequested -= OnTrackItemContextRequested;
            item.Classes.Remove("ctrl-selected");
        }
    }

    private ContextMenu GetOrCreateTrackMenu()
    {
        if (_menuBuilder != null) return _menuBuilder.Menu;

        if (DataContext is not AlbumDetailViewModel) return new ContextMenu();

        _menuBuilder = new TrackContextMenuBuilder();
        return _menuBuilder.Build("Remove from Library", null, this);
    }

    private void BindTrackMenuToTrack(Track track)
    {
        GetOrCreateTrackMenu();
        if (DataContext is not AlbumDetailViewModel vm || _menuBuilder == null) return;

        _menuBuilder.Bind(
            track,
            playCommand: vm.PlayFromCommand,
            shuffleCommand: vm.ShufflePlayCommand,
            playNextCommand: vm.PlayNextCommand,
            addToQueueCommand: vm.AddToQueueCommand,
            addToPlaylistCommand: vm.AddToNewPlaylistCommand,
            toggleFavoriteCommand: vm.ToggleFavoriteCommand,
            openMetadataCommand: vm.OpenMetadataCommand,
            searchLyricsCommand: vm.SearchLyricsCommand,
            showInExplorerCommand: vm.ShowInExplorerCommand,
            removeCommand: vm.RemoveFromLibraryCommand,
            convertCommand: vm.ConvertTrackCommand,
            scanReplayGainCommand: vm.ScanTrackReplayGainCommand,
            startRadioCommand: vm.StartRadioCommand,
            snoozeCommand: vm.SnoozeForMonthCommand);
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
        if (e.Handled) return;
        if (sender is not ListBoxItem item) return;
        if (item.DataContext is not Track track) return;

        if (DataContext is AlbumDetailViewModel vm)
            vm.CtrlSelectedTracks = _selectedTracks.ToList();

        BindTrackMenuToTrack(track);
        var menu = GetOrCreateTrackMenu();
        if (menu.IsOpen)
            menu.Close();

        DetachMenuFromOwner();
        _menuOwnerItem = item;
        item.ContextMenu = menu;
        menu.Placement = PlacementMode.Pointer;
        menu.Open(item);
        e.Handled = true;
    }

    private void OnTrackOptionsButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not Track track) return;
        if (DataContext is AlbumDetailViewModel vm)
            vm.CtrlSelectedTracks = _selectedTracks.ToList();

        BindTrackMenuToTrack(track);
        var menu = GetOrCreateTrackMenu();

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

    // Close any menu still open from a previous rapid right-click so menus
    // don't stack on top of each other.
    private void OnRelatedAlbumContextMenuOpening(object? sender, CancelEventArgs e)
        => ContextMenuCoordinator.NotifyOpening(sender as ContextMenu);

    private void OnAlbumFlyoutOpened(object? sender, EventArgs e) { }

    private void OnOptionsFlyoutButtonPointerPressed(object? sender, PointerPressedEventArgs e)
        => OnTrackRowPointerPressed(e);

    private void OnTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control source && source.FindAncestorOfType<Button>() != null)
            return;
        if (DataContext is not AlbumDetailViewModel vm) return;
        if (sender is ListBox lb && lb.SelectedItem is Track track)
        {
            DebugLogger.Info(DebugLogger.Category.UI, "AlbumDetail.DoubleTapped", $"track={track.Title}");
            vm.PlayFromCommand.Execute(track);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelPendingScrollRestore();

        // Unhook from _trackedVm (the VM the handler is actually subscribed to):
        // after an in-place VM swap the subscription follows the DataContext via
        // OnAlbumDataContextChanged, and _trackedVm is kept in step with it.
        if (_bgHandler != null)
        {
            if (_trackedVm != null)
                _trackedVm.PropertyChanged -= _bgHandler;
            _bgHandler = null;
        }

        if (DataContext is AlbumDetailViewModel vm)
        {
            vm.SavedScrollOffset = TrackScrollViewer.Offset.Y;
        }

        // Reset multi-selection so it doesn't leak back when the view is revisited.
        ClearTrackSelection();

        base.OnDetachedFromVisualTree(e);
    }

    private void CancelPendingScrollRestore()
    {
        if (_pendingScrollRestore != null)
        {
            DiscGroupList.LayoutUpdated -= _pendingScrollRestore;
            _pendingScrollRestore = null;
            DiscGroupList.Opacity = 1;
        }
    }

    /// <summary>
    /// When Avalonia reuses this view across AlbumDetailViewModel swaps (e.g. clicking
    /// an album in the Other Versions / More By Artist sections), neither
    /// OnDetachedFromVisualTree nor OnAttachedToVisualTree fire — so the underlying
    /// ScrollViewer keeps the previous album's physical scroll offset. Mirror the
    /// save/restore logic here so a fresh navigation always starts at the top.
    /// </summary>
    private void OnAlbumDataContextChanged(object? sender, EventArgs e)
    {
        // Save the outgoing VM's scroll offset before the new VM takes over, so
        // back-navigation still restores the previous album's position.
        if (_trackedVm != null)
            _trackedVm.SavedScrollOffset = TrackScrollViewer.Offset.Y;

        // Drop any selection from the previous album so it doesn't carry over.
        _selectedTracks.Clear();

        // Reset the shared track menu so it rebinds to the new VM's commands.
        DetachMenuFromOwner();
        _menuBuilder = null;

        CancelPendingScrollRestore();

        // Re-wire the BackgroundBrush watcher too: on an in-place VM swap neither
        // attach nor detach fires, so without this the handler would stay subscribed
        // to the old VM (rooting this view for as long as that VM sits in history).
        if (_trackedVm != null && _bgHandler != null)
            _trackedVm.PropertyChanged -= _bgHandler;

        var newVm = DataContext as AlbumDetailViewModel;
        _trackedVm = newVm;
        if (newVm == null) return;

        if (_bgHandler != null)
        {
            newVm.PropertyChanged += _bgHandler;
            AlbumGradientBg.Opacity = newVm.BackgroundBrush != null ? 1 : 0;
        }

        if (newVm.SavedScrollOffset > 0)
        {
            DiscGroupList.Opacity = 0;
            var targetOffset = newVm.SavedScrollOffset;
            var attempts = 0;
            _pendingScrollRestore = (s, args) =>
            {
                attempts++;
                var sv = TrackScrollViewer;
                if (sv.Extent.Height < targetOffset && attempts < 10) return;
                var clampedOffset = Math.Min(targetOffset, Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
                sv.Offset = new Vector(0, clampedOffset);
                DiscGroupList.Opacity = 1;
                CancelPendingScrollRestore();
            };
            DiscGroupList.LayoutUpdated += _pendingScrollRestore;
        }
        else
        {
            TrackScrollViewer.Offset = new Vector(0, 0);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is AlbumDetailViewModel vm2)
        {
            if (vm2.BackgroundBrush != null)
                AlbumGradientBg.Opacity = 1;

            _bgHandler = (_, args) =>
            {
                if (args.PropertyName == nameof(AlbumDetailViewModel.BackgroundBrush))
                    AlbumGradientBg.Opacity = ((AlbumDetailViewModel)DataContext!).BackgroundBrush != null ? 1 : 0;
            };
            vm2.PropertyChanged += _bgHandler;
        }

        // Scroll restore/reset is now driven by OnAlbumDataContextChanged so it also
        // fires when the view is recycled across VM swaps. Only fall back here if that
        // handler hasn't already processed the current DataContext.
        if (!ReferenceEquals(_trackedVm, DataContext)
            && DataContext is AlbumDetailViewModel vm && vm.SavedScrollOffset > 0)
        {
            DiscGroupList.Opacity = 0;
            var targetOffset = vm.SavedScrollOffset;
            var attempts = 0;

            _pendingScrollRestore = (s, args) =>
            {
                attempts++;
                var sv = TrackScrollViewer;

                if (sv.Extent.Height < targetOffset && attempts < 10)
                    return;

                var clampedOffset = Math.Min(targetOffset, Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
                sv.Offset = new Vector(0, clampedOffset);
                DiscGroupList.Opacity = 1;
                CancelPendingScrollRestore();
            };

            DiscGroupList.LayoutUpdated += _pendingScrollRestore;
            _trackedVm = vm;
        }
        else if (!ReferenceEquals(_trackedVm, DataContext))
        {
            // Fresh navigation: reset to top.
            TrackScrollViewer.Offset = new Vector(0, 0);
            _trackedVm = DataContext as AlbumDetailViewModel;
        }
    }
}
