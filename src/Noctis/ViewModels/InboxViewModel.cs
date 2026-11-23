using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// The "Inbox" section (Settings › Library › Inbox for New Music; Discord request, after
/// MusicBee's Inbox): songs a scan or the folder watcher found for the first time, listed
/// newest first with their format, bit depth, sample rate and bitrate so a corrupt or fake
/// lossless file can be spotted before it joins the library. They play and queue like any
/// list; Add to Library moves them in, Delete runs the app's Remove-from-Library flow.
/// </summary>
public partial class InboxViewModel : ViewModelBase, ISearchable
{
    private readonly ILibraryService _library;
    private readonly PlayerViewModel _player;
    private readonly SidebarViewModel _sidebar;
    private string _filter = string.Empty;
    private bool _isDirty = true;
    private bool _isActive;

    /// <summary>The Inbox songs on screen (filtered by the top-bar search).</summary>
    public BulkObservableCollection<Track> Tracks { get; } = new();

    /// <summary>Exposed so row templates can drive the playing-track EQ visualizer off player state.</summary>
    public PlayerViewModel Player => _player;

    /// <summary>"12 songs · 1.2 GB" under the title; empty when nothing waits.</summary>
    [ObservableProperty] private string _summaryText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(PlayAllCommand), nameof(ShuffleAllCommand), nameof(AcceptAllCommand))]
    private int _waitingCount;

    /// <summary>Nothing in the Inbox at all (a search with no match is not "empty").</summary>
    public bool IsEmpty => WaitingCount == 0;

    public InboxViewModel(ILibraryService library, PlayerViewModel player, SidebarViewModel sidebar)
    {
        _library = library;
        _player = player;
        _sidebar = sidebar;
        // A scan fires LibraryUpdated every ~1.5 s; rebuild only while the page is shown.
        _library.LibraryUpdated += (_, _) =>
        {
            _isDirty = true;
            if (_isActive) Dispatcher.UIThread.Post(Refresh);
        };
    }

    /// <summary>Set by MainWindowViewModel while the Inbox is the current view.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            if (value) Refresh();
        }
    }

    public bool IsSearchCleared => _filter.Length == 0;

    public void ApplyFilter(string query)
    {
        _filter = query ?? string.Empty;
        _isDirty = true;
        Refresh();
    }

    /// <summary>Rebuilds the list from the library when it changed (UI thread).</summary>
    public void Refresh()
    {
        if (!_isDirty) return;
        _isDirty = false;

        var waiting = Order(_library.InboxTracks);
        WaitingCount = waiting.Count;
        SummaryText = Summary(waiting);
        var shown = _filter.Length == 0 ? waiting : waiting.Where(SearchQuery.Parse(_filter).Matches).ToList();
        for (var i = 0; i < shown.Count; i++) shown[i].RowNumber = i + 1;
        if (!shown.SequenceEqual(Tracks)) Tracks.ReplaceAll(shown);
    }

    /// <summary>Newest arrivals first, then by artist / album / disc / track so a downloaded
    /// album stays together in its order. Internal for tests.</summary>
    internal static List<Track> Order(IEnumerable<Track> tracks)
        => tracks
            .OrderByDescending(t => t.DateAdded.Date)
            .ThenBy(t => t.AlbumArtist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Album, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.DiscNumber)
            .ThenBy(t => t.TrackNumber)
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static string Summary(IReadOnlyCollection<Track> tracks)
    {
        if (tracks.Count == 0) return string.Empty;
        var count = tracks.Count == 1 ? Loc.T("Inbox.SongCountOne") : Loc.T("Inbox.SongCount", tracks.Count.ToString("N0"));
        long bytes = 0;
        foreach (var t in tracks) bytes += t.FileSize;
        var gb = bytes / (1024.0 * 1024.0 * 1024.0);
        var mb = bytes / (1024.0 * 1024.0);
        var size = gb >= 1 ? $"{gb:0.#} GB" : mb >= 1 ? $"{mb:0} MB" : $"{Math.Max(1, bytes / 1024):0} KB";
        return $"{count} · {size}";
    }

    private bool HasWaiting() => WaitingCount > 0;

    [RelayCommand(CanExecute = nameof(HasWaiting))]
    private void PlayAll()
    {
        if (Tracks.Count > 0) _player.PlayAll(Tracks.ToList());
    }

    [RelayCommand(CanExecute = nameof(HasWaiting))]
    private void ShuffleAll()
    {
        if (Tracks.Count > 0) _player.PlayShuffled(Tracks.ToList());
    }

    /// <summary>Double-click / Play: plays the list from that song, so Next walks the Inbox.</summary>
    [RelayCommand]
    private void PlayTrack(Track track)
    {
        if (track == null) return;
        var list = Tracks.ToList();
        var index = list.IndexOf(track);
        _player.ReplaceQueueAndPlay(list, Math.Max(index, 0));
    }

    /// <summary>Moves the songs into the library (the row button, the menu, the header button
    /// for a selection).</summary>
    public Task AcceptAsync(IReadOnlyList<Track> tracks)
        => tracks.Count == 0 ? Task.CompletedTask : _library.AcceptInboxTracksAsync(tracks.Select(t => t.Id).ToList());

    [RelayCommand]
    private Task AcceptTrack(Track track) => track == null ? Task.CompletedTask : AcceptAsync(new[] { track });

    [RelayCommand(CanExecute = nameof(HasWaiting))]
    private Task AcceptAll() => AcceptAsync(_library.InboxTracks.ToList());

    /// <summary>Delete: the app's Remove-from-Library prompt (Keep Files leaves the file and
    /// keeps it out of later scans; Recycle Bin trashes it).</summary>
    public Task DeleteAsync(IReadOnlyList<Track> tracks)
        => tracks.Count == 0 ? Task.CompletedTask : LibraryRemovalHelper.RemoveWithPromptAsync(_library, tracks);

    [RelayCommand]
    private Task DeleteTrack(Track track) => track == null ? Task.CompletedTask : DeleteAsync(new[] { track });

    // ── Track context-menu commands (the Folders page's set) ──

    [RelayCommand]
    private void PlayNext(Track track) => _player.AddNext(track);

    [RelayCommand]
    private void AddToQueue(Track track) => _player.AddToQueue(track);

    [RelayCommand]
    private async Task AddToNewPlaylist(Track track)
    {
        if (track == null) return;
        await _sidebar.CreatePlaylistWithTracksAsync(new List<Track> { track });
    }

    [RelayCommand]
    private async Task OpenMetadata(Track track)
    {
        if (track == null) return;
        await MetadataHelper.OpenMetadataWindow(track);
    }

    [RelayCommand]
    private async Task ConvertTracks(Track track)
    {
        if (track == null) return;
        await MetadataHelper.OpenAudioConverterDialog(new List<Track> { track });
    }

    [RelayCommand]
    private async Task ToggleFavorite(Track track)
    {
        if (track == null) return;
        track.IsFavorite = !track.IsFavorite;
        await _library.SaveTrackUserStateAsync(new[] { track });
        _library.NotifyFavoritesChanged(new[] { track });
    }

    [RelayCommand]
    private void ShowInExplorer(Track track)
    {
        if (track == null || !File.Exists(track.FilePath)) return;
        PlatformHelper.ShowInFileManager(track.FilePath);
    }

    private Action<Track>? _searchLyricsAction;
    public void SetSearchLyricsAction(Action<Track> action) => _searchLyricsAction = action;

    [RelayCommand]
    private void SearchLyrics(Track track) => _searchLyricsAction?.Invoke(track);
}
