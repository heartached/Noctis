using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for viewing a single album's track listing.
/// Shown when the user clicks an album in the grid.
/// </summary>
public partial class AlbumDetailViewModel : ViewModelBase, IDisposable
{
    private readonly PlayerViewModel _player;
    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly IAnimatedCoverService _animatedCovers;
    private readonly ILastFmService _lastFm;
    private readonly SidebarViewModel _sidebar;
    private readonly SettingsViewModel? _settings;
    private readonly System.ComponentModel.PropertyChangedEventHandler _playerPropertyChangedHandler;
    private readonly EventHandler _libraryUpdatedHandler;
    private readonly EventHandler _favoritesChangedHandler;
    private readonly System.ComponentModel.PropertyChangedEventHandler? _settingsPropertyChangedHandler;

    /// <summary>Saved scroll offset for restoring position after navigation.</summary>
    public double SavedScrollOffset { get; set; }

    [ObservableProperty] private Album _album;
    [ObservableProperty] private Bitmap? _albumArt;

    /// <summary>True when this album's track is currently playing AND animated covers are enabled AND a cover exists.</summary>
    public bool IsCurrentAlbumPlaying =>
        (_settings?.EnableAnimatedCovers ?? false)
        && _player.CurrentTrack != null
        && _player.CurrentTrack.AlbumId == Album.Id
        && !string.IsNullOrEmpty(_player.CurrentAnimatedCoverPath);

    /// <summary>This album's own animated cover, if any — shown in the header whenever the
    /// album is being viewed, regardless of whether it's playing.</summary>
    [ObservableProperty] private string? _animatedCoverPath;

    /// <summary>True when the header should animate (an animated cover exists and the feature is on).</summary>
    public bool ShowAnimatedCover =>
        (_settings?.EnableAnimatedCovers ?? false) && !string.IsNullOrEmpty(AnimatedCoverPath);

    partial void OnAnimatedCoverPathChanged(string? value) => OnPropertyChanged(nameof(ShowAnimatedCover));

    public PlayerViewModel Player => _player;
    [ObservableProperty] private IBrush? _backgroundBrush;
    [ObservableProperty] private bool _isLightTint;
    [ObservableProperty] private IBrush _pageForegroundBrush = Brushes.White;
    [ObservableProperty] private IBrush _pageSubtleForegroundBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
    [ObservableProperty] private IBrush _pageDividerBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    [ObservableProperty] private Guid? _currentPlayingTrackId;
    [ObservableProperty] private bool _isPlayerPlaying;
    [ObservableProperty] private string _albumDescription = string.Empty;
    [ObservableProperty] private string _albumDescriptionFull = string.Empty;
    [ObservableProperty] private bool _isAlbumDescriptionOpen;
    [ObservableProperty] private bool _isAlbumDescriptionEditing;
    [ObservableProperty] private string _albumDescriptionEditorText = string.Empty;

    /// <summary>Whether this album is a single (1 track only).</summary>
    public bool IsSingle => Album?.TrackCount == 1;

    /// <summary>Whether all tracks in the album are favorited (for metadata row heart).</summary>
    public bool IsAlbumFavorited => Album?.IsAllTracksFavorite ?? false;

    /// <summary>Whether to show hearts on individual track rows (hide for singles and fully-favorited albums).</summary>
    public bool ShowTrackRowHearts => !IsSingle && !IsAlbumFavorited;

    public bool HasAlbumDescription => !string.IsNullOrWhiteSpace(AlbumDescription);
    public bool HasAlbumDescriptionOverflow =>
        !string.IsNullOrWhiteSpace(AlbumDescription) &&
        (
            AlbumDescription.Length > 260 ||
            (
                !string.IsNullOrWhiteSpace(AlbumDescriptionFull) &&
                !string.Equals(
                    AlbumDescription.Trim(),
                    AlbumDescriptionFull.Trim(),
                    StringComparison.Ordinal)
            )
        );
    public bool HasAlbumDescriptionChanges =>
        !string.Equals(
            (AlbumDescriptionEditorText ?? string.Empty).Trim(),
            (!string.IsNullOrWhiteSpace(AlbumDescriptionFull) ? AlbumDescriptionFull : AlbumDescription).Trim(),
            StringComparison.Ordinal);

    /// <summary>Tracks in this album, ordered by disc and track number.</summary>
    public ObservableCollection<Track> Tracks { get; } = new();

    /// <summary>Other versions of this album (same artist, normalized base title), excluding self.</summary>
    public ObservableCollection<Album> OtherVersions { get; } = new();

    /// <summary>Up to 20 random albums by the same artist, excluding the current album and any in OtherVersions.</summary>
    public ObservableCollection<Album> MoreByArtist { get; } = new();

    public bool HasOtherVersions => OtherVersions.Count > 0;
    public bool HasMoreByArtist => MoreByArtist.Count > 0;
    public string MoreByArtistTitle => $"More By {Album?.Artist}";

    /// <summary>Tracks grouped by disc number for multi-disc display.</summary>
    public ObservableCollection<DiscGroup> DiscGroups { get; } = new();

    /// <summary>Whether the album spans multiple discs.</summary>
    public bool HasMultipleDiscs => DiscGroups.Count > 1;

    /// <summary>Individual artist names parsed from the album artist field, with separator info for display.</summary>
    public ArtistTokenItem[] ArtistTokens { get; private set; } = Array.Empty<ArtistTokenItem>();

    /// <summary>Whether the album has multiple credited artists.</summary>
    public bool HasMultipleArtists => ArtistTokens.Length > 1;

    /// <summary>Exposes playlists for the Add to Playlist submenu.</summary>
    public ObservableCollection<Playlist> Playlists => _sidebar.Playlists;

    /// <summary>Fires when the user wants to go back to the album grid.</summary>
    public event EventHandler? BackRequested;

    /// <summary>Fires when the user wants to view a track's album (may differ from current album).</summary>
    public event EventHandler<Track>? ViewAlbumRequested;

    private Action<string>? _viewArtistAction;
    private Action<Track>? _searchLyricsAction;
    private Action<string, System.Collections.Generic.IEnumerable<Album>>? _openMoreByArtistAction;

    // Stable random pick of "More By Artist" for the currently-viewed album.
    // Captured the first time we build sections for an (artist, albumId) pair,
    // and reused on every rebuild so saves / library updates don't reshuffle.
    private string? _moreByArtistKey;
    private List<Guid>? _moreByArtistOrder;

    /// <summary>Sets the action to navigate to an artist's discography.</summary>
    public void SetViewArtistAction(Action<string> action) => _viewArtistAction = action;

    /// <summary>Sets the action to search lyrics for a track.</summary>
    public void SetSearchLyricsAction(Action<Track> action) => _searchLyricsAction = action;

    /// <summary>Sets the action to open the dedicated "More By {Artist}" page.</summary>
    public void SetOpenMoreByArtistAction(Action<string, System.Collections.Generic.IEnumerable<Album>> action)
        => _openMoreByArtistAction = action;

    public AlbumDetailViewModel(Album album, PlayerViewModel player, IPersistenceService persistence, ILibraryService library, SidebarViewModel sidebar, ILastFmService lastFm, SettingsViewModel? settings = null)
    {
        _player = player;
        _library = library;
        _persistence = persistence;
        _animatedCovers = new AnimatedCoverService(persistence);
        _lastFm = lastFm;
        _sidebar = sidebar;
        _settings = settings;
        _album = album;

        // Parse individual artist names from the album artist field
        var tokens = Track.ParseArtistTokens(album.Artist);
        if (tokens.Length == 0) tokens = new[] { album.Artist };
        ArtistTokens = tokens.Select((name, i) => new ArtistTokenItem(name, IsLast: i == tokens.Length - 1)).ToArray();

        // Load tracks
        foreach (var track in album.Tracks)
            Tracks.Add(track);
        BuildDiscGroups();
        AnimatedCoverPath = ResolveAlbumAnimatedCover();

        // Load artwork off UI thread
        var artPath = persistence.GetArtworkPath(album.Id);
        if (File.Exists(artPath))
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var bmp = new Bitmap(artPath);
                    Dispatcher.UIThread.Post(() => AlbumArt = bmp);
                }
                catch { }
            });
        }

        // Track the currently playing song — store handler so we can unsubscribe in Dispose
        UpdateCurrentPlayingTrack();
        IsPlayerPlaying = _player.State == Models.PlaybackState.Playing;
        _playerPropertyChangedHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack))
            {
                UpdateCurrentPlayingTrack();
                OnPropertyChanged(nameof(IsCurrentAlbumPlaying));
            }
            if (e.PropertyName == nameof(PlayerViewModel.State))
                IsPlayerPlaying = _player.State == Models.PlaybackState.Playing;
            if (e.PropertyName == nameof(PlayerViewModel.CurrentAnimatedCoverPath))
                OnPropertyChanged(nameof(IsCurrentAlbumPlaying));
        };
        _player.PropertyChanged += _playerPropertyChangedHandler;

        // Refresh when library metadata changes (e.g. metadata editor save)
        _libraryUpdatedHandler = (_, _) => Dispatcher.UIThread.Post(RefreshFromLibrary);
        _library.LibraryUpdated += _libraryUpdatedHandler;

        // Refresh album-level favorite indicators when any favorite changes externally
        _favoritesChangedHandler = (_, _) => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(IsAlbumFavorited));
            OnPropertyChanged(nameof(ShowTrackRowHearts));
        });
        _library.FavoritesChanged += _favoritesChangedHandler;

        // Live-toggle the cover-art tint when the user flips the setting.
        if (_settings != null)
        {
            _settingsPropertyChangedHandler = (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.AlbumDetailColorTintEnabled))
                    Dispatcher.UIThread.Post(RebuildBackgroundBrush);
                if (e.PropertyName == nameof(SettingsViewModel.EnableAnimatedCovers))
                    Dispatcher.UIThread.Post(() =>
                    {
                        OnPropertyChanged(nameof(IsCurrentAlbumPlaying));
                        OnPropertyChanged(nameof(ShowAnimatedCover));
                    });
            };
            _settings.PropertyChanged += _settingsPropertyChangedHandler;
        }

        // Build related-album sections from the local library.
        BuildRelatedSections();

        // Fetch album description asynchronously; fail silently if unavailable.
        _ = LoadAlbumDescriptionAsync();
    }

    partial void OnAlbumChanged(Album value)
    {
        var tokens = Track.ParseArtistTokens(value.Artist);
        if (tokens.Length == 0) tokens = new[] { value.Artist };
        ArtistTokens = tokens.Select((name, i) => new ArtistTokenItem(name, IsLast: i == tokens.Length - 1)).ToArray();
        OnPropertyChanged(nameof(ArtistTokens));
        OnPropertyChanged(nameof(HasMultipleArtists));
        OnPropertyChanged(nameof(MoreByArtistTitle));
        BuildRelatedSections();
    }

    partial void OnAlbumDescriptionChanged(string value)
    {
        OnPropertyChanged(nameof(HasAlbumDescription));
        OnPropertyChanged(nameof(HasAlbumDescriptionOverflow));
        OnPropertyChanged(nameof(HasAlbumDescriptionChanges));
    }

    partial void OnAlbumDescriptionFullChanged(string value)
    {
        // Keep visibility binding stable even if only one payload variant is available.
        OnPropertyChanged(nameof(HasAlbumDescription));
        OnPropertyChanged(nameof(HasAlbumDescriptionOverflow));
        OnPropertyChanged(nameof(HasAlbumDescriptionChanges));
    }

    partial void OnAlbumDescriptionEditorTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasAlbumDescriptionChanges));
    }

    private async Task LoadAlbumDescriptionAsync()
    {
        try
        {
            var summaryTask = _lastFm.GetAlbumDescriptionAsync(Album.Artist, Album.Name);
            var fullTask = _lastFm.GetAlbumDescriptionFullAsync(Album.Artist, Album.Name);
            await Task.WhenAll(summaryTask, fullTask);

            var summary = await summaryTask;
            var full = await fullTask;

            var snippet = !string.IsNullOrWhiteSpace(summary) ? summary : full;
            var fullText = !string.IsNullOrWhiteSpace(full) ? full : summary;
            if (!string.IsNullOrWhiteSpace(snippet))
                AlbumDescription = snippet;
            if (!string.IsNullOrWhiteSpace(fullText))
                AlbumDescriptionFull = fullText;
            if (!IsAlbumDescriptionEditing)
            {
                AlbumDescriptionEditorText = !string.IsNullOrWhiteSpace(AlbumDescriptionFull)
                    ? AlbumDescriptionFull
                    : AlbumDescription;
            }
        }
        catch
        {
            // Fail silently by design.
        }
    }

    /// <summary>Resolves this album's animated cover from its first track (sidecar or managed cache).</summary>
    private string? ResolveAlbumAnimatedCover()
        => Tracks.Count > 0 ? _animatedCovers.Resolve(Tracks[0]) : null;

    private void RefreshFromLibrary()
    {
        // Try to find album by current ID first
        var updatedAlbum = _library.Albums.FirstOrDefault(a => a.Id == Album.Id);

        // If not found, the album/artist name was edited causing AlbumId to change.
        // Find the new album by looking for one that contains any of our tracks.
        if (updatedAlbum == null)
        {
            var trackIds = Tracks.Select(t => t.Id).ToHashSet();
            updatedAlbum = _library.Albums
                .FirstOrDefault(a => a.Tracks.Any(t => trackIds.Contains(t.Id)));
        }

        if (updatedAlbum == null)
        {
            // Album truly removed — navigate back
            BackRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        Album = updatedAlbum;

        Tracks.Clear();
        foreach (var track in updatedAlbum.Tracks)
            Tracks.Add(track);
        BuildDiscGroups();
        AnimatedCoverPath = ResolveAlbumAnimatedCover();
        // OnAlbumChanged already rebuilds related sections; reaching here only when the
        // ID actually changed, but rebuild defensively in case the same-ID path missed.
        BuildRelatedSections();

        // Reload artwork in case it changed
        var oldArt = AlbumArt;
        var artPath = _persistence.GetArtworkPath(updatedAlbum.Id);
        if (File.Exists(artPath))
        {
            try { AlbumArt = new Bitmap(artPath); } catch { AlbumArt = null; }
        }
        else
        {
            AlbumArt = null;
        }
        oldArt?.Dispose();
    }

    partial void OnAlbumArtChanged(Bitmap? value)
    {
        RebuildBackgroundBrush();
    }

    private void RebuildBackgroundBrush()
    {
        var bmp = AlbumArt;
        if (bmp == null || _settings?.AlbumDetailColorTintEnabled == false)
        {
            BackgroundBrush = null;
            IsLightTint = false;
            PageForegroundBrush = Brushes.White;
            PageSubtleForegroundBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
            PageDividerBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            return;
        }

        // Extraction must run on the UI thread — DominantColorExtractor uses
        // RenderTargetBitmap/DrawImage, which Avalonia restricts to the UI thread.
        // The sample target is 50x50, so this is sub-millisecond and does not stutter the UI.
        try
        {
            var color = DominantColorExtractor.ExtractEdgeBackgroundColor(bmp);
            BackgroundBrush = new SolidColorBrush(color);

            var luminance = DominantColorExtractor.GetRelativeLuminance(color);
            var isLight = luminance > 0.55;
            IsLightTint = isLight;

            if (isLight)
            {
                PageForegroundBrush = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11));
                PageSubtleForegroundBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
                PageDividerBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
            }
            else
            {
                PageForegroundBrush = Brushes.White;
                PageSubtleForegroundBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
                PageDividerBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.UI, "AlbumDetail.GradientBg", ex.ToString());
            BackgroundBrush = null;
            IsLightTint = false;
            PageForegroundBrush = Brushes.White;
            PageSubtleForegroundBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
            PageDividerBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        }
    }

    private void BuildRelatedSections()
    {
        var currentArtist = Album?.Artist;
        var currentId = Album?.Id ?? Guid.Empty;
        if (string.IsNullOrWhiteSpace(currentArtist) || currentId == Guid.Empty)
        {
            OtherVersions.Clear();
            MoreByArtist.Clear();
            OnPropertyChanged(nameof(HasOtherVersions));
            OnPropertyChanged(nameof(HasMoreByArtist));
            OnPropertyChanged(nameof(MoreByArtistTitle));
            return;
        }

        var artistAlbums = _library.GetAlbumsByArtist(currentArtist)
            .Where(a => a.Id != currentId)
            .ToList();

        var currentNormalized = NormalizeAlbumTitle(Album!.Name);

        var versions = artistAlbums
            .Where(a => string.Equals(NormalizeAlbumTitle(a.Name), currentNormalized, StringComparison.OrdinalIgnoreCase))
            .ToList();

        OtherVersions.Clear();
        foreach (var a in versions)
            OtherVersions.Add(a);

        var versionIds = versions.Select(v => v.Id).ToHashSet();
        var pool = artistAlbums.Where(a => !versionIds.Contains(a.Id)).ToList();

        // Stable picks: sample once per (artist, albumId) and reuse on subsequent
        // rebuilds (e.g. metadata save → LibraryUpdated) so the section doesn't
        // reshuffle while the user is viewing the same album.
        var key = $"{currentArtist} {currentId}";
        var poolById = pool.ToDictionary(a => a.Id);
        List<Album> picks;
        if (_moreByArtistKey == key && _moreByArtistOrder != null)
        {
            picks = _moreByArtistOrder
                .Where(id => poolById.ContainsKey(id))
                .Select(id => poolById[id])
                .ToList();
        }
        else
        {
            picks = pool
                .OrderBy(_ => Random.Shared.Next())
                .Take(20)
                .ToList();
            _moreByArtistKey = key;
            _moreByArtistOrder = picks.Select(a => a.Id).ToList();
        }

        MoreByArtist.Clear();
        foreach (var a in picks)
            MoreByArtist.Add(a);

        OnPropertyChanged(nameof(HasOtherVersions));
        OnPropertyChanged(nameof(HasMoreByArtist));
        OnPropertyChanged(nameof(MoreByArtistTitle));
    }

    private static readonly System.Text.RegularExpressions.Regex s_featRegex =
        new(@"\s*[\(\[]\s*(feat\.?|ft\.?|featuring)\s+[^\)\]]+[\)\]]\s*",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex s_trailingParensRegex =
        new(@"\s*[\(\[][^\)\]]*[\)\]]\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex s_trailingDashSuffixRegex =
        new(@"\s*-\s*(single|ep)\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Normalizes an album title for "Other Versions" matching by stripping any
    /// trailing parenthetical/bracketed segment (e.g. "(Deluxe Edition)",
    /// "(3am Edition)", "(Til Dawn Edition)", "(Big Machine Radio Release Special)")
    /// plus " - Single" / " - EP" suffixes and any embedded "(feat. ...)" group.
    /// Mirrors Apple Music's "Other Versions" grouping behavior.
    /// </summary>
    private static string NormalizeAlbumTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var s = title.Trim();
        s = s_featRegex.Replace(s, " ").Trim();
        // Iteratively strip trailing markers so stacked suffixes like
        // "Album (Deluxe Edition) [Remastered]" collapse to "Album".
        for (int i = 0; i < 6; i++)
        {
            var prev = s;
            s = s_trailingParensRegex.Replace(s, string.Empty).Trim();
            s = s_trailingDashSuffixRegex.Replace(s, string.Empty).Trim();
            s = s.TrimEnd('-', '–', '—').Trim();
            if (s == prev) break;
        }
        return s;
    }

    private void BuildDiscGroups()
    {
        DiscGroups.Clear();
        var groups = Tracks
            .GroupBy(t => t.DiscNumber)
            .OrderBy(g => g.Key)
            .Select(g => new DiscGroup(g.Key, $"Disc {g.Key}", g.ToList()));
        foreach (var g in groups)
            DiscGroups.Add(g);
        OnPropertyChanged(nameof(HasMultipleDiscs));
    }

    private static List<Track> InAlbumOrder(IEnumerable<Track> tracks) =>
        tracks
            .OrderBy(t => t.DiscNumber <= 0 ? 1 : t.DiscNumber)
            .ThenBy(t => t.TrackNumber <= 0 ? int.MaxValue : t.TrackNumber)
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void UpdateCurrentPlayingTrack()
    {
        CurrentPlayingTrackId = _player.CurrentTrack?.Id;
    }

    [RelayCommand]
    private void PlayAll()
    {
        if (Tracks.Count == 0) return;
        var tracks = InAlbumOrder(Tracks);
        DebugLogger.Info(DebugLogger.Category.Queue, "Album.Play", $"tracks={tracks.Count}, startIdx=0");
        _player.ReplaceQueueAndPlay(tracks, 0);
    }

    [RelayCommand]
    private void ShufflePlay()
    {
        if (Tracks.Count == 0) return;
        var shuffled = InAlbumOrder(Tracks).OrderBy(_ => Random.Shared.Next()).ToList();
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    [RelayCommand]
    private void PlayFrom(Track track)
    {
        var tracks = InAlbumOrder(Tracks);
        var idx = tracks.FindIndex(t => t.Id == track.Id);
        if (idx < 0) idx = 0;
        DebugLogger.Info(DebugLogger.Category.Queue, "Album.PlayFrom", $"tracks={tracks.Count}, startIdx={idx}, track={track.Title}");
        _player.ReplaceQueueAndPlay(tracks, idx);
    }

    /// <summary>Navigates to a related album shown in Other Versions / More By Artist.</summary>
    [RelayCommand]
    private void OpenRelatedAlbum(Album? album)
    {
        if (album == null || album.Tracks.Count == 0) return;
        // Reuse the existing track-driven album-navigation event so MainWindow's
        // history/back-button wiring stays in one place.
        ViewAlbumRequested?.Invoke(this, album.Tracks[0]);
    }

    [RelayCommand]
    private void ViewArtist()
    {
        var artist = Album.Artist;
        if (!string.IsNullOrWhiteSpace(artist))
            _viewArtistAction?.Invoke(artist);
    }

    /// <summary>Opens the dedicated "More By {Artist}" grid page with the carousel's albums.</summary>
    [RelayCommand]
    private void OpenMoreByArtistPage()
    {
        var artist = Album?.Artist;
        if (string.IsNullOrWhiteSpace(artist) || MoreByArtist.Count == 0) return;
        // Snapshot the collection so the new page is independent of subsequent rebuilds.
        _openMoreByArtistAction?.Invoke(artist, MoreByArtist.ToArray());
    }

    [RelayCommand]
    private void ViewArtistFromTrack(string artistName)
    {
        if (!string.IsNullOrWhiteSpace(artistName))
            _viewArtistAction?.Invoke(artistName);
    }

    [RelayCommand]
    private void ViewIndividualArtist(string artistName)
    {
        if (!string.IsNullOrWhiteSpace(artistName))
            _viewArtistAction?.Invoke(artistName);
    }

    [RelayCommand]
    private void GoBack()
    {
        IsAlbumDescriptionOpen = false;
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task OpenAlbumDescription()
    {
        if (!HasAlbumDescription) return;
        AlbumDescriptionEditorText = !string.IsNullOrWhiteSpace(AlbumDescriptionFull)
            ? AlbumDescriptionFull
            : AlbumDescription;
        IsAlbumDescriptionEditing = false;
        await Views.AlbumDescriptionDialog.ShowAsync(this);
        // Dialog closed — clean up state
        IsAlbumDescriptionEditing = false;
    }

    [RelayCommand]
    private void CloseAlbumDescription()
    {
        IsAlbumDescriptionEditing = false;
        IsAlbumDescriptionOpen = false;
    }

    [RelayCommand]
    private void StartAlbumDescriptionEdit()
    {
        AlbumDescriptionEditorText = !string.IsNullOrWhiteSpace(AlbumDescriptionFull)
            ? AlbumDescriptionFull
            : AlbumDescription;
        IsAlbumDescriptionEditing = true;
    }

    [RelayCommand]
    private async Task SaveAlbumDescriptionEdit()
    {
        var edited = (AlbumDescriptionEditorText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(edited))
        {
            await _lastFm.SetAlbumDescriptionOverrideAsync(Album.Artist, Album.Name, string.Empty);
            AlbumDescription = string.Empty;
            AlbumDescriptionFull = string.Empty;
            AlbumDescriptionEditorText = string.Empty;
        }
        else
        {
            await _lastFm.SetAlbumDescriptionOverrideAsync(Album.Artist, Album.Name, edited);
            AlbumDescription = edited;
            AlbumDescriptionFull = edited;
        }

        IsAlbumDescriptionEditing = false;
    }

    [RelayCommand]
    private void CancelAlbumDescriptionEdit()
    {
        AlbumDescriptionEditorText = !string.IsNullOrWhiteSpace(AlbumDescriptionFull)
            ? AlbumDescriptionFull
            : AlbumDescription;
        IsAlbumDescriptionEditing = false;
    }

    [RelayCommand]
    private void PlayNext(Track track) => _player.AddNext(track);

    [RelayCommand]
    private void AddToQueue(Track track) => _player.AddToQueue(track);
    [RelayCommand]
    private void AddAlbumToQueue()
    {
        if (Tracks.Count == 0) return;
        _player.AddRangeToQueue(InAlbumOrder(Tracks));
    }

    [RelayCommand]
    private async Task OpenMetadata(Track track)
    {
        await MetadataHelper.OpenMetadataWindow(track);
    }

    [RelayCommand]
    private async Task OpenAlbumMetadata()
    {
        if (Tracks.Count == 0) return;
        await MetadataHelper.OpenMetadataWindow(Tracks[0], albumScoped: true);
    }

    [RelayCommand]
    private async Task ToggleFavorite(Track track)
    {
        track.IsFavorite = !track.IsFavorite;
        await _library.SaveAsync();
        _library.NotifyFavoritesChanged();
        // Refresh hearts visibility
        OnPropertyChanged(nameof(IsAlbumFavorited));
        OnPropertyChanged(nameof(ShowTrackRowHearts));
    }

    [RelayCommand]
    private async Task ToggleAlbumFavorites()
    {
        if (Tracks.Count == 0) return;

        var newState = !Album.IsAllTracksFavorite;
        foreach (var track in Tracks)
            track.IsFavorite = newState;

        await _library.SaveAsync();
        _library.NotifyFavoritesChanged();
        // Refresh hearts visibility
        OnPropertyChanged(nameof(IsAlbumFavorited));
        OnPropertyChanged(nameof(ShowTrackRowHearts));
    }

    [RelayCommand]
    private async Task AddToNewPlaylist(Track track)
    {
        await _sidebar.CreatePlaylistWithTrackAsync(track);
    }

    [RelayCommand]
    private async Task AddAlbumToNewPlaylist()
    {
        if (Tracks.Count == 0) return;
        await _sidebar.CreatePlaylistWithTracksAsync(InAlbumOrder(Tracks));
    }

    [RelayCommand]
    private async Task AddToExistingPlaylist(object[] parameters)
    {
        if (parameters == null || parameters.Length != 2) return;
        if (parameters[0] is not Track track || parameters[1] is not Playlist playlist) return;

        await _sidebar.AddTracksToPlaylist(playlist.Id, new[] { track });
    }

    [RelayCommand]
    private async Task AddAlbumToExistingPlaylist(Playlist playlist)
    {
        if (playlist == null || Tracks.Count == 0) return;
        await _sidebar.AddTracksToPlaylist(playlist.Id, InAlbumOrder(Tracks));
    }

    [RelayCommand]
    private async Task RemoveFromLibrary(Track track)
    {
        if (!await Views.ConfirmationDialog.ShowAsync("Do you want to remove the selected item from your Library?"))
            return;
        var idx = Tracks.IndexOf(track);
        if (idx >= 0)
            Tracks.RemoveAt(idx);
        await _library.RemoveTrackAsync(track.Id);
    }

    [RelayCommand]
    private async Task RemoveAlbumFromLibrary()
    {
        if (Tracks.Count == 0) return;
        if (!await Views.ConfirmationDialog.ShowAsync("Do you want to remove the selected item from your Library?"))
            return;
        var trackIds = Tracks.Select(t => t.Id).ToList();
        Tracks.Clear();
        await _library.RemoveTracksAsync(trackIds);
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowInExplorer(Track track)
    {
        if (track == null || !File.Exists(track.FilePath)) return;
        Helpers.PlatformHelper.ShowInFileManager(track.FilePath);
    }

    [RelayCommand]
    private void ShowAlbumInExplorer()
    {
        if (Tracks.Count == 0) return;
        var track = Tracks[0];
        if (!File.Exists(track.FilePath)) return;
        Helpers.PlatformHelper.ShowInFileManager(track.FilePath);
    }

    [RelayCommand]
    private void SearchLyrics(Track track) => _searchLyricsAction?.Invoke(track);
    [RelayCommand]
    private void ViewAlbum(Track track) => ViewAlbumRequested?.Invoke(this, track);

    [RelayCommand]
    private void ViewCurrentAlbum()
    {
        if (Tracks.Count == 0) return;
        ViewAlbumRequested?.Invoke(this, Tracks[0]);
    }

    // ----- Related album commands (Other Versions / More By Artist context menu) -----
    // These act on a specified Album (the right-clicked tile), not the currently displayed one.

    [RelayCommand]
    private void PlayRelatedAlbum(Album? album)
    {
        if (album == null || album.Tracks.Count == 0) return;
        _player.ReplaceQueueAndPlay(InAlbumOrder(album.Tracks), 0);
    }

    [RelayCommand]
    private void ShuffleRelatedAlbum(Album? album)
    {
        if (album == null || album.Tracks.Count == 0) return;
        var shuffled = InAlbumOrder(album.Tracks).OrderBy(_ => Random.Shared.Next()).ToList();
        _player.ReplaceQueueAndPlay(shuffled, 0);
    }

    [RelayCommand]
    private void PlayNextRelatedAlbum(Album? album)
    {
        if (album == null || album.Tracks.Count == 0) return;
        // Insert in reverse so playback order matches album order.
        var tracks = InAlbumOrder(album.Tracks);
        for (int i = tracks.Count - 1; i >= 0; i--)
            _player.AddNext(tracks[i]);
    }

    [RelayCommand]
    private void AddRelatedAlbumToQueue(Album? album)
    {
        if (album == null || album.Tracks.Count == 0) return;
        _player.AddRangeToQueue(InAlbumOrder(album.Tracks));
    }

    [RelayCommand]
    private async Task AddRelatedAlbumToNewPlaylist(Album? album)
    {
        if (album == null || album.Tracks.Count == 0) return;
        await _sidebar.CreatePlaylistWithTracksAsync(InAlbumOrder(album.Tracks));
    }

    [RelayCommand]
    private async Task AddRelatedAlbumToExistingPlaylist(object[] parameters)
    {
        if (parameters == null || parameters.Length != 2) return;
        if (parameters[0] is not Album album || parameters[1] is not Playlist playlist) return;
        if (album.Tracks.Count == 0) return;
        await _sidebar.AddTracksToPlaylist(playlist.Id, InAlbumOrder(album.Tracks));
    }

    [RelayCommand]
    private async Task ToggleRelatedAlbumFavorites(Album? album)
    {
        if (album == null || album.Tracks.Count == 0) return;
        var newState = !album.IsAllTracksFavorite;
        foreach (var track in album.Tracks)
            track.IsFavorite = newState;
        await _library.SaveAsync();
        _library.NotifyFavoritesChanged();
    }

    [RelayCommand]
    private async Task OpenRelatedAlbumMetadata(Album? album)
    {
        if (album == null || album.Tracks.Count == 0) return;
        await MetadataHelper.OpenMetadataWindow(album.Tracks[0], albumScoped: true);
    }

    [RelayCommand]
    private void ShowRelatedAlbumInExplorer(Album? album)
    {
        if (album == null || album.Tracks.Count == 0) return;
        var track = album.Tracks[0];
        if (!File.Exists(track.FilePath)) return;
        Helpers.PlatformHelper.ShowInFileManager(track.FilePath);
    }

    [RelayCommand]
    private async Task RemoveRelatedAlbumFromLibrary(Album? album)
    {
        if (album == null || album.Tracks.Count == 0) return;
        if (!await Views.ConfirmationDialog.ShowAsync("Do you want to remove the selected item from your Library?"))
            return;
        var trackIds = album.Tracks.Select(t => t.Id).ToList();
        await _library.RemoveTracksAsync(trackIds);
    }

    public void Dispose()
    {
        _player.PropertyChanged -= _playerPropertyChangedHandler;
        _library.LibraryUpdated -= _libraryUpdatedHandler;
        _library.FavoritesChanged -= _favoritesChangedHandler;
        if (_settings != null && _settingsPropertyChangedHandler != null)
            _settings.PropertyChanged -= _settingsPropertyChangedHandler;
        AlbumArt?.Dispose();
        AlbumArt = null;
    }
}

/// <summary>Display item for an individual artist name in a multi-artist album header.</summary>
public record ArtistTokenItem(string Name, bool IsLast);

/// <summary>A group of tracks belonging to a single disc within an album.</summary>
public record DiscGroup(int DiscNumber, string Header, List<Track> Tracks);
