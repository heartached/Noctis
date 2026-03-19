using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class GenreDetailView : UserControl
{
    private EventHandler? _pendingScrollRestore;

    public GenreDetailView()
    {
        InitializeComponent();

        TrackList.DoubleTapped += OnTrackDoubleTapped;

        // Right-click on track rows (handledEventsToo: true so the handler fires
        // even when ListBoxItem selection machinery marks PointerReleased as handled)
        TrackList.AddHandler(PointerReleasedEvent, OnTrackListPointerReleased, handledEventsToo: true);
    }

    private void OnTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not GenreDetailViewModel vm) return;
        if (TrackList.SelectedItem is Track track)
        {
            vm.PlayFromCommand.Execute(track);
        }
    }

    private void OnTrackListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (DataContext is not GenreDetailViewModel vm) return;

        // Walk up the visual tree to find the ListBoxItem container
        var source = e.Source as Control;
        while (source != null)
        {
            if (source is ListBoxItem item && item.DataContext is Track track)
            {
                var menu = CreateTrackMenu(vm, track);
                menu.Open(item);
                e.Handled = true;
                return;
            }
            source = source.GetVisualParent() as Control;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelPendingScrollRestore();

        if (DataContext is GenreDetailViewModel vm)
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

        if (DataContext is GenreDetailViewModel vm && vm.SavedScrollOffset > 0)
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

    private TrackContextMenu CreateTrackMenu(GenreDetailViewModel vm, Track track)
    {
        var menu = new TrackContextMenu();
        menu.Track = track;
        menu.PlayCommand = vm.PlayFromCommand;
        menu.PlayNextCommand = vm.PlayNextCommand;
        menu.AddToQueueCommand = vm.AddToQueueCommand;
        menu.AddToNewPlaylistCommand = vm.AddToNewPlaylistCommand;
        menu.AddToExistingPlaylistCommand = vm.AddToExistingPlaylistCommand;
        menu.Playlists = vm.Playlists;
        menu.ToggleFavoriteCommand = vm.ToggleFavoriteCommand;
        menu.MetadataCommand = vm.OpenMetadataCommand;
        menu.SearchLyricsCommand = vm.SearchLyricsCommand;
        menu.ViewArtistCommand = vm.ViewArtistCommand;
        menu.ShowInFolderCommand = vm.ShowInExplorerCommand;
        menu.RemoveCommand = vm.RemoveFromLibraryCommand;
        return menu;
    }
}
