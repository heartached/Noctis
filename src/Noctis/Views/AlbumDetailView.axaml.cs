using System;
using System.Collections.Generic;
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
    private readonly Dictionary<object, PlaylistMenuPopulator> _playlistPopulators = new();
    private AlbumDetailViewModel? _trackedVm;

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
    }

    private void UnwireListBox(ContentPresenter cp)
    {
        var lb = cp.FindDescendantOfType<ListBox>();
        if (lb == null) return;
        lb.DoubleTapped -= OnTrackDoubleTapped;
        lb.ContainerPrepared -= OnTrackContainerPrepared;
        lb.ContainerClearing -= OnTrackContainerClearing;
    }

    private void OnTrackContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is ListBoxItem item)
            item.ContextRequested += OnTrackItemContextRequested;
    }

    private void OnTrackContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container is ListBoxItem item)
            item.ContextRequested -= OnTrackItemContextRequested;
    }

    private void OnTrackItemContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled) return;
        if (sender is not ListBoxItem item) return;

        Grid? grid = null;
        foreach (var desc in item.GetVisualDescendants())
        {
            if (desc is Grid g && g.ContextMenu != null)
            { grid = g; break; }
        }
        if (grid?.ContextMenu == null) return;
        grid.ContextMenu.Open(grid);
        e.Handled = true;
    }

    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AlbumDetailViewModel vm) return;
        if (sender is not ContextMenu ctx) return;

        if (!_playlistPopulators.TryGetValue(ctx, out var populator))
        {
            MenuItem? addToPlaylist = null;
            Separator? separator = null;
            foreach (var item in ctx.Items)
            {
                if (item is MenuItem mi && mi.Header is string h && h == "Add to Playlist")
                {
                    addToPlaylist = mi;
                    foreach (var sub in mi.Items)
                    {
                        if (sub is Separator sep) { separator = sep; break; }
                    }
                    break;
                }
            }
            if (addToPlaylist == null || separator == null) return;
            populator = new PlaylistMenuPopulator(addToPlaylist, separator);
            _playlistPopulators[ctx] = populator;
        }

        var track = ctx.DataContext as Track;
        populator.Populate(vm.Playlists, vm.AddToExistingPlaylistCommand,
            playlist => new object[] { track!, playlist });
    }

    private void OnRelatedAlbumContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AlbumDetailViewModel vm) return;
        if (sender is not ContextMenu ctx) return;

        if (!_playlistPopulators.TryGetValue(ctx, out var populator))
        {
            MenuItem? addToPlaylist = null;
            Separator? separator = null;
            foreach (var item in ctx.Items)
            {
                if (item is MenuItem mi && mi.Header is string h && h == "Add to Playlist")
                {
                    addToPlaylist = mi;
                    foreach (var sub in mi.Items)
                    {
                        if (sub is Separator sep) { separator = sep; break; }
                    }
                    break;
                }
            }
            if (addToPlaylist == null || separator == null) return;
            populator = new PlaylistMenuPopulator(addToPlaylist, separator);
            _playlistPopulators[ctx] = populator;
        }

        var album = ctx.DataContext as Album;
        populator.Populate(vm.Playlists, vm.AddRelatedAlbumToExistingPlaylistCommand,
            playlist => new object[] { album!, playlist });
    }

    private void OnAlbumFlyoutOpened(object? sender, EventArgs e)
    {
        if (DataContext is not AlbumDetailViewModel vm) return;
        if (sender is not MenuFlyout flyout) return;

        if (!_playlistPopulators.TryGetValue(flyout, out var populator))
        {
            MenuItem? addToPlaylist = null;
            Separator? separator = null;
            foreach (var item in flyout.Items)
            {
                if (item is MenuItem mi && mi.Header is string h && h == "Add to Playlist")
                {
                    addToPlaylist = mi;
                    foreach (var sub in mi.Items)
                    {
                        if (sub is Separator sep) { separator = sep; break; }
                    }
                    break;
                }
            }
            if (addToPlaylist == null || separator == null) return;
            populator = new PlaylistMenuPopulator(addToPlaylist, separator);
            _playlistPopulators[flyout] = populator;
        }

        populator.Populate(vm.Playlists, vm.AddAlbumToExistingPlaylistCommand);
    }

    private void OnTrackFlyoutOpened(object? sender, EventArgs e)
    {
        if (DataContext is not AlbumDetailViewModel vm) return;
        if (sender is not MenuFlyout flyout) return;

        if (!_playlistPopulators.TryGetValue(flyout, out var populator))
        {
            MenuItem? addToPlaylist = null;
            Separator? separator = null;
            foreach (var item in flyout.Items)
            {
                if (item is MenuItem mi && mi.Header is string h && h == "Add to Playlist")
                {
                    addToPlaylist = mi;
                    foreach (var sub in mi.Items)
                    {
                        if (sub is Separator sep) { separator = sep; break; }
                    }
                    break;
                }
            }
            if (addToPlaylist == null || separator == null) return;
            populator = new PlaylistMenuPopulator(addToPlaylist, separator);
            _playlistPopulators[flyout] = populator;
        }

        var track = (flyout.Target as Button)?.Tag as Track;
        populator.Populate(vm.Playlists, vm.AddToExistingPlaylistCommand,
            playlist => new object[] { track!, playlist });
    }

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

        if (_bgHandler != null && DataContext is AlbumDetailViewModel bgVm)
        {
            bgVm.PropertyChanged -= _bgHandler;
            _bgHandler = null;
        }

        if (DataContext is AlbumDetailViewModel vm)
        {
            vm.SavedScrollOffset = TrackScrollViewer.Offset.Y;
        }
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

        CancelPendingScrollRestore();

        var newVm = DataContext as AlbumDetailViewModel;
        _trackedVm = newVm;
        if (newVm == null) return;

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
