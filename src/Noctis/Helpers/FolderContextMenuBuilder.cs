using System.Windows.Input;
using Avalonia.Controls;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// Builds and binds a reusable context menu for folder nodes in the Folders
/// view tree, mirroring <see cref="AlbumContextMenuBuilder"/> for FolderNode.
/// Stores named references to menu items to avoid fragile index-based access.
/// </summary>
public sealed class FolderContextMenuBuilder
{
    // ── Named menu item references ──
    public MenuItem Play { get; private set; } = null!;
    public MenuItem Shuffle { get; private set; } = null!;
    public MenuItem PlayNext { get; private set; } = null!;
    public MenuItem AddToQueue { get; private set; } = null!;
    public MenuItem AddToPlaylist { get; private set; } = null!;
    public MenuItem ShowFolder { get; private set; } = null!;

    public ContextMenu Menu { get; private set; } = null!;

    /// <summary>
    /// Builds the context menu. Call once per view lifetime.
    /// </summary>
    public ContextMenu Build()
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
        AddToQueue.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis/Assets/Icons/Queue%20ICON.png", 17);
        items.Add(AddToQueue);

        items.Add(new Separator());

        AddToPlaylist = new MenuItem { Header = "Add to Playlist" };
        AddToPlaylist.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis/Assets/Icons/Playlist%20icon.png");
        items.Add(AddToPlaylist);

        items.Add(new Separator());

        ShowFolder = new MenuItem { Header = "Show Folder" };
        ShowFolder.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis/Assets/Icons/Folder%20ICON.png");
        items.Add(ShowFolder);

        return Menu;
    }

    /// <summary>
    /// Binds folder data and commands to the menu. Call before showing.
    /// </summary>
    public void Bind(
        FolderNode node,
        ICommand playCommand,
        ICommand shuffleCommand,
        ICommand playNextCommand,
        ICommand addToQueueCommand,
        ICommand addToPlaylistCommand,
        ICommand showFolderCommand)
    {
        Menu.DataContext = node;

        Play.Command = playCommand;
        Play.CommandParameter = node;

        Shuffle.Command = shuffleCommand;
        Shuffle.CommandParameter = node;

        PlayNext.Command = playNextCommand;
        PlayNext.CommandParameter = node;

        AddToQueue.Command = addToQueueCommand;
        AddToQueue.CommandParameter = node;

        AddToPlaylist.Command = addToPlaylistCommand;
        AddToPlaylist.CommandParameter = node;

        ShowFolder.Command = showFolderCommand;
        ShowFolder.CommandParameter = node;
    }
}
