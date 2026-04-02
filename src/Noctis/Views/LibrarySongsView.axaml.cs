using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Converters;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LibrarySongsView : UserControl
{
    private LibrarySongsViewModel? _vm;
    private EventHandler? _pendingScrollRestore;
    private readonly Dictionary<object, PlaylistMenuPopulator> _playlistPopulators = new();
    private readonly HashSet<ListBoxItem> _selectedTrackItems = new();
    private ContextMenu? _sharedContextMenu;
    private ListBoxItem? _menuOwnerItem;
    private Grid? _headerGrid;
    private TextBlock? _headerTitle;
    private TextBlock? _headerArtist;
    private Image? _headerArtImage;
    private TextBlock? _headerPlaceholder;

    public LibrarySongsView()
    {
        InitializeComponent();

        // Double-click to play from here
        TrackList.DoubleTapped += OnTrackDoubleTapped;
        TrackList.AddHandler(PointerPressedEvent, OnTrackPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Attach context menu to every ListBoxItem so right-click works across the full row
        TrackList.ContainerPrepared += OnTrackContainerPrepared;
        TrackList.ContainerClearing += OnTrackContainerClearing;

        DataContextChanged += OnDataContextChanged;
    }

    private void OnTrackPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var source = e.Source as Control;
        while (source != null && source is not ListBoxItem)
            source = source.Parent as Control;
        if (source is not ListBoxItem item) return;

        MultiSelectHelper.HandleTrackRowClick(item, e, _selectedTrackItems);
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        MultiSelectHelper.HandleTrackSelectAll(e, TrackList, _selectedTrackItems);
    }

    /// <summary>
    /// Lazily creates and returns the shared context menu for track rows.
    /// One menu instance is reused for all rows — DataContext is set before showing.
    /// </summary>
    private ContextMenu GetOrCreateContextMenu()
    {
        if (_sharedContextMenu != null) return _sharedContextMenu;

        var vm = DataContext as LibrarySongsViewModel;
        if (vm == null) return new ContextMenu();

        _sharedContextMenu = new ContextMenu();
        _sharedContextMenu.Opened += OnContextMenuOpened;

        // Build menu items — these reference the VM's commands directly
        var items = _sharedContextMenu.Items;

        // Track header — content is set in BindContextMenuToTrack
        var header = new MenuItem { IsHitTestVisible = false, Focusable = false };
        header.Template = new Avalonia.Controls.Templates.FuncControlTemplate<MenuItem>((item, scope) =>
        {
            var border = new Avalonia.Controls.Border
            {
                Padding = new Thickness(11, 8),
                Background = Brushes.Transparent,
                Child = new ContentPresenter
                {
                    [!ContentPresenter.ContentProperty] = item[!MenuItem.HeaderProperty]
                }
            };
            return border;
        });
        items.Add(header);
        items.Add(new Separator());

        // Play
        var play = new MenuItem { MaxWidth = 400 };
        play.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Play%20ICON.png");
        items.Add(play);

        // Shuffle
        var shuffle = new MenuItem { Header = "Shuffle", Command = vm.ShuffleAllCommand };
        shuffle.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Shuffle%20ICON.png");
        items.Add(shuffle);

        // Play Next
        var playNext = new MenuItem { Header = "Play Next" };
        playNext.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Forward%20ICON.png");
        items.Add(playNext);

        // Add to Queue
        var addQueue = new MenuItem { Header = "Add to Queue" };
        addQueue.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Queue%20ICON.png");
        items.Add(addQueue);

        items.Add(new Separator());

        // Add to Playlist submenu
        var addToPlaylist = new MenuItem { Header = "Add to Playlist" };
        addToPlaylist.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Playlists%20ICON.png");
        var createNew = new MenuItem { Header = "Create New Playlist" };
        addToPlaylist.Items.Add(createNew);
        var playlistSep = new Separator();
        addToPlaylist.Items.Add(playlistSep);
        items.Add(addToPlaylist);

        items.Add(new Separator());

        // Favorites
        var fav = new MenuItem { Header = "Favorites" };
        fav.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Your%20Favorites%20ICON.png");
        items.Add(fav);

        var unfav = new MenuItem { Header = "Remove from Favorites" };
        unfav.Icon = new PathIcon { Width = 14, Height = 14, Data = (Geometry)this.FindResource("HeartFillIcon")!, Foreground = new SolidColorBrush(Color.Parse("#E74856")) };
        items.Add(unfav);

        // Metadata
        var metadata = new MenuItem { Header = "Metadata" };
        metadata.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Metadata%20ICON.png");
        items.Add(metadata);

        // Search Lyrics
        var lyrics = new MenuItem { Header = "Search Lyrics" };
        lyrics.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Lyrics%20ICON.png");
        items.Add(lyrics);

        // Show Folder
        var folder = new MenuItem { Header = "Show Folder" };
        folder.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Folder%20ICON.png");
        items.Add(folder);

        items.Add(new Separator());

        // Remove from Library
        var remove = new MenuItem { Header = "Remove from Library" };
        remove.Icon = new PathIcon { Width = 14, Height = 14, Data = (Geometry)this.FindResource("TrashIcon")! };
        items.Add(remove);

        // Store the playlist populator
        var populator = new PlaylistMenuPopulator(addToPlaylist, playlistSep);
        _playlistPopulators[_sharedContextMenu] = populator;

        return _sharedContextMenu;
    }

    /// <summary>
    /// Updates the shared context menu items to reference the current track.
    /// Called before showing the menu (from ContextRequested or 3-dots click).
    /// </summary>
    private void BindContextMenuToTrack(Track track)
    {
        var menu = GetOrCreateContextMenu();
        var vm = DataContext as LibrarySongsViewModel;
        if (vm == null) return;

        menu.DataContext = track;

        var items = menu.Items.OfType<MenuItem>().ToList();
        // items[0] = header — reuse cached controls, just update text/image
        if (_headerGrid == null)
        {
            items[0].Header = BuildTrackHeader(track);
        }
        else
        {
            UpdateTrackHeader(track);
            items[0].Header = _headerGrid;
        }
        // items[1] = Play — plain string header so it matches Shuffle's style exactly
        var playTitle = track.Title.Length > 50 ? track.Title[..47] + "..." : track.Title;
        items[1].Header = $"Play \"{playTitle}\"";
        items[1].Command = vm.PlayFromHereCommand;
        items[1].CommandParameter = track;
        // items[2] = Shuffle (no track-specific binding needed)
        // items[3] = Play Next
        items[3].Command = vm.PlayNextCommand;
        items[3].CommandParameter = track;
        // items[4] = Add to Queue
        items[4].Command = vm.AddToQueueCommand;
        items[4].CommandParameter = track;
        // items[5] = Add to Playlist
        var createNew = items[5].Items.OfType<MenuItem>().First();
        createNew.Command = vm.AddToNewPlaylistCommand;
        createNew.CommandParameter = track;
        // items[6] = Favorites
        items[6].Command = vm.ToggleFavoriteCommand;
        items[6].CommandParameter = track;
        items[6].IsVisible = !track.IsFavorite;
        // items[7] = Remove from Favorites
        items[7].Command = vm.ToggleFavoriteCommand;
        items[7].CommandParameter = track;
        items[7].IsVisible = track.IsFavorite;
        if (items[7].Icon is PathIcon heartIcon)
            heartIcon.Foreground = new SolidColorBrush(Color.Parse("#E74856"));
        // items[8] = Metadata
        items[8].Command = vm.OpenMetadataCommand;
        items[8].CommandParameter = track;
        // items[9] = Search Lyrics
        items[9].Command = vm.SearchLyricsCommand;
        items[9].CommandParameter = track;
        // items[10] = Show Folder
        items[10].Command = vm.ShowInExplorerCommand;
        items[10].CommandParameter = track;
        // items[11] = Remove from Library
        items[11].Command = vm.RemoveFromLibraryCommand;
        items[11].CommandParameter = track;

        // Populate playlist submenu
        if (_playlistPopulators.TryGetValue(menu, out var populator))
        {
            populator.Populate(vm.Playlists, vm.AddToExistingPlaylistCommand,
                playlist => new object[] { track, playlist });
        }
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
        // Also detach from any button that previously owned the menu
        if (_sharedContextMenu?.Parent is Control parent)
        {
            parent.ContextMenu = null;
        }
    }

    private void OnTrackItemContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        if (item.DataContext is not Track track) return;
        if (DataContext is LibrarySongsViewModel vm)
            vm.CtrlSelectedTracks = MultiSelectHelper.GetSelectedTrackData<Track>(_selectedTrackItems);

        BindContextMenuToTrack(track);
        var menu = GetOrCreateContextMenu();
        DetachMenuFromOwner();
        _menuOwnerItem = item;
        item.ContextMenu = menu;
    }

    private void OnOptionsButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not Track track) return;
        if (DataContext is LibrarySongsViewModel vm)
            vm.CtrlSelectedTracks = MultiSelectHelper.GetSelectedTrackData<Track>(_selectedTrackItems);

        BindContextMenuToTrack(track);
        var menu = GetOrCreateContextMenu();

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

    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        // Playlist populator refresh already done in BindContextMenuToTrack
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
            _vm.FilteredTracks.CollectionChanged -= OnFilteredTracksChanged;

        _vm = DataContext as LibrarySongsViewModel;
        if (_vm != null)
            _vm.FilteredTracks.CollectionChanged += OnFilteredTracksChanged;

        // Reset shared menu so it picks up new VM commands
        _sharedContextMenu = null;
        _playlistPopulators.Clear();
        _headerGrid = null;
        _headerTitle = null;
        _headerArtist = null;
        _headerArtImage = null;
        _headerPlaceholder = null;
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
        // Ignore double-taps on the 3-dot options button — it should only open the menu
        if (e.Source is Control source && source.FindAncestorOfType<Button>() != null)
            return;
        if (DataContext is not LibrarySongsViewModel vm) return;
        if (TrackList.SelectedItem is Track track)
        {
            vm.PlayFromHereCommand.Execute(track);
        }
    }

    private void OnQueueButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
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
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (_vm != null)
        {
            _vm.FilteredTracks.CollectionChanged -= OnFilteredTracksChanged;
            _vm.FilteredTracks.CollectionChanged += OnFilteredTracksChanged;
        }

        if (DataContext is LibrarySongsViewModel vm && vm.SavedScrollOffset > 0)
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

    private static Avalonia.Controls.Border CreatePngIcon(string assetUri)
    {
        var border = new Avalonia.Controls.Border { Width = 14, Height = 14 };
        border[!Avalonia.Controls.Border.BackgroundProperty] = border.GetResourceObservable("SystemControlForegroundBaseHighBrush").ToBinding();
        RenderOptions.SetBitmapInterpolationMode(border, BitmapInterpolationMode.HighQuality);
        border.OpacityMask = new ImageBrush
        {
            Source = new Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri(assetUri))),
            Stretch = Stretch.Uniform
        };
        return border;
    }

    private static readonly ArtworkPathConverter _sharedArtworkConverter = new();

    private Grid BuildTrackHeader(Track track)
    {
        if (_headerGrid != null)
        {
            UpdateTrackHeader(track);
            return _headerGrid;
        }

        _headerGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("40,10,*") };

        var artBorder = new Avalonia.Controls.Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(4), ClipToBounds = true,
        };
        artBorder[!Avalonia.Controls.Border.BackgroundProperty] = artBorder.GetResourceObservable("SystemControlBackgroundBaseLowBrush").ToBinding();

        var artPanel = new Panel();
        _headerPlaceholder = new TextBlock
        {
            Text = "\u266A", FontSize = 14,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Opacity = 0.3
        };
        artPanel.Children.Add(_headerPlaceholder);

        _headerArtImage = new Image { Stretch = Stretch.UniformToFill };
        artPanel.Children.Add(_headerArtImage);

        artBorder.Child = artPanel;
        Grid.SetColumn(artBorder, 0);
        _headerGrid.Children.Add(artBorder);

        var textStack = new StackPanel
        {
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        _headerTitle = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 13 };
        _headerArtist = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#E74856")), Margin = new Thickness(0, 2, 0, 0) };
        textStack.Children.Add(_headerTitle);
        textStack.Children.Add(_headerArtist);
        Grid.SetColumn(textStack, 2);
        _headerGrid.Children.Add(textStack);

        UpdateTrackHeader(track);
        return _headerGrid;
    }

    private void UpdateTrackHeader(Track track)
    {
        _headerTitle!.Text = track.Title;
        _headerArtist!.Text = track.Artist;

        var hasArt = !string.IsNullOrEmpty(track.AlbumArtworkPath);
        _headerPlaceholder!.IsVisible = !hasArt;
        _headerArtImage!.IsVisible = hasArt;
        if (hasArt)
        {
            _headerArtImage.Source = _sharedArtworkConverter.Convert(track.AlbumArtworkPath, typeof(Avalonia.Media.IImage), null!, System.Globalization.CultureInfo.InvariantCulture) as Avalonia.Media.IImage;
        }
        else
        {
            _headerArtImage.Source = null;
        }
    }
}
