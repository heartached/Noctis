using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Noctis.Views;

public partial class PlaylistView : UserControl
{
    private EventHandler? _pendingScrollRestore;
    // See LibrarySongsView for why this is data-tracked rather than container-tracked.
    private readonly HashSet<Track> _selectedTracks = new();
    private TrackContextMenuBuilder? _menuBuilder;
    private ListBoxItem? _menuOwnerItem;

    // ── Drag-reorder state (pointer-tracked, liquid motion shared with the queue) ──
    private const double PlaylistDragThreshold = 6.0;
    private Point _dragStartPos;
    private bool _dragActive;
    private Track? _dragTrack;
    private int _dragSourceIndex = -1;
    private double _dragRowOffsetY;

    public PlaylistView()
    {
        InitializeComponent();

        TrackList.DoubleTapped += OnTrackDoubleTapped;
        TrackList.AddHandler(PointerPressedEvent, OnTrackPointerPressed, RoutingStrategies.Tunnel);
        // Forward Ctrl+A from the window so it works without first clicking a row.
        _ = new WindowKeyForwarder(this, OnViewKeyDown);

        TrackList.ContainerPrepared += OnTrackContainerPrepared;
        TrackList.ContainerClearing += OnTrackContainerClearing;

        // Wire any containers that were realized before this subscription.
        // Without this, right-click on row padding/edges (outside the inner Grid's
        // own ContextMenu) silently does nothing because ContextRequested has no
        // listener at the ListBoxItem level.
        foreach (var container in TrackList.GetRealizedContainers())
        {
            if (container is ListBoxItem item)
                WireTrackItem(item);
        }

        DataContextChanged += OnDataContextChanged;
    }

    private void WireTrackItem(ListBoxItem item)
    {
        item.ContextRequested -= OnTrackItemContextRequested;
        item.ContextRequested += OnTrackItemContextRequested;
        item.RemoveHandler(PointerPressedEvent, OnTrackRowPointerPressed);
        item.AddHandler(PointerPressedEvent, OnTrackRowPointerPressed, RoutingStrategies.Tunnel);
        item.RemoveHandler(PointerMovedEvent, OnTrackRowPointerMoved);
        item.AddHandler(PointerMovedEvent, OnTrackRowPointerMoved, RoutingStrategies.Tunnel);
        item.RemoveHandler(PointerReleasedEvent, OnTrackRowPointerReleased);
        item.AddHandler(PointerReleasedEvent, OnTrackRowPointerReleased, RoutingStrategies.Tunnel);
        item.RemoveHandler(PointerCaptureLostEvent, OnTrackRowPointerCaptureLost);
        item.AddHandler(PointerCaptureLostEvent, OnTrackRowPointerCaptureLost, RoutingStrategies.Tunnel);
    }

    private PlaylistViewModel? _observedVm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Reset shared menu so it picks up new VM commands
        _menuBuilder?.Reset();
        _menuBuilder = null;

        if (_observedVm != null)
        {
            _observedVm.PropertyChanged -= OnVmPropertyChanged;
            _observedVm.Tracks.CollectionChanged -= OnTracksCollectionChanged;
        }
        _observedVm = DataContext as PlaylistViewModel;
        if (_observedVm != null)
        {
            _observedVm.PropertyChanged += OnVmPropertyChanged;
            _observedVm.Tracks.CollectionChanged += OnTracksCollectionChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Focus + select the inline rename box once it becomes visible.
        if (e.PropertyName == nameof(PlaylistViewModel.IsRenamingName)
            && _observedVm?.IsRenamingName == true)
        {
            Dispatcher.UIThread.Post(() =>
            {
                RenameBox.Focus();
                RenameBox.SelectAll();
            }, DispatcherPriority.Render);
        }
    }

    // ── Row number + repeated-album dimming ──
    // The # cell and album-run dim depend on the row's position, which a
    // virtualized item template can't know. Containers are stamped here on
    // realize/recycle and re-stamped whenever the displayed order changes.

    private void OnTracksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshAllRowVisuals, DispatcherPriority.Loaded);
    }

    // ── GitHub #74: Settings → Library → Playlist page → Album headers ──
    // The header is stamped per realized row, so a toggle flip re-stamps them all.
    private SettingsViewModel? _settingsVm;

    private static bool AlbumHeadersEnabled =>
        App.Services?.GetService<MainWindowViewModel>()?.Settings.PlaylistShowAlbumHeaders ?? true;

    private void HookSettings()
    {
        if (_settingsVm != null) return;
        _settingsVm = App.Services?.GetService<MainWindowViewModel>()?.Settings;
        if (_settingsVm != null) _settingsVm.PropertyChanged += OnSettingsPropertyChanged;
    }

    private void UnhookSettings()
    {
        if (_settingsVm != null) _settingsVm.PropertyChanged -= OnSettingsPropertyChanged;
        _settingsVm = null;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.PlaylistShowAlbumHeaders))
            Dispatcher.UIThread.Post(RefreshAllRowVisuals, DispatcherPriority.Loaded);
    }

    private void RefreshAllRowVisuals()
    {
        foreach (var container in TrackList.GetRealizedContainers())
            if (container is ListBoxItem item)
                UpdateRowIndexVisuals(item);
    }

    /// <param name="knownIndex">Index supplied by ContainerPrepared. Pass -1 to look it
    /// up: <see cref="ItemsControl.IndexFromContainer"/> is NOT yet valid while that
    /// event is being raised, but is fine once the container is registered.</param>
    /// <returns>true when the row's template was realized and the stamp applied.</returns>
    private bool UpdateRowIndexVisuals(ListBoxItem item, int knownIndex = -1)
    {
        if (DataContext is not PlaylistViewModel vm) return false;
        var index = knownIndex >= 0 ? knownIndex : TrackList.IndexFromContainer(item);
        if (index < 0 || index >= vm.Tracks.Count) return false;

        var descendants = item.GetVisualDescendants().ToList();

        var indexText = descendants.OfType<TextBlock>()
            .FirstOrDefault(t => t.Classes.Contains("row-index"));
        if (indexText != null)
            indexText.Text = (index + 1).ToString();

        var album = vm.Tracks[index].Album;
        var sameAsPrevious = index > 0
                             && !string.IsNullOrEmpty(album)
                             && string.Equals(album, vm.Tracks[index - 1].Album,
                                 StringComparison.OrdinalIgnoreCase);

        var albumBtn = descendants.OfType<Button>()
            .FirstOrDefault(b => b.Classes.Contains("album-btn"));
        // Class, not a local Opacity value: a local set beats every style, so the
        // :pointerover rule that un-dims the link could never win against it.
        albumBtn?.Classes.Set("repeat-dim", sameAsPrevious);

        // Zebra stripe parity (the row Border paints it now, not the ListBoxItem)
        var rowBody = descendants.OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("row-body"));
        rowBody?.Classes.Set("even", index % 2 == 1);

        return UpdateAlbumRunHeader(descendants, vm, index, album, isRunStart: !sameAsPrevious);
    }

    /// <summary>Shows "ALBUM · N TRACKS" above the first row of each consecutive
    /// same-album run, mirroring the approved mockup. Hidden while a filter is
    /// active (the filtered list no longer reflects real runs).</summary>
    /// <returns>false when the row's template has not been realized yet, so the caller
    /// can retry rather than leave the header unstamped.</returns>
    private static bool UpdateAlbumRunHeader(List<Avalonia.Visual> descendants,
        PlaylistViewModel vm, int index, string album, bool isRunStart)
    {
        var header = descendants.OfType<StackPanel>()
            .FirstOrDefault(p => p.Classes.Contains("album-run-header"));
        if (header == null) return false;

        var show = isRunStart
                   && !string.IsNullOrEmpty(album)
                   && string.IsNullOrWhiteSpace(vm.SearchText)
                   && AlbumHeadersEnabled;
        header.IsVisible = show;
        if (!show) return true;

        // Tighter gap for the very first header; breathing room between runs.
        header.Margin = index == 0 ? new Thickness(8, 4, 8, 6) : new Thickness(8, 18, 8, 6);

        var runLength = 1;
        while (index + runLength < vm.Tracks.Count
               && string.Equals(vm.Tracks[index + runLength].Album, album,
                   StringComparison.OrdinalIgnoreCase))
            runLength++;

        var albumText = header.Children.OfType<TextBlock>()
            .FirstOrDefault(t => t.Classes.Contains("run-album"));
        if (albumText != null)
            albumText.Text = album.ToUpperInvariant();

        var countText = header.Children.OfType<TextBlock>()
            .FirstOrDefault(t => t.Classes.Contains("run-count"));
        if (countText != null)
            countText.Text = $"·  {runLength} TRACK{(runLength == 1 ? "" : "S")}";

        return true;
    }

    // ── Inline title rename ──

    private async void OnRenameBoxKeyDown(object? sender, KeyEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            if (DataContext is not PlaylistViewModel vm) return;
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await vm.CommitRenameAsync();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                vm.CancelRename();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PlaylistView] Rename failed: {ex.Message}");
        }
    }

    private async void OnRenameBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            // Only commit when the edit is still active — Enter/Escape already
            // ended it before the focus moved away.
            if (DataContext is PlaylistViewModel vm && vm.IsRenamingName)
                await vm.CommitRenameAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PlaylistView] Rename failed: {ex.Message}");
        }
    }

    // ── Floating selection bar ──

    private void SyncSelectionToViewModel()
    {
        if (DataContext is not PlaylistViewModel vm) return;
        vm.CtrlSelectedTracks = _selectedTracks.ToList();
        vm.SelectedCount = _selectedTracks.Count;
    }

    private void ClearSelectionState()
    {
        MultiSelectHelper.ClearTrackSelectionsByData(_selectedTracks, TrackList);
        if (DataContext is PlaylistViewModel vm)
        {
            vm.CtrlSelectedTracks = new List<Track>();
            vm.SelectedCount = 0;
        }
    }

    private async void OnSelectionAddToPlaylistClick(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            if (DataContext is not PlaylistViewModel vm || _selectedTracks.Count == 0) return;
            vm.CtrlSelectedTracks = _selectedTracks.ToList();
            await vm.OpenAddSelectedToPlaylistAsync();
            ClearSelectionState();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PlaylistView] Selection add-to-playlist failed: {ex.Message}");
        }
    }

    private async void OnSelectionFavoriteClick(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            if (DataContext is not PlaylistViewModel vm || _selectedTracks.Count == 0) return;
            var tracks = _selectedTracks.ToList();
            vm.CtrlSelectedTracks = tracks;
            await vm.ToggleFavoriteCommand.ExecuteAsync(tracks[0]);
            ClearSelectionState();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PlaylistView] Selection favorite failed: {ex.Message}");
        }
    }

    private async void OnSelectionRemoveClick(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            if (DataContext is not PlaylistViewModel vm || _selectedTracks.Count == 0) return;
            var tracks = _selectedTracks.ToList();
            vm.CtrlSelectedTracks = tracks;
            await vm.RemoveTrackCommand.ExecuteAsync(tracks[0]);
            ClearSelectionState();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PlaylistView] Selection remove failed: {ex.Message}");
        }
    }

    private void OnSelectionClearClick(object? sender, RoutedEventArgs e)
    {
        ClearSelectionState();
    }

    private void OnTrackPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var source = e.Source as Control;
        while (source != null && source is not ListBoxItem)
            source = source.Parent as Control;
        if (source is not ListBoxItem item) return;
        if (item.DataContext is not Track track) return;

        MultiSelectHelper.HandleTrackRowClickByData(item, track, e, _selectedTracks);
        SyncSelectionToViewModel();

        if (_selectedTracks.Count > 0)
            Focus();
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _selectedTracks.Count > 0)
        {
            ClearSelectionState();
            e.Handled = true;
            return;
        }

        if (DataContext is PlaylistViewModel vm && vm.IsDescriptionOpen)
            return;
        if (MultiSelectHelper.HandleTrackSelectAllByData(e, TrackList, _selectedTracks))
            SyncSelectionToViewModel();
    }

    private ContextMenu GetOrCreateContextMenu()
    {
        if (_menuBuilder != null) return _menuBuilder.Menu;

        if (DataContext is not PlaylistViewModel) return new ContextMenu();

        _menuBuilder = new TrackContextMenuBuilder();
        return _menuBuilder.Build("Remove from Playlist",
            "avares://Noctis.UI/Assets/Icons/Remove%20from%20Playlist%20ICON.png", this);
    }

    private void BindContextMenuToTrack(Track track)
    {
        GetOrCreateContextMenu();
        var vm = DataContext as PlaylistViewModel;
        if (vm == null || _menuBuilder == null) return;

        _menuBuilder.Bind(
            track,
            playCommand: vm.PlayFromCommand,
            shuffleCommand: vm.ShuffleAllCommand,
            playNextCommand: vm.PlayNextCommand,
            addToQueueCommand: vm.AddToQueueCommand,
            addToPlaylistCommand: vm.AddToNewPlaylistCommand,
            toggleFavoriteCommand: vm.ToggleFavoriteCommand,
            openMetadataCommand: vm.OpenMetadataCommand,
            searchLyricsCommand: vm.SearchLyricsCommand,
            showInExplorerCommand: vm.ShowInExplorerCommand,
            removeCommand: vm.RemoveTrackCommand,
            convertCommand: vm.ConvertTracksCommand,
            scanReplayGainCommand: vm.ScanReplayGainCommand,
            startRadioCommand: vm.StartRadioCommand,
            snoozeCommand: vm.SnoozeForMonthCommand,
            rateCommand: vm.RateTrackCommand,
            fetchLyricsCommand: vm.FetchLyricsCommand,
            lyricsStudioCommand: vm.OpenLyricsStudioCommand,
            removeLyricsCommand: vm.RemoveLyricsCommand,
            sendToFolderCommand: vm.SendToFolderCommand,
            badgeCommand: vm.SetBadgeCommand,
            badgeNames: vm.BadgeNames);
        // Same gate as the selection bar's Remove: smart playlists are rule-driven.
        _menuBuilder.Remove.IsVisible = vm.IsManualPlaylist;
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

    private void OnOptionsButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not Track track) return;
        if (DataContext is PlaylistViewModel vm)
            vm.CtrlSelectedTracks = _selectedTracks.ToList();

        BindContextMenuToTrack(track);
        var menu = GetOrCreateContextMenu();

        if (menu.IsOpen) { menu.Close(); return; }

        // Detach from previous owner and attach to the button so Open() doesn't
        // throw "Cannot show ContextMenu on a different control".
        DetachMenuFromOwner();
        btn.ContextMenu = menu;
        _menuOwnerItem = null; // btn is not a ListBoxItem — track via ContextMenu directly

        menu.Placement = PlacementMode.BottomEdgeAlignedRight;
        menu.Open(btn);
        e.Handled = true;
    }

    private void OnTrackContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is ListBoxItem item)
        {
            WireTrackItem(item);
            MultiSelectHelper.SyncContainerVisual(item, _selectedTracks);

            // Stamp BEFORE the row is first measured. The album-run header changes the
            // row's height, so deciding it a dispatcher cycle later (this used to post
            // at DispatcherPriority.Loaded) re-heights a row that was already drawn and
            // shoves every row below it — measured at 29px of content movement on 68 of
            // 149 scroll steps, which is the scroll stutter. See
            // PlaylistRowStampGeometryTests.ContentJump_AcrossHeaderModes.
            //
            // Template children genuinely don't exist at prepare time, so realize them
            // up front. e.Index is authoritative here: IndexFromContainer is not yet
            // valid while this event is being raised.
            item.ApplyTemplate();
            item.Presenter?.UpdateChild();
            if (!UpdateRowIndexVisuals(item, e.Index))
            {
                // Template still not realized — fall back to the old timing so a row
                // can never end up unstamped (stale index/zebra/run header).
                Dispatcher.UIThread.Post(
                    () => UpdateRowIndexVisuals(item, e.Index), DispatcherPriority.Loaded);
            }
        }
    }

    private void OnTrackContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container is ListBoxItem item)
        {
            item.ContextRequested -= OnTrackItemContextRequested;
            item.ContextMenu = null;
            item.Classes.Remove("ctrl-selected");
            item.RemoveHandler(PointerPressedEvent, OnTrackRowPointerPressed);
            item.RemoveHandler(PointerMovedEvent, OnTrackRowPointerMoved);
            item.RemoveHandler(PointerReleasedEvent, OnTrackRowPointerReleased);
            item.RemoveHandler(PointerCaptureLostEvent, OnTrackRowPointerCaptureLost);
        }
    }

    /// <summary>
    /// Gives the row title an explicit width budget so it can ellipsize.
    ///
    /// The title cell packs title + explicit badge + NEW badge into Auto columns so the
    /// badges hug the end of the title. Auto measures with infinite width, so the title
    /// would otherwise report its full text width and paint over the Album column
    /// (issue #30). Same approach as LibrarySongsView.OnTitleCellLayoutUpdated: hand the
    /// title whatever the cell has left once the visible badges have taken their share.
    /// </summary>
    /// <summary>Child lookups for <see cref="OnTitleCellLayoutUpdated"/>, resolved once
    /// per cell and stashed in Tag: LayoutUpdated fires after EVERY window layout pass,
    /// and a template cell's children never change (recycling reuses the same Grid).</summary>
    private sealed record TitleCellChildren(TextBlock Title, Border[] Badges);

    private static void OnTitleCellLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is not Grid titleCell)
            return;

        if (titleCell.Tag is not TitleCellChildren children)
        {
            var title = titleCell.Children.OfType<TextBlock>().FirstOrDefault();
            if (title == null)
                return;

            // Every other child of this cell is a badge Border.
            children = new TitleCellChildren(title, titleCell.Children.OfType<Border>().ToArray());
            titleCell.Tag = children;
        }

        // Each badge reserves its own width (plus margin) from the title's budget
        // only while it is actually shown.
        var reservedWidth = 0.0;
        foreach (var badge in children.Badges)
        {
            if (!badge.IsVisible)
                continue;
            var width = badge.Bounds.Width > 0 ? badge.Bounds.Width : badge.DesiredSize.Width;
            reservedWidth += width + badge.Margin.Left + badge.Margin.Right;
        }

        var maxTitleWidth = Math.Max(0, titleCell.Bounds.Width - reservedWidth);
        // Threshold keeps this from re-entering layout forever on sub-pixel churn.
        if (Math.Abs(children.Title.MaxWidth - maxTitleWidth) > 0.5)
            children.Title.MaxWidth = maxTitleWidth;
    }

    private void OnTrackItemContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        if (item.DataContext is not Track track) return;
        if (DataContext is PlaylistViewModel vm)
            vm.CtrlSelectedTracks = _selectedTracks.ToList();

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

    private void OnTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Ignore double-taps on the 3-dot options button — it should only open the menu
        if (e.Source is Control source && source.FindAncestorOfType<Button>() != null)
            return;
        if (DataContext is not PlaylistViewModel vm) return;
        if (TrackList.SelectedItem is Track track)
        {
            vm.PlayFromCommand.Execute(track);
        }
    }

    // ── Drag-reorder handlers (pointer-tracked, same liquid motion as the queue) ──
    //
    // The dragged row is rendered as a floating card (#PlaylistDragPreview) that lifts and
    // springs after the pointer; its own slot stays empty and the rows in between slide
    // apart to open a gap where it will land (LiquidReorder). On release the card glides
    // into the gap and then vm.MoveTrack / vm.MoveTracks commits. No DragDrop.DoDragDrop.

    private LiquidReorder? _liquid;

    private LiquidReorder? Liquid
    {
        get
        {
            if (_liquid != null) return _liquid;
            var preview = this.FindControl<Border>("PlaylistDragPreview");
            if (preview == null) return null;
            return _liquid = new LiquidReorder(this, TrackList, preview);
        }
    }

    /// <summary>A row's slot is its body: rows that open an album run also carry the run
    /// header above the body, which must not count toward where the card lands.</summary>
    private static Rect RowBodySlot(Control container)
    {
        var body = container.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("row-body"));
        if (body?.TranslatePoint(new Point(0, 0), container) is not { } p)
            return new Rect(0, 0, container.Bounds.Width, container.Bounds.Height);
        return new Rect(0, p.Y, body.Bounds.Width, body.Bounds.Height);
    }

    private void OnTrackRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed) return;
        if (DataContext is not PlaylistViewModel vm) return;
        if (vm.IsSmartPlaylist) return;
        // No reordering while a search filter is active: indexes refer to the
        // filtered view, and persisting from it would drop the hidden tracks.
        if (!string.IsNullOrWhiteSpace(vm.SearchText)) return;
        // No reordering while a non-Manual sort is active (displayed order != saved order).
        if (vm.SortMode != PlaylistSortMode.Manual) return;
        if (item.DataContext is not Track track) return;

        // A new press lands before the last drop's glide finished: commit it now.
        Liquid?.FinishNow();

        _dragTrack = track;
        _dragSourceIndex = vm.Tracks.IndexOf(track);
        // Grab offset measured from the row BODY, which is what the card mirrors.
        _dragRowOffsetY = e.GetPosition(item).Y - RowBodySlot(item).Y;
        _dragStartPos = e.GetPosition(this);
        _dragActive = false;
    }

    private void OnTrackRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTrack == null || Liquid is not { IsSettling: false } liquid) return;
        if (sender is not ListBoxItem item) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var pos = e.GetPosition(this);
        if (!_dragActive)
        {
            if (Math.Abs(pos.X - _dragStartPos.X) < PlaylistDragThreshold &&
                Math.Abs(pos.Y - _dragStartPos.Y) < PlaylistDragThreshold)
                return;

            StartPlaylistDrag(item, e, liquid);
        }

        var cardTop = e.GetPosition(TrackListWrapper).Y - _dragRowOffsetY;
        liquid.MoveCardTo(cardTop);
        var target = LiquidReorder.NearestSlot(TrackList, TrackListWrapper, cardTop + liquid.Pitch / 2, RowBodySlot);
        if (target >= 0) liquid.TargetIndex = target;
    }

    private void OnTrackRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragActive)
            BeginPlaylistSettle();
        else
            ResetPlaylistDragState();
        e.Pointer.Capture(null);
    }

    private void OnTrackRowPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Releasing the capture on drop raises this too; the glide owns cleanup then.
        if (Liquid is { IsSettling: true }) return;
        // Otherwise lost capture is a cancel: restore visuals without performing the move.
        ResetPlaylistDragState();
    }

    private void StartPlaylistDrag(ListBoxItem rowItem, PointerEventArgs e, LiquidReorder liquid)
    {
        _dragActive = true;
        e.Pointer.Capture(rowItem);

        var preview = this.FindControl<Border>("PlaylistDragPreview");
        if (preview == null || _dragTrack == null) return;
        preview.DataContext = _dragTrack;

        var body = RowBodySlot(rowItem);
        liquid.Pitch = body.Height;
        liquid.SourceIndex = liquid.TargetIndex = _dragSourceIndex;
        // Start exactly over the grabbed row's body so the lift reads as the row rising.
        var rowTop = rowItem.TranslatePoint(new Point(0, body.Y), TrackListWrapper)?.Y
                     ?? e.GetPosition(TrackListWrapper).Y - _dragRowOffsetY;
        liquid.Begin(rowTop);
    }

    /// <summary>Release: glide the card into the open gap; the move commits when it lands.</summary>
    private void BeginPlaylistSettle()
    {
        if (DataContext is not PlaylistViewModel vm || _dragTrack == null || Liquid is not { } liquid)
        {
            ResetPlaylistDragState();
            return;
        }

        var track = _dragTrack;
        var from = vm.Tracks.IndexOf(track);
        if (from < 0 || liquid.TargetIndex < 0)
        {
            ResetPlaylistDragState();
            return;
        }
        var to = Math.Clamp(liquid.TargetIndex, 0, Math.Max(0, vm.Tracks.Count - 1));
        liquid.SourceIndex = from;
        liquid.TargetIndex = to;

        // GitHub #74: a row dragged out of a multi-selection carries the whole selection.
        // MoveTracks takes an INSERTION index: past the source, the slot the card took
        // is one further down once the block is lifted out.
        List<Track>? block = _selectedTracks.Count > 1 && _selectedTracks.Contains(track)
            ? _selectedTracks.ToList()
            : null;
        var insertIndex = to > from ? to + 1 : to;

        var landing = LiquidReorder.SlotTop(TrackList, to, TrackListWrapper, RowBodySlot) ?? liquid.CardY;
        _dragActive = false;
        _dragTrack = null;
        _dragSourceIndex = -1;
        liquid.Settle(landing, () => _ = CommitPlaylistMoveAsync(vm, track, from, to, block, insertIndex));
    }

    private static async System.Threading.Tasks.Task CommitPlaylistMoveAsync(
        PlaylistViewModel vm, Track track, int from, int to, List<Track>? block, int insertIndex)
    {
        try
        {
            if (block != null)
                await vm.MoveTracks(block, insertIndex);
            else if (from != to && vm.Tracks.IndexOf(track) == from)
                await vm.MoveTrack(from, to);
        }
        catch (Exception ex)
        {
            // Fire-and-forget from the animation frame: an escaped exception would be lost.
            System.Diagnostics.Debug.WriteLine($"[PlaylistView] Drop commit failed: {ex.Message}");
        }
    }

    private void ResetPlaylistDragState()
    {
        Liquid?.Cancel();
        _dragActive = false;
        _dragTrack = null;
        _dragSourceIndex = -1;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelPendingScrollRestore();
        ResetPlaylistDragState();
        UnhookSettings();

        // Unsubscribe VM events: DataContextChanged never fires for a view the
        // presenter discards, so these would pin the view alive via a VM that
        // navigation history retains.
        if (_observedVm != null)
        {
            _observedVm.PropertyChanged -= OnVmPropertyChanged;
            _observedVm.Tracks.CollectionChanged -= OnTracksCollectionChanged;
            _observedVm = null;
        }

        if (DataContext is PlaylistViewModel vm)
        {
            var sv = TrackList.FindDescendantOfType<ScrollViewer>();
            if (sv != null)
                vm.SavedScrollOffset = sv.Offset.Y;
        }

        // Reset multi-selection so it doesn't leak back when the view is revisited.
        _selectedTracks.Clear();
        foreach (var child in TrackList.GetVisualDescendants())
            if (child is ListBoxItem li) li.Classes.Remove("ctrl-selected");
        if (DataContext is PlaylistViewModel selVm)
        {
            selVm.CtrlSelectedTracks = new List<Track>();
            selVm.SelectedCount = 0;
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
        HookSettings();

        // Re-subscribe on re-attach (detach unsubscribed; DataContextChanged
        // won't fire again when the DataContext is unchanged).
        if (_observedVm == null && DataContext is PlaylistViewModel observed)
        {
            _observedVm = observed;
            _observedVm.PropertyChanged += OnVmPropertyChanged;
            _observedVm.Tracks.CollectionChanged += OnTracksCollectionChanged;
        }

        if (DataContext is PlaylistViewModel vm && vm.SavedScrollOffset > 0)
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
