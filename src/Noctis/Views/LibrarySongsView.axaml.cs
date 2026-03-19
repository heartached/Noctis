using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LibrarySongsView : UserControl
{
    private LibrarySongsViewModel? _vm;
    private EventHandler? _pendingScrollRestore;
    private readonly Dictionary<object, PlaylistMenuPopulator> _playlistPopulators = new();

    public LibrarySongsView()
    {
        InitializeComponent();

        // Double-click to play from here
        TrackList.DoubleTapped += OnTrackDoubleTapped;

        DataContextChanged += OnDataContextChanged;
    }

    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LibrarySongsViewModel vm) return;
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

    private void OnTrackFlyoutOpened(object? sender, EventArgs e)
    {
        if (DataContext is not LibrarySongsViewModel vm) return;
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

        // The flyout's target button has the Track as its Tag
        var track = (flyout.Target as Button)?.Tag as Track;
        populator.Populate(vm.Playlists, vm.AddToExistingPlaylistCommand,
            playlist => new object[] { track!, playlist });
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
            _vm.FilteredTracks.CollectionChanged -= OnFilteredTracksChanged;

        _vm = DataContext as LibrarySongsViewModel;
        if (_vm != null)
            _vm.FilteredTracks.CollectionChanged += OnFilteredTracksChanged;
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
        if (DataContext is not LibrarySongsViewModel vm) return;
        if (TrackList.SelectedItem is Track track)
        {
            vm.PlayFromHereCommand.Execute(track);
        }
    }

    private void OnQueueButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Walk up the logical tree to find MainWindow and navigate to queue
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
            TrackList.Opacity = 1;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Re-subscribe to collection changes (unsubscribed in OnDetachedFromVisualTree)
        if (_vm != null)
        {
            _vm.FilteredTracks.CollectionChanged -= OnFilteredTracksChanged;
            _vm.FilteredTracks.CollectionChanged += OnFilteredTracksChanged;
        }

        if (DataContext is LibrarySongsViewModel vm && vm.SavedScrollOffset > 0)
        {
            TrackList.Opacity = 0;
            var targetOffset = vm.SavedScrollOffset;
            var attempts = 0;

            _pendingScrollRestore = (s, args) =>
            {
                attempts++;
                var sv = TrackList.FindDescendantOfType<ScrollViewer>();
                if (sv == null) return;

                if (sv.Extent.Height < targetOffset && attempts < 50)
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
