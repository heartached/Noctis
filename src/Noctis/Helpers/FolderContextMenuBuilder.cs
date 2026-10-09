using System.Windows.Input;
using Avalonia.Controls;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// Builds and binds a reusable context menu for folder nodes in the Folders
/// view tree, mirroring <see cref="AlbumContextMenuBuilder"/> for FolderNode.
/// Stores named references to menu items to avoid fragile index-based access.
/// v2 layout (10-09 redesign): Play / Shuffle / Play Next / Add to Queue as quick tiles,
/// then [Add to Playlist] · [Show Folder, Hide from Library].
/// </summary>
public sealed class FolderContextMenuBuilder
{
    // ── Named menu item references ──
    // Play / Shuffle / Play Next / Add to Queue stay off-menu as the source the tiles copy
    // their command and parameter from (same scheme as the track and album builders).
    public MenuItem Play { get; private set; } = null!;
    public MenuItem Shuffle { get; private set; } = null!;
    public MenuItem PlayNext { get; private set; } = null!;
    public MenuItem AddToQueue { get; private set; } = null!;
    public MenuItem AddToPlaylist { get; private set; } = null!;
    public MenuItem ShowFolder { get; private set; } = null!;
    /// <summary>"Hide from Library" / "Show in Library", depending on the bound node.</summary>
    public MenuItem ToggleHidden { get; private set; } = null!;

    public Button QuickPlay { get; private set; } = null!;
    public Button QuickShuffle { get; private set; } = null!;
    public Button QuickPlayNext { get; private set; } = null!;
    public Button QuickAddToQueue { get; private set; } = null!;

    public ContextMenu Menu { get; private set; } = null!;

    private Control _host = null!;

    /// <summary>
    /// Builds the context menu. Call once per view lifetime (and again after a language
    /// switch: the labels are read here).
    /// </summary>
    /// <param name="resourceHost">Control the line icons resolve from; the application otherwise.</param>
    public ContextMenu Build(Control? resourceHost = null)
    {
        var host = _host = resourceHost ?? new Border();
        Menu = new ContextMenu();
        Menu.Classes.Add(MenuV2.MenuClass);
        var items = Menu.Items;

        Play = new MenuItem();
        Shuffle = new MenuItem();
        PlayNext = new MenuItem();
        AddToQueue = new MenuItem();

        QuickPlay = MenuV2.Tile(host, Menu, "MenuLinePlay", Loc.T("LibraryAlbums.Play"));
        QuickShuffle = MenuV2.Tile(host, Menu, "MenuLineShuffle", Loc.T("LibraryAlbums.Shuffle"));
        QuickPlayNext = MenuV2.Tile(host, Menu, "MenuLinePlayNext", Loc.T("LibraryAlbums.PlayNext"));
        QuickAddToQueue = MenuV2.Tile(host, Menu, "MenuLineQueue", Loc.T("LibraryAlbums.AddQueue"));
        items.Add(MenuV2.TileRow(QuickPlay, QuickShuffle, QuickPlayNext, QuickAddToQueue));

        items.Add(new Separator());
        AddToPlaylist = MenuV2.Row(host, Loc.T("LibraryAlbums.AddPlaylist"), "MenuLinePlaylistAdd");
        items.Add(AddToPlaylist);

        items.Add(new Separator());
        ShowFolder = MenuV2.Row(host, Loc.T("LibraryAlbums.ShowFolder"), "MenuLineFolder");
        items.Add(ShowFolder);
        ToggleHidden = MenuV2.Row(host, Loc.T("LibraryFolders.HideFromLibrary"), "MenuLineEyeOff");
        items.Add(ToggleHidden);

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
        ICommand showFolderCommand,
        ICommand toggleHiddenCommand)
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

        // A folder hidden through a parent can only be shown again from that parent.
        ToggleHidden.Header = Loc.T(node.IsInHiddenFolder ? "LibraryFolders.ShowInLibrary" : "LibraryFolders.HideFromLibrary");
        ToggleHidden.Icon = MenuV2.LineIcon(_host, node.IsInHiddenFolder ? "MenuLineEye" : "MenuLineEyeOff");
        ToggleHidden.IsEnabled = !node.IsInHiddenFolder || node.IsHiddenFromLibrary;
        ToggleHidden.Command = toggleHiddenCommand;
        ToggleHidden.CommandParameter = node;

        MenuV2.Sync(QuickPlay, Play);
        MenuV2.Sync(QuickShuffle, Shuffle);
        MenuV2.Sync(QuickPlayNext, PlayNext);
        MenuV2.Sync(QuickAddToQueue, AddToQueue);
    }
}
