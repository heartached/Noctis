using System;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// Builds and binds a reusable album context menu shared across views,
/// mirroring <see cref="TrackContextMenuBuilder"/> for album tiles.
/// Stores named references to menu items to avoid fragile index-based access.
/// </summary>
public sealed class AlbumContextMenuBuilder
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
    public MenuItem EditDescription { get; private set; } = null!;
    public MenuItem Convert { get; private set; } = null!;
    public MenuItem ScanReplayGain { get; private set; } = null!;
    public MenuItem SearchLyrics { get; private set; } = null!;
    public MenuItem ShowFolder { get; private set; } = null!;
    public MenuItem Remove { get; private set; } = null!;

    public ContextMenu Menu { get; private set; } = null!;

    /// <summary>
    /// Builds the context menu. Call once per view lifetime.
    /// </summary>
    /// <param name="removeHeader">Label for the last item (e.g. "Remove from Library").</param>
    /// <param name="resourceHost">Control used to resolve resources (e.g. icons).</param>
    public ContextMenu Build(string removeHeader, Control resourceHost)
    {
        Menu = new ContextMenu();
        var items = Menu.Items;

        Play = new MenuItem { Header = "Play", MaxWidth = 400 };
        Play.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis/Assets/Icons/Play%20ICON.png");
        items.Add(Play);

        Shuffle = new MenuItem { Header = "Shuffle" };
        Shuffle.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis/Assets/Icons/Shuffle%20ICON.png");
        items.Add(Shuffle);

        PlayNext = new MenuItem { Header = "Play Next" };
        PlayNext.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis/Assets/Icons/Forward%20ICON.png");
        items.Add(PlayNext);

        AddToQueue = new MenuItem { Header = "Add to Queue" };
        AddToQueue.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis/Assets/Icons/Queue%20icon.png", 17);
        items.Add(AddToQueue);

        items.Add(new Separator());

        AddToPlaylist = new MenuItem { Header = "Add to Playlist" };
        AddToPlaylist.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis/Assets/Icons/Playlist%20icon.png");
        items.Add(AddToPlaylist);

        items.Add(new Separator());

        Favorite = new MenuItem { Header = "Favorites" };
        Favorite.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis/Assets/Icons/Favorites%20icon.png");
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
        Metadata.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis/Assets/Icons/Metadata%20icon.png");
        items.Add(Metadata);

        EditDescription = new MenuItem { Header = "Edit Description", IsVisible = false };
        EditDescription.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis/Assets/Icons/Metadata%20icon.png");
        items.Add(EditDescription);

        Convert = new MenuItem { Header = "Convert Album", IsVisible = false };
        Convert.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis/Assets/Icons/Metadata%20icon.png");
        items.Add(Convert);

        ScanReplayGain = new MenuItem { Header = "Scan ReplayGain", IsVisible = false };
        ScanReplayGain.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis/Assets/Icons/Metadata%20icon.png");
        items.Add(ScanReplayGain);

        SearchLyrics = new MenuItem { Header = "Search Lyrics", IsVisible = false };
        SearchLyrics.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis/Assets/Icons/Lyrics%20ICON.png");
        items.Add(SearchLyrics);

        ShowFolder = new MenuItem { Header = "Show Folder" };
        ShowFolder.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis/Assets/Icons/Folder%20icon.png");
        items.Add(ShowFolder);

        items.Add(new Separator());

        Remove = new MenuItem { Header = removeHeader };
        if (removeHeader.StartsWith("Remove from", StringComparison.OrdinalIgnoreCase))
            Remove.Classes.Add("danger");
        Remove.Icon = new PathIcon { Width = 14, Height = 14, Data = (Geometry)resourceHost.FindResource("TrashIcon")! };
        items.Add(Remove);

        return Menu;
    }

    /// <summary>
    /// Binds album data and commands to the menu. Call before showing.
    /// Optional commands hide their menu item when null.
    /// </summary>
    public void Bind(
        Album album,
        ICommand playCommand,
        ICommand shuffleCommand,
        ICommand playNextCommand,
        ICommand addToQueueCommand,
        ICommand addToPlaylistCommand,
        ICommand toggleFavoritesCommand,
        ICommand openMetadataCommand,
        ICommand showInExplorerCommand,
        ICommand removeCommand,
        ICommand? editDescriptionCommand = null,
        ICommand? convertCommand = null,
        ICommand? scanReplayGainCommand = null,
        ICommand? searchLyricsCommand = null)
    {
        Menu.DataContext = album;

        Play.Command = playCommand;
        Play.CommandParameter = album;

        Shuffle.Command = shuffleCommand;
        Shuffle.CommandParameter = album;

        PlayNext.Command = playNextCommand;
        PlayNext.CommandParameter = album;

        AddToQueue.Command = addToQueueCommand;
        AddToQueue.CommandParameter = album;

        AddToPlaylist.Command = addToPlaylistCommand;
        AddToPlaylist.CommandParameter = album;

        Favorite.Command = toggleFavoritesCommand;
        Favorite.CommandParameter = album;
        Favorite.IsVisible = !album.IsAllTracksFavorite;

        Unfavorite.Command = toggleFavoritesCommand;
        Unfavorite.CommandParameter = album;
        Unfavorite.IsVisible = album.IsAllTracksFavorite;

        Metadata.Command = openMetadataCommand;
        Metadata.CommandParameter = album;

        BindOptional(EditDescription, editDescriptionCommand, album);
        BindOptional(Convert, convertCommand, album);
        BindOptional(ScanReplayGain, scanReplayGainCommand, album);
        BindOptional(SearchLyrics, searchLyricsCommand, album);

        ShowFolder.Command = showInExplorerCommand;
        ShowFolder.CommandParameter = album;

        Remove.Command = removeCommand;
        Remove.CommandParameter = album;
    }

    private static void BindOptional(MenuItem item, ICommand? command, Album album)
    {
        if (command != null)
        {
            item.Command = command;
            item.CommandParameter = album;
            item.IsVisible = true;
        }
        else
        {
            item.IsVisible = false;
        }
    }
}
