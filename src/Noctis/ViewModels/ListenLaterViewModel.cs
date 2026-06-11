using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>One display row on the Listen Later page.</summary>
public sealed class ListenLaterRow
{
    public required ListenLaterItem Item { get; init; }
    public string Name => Item.Name;
    public string Subtitle { get; init; } = string.Empty;
    public string KindLabel { get; init; } = string.Empty;
    public string? ArtworkPath { get; init; }
    public string AddedLabel { get; init; } = string.Empty;
    /// <summary>True when the bookmarked track/album no longer resolves in the library.</summary>
    public bool IsMissing { get; init; }
}

/// <summary>
/// Listen Later page: bookmarks for tracks/albums/artists to check out later,
/// separate from playlists.
/// </summary>
public partial class ListenLaterViewModel : ViewModelBase
{
    private readonly IListenLaterService _service;
    private readonly ILibraryService _library;
    private readonly PlayerViewModel _player;

    public ObservableCollection<ListenLaterRow> Rows { get; } = new();

    [ObservableProperty] private bool _hasItems;

    /// <summary>Raised when an album bookmark should open the album detail page.</summary>
    public event EventHandler<Album>? AlbumOpened;

    /// <summary>Raised when an artist bookmark should open the artist page.</summary>
    public event EventHandler<string>? ArtistOpened;

    public ListenLaterViewModel(IListenLaterService service, ILibraryService library, PlayerViewModel player)
    {
        _service = service;
        _library = library;
        _player = player;
        _service.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    }

    public void Refresh()
    {
        Rows.Clear();
        foreach (var item in _service.Items)
        {
            string subtitle = item.Subtitle;
            string? artwork = null;
            bool missing = false;

            switch (item.Kind)
            {
                case ListenLaterKind.Track:
                    var track = _library.GetTrackById(item.TargetId);
                    artwork = track?.AlbumArtworkPath;
                    missing = track == null;
                    break;
                case ListenLaterKind.Album:
                    var album = _library.GetAlbumById(item.TargetId);
                    artwork = album?.ArtworkPath;
                    missing = album == null;
                    break;
                case ListenLaterKind.Artist:
                    subtitle = "Artist";
                    break;
            }

            Rows.Add(new ListenLaterRow
            {
                Item = item,
                Subtitle = subtitle,
                KindLabel = item.Kind switch
                {
                    ListenLaterKind.Track => "Song",
                    ListenLaterKind.Album => "Album",
                    _ => "Artist",
                },
                ArtworkPath = artwork,
                AddedLabel = FormatAdded(item.AddedAtUtc.ToLocalTime()),
                IsMissing = missing,
            });
        }
        HasItems = Rows.Count > 0;
    }

    private static string FormatAdded(DateTime local)
    {
        var days = (DateTime.Now.Date - local.Date).Days;
        return days switch
        {
            <= 0 => "Added today",
            1 => "Added yesterday",
            < 30 => $"Added {days} days ago",
            < 365 => $"Added {local:MMM d}",
            _ => $"Added {local:MMM d yyyy}",
        };
    }

    /// <summary>Plays the bookmark: the track itself, the whole album, or all artist tracks.</summary>
    [RelayCommand]
    private void PlayRow(ListenLaterRow? row)
    {
        if (row == null) return;
        switch (row.Item.Kind)
        {
            case ListenLaterKind.Track:
                if (_library.GetTrackById(row.Item.TargetId) is { } track)
                    _player.ReplaceQueueAndPlay(new List<Track> { track }, 0);
                break;
            case ListenLaterKind.Album:
                var albumTracks = _library.Tracks
                    .Where(t => t.AlbumId == row.Item.TargetId)
                    .OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
                    .ToList();
                if (albumTracks.Count > 0)
                    _player.ReplaceQueueAndPlay(albumTracks, 0);
                break;
            case ListenLaterKind.Artist:
                var artistTracks = _library.Tracks
                    .Where(t => string.Equals(t.PrimaryArtist, row.Item.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (artistTracks.Count > 0)
                    _player.ReplaceQueueAndPlay(artistTracks, 0);
                break;
        }
    }

    /// <summary>Opens the bookmark's detail page (album/artist) or plays it (track).</summary>
    [RelayCommand]
    private void OpenRow(ListenLaterRow? row)
    {
        if (row == null) return;
        switch (row.Item.Kind)
        {
            case ListenLaterKind.Track:
                PlayRow(row);
                break;
            case ListenLaterKind.Album:
                if (_library.GetAlbumById(row.Item.TargetId) is { } album)
                    AlbumOpened?.Invoke(this, album);
                break;
            case ListenLaterKind.Artist:
                ArtistOpened?.Invoke(this, row.Item.Name);
                break;
        }
    }

    [RelayCommand]
    private void RemoveRow(ListenLaterRow? row)
    {
        if (row == null) return;
        _service.Remove(row.Item.Id);
    }

    [RelayCommand]
    private async Task ClearAll()
    {
        if (Rows.Count == 0) return;
        var confirmed = await Views.ConfirmationDialog.ShowAsync(
            "Clear all Listen Later bookmarks? This cannot be undone.");
        if (confirmed)
            _service.Clear();
    }
}
