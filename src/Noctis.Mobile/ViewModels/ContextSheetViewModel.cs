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

    /// <summary>An artist's sheet, headed by the artist's photo when the phone has one, else
    /// <paramref name="artworkPath"/> (the cover the row showed).</summary>
    public static ContextSheetViewModel ForArtist(ShellViewModel shell, Artist artist, string? artworkPath) =>
        new(shell, artist.Name, "Artist", shell.ArtistPhotos?.CachedPhoto(artist.Name) ?? artworkPath,
            MobileLibrary.SongsBy(shell.Library.Service, artist.Name)) { Artist = artist };

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
    public Artist? Artist { get; private init; }

    /// <summary>An artist sheet is about the artist (play, queue, pin), not a heart on every song.</summary>
    public bool CanFavourite => Artist == null;

    /// <summary>Artists show a round cover in the sheet header.</summary>
    public bool IsArtist => Artist != null;

    public bool IsFavourite => Tracks.Count > 0 && Tracks.All(t => t.IsFavorite);
    public string FavouriteLabel => IsFavourite ? "Remove from Favourites" : "Favourite";

    /// <summary>Anything can be pinned to Library → Pinned: albums, artists, playlists and songs.</summary>
    public bool CanPin => Album != null || Playlist != null || Artist != null || Track != null;
    public bool IsPinned =>
        Album != null ? _shell.Library.IsAlbumPinned(Album.Id)
        : Artist != null ? _shell.Library.IsArtistPinned(Artist.Name)
        : Playlist != null ? Playlist.IsPinned
        : Track != null && _shell.Library.IsTrackPinned(Track.Id);
    public string PinLabel => IsPinned ? "Unpin" : "Pin to Library";

    public bool CanGoToAlbum => Track != null;
    public bool CanGoToArtist => Track != null || Album != null;

    // ── Desktop songs (Settings → Account): offline copies ──

    /// <summary>The desktop's songs among <see cref="Tracks"/>; empty without an account service.</summary>
    private IReadOnlyList<Track> RemoteTracks => _remote ??= _shell.Account is { } account
        ? Tracks.Where(account.IsRemote).ToList()
        : Array.Empty<Track>();
    private IReadOnlyList<Track>? _remote;

    private IEnumerable<Track> NotDownloaded => RemoteTracks.Where(t => !_shell.Account!.IsDownloaded(t));
    private IEnumerable<Track> Downloaded => RemoteTracks.Where(t => _shell.Account!.IsDownloaded(t));

    /// <summary>Signed in and at least one desktop song here has no offline copy yet.</summary>
    public bool CanDownload => _shell.Account is { IsSignedIn: true } && NotDownloaded.Any();
    public bool CanRemoveDownload => _shell.Account != null && Downloaded.Any();

    public string DownloadLabel => Album != null ? "Download album" : Playlist != null ? "Download playlist" : "Download";
    public string RemoveDownloadLabel => Track != null ? "Remove download" : "Remove downloads";

    /// <summary>The sheet's second page: pick a playlist or name a new one.</summary>
    [ObservableProperty] private bool _isPickingPlaylist;

    [ObservableProperty] private string _newPlaylistName = string.Empty;

    /// <summary>Manual playlists only: a smart playlist's contents come from its rules.</summary>
    public IReadOnlyList<Playlist> Playlists => _shell.Library.Playlists.Where(p => !p.IsSmartPlaylist).ToList();

    [RelayCommand]
    private void PlayNext()
    {
        _shell.Player.PlayNext(Tracks, Album?.Name ?? Playlist?.Name);
        _shell.CloseSheet();
    }

    [RelayCommand]
    private void AddToQueue()
    {
        _shell.Player.AddToQueue(Tracks, Album?.Name ?? Playlist?.Name);
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
        else if (Artist != null) await _shell.Library.SetArtistPinnedAsync(Artist.Name, pin);
        else if (Playlist != null) await _shell.Library.SetPlaylistPinnedAsync(Playlist, pin);
        else if (Track != null) await _shell.Library.SetTrackPinnedAsync(Track.Id, pin);
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

    [RelayCommand]
    private async Task DownloadAsync()
    {
        var tracks = NotDownloaded.ToList();
        _shell.CloseSheet();
        if (tracks.Count > 0) await _shell.DownloadTracksAsync(tracks);
    }

    [RelayCommand]
    private async Task RemoveDownloadAsync()
    {
        var tracks = Downloaded.ToList();
        _shell.CloseSheet();
        if (tracks.Count > 0) await _shell.RemoveDownloadsAsync(tracks);
    }

    [RelayCommand] private void Close() => _shell.CloseSheet();
}
