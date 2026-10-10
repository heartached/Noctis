using System.IO;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LibraryArtistsView : UserControl
{
    private LibraryArtistsViewModel? _vm;
    private EventHandler? _pendingScrollRestore;
    /// <summary>The VM FilterKey the grid last reset its scroll for (see OnArtistRowsChanged).</summary>
    private string? _scrollResetFilterKey;

    public LibraryArtistsView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    // ── Artist tiles: one shared v2 menu (10-09 redesign), bound to the tile on open ──
    // Replaces the per-tile XAML ContextMenu (built for every realized tile): same entries,
    // now with line icons and Remove Picture last in red.

    private ArtistTileMenu? _artistMenu;
    /// <summary>Culture the menu's strings were read in (they are read once, at build).</summary>
    private string? _artistMenuCulture;
    private Control? _menuOwner;

    private void OnArtistTileContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Button tile && OpenArtistMenu(tile))
            e.Handled = true;
    }

    private bool OpenArtistMenu(Button tile)
    {
        if (tile.DataContext is not Artist artist) return false;
        if (DataContext is not LibraryArtistsViewModel) return false;

        // Rebuilt after a live language switch so the menu follows it.
        if (_artistMenu == null || _artistMenuCulture != Loc.Instance.Culture.Name)
        {
            if (_artistMenu?.Menu.IsOpen == true) _artistMenu.Menu.Close();
            _artistMenuCulture = Loc.Instance.Culture.Name;
            _artistMenu = new ArtistTileMenu(this,
                toggleFavorite: new RelayCommand<Artist>(a => { if (a != null) ToggleFavoriteArtist(a); }),
                chooseImage: new RelayCommand<Artist>(a => { if (a != null) _ = ChangeArtistImageAsync(a); }),
                findImage: new RelayCommand<Artist>(a => { if (a != null) _ = SearchArtistImageAsync(a); }),
                removeImage: new RelayCommand<Artist>(a => { if (a != null) RemoveArtistImage(a); }),
                snoozeArtist: new RelayCommand<Artist>(a => { if (a != null) SnoozeArtistForMonth(a); }));
        }
        _artistMenu.Bind(artist);
        _artistMenu.BindSendToFolder(artist, (DataContext as LibraryArtistsViewModel)?.SendArtistToFolderCommand); // GitHub #121

        var menu = _artistMenu.Menu;
        // Close any menu still open from a previous rapid right-click so menus
        // don't stack on top of each other.
        ContextMenuCoordinator.NotifyOpening(menu);
        MenuOpenAnimation.CloseNow(menu);
        // Detach from the previous owner so Open() doesn't throw
        // "Cannot show ContextMenu on a different control".
        if (_menuOwner != null && !ReferenceEquals(_menuOwner, tile))
            _menuOwner.ContextMenu = null;
        if (menu.Parent is Control prev && !ReferenceEquals(prev, tile))
            prev.ContextMenu = null;
        _menuOwner = tile;
        tile.ContextMenu = menu;
        menu.Placement = PlacementMode.Pointer;
        menu.Open(tile);
        return true;
    }

    /// <summary>
    /// The artist tile's v2 menu: [Set as Favorite / Remove from Favorites] · [Snooze for a
    /// Month] · [Choose from File, Find Picture Online] · [Remove Picture]. No quick tiles: the
    /// Artists page has no artist-wide play commands.
    /// </summary>
    internal sealed class ArtistTileMenu
    {
        public ContextMenu Menu { get; } = new();
        public MenuItem Favorite { get; }
        public MenuItem Unfavorite { get; }
        public MenuItem SnoozeForMonth { get; }
        public MenuItem ChooseImage { get; }
        public MenuItem FindImage { get; }
        public MenuItem RemoveImage { get; }
        public MenuItem SendToFolder { get; }

        public ArtistTileMenu(Control host, ICommand toggleFavorite, ICommand chooseImage, ICommand findImage, ICommand removeImage, ICommand snoozeArtist)
        {
            Menu.Classes.Add(MenuV2.MenuClass);
            var items = Menu.Items;
            items.Add(Favorite = MenuV2.Row(host, Loc.T("LibraryArtists.SetAsFavorite"), "MenuLineHeart"));
            items.Add(Unfavorite = MenuV2.Row(host, Loc.T("LibraryArtists.RemoveFromFavorites"), "MenuLineHeart"));
            MenuV2.IconPath(Unfavorite.Icon)?.Classes.Add("mv2-fav");
            items.Add(new Separator());
            items.Add(SnoozeForMonth = MenuV2.Row(host, Loc.T("Menu.Snooze"), "MenuLineSnooze"));
            items.Add(new Separator());
            items.Add(ChooseImage = MenuV2.Row(host, Loc.T("LibraryArtists.ChooseFromFile"), "MenuLineImage"));
            items.Add(FindImage = MenuV2.Row(host, Loc.T("LibraryArtists.FindPictureOnline"), "MenuLineSearch"));
            // GitHub #121: the artist's songs → Send to Folder (bound by BindSendToFolder).
            items.Add(new Separator());
            items.Add(SendToFolder = MenuV2.Row(host, Loc.T("SendTo.Title"), "MenuLineSendToFolder", visible: false));
            items.Add(new Separator());
            RemoveImage = MenuV2.Row(host, Loc.T("LibraryArtists.RemovePicture"), "MenuLineTrash");
            RemoveImage.Classes.Add("danger");
            items.Add(RemoveImage);

            Favorite.Command = Unfavorite.Command = toggleFavorite;
            SnoozeForMonth.Command = snoozeArtist;
            ChooseImage.Command = chooseImage;
            FindImage.Command = findImage;
            RemoveImage.Command = removeImage;
        }

        public void Bind(Artist artist)
        {
            Menu.DataContext = artist;
            foreach (var item in new[] { Favorite, Unfavorite, SnoozeForMonth, ChooseImage, FindImage, RemoveImage })
                item.CommandParameter = artist;
            Favorite.IsVisible = !artist.IsFavorite;
            Unfavorite.IsVisible = artist.IsFavorite;
            RemoveImage.IsVisible = !string.IsNullOrEmpty(artist.ImagePath);
            MenuV2.RefreshLayout(Menu.Items);
        }

        /// <summary>GitHub #121: Send to Folder for the artist (hidden when null). Call after Bind.</summary>
        public void BindSendToFolder(Artist artist, ICommand? command)
        {
            SendToFolder.Command = command;
            SendToFolder.CommandParameter = artist;
            SendToFolder.IsVisible = command != null;
            MenuV2.RefreshLayout(Menu.Items);
        }
    }

    private async Task ChangeArtistImageAsync(Artist artist)
    {
        // Fire-and-forget from the menu: an escaped exception would go unobserved.
        try
        {
            if (DataContext is not LibraryArtistsViewModel vm) return;

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
            try
            {
                await using var stream = await files[0].OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                data = ms.ToArray();
            }
            catch
            {
                return;
            }

            if (data.Length == 0) return;
            await vm.ChangeArtistImageAsync(artist, data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ArtistsView] Change artist image failed: {ex.Message}");
        }
    }

    private async Task SearchArtistImageAsync(Artist artist)
    {
        try
        {
            if (DataContext is LibraryArtistsViewModel vm)
                await vm.SearchArtistImageAsync(artist);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ArtistsView] Artist image search failed: {ex.Message}");
        }
    }

    private void RemoveArtistImage(Artist artist)
    {
        if (DataContext is LibraryArtistsViewModel vm)
            vm.RemoveArtistImage(artist);
    }

    private void ToggleFavoriteArtist(Artist artist)
    {
        if (DataContext is LibraryArtistsViewModel vm)
            vm.ToggleFavoriteArtist(artist);
    }

    private void SnoozeArtistForMonth(Artist artist)
    {
        if (DataContext is LibraryArtistsViewModel vm)
            vm.SnoozeArtistForMonthCommand.Execute(artist);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
            _vm.ArtistRows.CollectionChanged -= OnArtistRowsChanged;

        _vm = DataContext as LibraryArtistsViewModel;
        if (_vm != null)
        {
            _vm.ArtistRows.CollectionChanged += OnArtistRowsChanged;
            _scrollResetFilterKey = _vm.FilterKey;
        }
    }

    private void OnArtistRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Scroll to top when the active filter (search text) changes, not on every rebuild:
        // a library reload, portrait refresh or favorite toggle keeps the user's place.
        // BUT skip if a scroll restore is pending (returning from artist detail)
        if (_vm == null || _vm.FilterKey == _scrollResetFilterKey)
            return;
        _scrollResetFilterKey = _vm.FilterKey;

        if (!_vm.HasActiveFilter)
            return;
        if (_pendingScrollRestore != null)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            var sv = ArtistListBox.FindDescendantOfType<ScrollViewer>();
            if (sv != null)
                sv.Offset = new Vector(0, 0);
        }, DispatcherPriority.Background);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelPendingScrollRestore();

        if (DataContext is LibraryArtistsViewModel vm)
        {
            var sv = ArtistListBox.FindDescendantOfType<ScrollViewer>();
            if (sv != null)
                vm.SavedScrollOffset = sv.Offset.Y;
        }
        if (_vm != null)
            _vm.ArtistRows.CollectionChanged -= OnArtistRowsChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void CancelPendingScrollRestore()
    {
        if (_pendingScrollRestore != null)
        {
            ArtistListBox.LayoutUpdated -= _pendingScrollRestore;
            _pendingScrollRestore = null;
            ArtistListBox.Opacity = 1;
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateGridColumns(e.NewSize.Width);
    }

    /// <summary>DockPanel margin (16 + 2) plus the vertical scrollbar's gutter.</summary>
    private const double GridChromeWidth = 30;

    private void UpdateGridColumns(double viewWidth)
    {
        if (viewWidth > 0 && DataContext is LibraryArtistsViewModel vm)
            vm.UpdateGridColumns(viewWidth - GridChromeWidth);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateGridColumns(Bounds.Width);

        // Re-subscribe to collection changes (unsubscribed in OnDetachedFromVisualTree)
        if (_vm != null)
        {
            _vm.ArtistRows.CollectionChanged -= OnArtistRowsChanged;
            _vm.ArtistRows.CollectionChanged += OnArtistRowsChanged;
            _scrollResetFilterKey = _vm.FilterKey;
        }

        if (DataContext is LibraryArtistsViewModel vm && vm.SavedScrollOffset > 0)
        {
            // Hide ListBox until scroll is restored to prevent flash-at-top flicker
            ArtistListBox.Opacity = 0;
            var targetOffset = vm.SavedScrollOffset;
            var attempts = 0;

            _pendingScrollRestore = (s, args) =>
            {
                attempts++;
                var sv = ArtistListBox.FindDescendantOfType<ScrollViewer>();
                if (sv == null) return;

                // Wait until the ScrollViewer extent is tall enough, with safety limit
                if (sv.Extent.Height < targetOffset && attempts < 10)
                    return;

                // Clamp to actual extent if content shrank since last visit
                var clampedOffset = Math.Min(targetOffset, Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
                sv.Offset = new Vector(0, clampedOffset);
                ArtistListBox.Opacity = 1;
                CancelPendingScrollRestore();
            };

            ArtistListBox.LayoutUpdated += _pendingScrollRestore;
        }
    }
}
