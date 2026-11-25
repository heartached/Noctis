using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the unified "Add to Playlist" dialog. Lets the user pick one or more
/// existing playlists or create a new one inline; the host wires the actual add/create
/// work through events. A click on a row ticks it (Add puts the songs in every ticked
/// playlist); a double click or Enter on a row adds at once, as the one-pick dialog did.
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

    /// <summary>How many rows are ticked; Add is off at 0.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddButtonText))]
    [NotifyCanExecuteChangedFor(nameof(AddCheckedCommand))]
    private int _checkedCount;

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

    /// <summary>"Add" for none or one ticked playlist, "Add to 3 Playlists" for more.</summary>
    public string AddButtonText => CheckedCount > 1
        ? Loc.T("AddToPlaylist.AddToCount", CheckedCount)
        : Loc.T("AddToPlaylist.Add");

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

    /// <summary>Fires once with the playlists the songs go in (list order): the ticked rows
    /// on Add, or the row double clicked / Entered (with any ticked ones).</summary>
    public event EventHandler<IReadOnlyList<PlaylistNavItem>>? PlaylistsSelected;

    /// <summary>Fires when the user submits the inline "create new" form.</summary>
    public event EventHandler<(string Name, string Description)>? NewPlaylistRequested;

    /// <summary>Fires when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>A click on a row: ticks it, or unticks it. A full row (every song on it
    /// already) never ticks.</summary>
    [RelayCommand]
    private void ToggleRow(AddToPlaylistRow? row)
    {
        if (row == null || _done || IsCreatingNew || row.IsAlreadyOnPlaylist) return;
        row.IsChecked = !row.IsChecked;
        CheckedCount = Rows.Count(r => r.IsChecked);
    }

    /// <summary>Double click or Enter on a row: adds at once, to that row and any ticked
    /// ones (the quick single add of the one-pick dialog).</summary>
    [RelayCommand]
    private void SelectPlaylist(PlaylistNavItem? playlist)
    {
        if (playlist == null || _done || IsCreatingNew) return;
        if (!_rowOf.TryGetValue(playlist, out var row)) return;
        // Every song is in it already: nothing to add (the row is greyed and takes no clicks).
        if (row.IsAlreadyOnPlaylist) return;
        row.IsChecked = true;
        CheckedCount = Rows.Count(r => r.IsChecked);
        Finish();
    }

    private bool CanAddChecked() => CheckedCount > 0;

    /// <summary>The footer's Add: every ticked playlist, then close.</summary>
    [RelayCommand(CanExecute = nameof(CanAddChecked))]
    private void AddChecked()
    {
        if (_done || IsCreatingNew || CheckedCount == 0) return;
        Finish();
    }

    private void Finish()
    {
        var picked = Rows.Where(r => r.IsChecked).Select(r => r.Playlist).ToList();
        if (picked.Count == 0) return;
        _done = true;
        PlaylistsSelected?.Invoke(this, picked);
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
public sealed partial class AddToPlaylistRow : ObservableObject
{
    /// <summary>Ticked for the footer's Add.</summary>
    [ObservableProperty] private bool _isChecked;

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
