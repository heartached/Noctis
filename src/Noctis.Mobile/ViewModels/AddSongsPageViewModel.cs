using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>One library song in the Add Songs picker: a plus until it is ticked, a check once it
/// is (or when the playlist has it already, which cannot be unticked).</summary>
public sealed partial class AddSongRow : ObservableObject
{
    public AddSongRow(Track track, bool inPlaylist)
    {
        Track = track;
        IsInPlaylist = inPlaylist;
    }

    public Track Track { get; }

    /// <summary>The playlist had the song when the picker opened: shown as added, not tickable.</summary>
    public bool IsInPlaylist { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdded))]
    private bool _isPicked;

    /// <summary>The row's check: picked now, or in the playlist already.</summary>
    public bool IsAdded => IsPicked || IsInPlaylist;
}

/// <summary>
/// Add Songs on a playlist's page (tester 11-14, the desktop's AddSongsDialog in Apple Music's
/// shape): a sheet over the app with the library's songs and a search field; a tap ticks a song
/// (plus → check) and Done appends the ticked songs to the end of the playlist in the order they
/// were ticked. Songs the playlist has already show a check and stay as they are. Cancel (or a
/// swipe down, or Back) adds nothing. Only for a playlist the phone edits (not a smart one, whose
/// songs come from its rules); the add goes through LibraryViewModel.AddToPlaylistAsync, as a
/// song's Add to Playlist does, so a computer's playlist takes it on the next sync.
/// </summary>
public sealed partial class AddSongsPageViewModel : MobilePage
{
    private readonly Playlist _playlist;
    private readonly List<AddSongRow> _all;
    private readonly List<AddSongRow> _picked = new();
    private bool _done;

    public AddSongsPageViewModel(ShellViewModel shell, Playlist playlist)
    {
        Shell = shell;
        _playlist = playlist;
        var have = new HashSet<Guid>(shell.CurrentPlaylist(playlist).TrackIds);
        _all = shell.Library.Songs.Select(t => new AddSongRow(t, have.Contains(t.Id))).ToList();
        Apply();
    }

    /// <summary>Which playlists offer Add Songs: the ones whose songs the phone may change (Remove
    /// from Playlist's rule), so not a smart playlist.</summary>
    public static bool CanAddTo(Playlist? playlist) => playlist is { IsSmartPlaylist: false };

    public ShellViewModel Shell { get; }

    public override string Title => Loc.T("Mobile.AddSongs");

    /// <summary>The playlist as the library holds it now.</summary>
    public Playlist Playlist => Shell.CurrentPlaylist(_playlist);

    /// <summary>The rows shown: the library's songs (by title), filtered by the search.</summary>
    public BulkObservableCollection<AddSongRow> Rows { get; } = new();

    [ObservableProperty] private string _filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => Apply();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText), nameof(HasPicked))]
    private int _pickedCount;

    public bool HasPicked => PickedCount > 0;

    /// <summary>The bar's middle: "Add Songs", then "3 Selected" once songs are ticked.</summary>
    public string HeaderText => PickedCount == 0 ? Title : Loc.T("Mobile.Select.Count", PickedCount);

    public bool ShowEmpty => Rows.Count == 0;

    public string EmptyText => _all.Count == 0 ? Loc.T("Mobile.SongList.NoSongs") : Loc.T("Mobile.SongList.NoMatch");

    /// <summary>The ticked songs, in the order they were ticked.</summary>
    public IReadOnlyList<Track> PickedTracks => _picked.Select(r => r.Track).ToList();

    private void Apply()
    {
        IEnumerable<AddSongRow> rows = _all;
        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var match = new MobileSearch.Matcher(FilterText);
            rows = rows.Where(r => match.HitTrack(r.Track));
        }
        MobileLibrary.ReplaceIfChanged(Rows, rows.ToList());
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    /// <summary>A row tap: tick or untick the song. A song the playlist has already stays checked.</summary>
    [RelayCommand]
    private void Toggle(AddSongRow? row)
    {
        if (row == null || row.IsInPlaylist || _done) return;
        row.IsPicked = !row.IsPicked;
        if (row.IsPicked) _picked.Add(row);
        else _picked.Remove(row);
        PickedCount = _picked.Count;
    }

    /// <summary>Done: the ticked songs join the end of the playlist (any it gained meanwhile are
    /// skipped, as Add to Playlist skips them), "Added to …" on the banner, and the sheet closes.
    /// With nothing ticked it only closes.</summary>
    [RelayCommand]
    private async Task DoneAsync()
    {
        if (_done) return;
        _done = true;
        var tracks = PickedTracks;
        var playlist = Playlist;
        Shell.GoBack();
        if (tracks.Count == 0 || !CanAddTo(playlist)) return;
        await Shell.Library.AddToPlaylistAsync(playlist, tracks);
        Shell.Player.Notice = Loc.T("Mobile.AddedTo", playlist.Name);
    }

    /// <summary>Cancel: closes the sheet, adding nothing.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (_done) return;
        _done = true;
        Shell.GoBack();
    }
}
