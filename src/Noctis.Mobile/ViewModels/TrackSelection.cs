using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// A song list's Select mode (Apple Music's): rows show a check circle and a tap ticks one
/// rather than playing it; the page's top bar says "N Selected" with Select All and Done, and
/// the shell's action bar above the tabs acts on the ticked songs (Add to Playlist, Play Next,
/// Add to Queue, Favorite, Download, and Remove from Playlist on a playlist's page).
/// <para>The ticked songs are a set of ids; rows learn of a change through
/// <see cref="Changed"/>, which names the one song toggled (null: every row), so a tick on a
/// list of thousands touches only the rows on screen and allocates nothing per row.</para>
/// </summary>
public sealed partial class TrackSelection : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly Func<IReadOnlyList<Track>> _rows;
    private readonly Func<string?> _source;
    private readonly HashSet<Guid> _ids = new();

    /// <param name="rows">The rows the page shows now, in order: Select All ticks them, and the
    /// actions take the ticked ones in this order.</param>
    /// <param name="source">The queue's "playing from" name for Play Next / Add to Queue.</param>
    /// <param name="playlist">The playlist whose page this is (Remove from Playlist), or null.</param>
    public TrackSelection(ShellViewModel shell, Func<IReadOnlyList<Track>> rows, Func<string?> source, Func<Playlist?>? playlist = null)
    {
        _shell = shell;
        _rows = rows;
        _source = source;
        _playlist = playlist ?? (() => null);
    }

    private readonly Func<Playlist?> _playlist;

    /// <summary>A row's tick changed (its song's id), or every row's (null): Select mode came
    /// or went, Select All, or the list changed under it.</summary>
    public event Action<Guid?>? Changed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText), nameof(HasSelection), nameof(AllSelected), nameof(SelectAllLabel),
        nameof(CanRemoveFromPlaylist), nameof(CanDownload), nameof(IsAllFavourite), nameof(FavouriteLabel))]
    private int _count;

    public bool HasSelection => Count > 0;

    /// <summary>"3 Selected", or Apple's "Select Items" before the first tick.</summary>
    public string HeaderText => Count == 0 ? Loc.T("Mobile.Select.Items") : Loc.T("Mobile.Select.Count", Count);

    public bool AllSelected
    {
        get
        {
            if (Count == 0) return false;
            foreach (var t in _rows())
                if (!_ids.Contains(t.Id)) return false;
            return true;
        }
    }

    public string SelectAllLabel => AllSelected ? Loc.T("Mobile.Select.DeselectAll") : Loc.T("Mobile.Select.All");

    public bool IsSelected(Track track) => _ids.Contains(track.Id);

    public bool IsSelected(Guid id) => _ids.Contains(id);

    /// <summary>Enters Select mode, with <paramref name="first"/> ticked (a song's menu › Select).</summary>
    public void Begin(Track? first = null)
    {
        _ids.Clear();
        if (first != null) _ids.Add(first.Id);
        Count = _ids.Count;
        if (!IsActive)
        {
            IsActive = true;
            _shell.BeginSelection(this);
        }
        Changed?.Invoke(null);
    }

    /// <summary>Done, Back, a tab switch or a page change: Select mode ends, every tick goes.</summary>
    [RelayCommand]
    public void End()
    {
        if (!IsActive) return;
        _ids.Clear();
        Count = 0;
        IsActive = false;
        _shell.EndSelection(this);
        Changed?.Invoke(null);
    }

    [RelayCommand]
    public void Toggle(Track? track)
    {
        if (track == null || !IsActive) return;
        if (!_ids.Remove(track.Id)) _ids.Add(track.Id);
        Count = _ids.Count;
        Changed?.Invoke(track.Id);
    }

    /// <summary>Select All, or Deselect All once every row is ticked.</summary>
    [RelayCommand]
    public void ToggleAll()
    {
        if (!IsActive) return;
        if (AllSelected) _ids.Clear();
        else
            foreach (var t in _rows()) _ids.Add(t.Id);
        Count = _ids.Count;
        Changed?.Invoke(null);
    }

    /// <summary>The page's rows changed (a refresh, a filter, a removal): ticks on songs no longer
    /// shown go, so the count and the actions match what is on screen.</summary>
    public void Prune()
    {
        if (!IsActive || _ids.Count == 0) return;
        var shown = new HashSet<Guid>();
        foreach (var t in _rows()) shown.Add(t.Id);
        if (_ids.RemoveWhere(id => !shown.Contains(id)) == 0)
        {
            OnPropertyChanged(nameof(AllSelected));
            OnPropertyChanged(nameof(SelectAllLabel));
            return;
        }
        Count = _ids.Count;
        Changed?.Invoke(null);
    }

    /// <summary>The ticked songs in the list's order (a song listed twice, once).</summary>
    public IReadOnlyList<Track> SelectedTracks
    {
        get
        {
            var seen = new HashSet<Guid>();
            var list = new List<Track>(_ids.Count);
            foreach (var t in _rows())
                if (_ids.Contains(t.Id) && seen.Add(t.Id)) list.Add(t);
            return list;
        }
    }

    // ── The action bar ──

    /// <summary>A playlist's page (not a smart playlist's: its songs come from its rules).</summary>
    public bool OffersRemoveFromPlaylist => _playlist() is { IsSmartPlaylist: false };

    public bool CanRemoveFromPlaylist => HasSelection && OffersRemoveFromPlaylist;

    /// <summary>Signed in to a computer: its songs among the ticked ones can be kept offline.</summary>
    public bool OffersDownload => _shell.Account is { IsSignedIn: true };

    public bool CanDownload => HasSelection && OffersDownload;

    public bool IsAllFavourite
    {
        get
        {
            if (!HasSelection) return false;
            foreach (var t in _rows())
                if (_ids.Contains(t.Id) && !t.IsFavorite) return false;
            return true;
        }
    }

    public string FavouriteLabel => IsAllFavourite ? Loc.T("Mobile.RemoveFromFavorites") : Loc.T("Mobile.Favorite");

    /// <summary>The playlist list on the bottom sheet (with New Playlist), for the ticked songs;
    /// Select mode ends once one is picked (the sheet keeps the songs).</summary>
    [RelayCommand]
    private void AddToPlaylist()
    {
        var tracks = SelectedTracks;
        if (tracks.Count == 0) return;
        _shell.ShowPlaylistPicker(tracks);
        End();
    }

    [RelayCommand]
    private void PlayNext()
    {
        var tracks = SelectedTracks;
        if (tracks.Count == 0) return;
        _shell.Player.PlayNext(tracks, _source());
        End();
    }

    [RelayCommand]
    private void AddToQueue()
    {
        var tracks = SelectedTracks;
        if (tracks.Count == 0) return;
        _shell.Player.AddToQueue(tracks, _source());
        End();
    }

    /// <summary>Favorite, or Remove from Favorites when every ticked song already is one.</summary>
    [RelayCommand]
    private async Task ToggleFavouriteAsync()
    {
        var tracks = SelectedTracks;
        if (tracks.Count == 0) return;
        var favourite = !tracks.All(t => t.IsFavorite);
        End();
        await _shell.SetFavouriteAsync(tracks, favourite);
    }

    /// <summary>The desktop's songs among the ticked ones that have no offline copy yet.</summary>
    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (_shell.Account is not { } account) return;
        var tracks = SelectedTracks.Where(t => account.IsRemote(t) && !account.IsDownloaded(t)).ToList();
        End();
        if (tracks.Count > 0) await _shell.DownloadTracksAsync(tracks);
    }

    [RelayCommand]
    private async Task RemoveFromPlaylistAsync()
    {
        var playlist = _playlist();
        var tracks = SelectedTracks;
        if (playlist == null || playlist.IsSmartPlaylist || tracks.Count == 0) return;
        End();
        await _shell.RemoveFromPlaylistAsync(playlist, tracks);
    }
}
