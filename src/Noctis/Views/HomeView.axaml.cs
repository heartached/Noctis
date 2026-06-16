using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class HomeView : UserControl
{
    private EventHandler? _pendingScrollRestore;

    private readonly HashSet<Button> _selectedTiles = new();

    public HomeView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnTilePointerPressed, RoutingStrategies.Tunnel);
        // Forward Ctrl+A from the window so it works without first clicking a tile.
        _ = new WindowKeyForwarder(this, OnViewKeyDown);
    }

    private void OnTilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var source = e.Source as Control;
        while (source != null && !(source is Button b && b.Classes.Contains("album-tile")))
            source = source.Parent as Control;
        if (source is not Button tile) return;

        MultiSelectHelper.HandleAlbumTileClick(tile, e, _selectedTiles);

        // Ensure this view has focus so Ctrl+A reaches OnViewKeyDown
        if (_selectedTiles.Count > 0)
            Focus();
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        var allTiles = new List<Button>();
        foreach (var desc in this.GetVisualDescendants())
        {
            if (desc is Button b && b.Classes.Contains("album-tile"))
                allTiles.Add(b);
        }
        MultiSelectHelper.HandleAlbumSelectAll(e, allTiles, _selectedTiles);
    }

    // ── Context menus (shared builders, same menus as Songs/Playlist views) ──

    private TrackContextMenuBuilder? _trackMenuBuilder;
    private AlbumContextMenuBuilder? _albumMenuBuilder;
    private Control? _menuOwner;

    private void OnTopSongContextRequested(object? sender, ContextRequestedEventArgs e)
        => OpenTrackMenu(sender, e, static vm => vm.PlayTopSongCommand, static vm => vm.ShuffleTopSongsCommand);

    private void OnTimeRotationContextRequested(object? sender, ContextRequestedEventArgs e)
        => OpenTrackMenu(sender, e, static vm => vm.PlayTimeRotationCommand, static vm => vm.ShuffleTimeRotationCommand);

    private void OnHeavyRotationContextRequested(object? sender, ContextRequestedEventArgs e)
        => OpenTrackMenu(sender, e, static vm => vm.PlayHeavyRotationCommand, static vm => vm.ShuffleHeavyRotationCommand);

    private void OnRediscoveredContextRequested(object? sender, ContextRequestedEventArgs e)
        => OpenTrackMenu(sender, e, static vm => vm.PlayRediscoveredCommand, static vm => vm.ShuffleRediscoveredCommand);

    private void OpenTrackMenu(object? sender, ContextRequestedEventArgs e,
        Func<HomeViewModel, ICommand> playCommand, Func<HomeViewModel, ICommand> shuffleCommand)
    {
        if (sender is not Control owner || owner.DataContext is not Track track) return;
        if (DataContext is not HomeViewModel vm) return;

        if (_trackMenuBuilder == null)
        {
            _trackMenuBuilder = new TrackContextMenuBuilder();
            _trackMenuBuilder.Build("Remove from Library", null, this);
        }

        _trackMenuBuilder.Bind(
            track,
            playCommand: playCommand(vm),
            shuffleCommand: shuffleCommand(vm),
            playNextCommand: vm.PlayNextCommand,
            addToQueueCommand: vm.AddToQueueCommand,
            addToPlaylistCommand: vm.AddTrackToNewPlaylistCommand,
            toggleFavoriteCommand: vm.ToggleTrackFavoriteCommand,
            openMetadataCommand: vm.OpenTrackMetadataCommand,
            searchLyricsCommand: vm.SearchLyricsTrackCommand,
            showInExplorerCommand: vm.ShowInExplorerTrackCommand,
            removeCommand: vm.RemoveTrackFromLibraryCommand,
            convertCommand: vm.ConvertTrackCommand,
            scanReplayGainCommand: vm.ScanTrackReplayGainCommand,
            startRadioCommand: vm.StartRadioCommand,
            snoozeCommand: vm.SnoozeForMonthCommand);

        OpenMenu(_trackMenuBuilder.Menu, owner);
        e.Handled = true;
    }

    private void OnRecentAlbumContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control owner || owner.DataContext is not Album album) return;
        if (DataContext is not HomeViewModel vm) return;

        // Push ctrl-selected albums to ViewModel so commands can operate on all of them
        vm.CtrlSelectedAlbums = MultiSelectHelper.GetSelectedData<Album>(_selectedTiles);

        if (_albumMenuBuilder == null)
        {
            _albumMenuBuilder = new AlbumContextMenuBuilder();
            _albumMenuBuilder.Build("Remove from Library", this);
        }

        _albumMenuBuilder.Bind(
            album,
            playCommand: vm.PlayAlbumCommand,
            shuffleCommand: vm.ShuffleAlbumCommand,
            playNextCommand: vm.PlayNextAlbumCommand,
            addToQueueCommand: vm.AddAlbumToQueueCommand,
            addToPlaylistCommand: vm.AddAlbumToNewPlaylistCommand,
            toggleFavoritesCommand: vm.ToggleAlbumFavoritesCommand,
            openMetadataCommand: vm.OpenMetadataCommand,
            showInExplorerCommand: vm.ShowInExplorerAlbumCommand,
            removeCommand: vm.RemoveFromLibraryCommand,
            convertCommand: vm.ConvertAlbumCommand,
            scanReplayGainCommand: vm.ScanAlbumReplayGainCommand,
            searchLyricsCommand: vm.SearchLyricsAlbumCommand);

        OpenMenu(_albumMenuBuilder.Menu, owner);
        e.Handled = true;
    }

    private void OpenMenu(ContextMenu menu, Control owner)
    {
        // Close any menu still open from a previous rapid right-click so menus
        // don't stack on top of each other.
        ContextMenuCoordinator.NotifyOpening(menu);
        if (menu.IsOpen)
            menu.Close();

        // Detach from the previous owner so Open() doesn't throw
        // "Cannot show ContextMenu on a different control".
        if (_menuOwner != null && !ReferenceEquals(_menuOwner, owner))
            _menuOwner.ContextMenu = null;
        if (menu.Parent is Control prev && !ReferenceEquals(prev, owner))
            prev.ContextMenu = null;

        _menuOwner = owner;
        owner.ContextMenu = menu;
        menu.Placement = PlacementMode.Pointer;
        menu.Open(owner);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelPendingScrollRestore();

        if (DataContext is HomeViewModel vm)
        {
            var sv = this.FindDescendantOfType<ScrollViewer>();
            if (sv != null)
                vm.SavedScrollOffset = sv.Offset.Y;
        }

        // Reset multi-selection so it doesn't leak back when the view is revisited.
        MultiSelectHelper.ClearAlbumSelections(_selectedTiles);
        if (DataContext is HomeViewModel selVm) selVm.CtrlSelectedAlbums = new List<Album>();

        base.OnDetachedFromVisualTree(e);
    }

    private void CancelPendingScrollRestore()
    {
        if (_pendingScrollRestore != null)
        {
            LayoutUpdated -= _pendingScrollRestore;
            _pendingScrollRestore = null;
            Opacity = 1;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is HomeViewModel vm && vm.SavedScrollOffset > 0)
        {
            Opacity = 0;
            var targetOffset = vm.SavedScrollOffset;
            var attempts = 0;

            _pendingScrollRestore = (s, args) =>
            {
                attempts++;
                var sv = this.FindDescendantOfType<ScrollViewer>();
                if (sv == null) return;

                if (sv.Extent.Height < targetOffset && attempts < 10)
                    return;

                var clampedOffset = Math.Min(targetOffset, Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
                sv.Offset = new Vector(0, clampedOffset);
                Opacity = 1;
                CancelPendingScrollRestore();
            };

            LayoutUpdated += _pendingScrollRestore;
        }
    }
}
