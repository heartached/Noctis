using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Converters;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Helpers;

/// <summary>Parameter of the Rate ▸ menu: which row was clicked and the stars chosen (0 = clear).</summary>
public sealed record RateRequest(Track Track, int Stars);

/// <summary>
/// Builds and binds a reusable track context menu shared across views.
/// Stores named references to menu items to avoid fragile index-based access.
/// </summary>
public sealed class TrackContextMenuBuilder
{
    private static IBrush ResolveAccentBrush()
    {
        if (Application.Current?.Resources.TryGetResource("AccentColorBrush", null, out var brush) == true && brush is IBrush b)
            return b;
        return new SolidColorBrush(Color.Parse("#E74856"));
    }

    // ── Named menu item references ──
    public MenuItem Play { get; private set; } = null!;
    public MenuItem Shuffle { get; private set; } = null!;
    public MenuItem PlayNext { get; private set; } = null!;
    public MenuItem AddToQueue { get; private set; } = null!;
    public MenuItem StartRadio { get; private set; } = null!;
    public MenuItem SnoozeForMonth { get; private set; } = null!;
    public MenuItem AddToPlaylist { get; private set; } = null!;
    public MenuItem Favorite { get; private set; } = null!;
    public MenuItem Unfavorite { get; private set; } = null!;
    public MenuItem Metadata { get; private set; } = null!;
    public MenuItem Convert { get; private set; } = null!;
    public MenuItem ScanReplayGain { get; private set; } = null!;
    public MenuItem Spectrogram { get; private set; } = null!;
    public MenuItem SearchLyrics { get; private set; } = null!;
    /// <summary>"Lyrics ▸" submenu: Search Lyrics plus the bulk actions (hidden until a view wires them).</summary>
    public MenuItem Lyrics { get; private set; } = null!;
    public MenuItem FetchLyrics { get; private set; } = null!;
    public MenuItem LyricsStudio { get; private set; } = null!;
    public MenuItem RemoveLyrics { get; private set; } = null!;
    /// <summary>"Rate ▸" submenu: ★ … ★★★★★ and Clear rating. Hidden unless the view passes a rateCommand.</summary>
    public MenuItem Rate { get; private set; } = null!;
    private readonly MenuItem[] _rateItems = new MenuItem[6];
    public MenuItem SendToFolder { get; private set; } = null!;
    public MenuItem ShowFolder { get; private set; } = null!;
    public MenuItem OpenWith { get; private set; } = null!;
    public MenuItem Remove { get; private set; } = null!;

    public ContextMenu Menu { get; private set; } = null!;

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
        AddToQueue.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Queue%20ICON.png", 17);
        items.Add(AddToQueue);

        // Hidden unless the view supplies a startRadioCommand in Bind().
        StartRadio = new MenuItem { Header = "Start Radio", IsVisible = false };
        StartRadio.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Shuffle%20ICON.png");
        items.Add(StartRadio);

        // Hidden unless the view supplies a snoozeCommand in Bind().
        SnoozeForMonth = new MenuItem { Header = "Snooze for a month", IsVisible = false };
        // placeholder icon: no dedicated snooze glyph in resources
        SnoozeForMonth.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Shuffle%20ICON.png");
        items.Add(SnoozeForMonth);

        items.Add(new Separator());

        AddToPlaylist = new MenuItem { Header = "Add to Playlist" };
        AddToPlaylist.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Playlist%20icon.png");
        items.Add(AddToPlaylist);

        items.Add(new Separator());

        Favorite = new MenuItem { Header = "Favorites" };
        Favorite.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Favorites%20icon.png");
        items.Add(Favorite);

        Unfavorite = new MenuItem { Header = "Remove from Favorites" };
        Unfavorite.Icon = new PathIcon
        {
            Width = 14, Height = 14,
            Data = (Geometry)resourceHost.FindResource("HeartFillIcon")!,
            Foreground = new SolidColorBrush(Color.Parse("#E74856"))
        };
        items.Add(Unfavorite);

        // Rate: ★ … ★★★★★ + Clear. Bulk-aware through the view's command (the whole
        // Ctrl-selection is rated when the clicked row is part of it).
        Rate = new MenuItem { Header = "Rate", IsVisible = false };
        Rate.Icon = new PathIcon { Width = 14, Height = 14, Data = (Geometry)resourceHost.FindResource("StarIcon")! };
        for (var stars = 1; stars <= 5; stars++)
        {
            var item = new MenuItem { Header = new string('★', stars) + new string('☆', 5 - stars) };
            _rateItems[stars] = item;
            Rate.Items.Add(item);
        }
        Rate.Items.Add(new Separator());
        _rateItems[0] = new MenuItem { Header = "Clear rating" };
        Rate.Items.Add(_rateItems[0]);
        items.Add(Rate);

        Metadata = new MenuItem { Header = "Metadata" };
        Metadata.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Metadata%20ICON.png");
        items.Add(Metadata);

        Convert = new MenuItem { Header = "Convert File", IsVisible = false };
        Convert.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Metadata%20ICON.png");
        items.Add(Convert);

        ScanReplayGain = new MenuItem { Header = "Scan ReplayGain", IsVisible = false };
        ScanReplayGain.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Metadata%20ICON.png");
        items.Add(ScanReplayGain);

        // Spek-style spectrum analysis of the file. Self-contained (shared static command),
        // so every view that uses this builder gets it without wiring a command.
        Spectrogram = new MenuItem { Header = "Spectrogram", Command = SpectrogramLauncher.OpenCommand };
        Spectrogram.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Metadata%20ICON.png");
        items.Add(Spectrogram);

        // Lyrics ▸ — Search Lyrics stays where it always was, now with the bulk actions
        // beneath it. The bulk entries stay hidden on views that don't wire them, so the
        // submenu reads as "Search Lyrics" plus nothing extra there.
        Lyrics = new MenuItem { Header = "Lyrics" };
        Lyrics.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Lyrics%20ICON.png");
        SearchLyrics = new MenuItem { Header = "Search Lyrics" };
        Lyrics.Items.Add(SearchLyrics);
        FetchLyrics = new MenuItem { Header = "Fetch & Save Lyrics", IsVisible = false };
        Lyrics.Items.Add(FetchLyrics);
        LyricsStudio = new MenuItem { Header = "Open in Lyrics Studio…", IsVisible = false };
        Lyrics.Items.Add(LyricsStudio);
        RemoveLyrics = new MenuItem { Header = "Remove Lyrics", IsVisible = false };
        RemoveLyrics.Classes.Add("danger");
        Lyrics.Items.Add(RemoveLyrics);
        items.Add(Lyrics);

        // Send to Folder (MusicBee's Send To → Folder): copies the selection to a drive/folder.
        SendToFolder = new MenuItem { Header = "Send to Folder…", IsVisible = false };
        SendToFolder.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Folder%20ICON.png");
        items.Add(SendToFolder);

        ShowFolder = new MenuItem { Header = "Show Folder" };
        ShowFolder.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Folder%20ICON.png");
        items.Add(ShowFolder);

        // "Open in <app>" / native Open-with picker. Header and visibility are
        // refreshed in Bind() from the configured external app.
        // placeholder icon: no dedicated open-with glyph in resources
        OpenWith = new MenuItem { Header = "Open File With" };
        OpenWith.Icon = CreatePngIcon("avares://Noctis/Assets/Icons/Metadata%20ICON.png");
        items.Add(OpenWith);

        items.Add(new Separator());

        Remove = new MenuItem { Header = removeHeader };
        var isDanger = removeHeader.StartsWith("Remove from", StringComparison.OrdinalIgnoreCase);
        if (isDanger)
            Remove.Classes.Add("danger");
        if (removeIconUri != null)
        {
            Remove.Icon = CreatePngIcon(removeIconUri, 14,
                isDanger ? new SolidColorBrush(Color.Parse("#E74856")) : null);
        }
        else
            Remove.Icon = new PathIcon { Width = 14, Height = 14, Data = (Geometry)resourceHost.FindResource("TrashIcon")! };
        items.Add(Remove);

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
        ICommand addToPlaylistCommand,
        ICommand toggleFavoriteCommand,
        ICommand openMetadataCommand,
        ICommand searchLyricsCommand,
        ICommand showInExplorerCommand,
        ICommand removeCommand,
        ObservableCollection<Playlist>? playlists = null,
        ICommand? addToExistingPlaylistCommand = null,
        ICommand? convertCommand = null,
        ICommand? scanReplayGainCommand = null,
        ICommand? startRadioCommand = null,
        ICommand? snoozeCommand = null,
        ICommand? rateCommand = null,
        ICommand? fetchLyricsCommand = null,
        ICommand? lyricsStudioCommand = null,
        ICommand? removeLyricsCommand = null,
        ICommand? sendToFolderCommand = null)
    {
        Menu.DataContext = track;

        // Rate ▸ (optional). Parameter carries the track so the same command serves every row.
        Rate.IsVisible = rateCommand != null;
        if (rateCommand != null)
        {
            for (var stars = 0; stars <= 5; stars++)
            {
                var item = _rateItems[stars];
                item.Command = rateCommand;
                item.CommandParameter = new RateRequest(track, stars);
                item.FontWeight = stars != 0 && stars == track.Rating ? FontWeight.Bold : FontWeight.Normal;
            }
        }

        // Lyrics ▸ bulk entries (optional).
        BindOptional(FetchLyrics, fetchLyricsCommand, track);
        BindOptional(LyricsStudio, lyricsStudioCommand, track);
        BindOptional(RemoveLyrics, removeLyricsCommand, track);
        BindOptional(SendToFolder, sendToFolderCommand, track);

        // Play
        Play.Header = "Play";
        Play.Command = playCommand;
        Play.CommandParameter = track;

        Shuffle.Command = shuffleCommand;

        PlayNext.Command = playNextCommand;
        PlayNext.CommandParameter = track;

        AddToQueue.Command = addToQueueCommand;
        AddToQueue.CommandParameter = track;

        // Start Radio is optional — only views that pass a startRadioCommand surface it.
        if (startRadioCommand != null)
        {
            StartRadio.Command = startRadioCommand;
            StartRadio.CommandParameter = track;
            StartRadio.IsVisible = true;
        }
        else
        {
            StartRadio.IsVisible = false;
        }

        // Snooze for a month is optional — only views that pass a snoozeCommand surface it.
        if (snoozeCommand != null)
        {
            SnoozeForMonth.Command = snoozeCommand;
            SnoozeForMonth.CommandParameter = track;
            SnoozeForMonth.IsVisible = true;
        }
        else
        {
            SnoozeForMonth.IsVisible = false;
        }

        // Add to Playlist: opens unified dialog
        AddToPlaylist.Command = addToPlaylistCommand;
        AddToPlaylist.CommandParameter = track;

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

        // Convert is optional — only views that pass a convertCommand surface it.
        if (convertCommand != null)
        {
            Convert.Command = convertCommand;
            Convert.CommandParameter = track;
            Convert.IsVisible = true;
        }
        else
        {
            Convert.IsVisible = false;
        }

        if (scanReplayGainCommand != null)
        {
            ScanReplayGain.Command = scanReplayGainCommand;
            ScanReplayGain.CommandParameter = track;
            ScanReplayGain.IsVisible = true;
        }
        else
        {
            ScanReplayGain.IsVisible = false;
        }

        Spectrogram.CommandParameter = track;

        SearchLyrics.Command = searchLyricsCommand;
        SearchLyrics.CommandParameter = track;

        ShowFolder.Command = showInExplorerCommand;
        ShowFolder.CommandParameter = track;

        // Self-contained: the action is a pure function of track + settings, so no view
        // supplies a command. Re-read per open so Settings changes apply immediately.
        OpenWith.Header = ExternalOpenApp.MenuHeader;
        OpenWith.IsVisible = ExternalOpenApp.IsAvailable;
        OpenWith.Command ??= new RelayCommand<Track>(ExternalOpenApp.Open);
        OpenWith.CommandParameter = track;

        Remove.Command = removeCommand;
        Remove.CommandParameter = track;
    }

    private static void BindOptional(MenuItem item, ICommand? command, Track track)
    {
        item.IsVisible = command != null;
        item.Command = command;
        item.CommandParameter = track;
    }

    /// <summary>
    /// Resets cached state so a fresh menu is built on next access.
    /// Call when DataContext changes.
    /// </summary>
    public void Reset()
    {
    }

    // ── Shared helpers ──

    public static Avalonia.Controls.Border CreatePngIcon(string assetUri, double size = 14, IBrush? color = null)
    {
        var border = new Avalonia.Controls.Border { Width = size, Height = size };
        // A fixed color must win over the themed-foreground resource binding, which
        // otherwise fires on attach (when the menu opens) and overrides a directly
        // assigned Background. So only bind to the resource when no color is given.
        if (color != null)
            border.Background = color;
        else
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
