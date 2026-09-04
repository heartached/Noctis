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
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class ArtistDetailView : UserControl
{
    // One shared track menu for the Popular pills (TrackContextMenuBuilder pattern).
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
        _trackedVm = DataContext as ArtistDetailViewModel;
        TryRestoreScroll();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelPendingScrollRestore();
        if (_trackedVm != null)
            _trackedVm.SavedScrollOffset = PageScrollViewer.Offset.Y;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // An in-place VM swap (same view reused for another artist) fires neither
        // attach nor detach: save the outgoing artist's offset, restore the incoming one's.
        if (_trackedVm != null && !ReferenceEquals(_trackedVm, DataContext))
            _trackedVm.SavedScrollOffset = PageScrollViewer.Offset.Y;
        _trackedVm = DataContext as ArtistDetailViewModel;
        if (this.IsAttachedToVisualTree())
            TryRestoreScroll();
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

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.NewSize.Width <= 0 || DataContext is not ArtistDetailViewModel vm) return;

        // Five tiles per row: section margin 26+26, tile Margin="2" (4px horiz each).
        var usable = e.NewSize.Width - 52;
        var tileContentWidth = usable / 5.0 - 8;
        var newSize = Math.Max(80, tileContentWidth);
        if (Math.Abs(newSize - vm.TileArtworkSize) < 0.5) return;

        var savedY = PageScrollViewer.Offset.Y;
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

    private void OnAlbumContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (DataContext is not ArtistDetailViewModel vm) return;
        if (vm.LibraryAlbumsVm is not { } albumsVm) return;
        // Single-album right-click on this page; clear any stale ctrl-selection on the shared VM.
        albumsVm.CtrlSelectedAlbums = new List<Album>();
    }

    private void OnPopularContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control owner) return;
        if (owner.DataContext is not TopSongRow row) return;
        if (DataContext is not ArtistDetailViewModel vm) return;
        if (vm.LibraryAlbumsVm is not { } albumsVm) return;

        if (_trackMenuBuilder == null)
        {
            _trackMenuBuilder = new TrackContextMenuBuilder();
            _trackMenuBuilder.Build("Remove from Library", null, this);
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

        OpenMenu(_trackMenuBuilder.Menu, owner);
        e.Handled = true;
    }

    private void OpenMenu(ContextMenu menu, Control owner)
    {
        ContextMenuCoordinator.NotifyOpening(menu);
        if (menu.IsOpen)
            menu.Close();

        if (_menuOwner != null && !ReferenceEquals(_menuOwner, owner))
            _menuOwner.ContextMenu = null;
        if (menu.Parent is Control prev && !ReferenceEquals(prev, owner))
            prev.ContextMenu = null;

        _menuOwner = owner;
        owner.ContextMenu = menu;
        menu.Placement = PlacementMode.Pointer;
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
}
