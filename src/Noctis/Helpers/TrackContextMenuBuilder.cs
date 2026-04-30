using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Noctis.Converters;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// Builds and binds a reusable track context menu shared across views.
/// Stores named references to menu items to avoid fragile index-based access.
/// </summary>
public sealed class TrackContextMenuBuilder
{
    // ── Named menu item references ──
    public MenuItem Play { get; private set; } = null!;
    public MenuItem Shuffle { get; private set; } = null!;
    public MenuItem PlayNext { get; private set; } = null!;
    public MenuItem AddToQueue { get; private set; } = null!;
    public MenuItem AddToPlaylist { get; private set; } = null!;
    public MenuItem Favorite { get; private set; } = null!;
    public MenuItem Unfavorite { get; private set; } = null!;
    public MenuItem Metadata { get; private set; } = null!;
    public MenuItem SearchLyrics { get; private set; } = null!;
    public MenuItem ShowFolder { get; private set; } = null!;
    public MenuItem Remove { get; private set; } = null!;

    public ContextMenu Menu { get; private set; } = null!;
    public PlaylistMenuPopulator Populator { get; private set; } = null!;

    /// <summary>
    /// Builds the context menu. Call once per view lifetime.
    /// </summary>
    /// <param name="removeHeader">Label for the last item (e.g. "Remove from Library" or "Remove from Playlist").</param>
    /// <param name="removeIconUri">Asset URI for the remove icon, or null to use the TrashIcon resource.</param>
    /// <param name="resourceHost">Control used to resolve resources (e.g. icons).</param>
    public ContextMenu Build(string removeHeader, string? removeIconUri, Control resourceHost)
    {
        Menu = new ContextMenu();
        var items = Menu.Items;

        Play = new MenuItem { MaxWidth = 400 };
        Play.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Play%20ICON.png");
        items.Add(Play);

        Shuffle = new MenuItem { Header = "Shuffle" };
        Shuffle.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Shuffle%20ICON.png");
        items.Add(Shuffle);

        PlayNext = new MenuItem { Header = "Play Next" };
        PlayNext.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Forward%20ICON.png");
        items.Add(PlayNext);

        AddToQueue = new MenuItem { Header = "Add to Queue" };
        AddToQueue.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Queue%20ICON.png");
        items.Add(AddToQueue);

        items.Add(new Separator());

        AddToPlaylist = new MenuItem { Header = "Add to Playlist" };
        AddToPlaylist.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Playlists%20ICON.png");
        var createNew = new MenuItem { Header = "Create New Playlist" };
        AddToPlaylist.Items.Add(createNew);
        var playlistSep = new Separator();
        AddToPlaylist.Items.Add(playlistSep);
        items.Add(AddToPlaylist);

        items.Add(new Separator());

        Favorite = new MenuItem { Header = "Favorites" };
        Favorite.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Your%20Favorites%20ICON.png");
        items.Add(Favorite);

        Unfavorite = new MenuItem { Header = "Remove from Favorites" };
        Unfavorite.Icon = new PathIcon
        {
            Width = 14, Height = 14,
            Data = (Geometry)resourceHost.FindResource("HeartFillIcon")!,
            Foreground = new SolidColorBrush(Color.Parse("#E74856"))
        };
        items.Add(Unfavorite);

        Metadata = new MenuItem { Header = "Metadata" };
        Metadata.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Metadata%20ICON.png");
        items.Add(Metadata);

        SearchLyrics = new MenuItem { Header = "Search Lyrics" };
        SearchLyrics.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Lyrics%20ICON.png");
        items.Add(SearchLyrics);

        ShowFolder = new MenuItem { Header = "Show Folder" };
        ShowFolder.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Folder%20ICON.png");
        items.Add(ShowFolder);

        items.Add(new Separator());

        Remove = new MenuItem { Header = removeHeader };
        if (removeHeader.Contains("Library", StringComparison.OrdinalIgnoreCase))
            Remove.Classes.Add("danger");
        if (removeIconUri != null)
            Remove.Icon = CreatePngIcon(removeIconUri);
        else
            Remove.Icon = new PathIcon { Width = 14, Height = 14, Data = (Geometry)resourceHost.FindResource("TrashIcon")! };
        items.Add(Remove);

        Populator = new PlaylistMenuPopulator(AddToPlaylist, playlistSep);

        return Menu;
    }

    /// <summary>
    /// Binds track data and commands to the menu. Call before showing.
    /// </summary>
    public void Bind(
        Track track,
        ICommand playCommand,
        ICommand shuffleCommand,
        ICommand playNextCommand,
        ICommand addToQueueCommand,
        ICommand addToNewPlaylistCommand,
        ICommand toggleFavoriteCommand,
        ICommand openMetadataCommand,
        ICommand searchLyricsCommand,
        ICommand showInExplorerCommand,
        ICommand removeCommand,
        ObservableCollection<Playlist>? playlists,
        ICommand? addToExistingPlaylistCommand)
    {
        Menu.DataContext = track;

        // Play
        Play.Header = "Play";
        Play.Command = playCommand;
        Play.CommandParameter = track;

        Shuffle.Command = shuffleCommand;

        PlayNext.Command = playNextCommand;
        PlayNext.CommandParameter = track;

        AddToQueue.Command = addToQueueCommand;
        AddToQueue.CommandParameter = track;

        // Add to Playlist > Create New
        var createNew = AddToPlaylist.Items[0] as MenuItem;
        if (createNew != null)
        {
            createNew.Command = addToNewPlaylistCommand;
            createNew.CommandParameter = track;
        }

        // Favorites
        Favorite.Command = toggleFavoriteCommand;
        Favorite.CommandParameter = track;
        Favorite.IsVisible = !track.IsFavorite;

        Unfavorite.Command = toggleFavoriteCommand;
        Unfavorite.CommandParameter = track;
        Unfavorite.IsVisible = track.IsFavorite;
        if (Unfavorite.Icon is PathIcon heartIcon)
            heartIcon.Foreground = new SolidColorBrush(Color.Parse("#E74856"));

        Metadata.Command = openMetadataCommand;
        Metadata.CommandParameter = track;

        SearchLyrics.Command = searchLyricsCommand;
        SearchLyrics.CommandParameter = track;

        ShowFolder.Command = showInExplorerCommand;
        ShowFolder.CommandParameter = track;

        Remove.Command = removeCommand;
        Remove.CommandParameter = track;

        // Populate playlist submenu
        Populator.Populate(playlists, addToExistingPlaylistCommand,
            playlist => new object[] { track, playlist });
    }

    /// <summary>
    /// Resets cached state so a fresh menu is built on next access.
    /// Call when DataContext changes.
    /// </summary>
    public void Reset()
    {
    }

    // ── Shared helpers ──

    public static Avalonia.Controls.Border CreatePngIcon(string assetUri)
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
}
