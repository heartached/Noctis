using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// Dedicated page for the "More By {Artist}" carousel — shows the same set of
/// related albums as the carousel, in a scrollable grid.
/// </summary>
public partial class MoreByArtistViewModel : ViewModelBase, ISearchable
{
    private readonly PlayerViewModel? _player;
    private readonly List<Album> _allAlbums = new();

    public string ArtistName { get; }

    public string Title => $"More By {ArtistName}";

    public ObservableCollection<Album> Albums { get; } = new();

    private const double TileLabelHeight = 64;

    [ObservableProperty] private double _tileArtworkSize = 220;

    public double TileHeight => TileArtworkSize + TileLabelHeight;

    partial void OnTileArtworkSizeChanged(double value)
    {
        OnPropertyChanged(nameof(TileHeight));
    }

    /// <summary>Reused albums VM that owns context-menu commands (Play, Shuffle, Add to Queue, Metadata, etc.).</summary>
    public LibraryAlbumsViewModel? LibraryAlbumsVm { get; }

    public event EventHandler? BackRequested;
    public event EventHandler<Album>? AlbumOpened;

    public MoreByArtistViewModel(string artistName, IEnumerable<Album> albums, PlayerViewModel? player = null, LibraryAlbumsViewModel? libraryAlbumsVm = null)
    {
        ArtistName = artistName ?? string.Empty;
        _player = player;
        LibraryAlbumsVm = libraryAlbumsVm;
        _allAlbums.AddRange(albums);
        foreach (var album in _allAlbums)
            Albums.Add(album);
    }

    /// <summary>Filters the album grid by album name via the top-bar search field.</summary>
    public void ApplyFilter(string query)
    {
        var q = query?.Trim() ?? string.Empty;
        Albums.Clear();
        foreach (var album in _allAlbums)
        {
            if (q.Length == 0 || (album.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                Albums.Add(album);
        }
    }

    [RelayCommand]
    private void GoBack() => BackRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenAlbum(Album? album)
    {
        if (album == null) return;
        AlbumOpened?.Invoke(this, album);
    }

    [RelayCommand]
    private void PlayAll()
    {
        if (_player == null) return;
        var tracks = GetAllTracks();
        if (tracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(tracks, 0);
    }

    [RelayCommand]
    private void ShuffleAll()
    {
        if (_player == null) return;
        var tracks = GetAllTracks();
        if (tracks.Count == 0) return;
        var shuffled = Helpers.ShuffleHelper.WeightedShuffle(tracks);
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    private List<Track> GetAllTracks()
    {
        var tracks = new List<Track>();
        foreach (var album in Albums)
        {
            if (album.Tracks != null)
                tracks.AddRange(album.Tracks);
        }
        return tracks;
    }
}
