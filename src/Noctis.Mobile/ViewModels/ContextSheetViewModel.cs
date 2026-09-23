using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// The long-press sheet for a song, an album or a playlist (spec §5 item 1: play next, add to
/// queue, add to playlist, favourite; plus pin, and go to album/artist for a song). Every
/// action closes the sheet; the Shell owns opening and Back.
/// </summary>
public sealed partial class ContextSheetViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    private ContextSheetViewModel(ShellViewModel shell, string title, string subtitle, string? artworkPath, IReadOnlyList<Track> tracks)
    {
        _shell = shell;
        Title = title;
        Subtitle = subtitle;
        ArtworkPath = artworkPath;
        Tracks = tracks;
    }

    public static ContextSheetViewModel ForTrack(ShellViewModel shell, Track track) =>
        new(shell, track.Title, track.Artist, track.AlbumArtworkPath, new[] { track }) { Track = track };

    public static ContextSheetViewModel ForAlbum(ShellViewModel shell, Album album) =>
        new(shell, album.Name, album.Artist, album.ArtworkPath, album.Tracks.ToList()) { Album = album };

    public static ContextSheetViewModel ForPlaylist(ShellViewModel shell, Playlist playlist) =>
        new(shell, playlist.Name, playlist.TrackIds.Count == 1 ? "1 song" : $"{playlist.TrackIds.Count} songs",
            shell.Library.PlaylistArtwork(playlist), shell.ResolvePlaylist(playlist).ToList()) { Playlist = playlist };

    public string Title { get; }
    public string Subtitle { get; }
    public string? ArtworkPath { get; }

    /// <summary>What every action applies to: one song, or the album's / playlist's songs in order.</summary>
    public IReadOnlyList<Track> Tracks { get; }

    public Track? Track { get; private init; }
    public Album? Album { get; private init; }
    public Playlist? Playlist { get; private init; }

    public bool IsFavourite => Tracks.Count > 0 && Tracks.All(t => t.IsFavorite);
    public string FavouriteLabel => IsFavourite ? "Remove from Favourites" : "Favourite";

    public bool CanPin => Album != null || Playlist != null;
    public bool IsPinned => Album != null ? _shell.Library.IsAlbumPinned(Album.Id) : Playlist?.IsPinned == true;
    public string PinLabel => IsPinned ? "Unpin" : "Pin to Library";

    public bool CanGoToAlbum => Track != null;
    public bool CanGoToArtist => Track != null || Album != null;

    /// <summary>The sheet's second page: pick a playlist or name a new one.</summary>
    [ObservableProperty] private bool _isPickingPlaylist;

    [ObservableProperty] private string _newPlaylistName = string.Empty;

    /// <summary>Manual playlists only: a smart playlist's contents come from its rules.</summary>
    public IReadOnlyList<Playlist> Playlists => _shell.Library.Playlists.Where(p => !p.IsSmartPlaylist).ToList();

    [RelayCommand]
    private void PlayNext()
    {
        _shell.Player.PlayNext(Tracks);
        _shell.CloseSheet();
    }

    [RelayCommand]
    private void AddToQueue()
    {
        _shell.Player.AddToQueue(Tracks);
        _shell.CloseSheet();
    }

    [RelayCommand]
    private async Task ToggleFavouriteAsync()
    {
        _shell.CloseSheet();
        await _shell.SetFavouriteAsync(Tracks, !IsFavourite);
    }

    [RelayCommand] private void ShowPlaylists() => IsPickingPlaylist = true;

    [RelayCommand]
    private async Task AddToPlaylistAsync(Playlist? playlist)
    {
        if (playlist == null) return;
        _shell.CloseSheet();
        await _shell.Library.AddToPlaylistAsync(playlist, Tracks);
    }

    [RelayCommand]
    private async Task CreatePlaylistAsync()
    {
        var name = NewPlaylistName.Trim();
        if (name.Length == 0) return;
        _shell.CloseSheet();
        var playlist = await _shell.Library.CreatePlaylistAsync(name);
        await _shell.Library.AddToPlaylistAsync(playlist, Tracks);
    }

    [RelayCommand]
    private async Task TogglePinAsync()
    {
        var pin = !IsPinned;
        _shell.CloseSheet();
        if (Album != null) await _shell.Library.SetAlbumPinnedAsync(Album.Id, pin);
        else if (Playlist != null) await _shell.Library.SetPlaylistPinnedAsync(Playlist, pin);
    }

    [RelayCommand]
    private void GoToAlbum()
    {
        var album = Track == null ? null : _shell.Library.Service.GetAlbumById(Track.AlbumId);
        _shell.CloseSheet();
        if (album != null) _shell.OpenAlbumCommand.Execute(album);
    }

    [RelayCommand]
    private void GoToArtist()
    {
        var name = Track?.GroupingArtist ?? Album?.Artist;
        _shell.CloseSheet();
        if (!string.IsNullOrWhiteSpace(name)) _shell.OpenArtistCommand.Execute(name);
    }

    [RelayCommand] private void Close() => _shell.CloseSheet();
}
