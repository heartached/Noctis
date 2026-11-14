using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.Views;

namespace Noctis.ViewModels;

/// <summary>Where the songs go: an existing album's name and album artist, or a new album's.</summary>
public sealed record MoveToAlbumTarget(string Album, string AlbumArtist, bool IsNew);

/// <summary>
/// GitHub #239 "Move to Album": pick an existing album (searchable list with covers) or name a
/// new one, then Move. The host does the work through <see cref="Confirmed"/>; see
/// <see cref="MoveToAlbumFlow"/>.
/// </summary>
public partial class MoveToAlbumViewModel : ViewModelBase
{
    /// <summary>At most this many rows are listed; the search box narrows the rest.</summary>
    internal const int MaxRows = 200;

    private readonly List<MoveToAlbumRow> _all;
    private bool _done;

    public IReadOnlyList<Track> Tracks { get; }

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private IReadOnlyList<MoveToAlbumRow> _rows = Array.Empty<MoveToAlbumRow>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmText))]
    [NotifyCanExecuteChangedFor(nameof(MoveCommand))]
    private MoveToAlbumRow? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmText), nameof(TitleText))]
    [NotifyCanExecuteChangedFor(nameof(MoveCommand))]
    private bool _isCreatingNew;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmText))]
    [NotifyCanExecuteChangedFor(nameof(MoveCommand))]
    private string _newAlbumName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveCommand))]
    private string _newAlbumArtist = string.Empty;

    public bool HasRows => Rows.Count > 0;

    public string TitleText => IsCreatingNew ? Loc.T("MoveToAlbum.NewTitle") : Loc.T("MoveToAlbum.Title");

    /// <summary>The song's title for one song, else "3 songs".</summary>
    public string SubtitleText => Tracks.Count == 1 ? Tracks[0].Title : Loc.T("MoveToAlbum.SongCount", Tracks.Count);

    /// <summary>What Move will do, files included (the move changes the files' tags).</summary>
    public string ConfirmText
    {
        get
        {
            if (IsCreatingNew)
                return Track.IsRealAlbumName(NewAlbumName)
                    ? Loc.T("MoveToAlbum.ConfirmNew", Tracks.Count, NewAlbumName.Trim())
                    : Loc.T("MoveToAlbum.NewHint");
            return SelectedRow is { } row
                ? Loc.T("MoveToAlbum.ConfirmExisting", Tracks.Count, row.Name)
                : Loc.T("MoveToAlbum.PickHint");
        }
    }

    /// <summary>Fires once with the chosen album when the user presses Move.</summary>
    public event EventHandler<MoveToAlbumTarget>? Confirmed;

    /// <summary>Fires when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    public MoveToAlbumViewModel(IReadOnlyList<Track> tracks, IEnumerable<Album> albums)
    {
        Tracks = tracks;
        var from = tracks.Select(t => t.AlbumId).ToHashSet();
        _all = albums
            .Where(a => a.Tracks is { Count: > 0 } && Track.IsRealAlbumName(a.Name) && a.Id != Track.UnknownAlbumBucketId)
            .Select(a => new MoveToAlbumRow(a, isCurrent: from.Count == 1 && from.Contains(a.Id)))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        NewAlbumArtist = AlbumGrouping.SuggestGroupTarget(tracks).AlbumArtist;
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnRowsChanged(IReadOnlyList<MoveToAlbumRow> value) => OnPropertyChanged(nameof(HasRows));

    private void ApplyFilter()
    {
        var words = (SearchText ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Rows = _all
            .Where(r => words.All(w => r.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                                       || r.Artist.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            .Take(MaxRows)
            .ToList();
        if (SelectedRow != null && !Rows.Contains(SelectedRow)) SelectAlbum(null);
    }

    [RelayCommand]
    private void SelectAlbum(MoveToAlbumRow? row)
    {
        if (_done || row is { IsCurrent: true }) return;
        if (SelectedRow != null) SelectedRow.IsSelected = false;
        SelectedRow = row;
        if (row != null) row.IsSelected = true;
    }

    [RelayCommand]
    private void ShowCreate()
    {
        if (_done) return;
        IsCreatingNew = true;
    }

    [RelayCommand]
    private void CancelCreate()
    {
        if (_done) return;
        IsCreatingNew = false;
    }

    private bool CanMove() => IsCreatingNew
        ? Track.IsRealAlbumName(NewAlbumName) && !string.IsNullOrWhiteSpace(NewAlbumArtist)
        : SelectedRow != null;

    [RelayCommand(CanExecute = nameof(CanMove))]
    private void Move()
    {
        if (_done || !CanMove()) return; // Execute() does not ask CanExecute (Enter in a field)
        _done = true;
        var target = IsCreatingNew
            ? new MoveToAlbumTarget(NewAlbumName.Trim(), NewAlbumArtist.Trim(), IsNew: true)
            : new MoveToAlbumTarget(SelectedRow!.TargetAlbum, SelectedRow.TargetAlbumArtist, IsNew: false);
        Confirmed?.Invoke(this, target);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>One album in the Move to Album list.</summary>
public sealed partial class MoveToAlbumRow : ObservableObject
{
    public MoveToAlbumRow(Album album, bool isCurrent)
    {
        Album = album;
        IsCurrent = isCurrent;
        var first = album.Tracks.FirstOrDefault();
        // The songs' own spelling: the album's id is computed from it.
        TargetAlbum = first?.Album is { } a && Track.IsRealAlbumName(a) ? a : album.Name;
        TargetAlbumArtist = string.IsNullOrWhiteSpace(first?.AlbumArtist) ? album.Artist : first!.AlbumArtist;
    }

    public Album Album { get; }
    public string Name => Album.Name;
    public string Artist => Album.Artist;
    public string? CoverPath => Album.ArtworkPath;
    public bool HasCover => !string.IsNullOrEmpty(Album.ArtworkPath);
    internal string TargetAlbum { get; }
    internal string TargetAlbumArtist { get; }

    /// <summary>Every song is on this album already: muted, not pickable.</summary>
    public bool IsCurrent { get; }

    [ObservableProperty] private bool _isSelected;

    /// <summary>"Artist · 12 songs" (+ "· Current album").</summary>
    public string SubtitleText
    {
        get
        {
            var text = $"{Artist} · {Loc.T("MoveToAlbum.SongCount", Album.Tracks.Count)}";
            return IsCurrent ? $"{text} · {Loc.T("MoveToAlbum.CurrentAlbum")}" : text;
        }
    }
}

/// <summary>
/// GitHub #239: Move to Album from any song menu: the dialog, then the move (the files' tags are
/// written by <see cref="ILibraryService.MoveToAlbumAsync"/>), then a notice listing the files
/// that could not be written, as Group into One Album does.
/// </summary>
public static class MoveToAlbumFlow
{
    /// <param name="showDialog">Shows the dialog until it closes (tests pass their own).</param>
    /// <param name="showNotice">Shows the not-written notice (tests pass their own).</param>
    /// <returns>The move's result, or null when nothing was moved (cancelled, no songs).</returns>
    public static async Task<AlbumGroupResult?> RunAsync(IReadOnlyList<Track> tracks, ILibraryService library,
        Func<MoveToAlbumViewModel, Task> showDialog,
        Func<ConfirmationRequest, Task<ConfirmationResult>> showNotice)
    {
        var songs = tracks?.Where(t => t != null).Distinct().ToList() ?? new List<Track>();
        if (songs.Count == 0) return null;
        var vm = new MoveToAlbumViewModel(songs, library.Albums);
        MoveToAlbumTarget? target = null;
        vm.Confirmed += (_, t) => target = t;
        await showDialog(vm);
        if (target is null) return null;

        var result = await library.MoveToAlbumAsync(songs, target.Album, target.AlbumArtist);
        if (result.UnwrittenFiles.Count > 0)
            await showNotice(new ConfirmationRequest(
                Loc.T("MoveToAlbum.NotWritten", result.UnwrittenFiles.Count), Loc.T("MoveToAlbum.Title"),
                Details: result.UnwrittenFiles.Take(8).ToList(), InfoOnly: true));
        return result;
    }
}
