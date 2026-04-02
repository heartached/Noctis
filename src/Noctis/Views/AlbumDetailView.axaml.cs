using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    public AlbumDetailView()
    {
        InitializeComponent();

        TrackList.DoubleTapped += OnTrackDoubleTapped;
        TrackList.ContainerPrepared += OnTrackContainerPrepared;
        TrackList.ContainerClearing += OnTrackContainerClearing;
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
        if (e.Handled) return; // Grid's AXAML ContextMenu already handled it (non-dead-zone click)
        if (sender is not ListBoxItem item) return;

        // Dead zone click — find the Grid's context menu and show it
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
        // Ignore double-taps on the 3-dot options button — it should only open the menu
        if (e.Source is Control source && source.FindAncestorOfType<Button>() != null)
            return;
        if (DataContext is not AlbumDetailViewModel vm) return;
        if (TrackList.SelectedItem is Track track)
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
            TrackList.Opacity = 1;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Fade in gradient background when BackgroundBrush becomes available
        if (DataContext is AlbumDetailViewModel vm2)
        {
            // If brush is already set (re-attach), show immediately
            if (vm2.BackgroundBrush != null)
                AlbumGradientBg.Opacity = 1;

            _bgHandler = (_, args) =>
            {
                if (args.PropertyName == nameof(AlbumDetailViewModel.BackgroundBrush))
                    AlbumGradientBg.Opacity = ((AlbumDetailViewModel)DataContext!).BackgroundBrush != null ? 1 : 0;
            };
            vm2.PropertyChanged += _bgHandler;
        }

        if (DataContext is AlbumDetailViewModel vm && vm.SavedScrollOffset > 0)
        {
            TrackList.Opacity = 0;
            var targetOffset = vm.SavedScrollOffset;
            var attempts = 0;

            _pendingScrollRestore = (s, args) =>
            {
                attempts++;
                var sv = TrackList.FindDescendantOfType<ScrollViewer>();
                if (sv == null) return;

                if (sv.Extent.Height < targetOffset && attempts < 10)
                    return;

                var clampedOffset = Math.Min(targetOffset, Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
                sv.Offset = new Vector(0, clampedOffset);
                TrackList.Opacity = 1;
                CancelPendingScrollRestore();
            };

            TrackList.LayoutUpdated += _pendingScrollRestore;
        }
    }
}
