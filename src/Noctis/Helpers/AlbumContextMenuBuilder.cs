using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media;
using Noctis.Localization;
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
    public MenuItem LyricsBackground { get; private set; } = null!;
    public MenuItem LyricsBackgroundChoose { get; private set; } = null!;
    public MenuItem LyricsBackgroundClear { get; private set; } = null!;
    public MenuItem DontScrobble { get; private set; } = null!;
    public MenuItem Shuffle { get; private set; } = null!;
    public MenuItem PlayNext { get; private set; } = null!;
    public MenuItem AddToQueue { get; private set; } = null!;
    public MenuItem AddToPlaylist { get; private set; } = null!;
    public MenuItem Favorite { get; private set; } = null!;
    public MenuItem Unfavorite { get; private set; } = null!;
    /// <summary>Snooze every track of the album for a month (optional; hidden unless Bind gets a command).</summary>
    public MenuItem SnoozeForMonth { get; private set; } = null!;
    public MenuItem Metadata { get; private set; } = null!;
    /// <summary>GitHub #178: the album (or the Ctrl-selection) → one album. Bound by <see cref="BindGroupAlbums"/>.</summary>
    public MenuItem GroupAlbums { get; private set; } = null!;
    /// <summary>Undo Grouping: puts back what Group into One Album / Move to Album changed. Bound by <see cref="BindUndoGroupAlbums"/>.</summary>
    public MenuItem UndoGroupAlbums { get; private set; } = null!;
    public MenuItem EditDescription { get; private set; } = null!;
    public MenuItem Convert { get; private set; } = null!;
    public MenuItem ScanReplayGain { get; private set; } = null!;
    /// <summary>GitHub #195: every song of the album (or the Ctrl-selection) → the bulk Find Lyrics dialog. Bound by <see cref="BindFindLyrics"/>.</summary>
    public MenuItem FindLyrics { get; private set; } = null!;
    /// <summary>GitHub #267: the album (or the Ctrl-selection) → Get Genres from Discogs. Bound by <see cref="BindDiscogsGenres"/>.</summary>
    public MenuItem DiscogsGenres { get; private set; } = null!;
    public MenuItem ShowFolder { get; private set; } = null!;
    /// <summary>GitHub #121: the album's songs → Send to Folder (copy or move). Bound by <see cref="BindSendToFolder"/>.</summary>
    public MenuItem SendToFolder { get; private set; } = null!;
    public MenuItem Remove { get; private set; } = null!;
    /// <summary>GitHub #233: "Carousel Style ▸" Default / Cover / CD Jewel Case / Vinyl / Cassette
    /// for this album. Bound by <see cref="BindCarouselStyle"/>.</summary>
    public MenuItem CarouselStyle { get; private set; } = null!;
    /// <summary>The Carousel Style ▸ rows: [0] Default, then one per <see cref="AlbumCarouselStyle"/>.</summary>
    public MenuItem[] CarouselStyleRows { get; private set; } = Array.Empty<MenuItem>();
    private Control? _host;

    public ContextMenu Menu { get; private set; } = null!;

    // ── v2 layout (10-09 redesign, opt-in via Build(..., v2: true)) ──

    /// <summary>True when this menu was built with the v2 layout.</summary>
    public bool IsV2 { get; private set; }
    /// <summary>v2: the Play / Shuffle / Play Next / Add to Queue tiles that replace those four rows.</summary>
    public Button QuickPlay { get; private set; } = null!;
    public Button QuickShuffle { get; private set; } = null!;
    public Button QuickPlayNext { get; private set; } = null!;
    public Button QuickAddToQueue { get; private set; } = null!;
    /// <summary>v2: "Tools ▸" holding Convert Album, Scan ReplayGain and Don't Scrobble.</summary>
    public MenuItem Tools { get; private set; } = null!;

    /// <summary>
    /// Builds the context menu. Call once per view lifetime.
    /// </summary>
    /// <param name="removeHeader">Label for the last item (e.g. "Remove from Library").</param>
    /// <param name="resourceHost">Control used to resolve resources (e.g. icons).</param>
    /// <param name="v2">Build the redesigned (10-09) layout: header, quick tiles, grouped rows, Tools ▸.</param>
    /// <param name="removeIsDanger">Red Remove row; null = infer from an English "Remove from…" header.</param>
    public ContextMenu Build(string removeHeader, Control resourceHost, bool v2 = false, bool? removeIsDanger = null)
    {
        _host = resourceHost;
        if (v2)
            return BuildV2(removeHeader, resourceHost,
                removeIsDanger ?? removeHeader.StartsWith("Remove from", StringComparison.OrdinalIgnoreCase));

        Menu = new ContextMenu();
        var items = Menu.Items;

        Play = new MenuItem { Header = "Play", MaxWidth = 400 };
        Play.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis.UI/Assets/Icons/Play%20ICON.png");
        items.Add(Play);

        Shuffle = new MenuItem { Header = "Shuffle" };
        Shuffle.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis.UI/Assets/Icons/Shuffle%20ICON.png");
        items.Add(Shuffle);

        PlayNext = new MenuItem { Header = "Play Next" };
        PlayNext.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis.UI/Assets/Icons/Forward%20ICON.png");
        items.Add(PlayNext);

        AddToQueue = new MenuItem { Header = "Add to Queue" };
        AddToQueue.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis.UI/Assets/Icons/Queue%20ICON.png", 17);
        items.Add(AddToQueue);

        items.Add(new Separator());

        AddToPlaylist = new MenuItem { Header = "Add to Playlist" };
        AddToPlaylist.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis.UI/Assets/Icons/Playlist%20icon.png");
        items.Add(AddToPlaylist);

        items.Add(new Separator());

        Favorite = new MenuItem { Header = "Favorites" };
        Favorite.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis.UI/Assets/Icons/Favorites%20icon.png");
        items.Add(Favorite);

        Unfavorite = new MenuItem { Header = "Remove from Favorites" };
        Unfavorite.Icon = new PathIcon
        {
            Width = 14, Height = 14,
            Data = (Geometry)resourceHost.FindResource("HeartFillIcon")!,
            Foreground = new SolidColorBrush(Color.Parse("#E74856"))
        };
        items.Add(Unfavorite);

        SnoozeForMonth = MenuV2.Row(resourceHost, Loc.T("Menu.Snooze"), "MenuLineSnooze", visible: false);
        items.Add(SnoozeForMonth);

        Metadata = new MenuItem { Header = "Metadata" };
        Metadata.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis.UI/Assets/Icons/Metadata%20ICON.png");
        items.Add(Metadata);

        GroupAlbums = new MenuItem { Header = Loc.T("GroupAlbums.Menu"), IsVisible = false };
        GroupAlbums.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis.UI/Assets/Icons/Metadata%20ICON.png");
        items.Add(GroupAlbums);

        UndoGroupAlbums = MenuV2.Row(resourceHost, Loc.T("GroupAlbums.Undo"), "MenuLineUngroup", visible: false);
        items.Add(UndoGroupAlbums);

        EditDescription = new MenuItem { Header = "Update Description", IsVisible = false };
        EditDescription.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis.UI/Assets/Icons/Metadata%20ICON.png");
        items.Add(EditDescription);

        Convert = new MenuItem { Header = "Convert Album", IsVisible = false };
        Convert.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis.UI/Assets/Icons/Metadata%20ICON.png");
        items.Add(Convert);

        ScanReplayGain = new MenuItem { Header = "Scan ReplayGain", IsVisible = false };
        ScanReplayGain.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis.UI/Assets/Icons/Metadata%20ICON.png");
        items.Add(ScanReplayGain);

        DiscogsGenres = MenuV2.Row(resourceHost, Loc.T("DiscogsGenres.Menu"), "MenuLineBadge", visible: false);
        items.Add(DiscogsGenres);

        // No "Search Lyrics" here (GitHub #208): it searched the album's first song only and opened
        // the lyrics page over whatever was playing. Find Lyrics… covers every song of the album.
        FindLyrics = new MenuItem { Header = Loc.T("LibraryAlbums.FindLyrics"), IsVisible = false };
        FindLyrics.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis.UI/Assets/Icons/Lyrics%20ICON.png");
        items.Add(FindLyrics);

        // Lyrics Background Video ▸ — one clip for every song on this album (a song's own
        // clip still wins). Static commands; see Helpers.LyricsBackgroundOverrides.
        LyricsBackground = new MenuItem { Header = "Lyrics Background Video" };
        LyricsBackground.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis.UI/Assets/Icons/Lyrics%20ICON.png");
        LyricsBackgroundChoose = new MenuItem { Header = "Choose video for this album", Command = LyricsBackgroundOverrides.ChooseForAlbumCommand };
        LyricsBackground.Items.Add(LyricsBackgroundChoose);
        LyricsBackgroundClear = new MenuItem { Header = "Use default video", Command = LyricsBackgroundOverrides.ClearForAlbumCommand };
        LyricsBackground.Items.Add(LyricsBackgroundClear);
        items.Add(LyricsBackground);
        items.Add(BuildCarouselStyle(resourceHost));

        // Don't Scrobble This Album (static command; header and visibility set per Bind).
        DontScrobble = new MenuItem { IsVisible = false };
        items.Add(DontScrobble);

        ShowFolder = new MenuItem { Header = "Show Folder" };
        ShowFolder.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis.UI/Assets/Icons/Folder%20ICON.png");
        items.Add(ShowFolder);

        SendToFolder = new MenuItem { Header = Loc.T("SendTo.Title"), IsVisible = false };
        SendToFolder.Icon = TrackContextMenuBuilder.CreatePngIcon("avares://Noctis.UI/Assets/Icons/Folder%20ICON.png");
        items.Add(SendToFolder);

        items.Add(new Separator());

        Remove = new MenuItem { Header = removeHeader };
        if (removeHeader.StartsWith("Remove from", StringComparison.OrdinalIgnoreCase))
            Remove.Classes.Add("danger");
        Remove.Icon = new PathIcon { Width = 14, Height = 14, Data = (Geometry)resourceHost.FindResource("TrashIcon")! };
        items.Add(Remove);

        return Menu;
    }

    /// <summary>
    /// The v2 layout: [header] [tiles] · [playlist, favorite] · [metadata, description,
    /// find lyrics, lyrics video ▸] · [folder, tools ▸] · [remove]. Same named items as the
    /// classic menu, so <see cref="Bind"/> serves both; Play / Shuffle / Play Next / Add to
    /// Queue stay off-menu as the source the tiles copy their command from.
    /// </summary>
    private ContextMenu BuildV2(string removeHeader, Control host, bool removeIsDanger)
    {
        IsV2 = true;
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
        QuickAddToQueue = MenuV2.Tile(host, Menu, "MenuLineAddToQueue", Loc.T("LibraryAlbums.AddQueue"));
        items.Add(MenuV2.TileRow(QuickPlay, QuickShuffle, QuickPlayNext, QuickAddToQueue));

        items.Add(new Separator());
        AddToPlaylist = MenuV2.Row(host, Loc.T("LibraryAlbums.AddPlaylist"), "MenuLinePlaylistAdd");
        items.Add(AddToPlaylist);
        Favorite = MenuV2.Row(host, Loc.T("LibraryAlbums.Favorites"), "MenuLineHeart");
        items.Add(Favorite);
        Unfavorite = MenuV2.Row(host, Loc.T("LibraryAlbums.RemoveFromFavorites"), "MenuLineHeart");
        MenuV2.IconPath(Unfavorite.Icon)?.Classes.Add("mv2-fav");
        items.Add(Unfavorite);
        // Same row and icon as the track menu's Snooze (TrackContextMenuBuilder.BuildV2).
        SnoozeForMonth = MenuV2.Row(host, Loc.T("Menu.Snooze"), "MenuLineSnooze", visible: false);
        items.Add(SnoozeForMonth);

        items.Add(new Separator());
        Metadata = MenuV2.Row(host, Loc.T("LibraryAlbums.Metadata"), "MenuLineEdit");
        items.Add(Metadata);
        GroupAlbums = MenuV2.Row(host, Loc.T("GroupAlbums.Menu"), "MenuLineAlbum", visible: false);
        items.Add(GroupAlbums);
        UndoGroupAlbums = MenuV2.Row(host, Loc.T("GroupAlbums.Undo"), "MenuLineUngroup", visible: false);
        items.Add(UndoGroupAlbums);
        EditDescription = MenuV2.Row(host, Loc.T("AlbumDetail.EditDescription"), null, visible: false);
        items.Add(EditDescription);
        FindLyrics = MenuV2.Row(host, Loc.T("LibraryAlbums.FindLyrics"), "MenuLineLyricsBulk", visible: false);
        items.Add(FindLyrics);
        LyricsBackground = MenuV2.Row(host, Loc.T("AlbumDetail.LyricsBackgroundVideo"), "MenuLineVideo");
        LyricsBackgroundChoose = new MenuItem { Header = Loc.T("AlbumDetail.ChooseAlbumVideo"), Command = LyricsBackgroundOverrides.ChooseForAlbumCommand };
        LyricsBackground.Items.Add(LyricsBackgroundChoose);
        LyricsBackgroundClear = new MenuItem { Header = Loc.T("AlbumDetail.UseDefaultVideo"), Command = LyricsBackgroundOverrides.ClearForAlbumCommand };
        LyricsBackground.Items.Add(LyricsBackgroundClear);
        items.Add(LyricsBackground);
        items.Add(BuildCarouselStyle(host));

        items.Add(new Separator());
        ShowFolder = MenuV2.Row(host, Loc.T("LibraryAlbums.ShowFolder"), "MenuLineFolder");
        items.Add(ShowFolder);
        Tools = MenuV2.Row(host, Loc.T("Favorites.Tools"), "MenuLineTools");
        Tools.Classes.Add(MenuV2.AutoHideClass);
        // GitHub #121: in Tools ▸ like the song menu's Send to Folder.
        SendToFolder = MenuV2.Row(host, Loc.T("SendTo.Title"), "MenuLineSendToFolder", visible: false);
        Tools.Items.Add(SendToFolder);
        Convert = MenuV2.Row(host, Loc.T("LibraryAlbums.ConvertAlbum"), "MenuLineConvert", visible: false);
        Tools.Items.Add(Convert);
        ScanReplayGain = MenuV2.Row(host, Loc.T("LibraryAlbums.ScanReplayGain"), "MenuLineReplayGain", visible: false);
        Tools.Items.Add(ScanReplayGain);
        // GitHub #267
        DiscogsGenres = MenuV2.Row(host, Loc.T("DiscogsGenres.Menu"), "MenuLineBadge", visible: false);
        Tools.Items.Add(DiscogsGenres);
        DontScrobble = MenuV2.Row(host, string.Empty, "MenuLineScrobbleOff", visible: false);
        Tools.Items.Add(DontScrobble);
        items.Add(Tools);

        items.Add(new Separator());
        Remove = MenuV2.Row(host, removeHeader, "MenuLineTrash");
        if (removeIsDanger)
            Remove.Classes.Add("danger");
        items.Add(Remove);

        Menu.Opening += (_, _) => MenuV2.RefreshLayout(Menu.Items);
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
        ICommand? snoozeCommand = null)
    {
        Menu.DataContext = album;

        LyricsBackgroundChoose.CommandParameter = album;
        LyricsBackgroundClear.CommandParameter = album;
        LyricsBackgroundClear.IsVisible = LyricsBackgroundOverrides.HasOverride(LyricsBackgroundOverrides.KeyForAlbum(album));

        ScrobbleMenu.BindAlbum(DontScrobble, album);

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
        BindOptional(SnoozeForMonth, snoozeCommand, album);

        ShowFolder.Command = showInExplorerCommand;
        ShowFolder.CommandParameter = album;

        Remove.Command = removeCommand;
        Remove.CommandParameter = album;

        if (IsV2)
        {
            MenuV2.Sync(QuickPlay, Play);
            MenuV2.Sync(QuickShuffle, Shuffle);
            MenuV2.Sync(QuickPlayNext, PlayNext);
            MenuV2.Sync(QuickAddToQueue, AddToQueue);
            MenuV2.RefreshLayout(Menu.Items);
        }
    }

    /// <summary>
    /// GitHub #121 (2026-10-10): Send to Folder for the album (hidden when null). Separate from
    /// <see cref="Bind"/> so views opt in one line each; call it after Bind.
    /// </summary>
    public void BindSendToFolder(Album album, ICommand? sendToFolderCommand)
    {
        BindOptional(SendToFolder, sendToFolderCommand, album);
        if (IsV2) MenuV2.RefreshLayout(Menu.Items);
    }

    /// <summary>
    /// GitHub #178: "Group into One Album" for the album or the Ctrl-selection it is in (hidden
    /// when null). Separate from <see cref="Bind"/> like <see cref="BindSendToFolder"/>.
    /// </summary>
    public void BindGroupAlbums(Album album, ICommand? groupCommand)
    {
        BindOptional(GroupAlbums, groupCommand, album);
        if (IsV2) MenuV2.RefreshLayout(Menu.Items);
    }

    /// <summary>
    /// Undo Grouping, next to Group into One Album: shown only with a command, which the view
    /// passes only when the album holds songs a grouping changed that can be put back.
    /// </summary>
    public void BindUndoGroupAlbums(Album album, ICommand? undoCommand)
    {
        BindOptional(UndoGroupAlbums, undoCommand, album);
        if (IsV2) MenuV2.RefreshLayout(Menu.Items);
    }

    /// <summary>
    /// GitHub #195: "Find Lyrics…" for every song of the album or the Ctrl-selection it is in
    /// (hidden when null). Separate from <see cref="Bind"/> like <see cref="BindSendToFolder"/>.
    /// </summary>
    public void BindFindLyrics(Album album, ICommand? findLyricsCommand)
    {
        BindOptional(FindLyrics, findLyricsCommand, album);
        // GitHub #214: while a hidden Find Lyrics run is pending, this entry brings it back.
        FindLyrics.Header = ViewModels.MetadataHelper.PendingFindLyricsMenuHeader() ?? Loc.T("LibraryAlbums.FindLyrics");
        if (IsV2) MenuV2.RefreshLayout(Menu.Items);
    }

    /// <summary>
    /// GitHub #267: "Get Genres from Discogs" for the album or the Ctrl-selection it is in (hidden
    /// when null). Separate from <see cref="Bind"/> like <see cref="BindSendToFolder"/>.
    /// </summary>
    public void BindDiscogsGenres(Album album, ICommand? discogsGenresCommand)
    {
        BindOptional(DiscogsGenres, discogsGenresCommand, album);
        if (IsV2) MenuV2.RefreshLayout(Menu.Items);
    }

    private MenuItem BuildCarouselStyle(Control host)
    {
        CarouselStyle = MenuV2.Row(host, Loc.T("Menu.CarouselStyle"), "MenuLineAlbum", visible: false);
        var rows = new List<MenuItem> { new() { Header = Loc.T("Menu.CarouselStyleDefault"), Command = AlbumCarouselStyleOverrides.SetCommand } };
        foreach (var style in Enum.GetValues<AlbumCarouselStyle>())
            rows.Add(new MenuItem { Header = Loc.T(StyleKey(style)), Command = AlbumCarouselStyleOverrides.SetCommand });
        CarouselStyleRows = rows.ToArray();
        foreach (var row in CarouselStyleRows) CarouselStyle.Items.Add(row);
        return CarouselStyle;
    }

    private static string StyleKey(AlbumCarouselStyle style) => style switch
    {
        AlbumCarouselStyle.JewelCase => "Main.CarouselJewelCase",
        AlbumCarouselStyle.Vinyl => "Main.CarouselVinyl",
        AlbumCarouselStyle.Cassette => "Main.CarouselCassette",
        _ => "Main.CarouselCover",
    };

    /// <summary>
    /// GitHub #233: Carousel Style ▸ for the album, a check on <paramref name="current"/> (null =
    /// Default, the global style). Hidden when <paramref name="visible"/> is false (the Album
    /// Carousel built-in feature is off). Call after Bind.
    /// </summary>
    public void BindCarouselStyle(Album album, AlbumCarouselStyle? current, bool visible)
    {
        CarouselStyle.IsVisible = visible;
        for (var i = 0; i < CarouselStyleRows.Length; i++)
        {
            AlbumCarouselStyle? style = i == 0 ? null : (AlbumCarouselStyle)(i - 1);
            var row = CarouselStyleRows[i];
            row.CommandParameter = new AlbumCarouselStyleOverrides.Choice(album, style);
            var on = style == current;
            row.Icon = on && _host != null ? MenuV2.LineIcon(_host, "MenuLineCheck") : null;
            row.Tag = on; // read by tests
        }
        if (IsV2) MenuV2.RefreshLayout(Menu.Items);
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
