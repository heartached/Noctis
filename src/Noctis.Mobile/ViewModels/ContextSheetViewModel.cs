using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Localization;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// The ⋯ / long-press menu for a song, an album, a playlist or an artist (spec §5 item 1: play
/// next, add to queue, add to playlist, favourite; plus pin, and go to album/artist for a song),
/// shown on the glass pop menu; its Add to Playlist list on the bottom sheet. Every action
/// closes it; the Shell owns opening and Back.
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

    /// <summary>A song's menu; <paramref name="inPlaylist"/> is the playlist whose page it was opened
    /// on, for Remove from Playlist.</summary>
    public static ContextSheetViewModel ForTrack(ShellViewModel shell, Track track, Playlist? inPlaylist = null, TrackSelection? selection = null) =>
        new(shell, track.Title, track.Artist, track.AlbumArtworkPath, new[] { track }) { Track = track, InPlaylist = inPlaylist, SelectIn = selection };

    /// <summary>Several songs (Select mode's Add to Playlist): "3 songs", the first one's cover.</summary>
    public static ContextSheetViewModel ForTracks(ShellViewModel shell, IReadOnlyList<Track> tracks) =>
        new(shell, Loc.Plural("Mobile.Count.Songs", tracks.Count), string.Empty,
            tracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.AlbumArtworkPath))?.AlbumArtworkPath, tracks);

    public static ContextSheetViewModel ForAlbum(ShellViewModel shell, Album album, TrackSelection? selection = null) =>
        new(shell, album.Name, album.Artist, album.ArtworkPath, album.Tracks.ToList()) { Album = album, SelectIn = selection };

    /// <summary>An artist's sheet, headed by the artist's photo when the phone has one, else
    /// <paramref name="artworkPath"/> (the cover the row showed).</summary>
    public static ContextSheetViewModel ForArtist(ShellViewModel shell, Artist artist, string? artworkPath) =>
        new(shell, artist.Name, Loc.T("Mobile.Artist"), shell.ArtistPhotos?.CachedPhoto(artist.Name) ?? artworkPath,
            MobileLibrary.SongsBy(shell.Library.Service, artist.Name)) { Artist = artist };

    public static ContextSheetViewModel ForPlaylist(ShellViewModel shell, Playlist playlist) =>
        new(shell, playlist.Name, Loc.Plural("Mobile.Count.Songs", playlist.TrackIds.Count),
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

    /// <summary>The playlist page a song's menu was opened on.</summary>
    public Playlist? InPlaylist { get; private init; }

    /// <summary>The song list a song's menu was opened on, when it has Select mode: the menu's
    /// Select enters it with this song ticked (Apple's long press › Select).</summary>
    public TrackSelection? SelectIn { get; private init; }

    public bool CanSelect => SelectIn != null && (Track != null || Album != null);

    [RelayCommand]
    private void Select()
    {
        var selection = SelectIn;
        var track = Track;
        _shell.CloseSheet();
        selection?.Begin(track);
    }

    // ── Playlist editing (desktop parity) ──

    /// <summary>A song on its playlist's page: Remove from Playlist (a smart playlist's songs
    /// come from its rules, so not there).</summary>
    public bool CanRemoveFromPlaylist => Track != null && InPlaylist is { IsSmartPlaylist: false } p
        && _shell.CurrentPlaylist(p).TrackIds.Contains(Track.Id);

    /// <summary>A playlist's menu: Rename and Delete Playlist.</summary>
    public bool CanEditPlaylist => Playlist != null;

    [RelayCommand]
    private async Task RemoveFromPlaylistAsync()
    {
        var playlist = InPlaylist;
        _shell.CloseSheet();
        if (playlist != null) await _shell.RemoveFromPlaylistAsync(playlist, Tracks);
    }

    [RelayCommand]
    private void RenamePlaylist()
    {
        if (Playlist is { } playlist) _shell.AskRenamePlaylist(playlist);
    }

    [RelayCommand]
    private void DeletePlaylist()
    {
        if (Playlist is { } playlist) _shell.AskDeletePlaylist(playlist);
    }

    /// <summary>A playlist's menu: Change Cover (a picture from the phone), where there is a picker.</summary>
    public bool CanChangeCover => Playlist != null && _shell.PlaylistFiles != null;

    /// <summary>A playlist's menu: Remove Cover, while it has a custom one.</summary>
    public bool CanRemoveCover => Playlist is { } p && LibraryViewModel.HasCustomCover(_shell.CurrentPlaylist(p));

    [RelayCommand]
    private async Task ChangeCoverAsync()
    {
        var playlist = Playlist;
        _shell.CloseSheet();
        if (playlist != null) await _shell.ChangePlaylistCoverAsync(playlist);
    }

    [RelayCommand]
    private async Task RemoveCoverAsync()
    {
        var playlist = Playlist;
        _shell.CloseSheet();
        if (playlist != null) await _shell.RemovePlaylistCoverAsync(playlist);
    }

    /// <summary>An artist sheet is about the artist (play, queue, pin), not a heart on every song.</summary>
    public bool CanFavourite => Artist == null;

    /// <summary>Artists show a round cover in the sheet header.</summary>
    public bool IsArtist => Artist != null;

    public bool IsFavourite => Tracks.Count > 0 && Tracks.All(t => t.IsFavorite);
    public string FavouriteLabel => IsFavourite ? Loc.T("Mobile.RemoveFromFavorites") : Loc.T("Mobile.Favorite");

    /// <summary>The quick-action tile's short label: what the heart shows now.</summary>
    public string FavouriteTileLabel => IsFavourite ? Loc.T("Mobile.Favorited") : Loc.T("Mobile.Favorite");

    /// <summary>Anything can be pinned to Home → Pinned: albums, artists, playlists and songs.</summary>
    public bool CanPin => Album != null || Playlist != null || Artist != null || Track != null;
    public bool IsPinned =>
        Album != null ? _shell.Library.IsAlbumPinned(Album.Id)
        : Artist != null ? _shell.Library.IsArtistPinned(Artist.Name)
        : Playlist != null ? Playlist.IsPinned
        : Track != null && _shell.Library.IsTrackPinned(Track.Id);
    // Pins show on Home (its Pinned rail) since Home became the start tab: "Pin to Library"
    // sent people looking for them in the Library tab.
    public string PinLabel => IsPinned ? Loc.T("Mobile.Unpin") : Loc.T("Mobile.PinToHome");
    public string PinTileLabel => IsPinned ? Loc.T("Mobile.PinnedTile") : Loc.T("Mobile.Pin");

    // Not on that album's or artist's own page (it pushed the same page again), unless the
    // player covers it: then the row uncovers the page.
    public bool CanGoToAlbum => Track != null && (_shell.IsPlayerUp || !_shell.IsShowingAlbum(Track.AlbumId));
    public bool CanGoToArtist => (Track != null || Album != null)
        && (_shell.IsPlayerUp || ArtistTarget is not { } name || !_shell.IsShowingArtist(name));

    private string? ArtistTarget => Track?.GroupingArtist ?? Album?.Artist;

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

    public string DownloadLabel => Album != null ? Loc.T("Mobile.DownloadAlbum") : Playlist != null ? Loc.T("Mobile.DownloadPlaylist") : Loc.T("Mobile.Download");

    /// <summary>Every desktop song here already has its offline copy: the tile shows a check.</summary>
    public bool IsAllDownloaded => _shell.Account != null && RemoteTracks.Count > 0 && !NotDownloaded.Any();

    /// <summary>The Download tile shows for desktop songs only (a phone's own files are already offline).</summary>
    public bool ShowDownloadTile => CanDownload || IsAllDownloaded;

    public string DownloadTileLabel => IsAllDownloaded ? Loc.T("Mobile.Downloaded") : Loc.T("Mobile.Download");
    public string RemoveDownloadLabel => Track != null ? Loc.T("Mobile.RemoveDownload") : Loc.T("Mobile.Account.RemoveDownloads");

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

    /// <summary>Add Before Current Song shows while a song is loaded, and not on that song's own
    /// menu (it would only go in before itself).</summary>
    public bool CanAddBeforeCurrent => _shell.Player.CurrentTrack is { } current && Tracks.Count > 0
        && !(Tracks.Count == 1 && Tracks[0].Id == current.Id);

    /// <summary>The songs go in just before the playing one (Previous plays them), which keeps
    /// playing; a short banner says so, since nothing on screen changes unless the Queue shows
    /// its History.</summary>
    [RelayCommand]
    private void AddBeforeCurrent()
    {
        _shell.Player.AddBeforeCurrent(Tracks, Album?.Name ?? Playlist?.Name);
        _shell.CloseSheet();
        _shell.Player.Notice = Loc.T("Mobile.AddedBeforeCurrent");
    }

    [RelayCommand]
    private async Task ToggleFavouriteAsync()
    {
        _shell.CloseSheet();
        await _shell.SetFavouriteAsync(Tracks, !IsFavourite);
    }

    // ── Rating (a song's menu): five stars and Clear, synced as the heart is ──

    /// <summary>A song's menu rates it; an album's or playlist's would rate every song at once.</summary>
    public bool CanRate => Track != null;

    public int Rating => Track?.Rating ?? 0;

    public bool IsRated => Rating > 0;

    /// <summary>The Rate row's five stars, filled up to the song's rating.</summary>
    public IReadOnlyList<RateStar> Stars => Enumerable.Range(1, 5).Select(n => new RateStar(n, n <= Rating)).ToList();

    /// <summary>A star (1-5) sets the rating, 0 clears it; the menu closes and the song's rows
    /// follow (Track.Rating notifies). A desktop song's rating goes to the computer on the next sync.</summary>
    [RelayCommand]
    private async Task RateAsync(int stars)
    {
        _shell.CloseSheet();
        await _shell.SetRatingAsync(Tracks, stars);
    }

    [RelayCommand] private Task ClearRatingAsync() => RateAsync(0);

    // ── Sleep Timer (the playing song's menu: Now Playing's ⋯, the Queue's) ──

    /// <summary>The playing song's menu offers the sleep timer, as Now Playing's ⋯ does in Apple Music.</summary>
    public bool CanSleepTimer => Track != null && _shell.Player.CurrentTrack?.Id == Track.Id;

    /// <summary>The row's second line while a timer is set ("Pauses in 29:54").</summary>
    public string SleepTimerDetail => _shell.Player.SleepTimerStatus;

    public bool HasSleepTimerDetail => _shell.Player.HasSleepTimer;

    [RelayCommand]
    private void ShowSleepTimer() => _shell.ShowSleepTimerMenuCommand.Execute(null);

    // ── Hide Song (Settings › Hidden shows it again) ──

    /// <summary>A song's menu: one file hidden from the library, the desktop's Hide Folder for a song.</summary>
    public bool CanHide => Track is { FilePath.Length: > 0 };

    [RelayCommand]
    private async Task HideAsync()
    {
        var path = Track?.FilePath;
        _shell.CloseSheet();
        if (!string.IsNullOrEmpty(path)) await _shell.SetHiddenAsync(new[] { path }, hidden: true);
    }

    // ── Snooze for a Month (the desktop's song and album menus) ──

    /// <summary>A song's or an album's menu: Shuffle and the mixes leave the songs out for a month.</summary>
    public bool CanSnooze => (Track != null || Album != null) && Tracks.Count > 0;

    /// <summary>Every song here is snoozed now: the row ends the snooze instead.</summary>
    public bool IsSnoozed => Tracks.Count > 0 && Tracks.All(t => t.IsSnoozed);

    public string SnoozeLabel => IsSnoozed ? Loc.T("Mobile.Unsnooze") : Loc.T("Mobile.SnoozeForMonth");

    [RelayCommand]
    private async Task ToggleSnoozeAsync()
    {
        var snooze = !IsSnoozed;
        _shell.CloseSheet();
        await _shell.SetSnoozedAsync(Tracks, snooze);
    }

    /// <summary>The menu's Add to Playlist: the menu closes and the bottom sheet opens on the
    /// playlist list (with its keyboard-aware New playlist box), for whatever the menu was on.</summary>
    [RelayCommand] private void ShowPlaylists() => IsPickingPlaylist = true;

    /// <summary>Lyrics are the playing song's page: the row shows only on that song's menu, and
    /// not on the menu opened from the lyrics page itself (where it did nothing).</summary>
    public bool CanShowLyrics => Track != null && _shell.Player.CurrentTrack?.Id == Track.Id && !_shell.IsLyricsOpen;

    /// <summary>The menu's Lyrics: the lyrics page, over the player or a list (the Queue, which
    /// would sit over it, gives way).</summary>
    [RelayCommand]
    private void ShowLyrics()
    {
        _shell.CloseSheet();
        _shell.IsQueueOpen = false;
        _shell.IsLyricsOpen = true;
    }

    /// <summary>The second line under Go to Artist: the song's, or the album's, artists.</summary>
    public string? ArtistLine => Track?.Artist ?? Album?.Artist;

    [RelayCommand]
    private async Task AddToPlaylistAsync(Playlist? playlist)
    {
        if (playlist == null) return;
        _shell.CloseSheet();
        var current = _shell.CurrentPlaylist(playlist);
        var already = Tracks.Count > 0 && Tracks.All(t => current.TrackIds.Contains(t.Id));
        await _shell.Library.AddToPlaylistAsync(playlist, Tracks);
        Added(current.Name, already);
    }

    /// <summary>The short glass banner after Add to Playlist (the player's notice banner), as
    /// Apple's "Added to …": says so too when every song was there already (nothing was added).</summary>
    private void Added(string name, bool already) =>
        _shell.Player.Notice = already ? Loc.T("Mobile.AlreadyIn", name) : Loc.T("Mobile.AddedTo", name);

    [RelayCommand]
    private async Task CreatePlaylistAsync()
    {
        var name = NewPlaylistName.Trim();
        if (name.Length == 0) return;
        _shell.CloseSheet();
        var playlist = await _shell.Library.CreatePlaylistAsync(name);
        await _shell.Library.AddToPlaylistAsync(playlist, Tracks);
        Added(name, already: false);
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
        var name = ArtistTarget;
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

    /// <summary>Every desktop song here, not only the downloaded ones: the service removes the
    /// copies and withdraws the rest from a download still running (an album's batch went on to
    /// fetch the songs it had not reached yet).</summary>
    [RelayCommand]
    private async Task RemoveDownloadAsync()
    {
        var tracks = Downloaded.Any() ? RemoteTracks.ToList() : new List<Track>();
        _shell.CloseSheet();
        if (tracks.Count > 0) await _shell.RemoveDownloadsAsync(tracks);
    }

    [RelayCommand] private void Close() => _shell.CloseSheet();
}

/// <summary>One star of the song menu's Rate row.</summary>
public sealed record RateStar(int Value, bool IsOn)
{
    public string AccessibleName => Loc.T("Mobile.RateStars", Value);
}
