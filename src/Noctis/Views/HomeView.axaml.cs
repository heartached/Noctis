using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class HomeView : UserControl
{
    private EventHandler? _pendingScrollRestore;

    private readonly HashSet<Button> _selectedTiles = new();

    /// <summary>Below this width Last Played drops under Most Played.</summary>
    private const double HeroTwoColumnMinWidth = 1100;
    private bool _heroNarrow;

    public HomeView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnTilePointerPressed, RoutingStrategies.Tunnel);
        SizeChanged += (_, e) => UpdateHeroLayout(e.NewSize.Width);
        // Forward Ctrl+A from the window so it works without first clicking a tile.
        _ = new WindowKeyForwarder(this, OnViewKeyDown);
    }

    /// <summary>
    /// Two equal columns (Most Played | Last Played) when there is room; one column with
    /// Last Played underneath otherwise. Only touches the grid when the mode actually flips.
    /// </summary>
    private void UpdateHeroLayout(double width)
    {
        var narrow = width > 0 && width < HeroTwoColumnMinWidth;
        if (narrow == _heroNarrow) return;
        _heroNarrow = narrow;

        if (narrow)
        {
            HeroGrid.ColumnDefinitions[1].Width = new GridLength(0);
            HeroGrid.ColumnDefinitions[2].Width = new GridLength(0);
            Grid.SetColumn(LastPlayedPanel, 0);
            Grid.SetRow(LastPlayedPanel, 2);
            LastPlayedPanel.Margin = new Thickness(0, 32, 0, 0);
        }
        else
        {
            HeroGrid.ColumnDefinitions[1].Width = new GridLength(28);
            HeroGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(LastPlayedPanel, 2);
            Grid.SetRow(LastPlayedPanel, 1);
            LastPlayedPanel.Margin = default;
        }
    }

    /// <summary>
    /// Albums row covers follow the Albums-page tile size for the row's own width. The
    /// row measures itself rather than the page: the page width minus margins ignored the
    /// scroll viewer's bar, so five tiles came out a few pixels too wide and the fifth
    /// wrapped (09-13). The 2px slack absorbs layout rounding.
    /// </summary>
    private void OnAlbumsRowSizeChanged(object? sender, SizeChangedEventArgs e)
        => (DataContext as HomeViewModel)?.UpdateAlbumTileSize(e.NewSize.Width - 2);

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
        if (e.Key == Key.Escape && _selectedTiles.Count > 0)
        {
            MultiSelectHelper.ClearAlbumSelections(_selectedTiles);
            if (DataContext is HomeViewModel vm) vm.CtrlSelectedAlbums = new List<Album>();
            e.Handled = true;
            return;
        }

        var allTiles = new List<Button>();
        foreach (var desc in this.GetVisualDescendants())
        {
            if (desc is Button b && b.Classes.Contains("album-tile"))
                allTiles.Add(b);
        }
        MultiSelectHelper.HandleAlbumSelectAll(e, allTiles, _selectedTiles);
    }

    // ── Context menus (shared builders, v2 layout as on the Albums and album pages) ──

    private TrackContextMenuBuilder? _trackMenuBuilder;
    private AlbumContextMenuBuilder? _albumMenuBuilder;
    /// <summary>Culture each menu's strings were read in (they are read once, at Build).</summary>
    private string? _trackMenuCulture, _albumMenuCulture;
    private Control? _menuOwner;

    // Most Played and Last Played share one row template; the row says which list it is in.
    private static bool IsLastPlayedRow(Control? c) => c?.DataContext is TopSongRow { IsLastPlayed: true };

    /// <summary>A chart row plays on DOUBLE click (user ask 09-14), like every flat track
    /// list in the app; a single click leaves the row alone. Taps that land on a button
    /// inside the row belong to that button.</summary>
    private void OnChartRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control row || row.DataContext is not TopSongRow item) return;
        if (e.Source is Control source && source.FindAncestorOfType<Button>(true) is { } inner
            && !ReferenceEquals(inner, row)) return;
        if (DataContext is not HomeViewModel vm) return;
        vm.PlayChartRowCommand.Execute(item);
    }

    private void OnChartRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (IsLastPlayedRow(sender as Control))
            OpenTrackMenu(sender, e, static vm => vm.PlayLastPlayedCommand, static vm => vm.ShuffleLastPlayedCommand);
        else
            OpenTrackMenu(sender, e, static vm => vm.PlayTopSongCommand, static vm => vm.ShuffleTopSongsCommand);
    }


    private void OnTimeRotationContextRequested(object? sender, ContextRequestedEventArgs e)
        => OpenTrackMenu(sender, e, static vm => vm.PlayTimeRotationCommand, static vm => vm.ShuffleTimeRotationCommand);

    private void OnHeavyRotationContextRequested(object? sender, ContextRequestedEventArgs e)
        => OpenTrackMenu(sender, e, static vm => vm.PlayHeavyRotationCommand, static vm => vm.ShuffleHeavyRotationCommand);

    private void OnRediscoveredContextRequested(object? sender, ContextRequestedEventArgs e)
        => OpenTrackMenu(sender, e, static vm => vm.PlayRediscoveredCommand, static vm => vm.ShuffleRediscoveredCommand);

    private void OpenTrackMenu(object? sender, ContextRequestedEventArgs e,
        Func<HomeViewModel, ICommand> playCommand, Func<HomeViewModel, ICommand> shuffleCommand)
    {
        if (OpenTrackMenu(sender as Control, playCommand, shuffleCommand))
            e.Handled = true;
    }

    private bool OpenTrackMenu(Control? owner,
        Func<HomeViewModel, ICommand> playCommand, Func<HomeViewModel, ICommand> shuffleCommand)
    {
        if (owner == null) return false;
        // Top-song rows wrap their Track in a TopSongRow for rank/bar display.
        var track = owner.DataContext switch
        {
            Track t => t,
            TopSongRow r => r.Track,
            _ => null,
        };
        if (track == null) return false;
        if (DataContext is not HomeViewModel vm) return false;

        // v2 layout (10-09 redesign); rebuilt after a live language switch so it follows it.
        if (_trackMenuBuilder == null || _trackMenuCulture != Loc.Instance.Culture.Name)
        {
            _trackMenuCulture = Loc.Instance.Culture.Name;
            _trackMenuBuilder = new TrackContextMenuBuilder();
            _trackMenuBuilder.Build(Loc.T("LibraryAlbums.RemoveFromLibrary"), null, this, v2: true, removeIsDanger: true);
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
            snoozeCommand: vm.SnoozeForMonthCommand,
            viewAlbumCommand: vm.ViewAlbumFromTrackCommand,
            viewArtistCommand: vm.ViewArtistCommand);

        OpenMenu(_trackMenuBuilder.Menu, owner);
        return true;
    }

    private void OnRecentAlbumContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control owner && OpenAlbumMenu(owner))
            e.Handled = true;
    }

    private bool OpenAlbumMenu(Control owner)
    {
        // The rail's featured card sits on the page VM and carries its Album in Tag.
        var album = owner.DataContext as Album ?? owner.Tag as Album;
        if (album == null) return false;
        if (DataContext is not HomeViewModel vm) return false;

        // Push ctrl-selected albums to ViewModel so commands can operate on all of them
        vm.CtrlSelectedAlbums = MultiSelectHelper.GetSelectedData<Album>(_selectedTiles);

        if (_albumMenuBuilder == null || _albumMenuCulture != Loc.Instance.Culture.Name)
        {
            _albumMenuCulture = Loc.Instance.Culture.Name;
            _albumMenuBuilder = new AlbumContextMenuBuilder();
            _albumMenuBuilder.Build(Loc.T("LibraryAlbums.RemoveFromLibrary"), this, v2: true, removeIsDanger: true);
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
        return true;
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

    /// <summary>Tile hover dots: the same menu a right-click on the tile opens, bound
    /// afresh (the tile may still hold the shared menu from an older language or album).</summary>
    private void OnTileMoreClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        for (var c = sender as Control; c != null; c = c.Parent as Control)
        {
            if (c is Button tile && tile.Classes.Contains("album-tile"))
            {
                OpenAlbumMenu(tile);
                break;
            }
        }
        e.Handled = true;
    }
}
