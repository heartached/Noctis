using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the unified "Add to Playlist" dialog. Lets the user pick an
/// existing playlist or create a new one inline; the host wires the actual
/// add/create work through events.
/// </summary>
public partial class AddToPlaylistDialogViewModel : ViewModelBase
{
    [ObservableProperty] private bool _isCreatingNew;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCreateCommand))]
    private string _newPlaylistName = string.Empty;
    [ObservableProperty] private string _newPlaylistDescription = string.Empty;
    [ObservableProperty] private bool _showNameRequiredError;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackCountText))]
    private int _trackCount;

    /// <summary>Set once a playlist was picked or a new one requested. The dialog animates out
    /// after that, and a second pick or Create meanwhile (Enter in the name, a double click)
    /// raised the result event again.</summary>
    private bool _done;

    public ObservableCollection<PlaylistNavItem> Playlists { get; }

    /// <summary>The list's rows: each playlist with how many of the songs going in it holds already.</summary>
    public IReadOnlyList<AddToPlaylistRow> Rows { get; }

    private readonly Dictionary<PlaylistNavItem, AddToPlaylistRow> _rowOf = new();

    public bool HasPlaylists => Playlists.Count > 0;

    /// <summary>Dialog title follows the mode: list view vs inline create form (localized; it was
    /// hard-coded English while the keys were translated).</summary>
    public string DialogTitle => IsCreatingNew
        ? Loc.T("AddToPlaylist.CreateNewPlaylist")
        : Loc.T("AddToPlaylist.AddPlaylist");

    /// <summary>"Adding 1 song" / "Adding 3 songs" under the title.</summary>
    public string TrackCountText => TrackCount == 1
        ? Loc.T("AddToPlaylist.AddingOneSong")
        : Loc.T("AddToPlaylist.AddingSongs", TrackCount);

    /// <summary>The existing-playlists section is hidden while the create form is open.</summary>
    public bool ShowPlaylistList => HasPlaylists && !IsCreatingNew;

    public bool ShowEmptyState => !HasPlaylists && !IsCreatingNew;

    /// <param name="trackIds">The songs going in (null: unknown, no row is marked).</param>
    /// <param name="playlistTrackIds">A playlist's current track ids by playlist id.</param>
    public AddToPlaylistDialogViewModel(ObservableCollection<PlaylistNavItem> playlists, int trackCount,
        IEnumerable<Guid>? trackIds = null, Func<Guid, IEnumerable<Guid>?>? playlistTrackIds = null)
    {
        // Smart playlists are filled by their rules, so they can't take added tracks.
        Playlists = new ObservableCollection<PlaylistNavItem>(playlists.Where(p => !p.IsSmartPlaylist));
        TrackCount = trackCount;

        // Discord (Mistery, 10-10) "Already on playlist" indicator: a playlist holds a song once,
        // so picking one that has every song already silently added nothing. One set of the
        // songs going in; each playlist's ids are walked once against it (no per-row set).
        var adding = trackIds == null ? new HashSet<Guid>() : new HashSet<Guid>(trackIds);
        var rows = new List<AddToPlaylistRow>(Playlists.Count);
        foreach (var p in Playlists)
        {
            var already = 0;
            if (adding.Count > 0 && p.PlaylistId is Guid id && playlistTrackIds?.Invoke(id) is { } ids)
                already = ids.Where(adding.Contains).Distinct().Count();
            var row = new AddToPlaylistRow(p, already, adding.Count);
            rows.Add(row);
            _rowOf[p] = row;
        }
        Rows = rows;
    }

    /// <summary>Fires when the user picks an existing playlist row.</summary>
    public event EventHandler<PlaylistNavItem>? PlaylistSelected;

    /// <summary>Fires when the user submits the inline "create new" form.</summary>
    public event EventHandler<(string Name, string Description)>? NewPlaylistRequested;

    /// <summary>Fires when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void SelectPlaylist(PlaylistNavItem? playlist)
    {
        if (playlist == null || _done || IsCreatingNew) return;
        // Every song is in it already: nothing to add (the row is greyed and takes no clicks).
        if (_rowOf.TryGetValue(playlist, out var row) && row.IsAlreadyOnPlaylist) return;
        _done = true;
        PlaylistSelected?.Invoke(this, playlist);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowCreate()
    {
        if (_done || IsCreatingNew) return;
        // The form opens empty. Cleared here rather than on Back, so the text doesn't blink
        // out while the form is still fading away.
        NewPlaylistName = string.Empty;
        NewPlaylistDescription = string.Empty;
        ShowNameRequiredError = false;
        IsCreatingNew = true;
    }

    [RelayCommand]
    private void CancelCreate()
    {
        if (_done) return;
        IsCreatingNew = false;
        ShowNameRequiredError = false;
    }

    private bool CanConfirmCreate() => !string.IsNullOrWhiteSpace(NewPlaylistName);

    [RelayCommand(CanExecute = nameof(CanConfirmCreate))]
    private void ConfirmCreate()
    {
        if (_done) return;
        // Execute() doesn't consult CanExecute, so a blank name is still refused here.
        if (string.IsNullOrWhiteSpace(NewPlaylistName))
        {
            ShowNameRequiredError = true;
            return;
        }

        ShowNameRequiredError = false;
        _done = true;
        NewPlaylistRequested?.Invoke(this, (NewPlaylistName.Trim(), NewPlaylistDescription.Trim()));
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnIsCreatingNewChanged(bool value)
    {
        OnPropertyChanged(nameof(DialogTitle));
        OnPropertyChanged(nameof(ShowPlaylistList));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    partial void OnNewPlaylistNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            ShowNameRequiredError = false;
    }
}

/// <summary>One playlist row in the Add to Playlist dialog.</summary>
public sealed class AddToPlaylistRow
{
    public AddToPlaylistRow(PlaylistNavItem playlist, int alreadyCount, int addingCount)
    {
        Playlist = playlist;
        AlreadyCount = alreadyCount;
        IsAlreadyOnPlaylist = addingCount > 0 && alreadyCount >= addingCount;
    }

    public PlaylistNavItem Playlist { get; }

    /// <summary>How many of the songs going in the playlist holds already.</summary>
    public int AlreadyCount { get; }

    /// <summary>Every song is in it already: greyed and not pickable, like the Add Songs
    /// picker's "Added" rows. Some of them: still pickable, only the rest go in.</summary>
    public bool IsAlreadyOnPlaylist { get; }

    /// <summary>"67 tracks · 3 hr 17 min · Already on playlist" / "… · 2 already on playlist".</summary>
    public string SubtitleText
    {
        get
        {
            var meta = Playlist.MetaText;
            if (AlreadyCount == 0) return meta;
            var note = IsAlreadyOnPlaylist
                ? Loc.T("AddToPlaylist.AlreadyOnPlaylist")
                : Loc.T("AddToPlaylist.SomeAlreadyOnPlaylist", AlreadyCount);
            return string.IsNullOrEmpty(meta) ? note : $"{meta} · {note}";
        }
    }
}
