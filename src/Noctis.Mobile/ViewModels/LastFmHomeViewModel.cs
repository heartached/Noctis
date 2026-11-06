using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// One From Your Last.fm mix on a Home rail: its name (a Mobile.* key) and its songs, by id, in
/// the mix's order. A tap opens them as a song list; the ids are re-read from the library then,
/// so a rescan in between plays what the library holds now. Equal when the songs are.
/// </summary>
public sealed record LastFmMix(string TitleKey, IReadOnlyList<Guid> TrackIds)
{
    public bool Equals(LastFmMix? other) =>
        other is not null && TitleKey == other.TitleKey && TrackIds.SequenceEqual(other.TrackIds);

    public override int GetHashCode() => HashCode.Combine(TitleKey, TrackIds.Count, TrackIds.FirstOrDefault());
}

/// <summary>One Discover on Last.fm entry: an artist or album the library lacks. A tap opens its
/// Last.fm page in the browser, and nothing else (no store, no download).</summary>
public sealed record LastFmDiscoverEntry(LastFmDiscoverItem Item, string Title, string Detail)
{
    public static LastFmDiscoverEntry From(LastFmDiscoverItem item) => new(item, item.Name, item.Reason switch
    {
        LastFmDiscoverReason.SimilarTo => Loc.T("Mobile.LastFm.SimilarTo", string.Join(", ", item.Because.Take(2))),
        LastFmDiscoverReason.TopAlbumMissing => Loc.T("Mobile.LastFm.AlbumYouPlay", item.Artist ?? string.Empty),
        _ => Loc.T("Mobile.LastFm.ArtistYouPlay"),
    });

    public bool IsAlbum => Item.IsAlbum;
    public string Url => Item.Url;
    public string AccessibleName => Loc.T("Mobile.LastFm.OpenOnLastFm", Title);

    /// <summary>By what it shows and opens: every rebuild makes new items, and an unchanged list
    /// is not redrawn.</summary>
    public bool Equals(LastFmDiscoverEntry? other) =>
        other is not null && Url == other.Url && Title == other.Title && Detail == other.Detail;

    public override int GetHashCode() => HashCode.Combine(Url, Title, Detail);
}

/// <summary>
/// GitHub #177 on the phone's Home: From Your Last.fm (three mixes made only of library songs,
/// from the charts of the Last.fm account connected on this phone) and Discover on Last.fm
/// (artists and albums the library lacks, each opening its Last.fm page). Shown only while
/// Last.fm is connected here (<see cref="Services.Scrobbling.PhoneScrobbler"/>) and Settings ›
/// Scrobbling › Mixes and Recommendations is on; otherwise hidden and emptied. Reads need only
/// the account name; the charts are cached for 12 h (<see cref="LastFmTasteService"/>), so a
/// Home visit normally makes no request.
/// </summary>
public sealed partial class LastFmHomeViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly LastFmTasteService? _service;
    private LastFmTasteSnapshot? _builtFrom;
    private bool _needsRebuild = true;
    private int _generation;
    private bool _running;
    private bool _again;
    private string? _user;

    public LastFmHomeViewModel(ShellViewModel shell, LastFmTasteService? service)
    {
        _shell = shell;
        _service = service;
        if (service == null) return;
        shell.Scrobbler.Changed += (_, _) => shell.Marshal(OnScrobblerChanged);
        shell.Library.Refreshed += OnLibraryRefreshed;
        _user = ActiveUser;
    }

    /// <summary>The three mixes that have songs, as rail tiles.</summary>
    public BulkObservableCollection<RailItem> Mixes { get; } = new();

    public BulkObservableCollection<LastFmDiscoverEntry> Discover { get; } = new();

    /// <summary>The line under From Your Last.fm when there are no mixes (reading, an error, no
    /// matches) or the saved ones are shown after a failed read; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus), nameof(IsSectionVisible))]
    private string _status = string.Empty;

    public bool HasStatus => Status.Length > 0;

    /// <summary>A Last.fm account is connected on this phone, its name known, and the switch on.</summary>
    public bool IsActive => ActiveUser != null;

    public bool IsSectionVisible => IsActive && (Mixes.Count > 0 || HasStatus);

    public bool IsDiscoverVisible => IsActive && Discover.Count > 0;

    private string? ActiveUser
    {
        get
        {
            if (_service == null) return null;
            var scrobbler = _shell.Scrobbler;
            var user = scrobbler.LastFmUser?.Trim();
            return scrobbler.LastFmConnected && scrobbler.LastFmTasteEnabled && !string.IsNullOrEmpty(user) ? user : null;
        }
    }

    private void OnScrobblerChanged()
    {
        var user = ActiveUser;
        var signedOut = _user != null && !_shell.Scrobbler.LastFmConnected;
        if (user == _user && !signedOut) return;   // a queue or route change, not this section's
        _user = user;
        _needsRebuild = true;
        // Disconnected: the saved charts belong to that account; forget them.
        if (signedOut) _ = _service!.ClearAsync();
        _ = RefreshAsync(force: false);
    }

    /// <summary>A rescan or edit: the same charts matched against the library as it is now.</summary>
    private void OnLibraryRefreshed(object? sender, EventArgs e)
    {
        if (!IsActive || _builtFrom == null) return;
        _needsRebuild = true;
        if (_running) { _again = true; return; }
        _ = RefreshAsync(force: false);
    }

    /// <summary>The UI language changed: the tiles' names and the entries' lines are built in code.</summary>
    public void Relabel()
    {
        _needsRebuild = true;
        _ = RefreshAsync(force: false);
    }

    /// <summary>
    /// Brings the rows up to date: hidden and emptied while inactive; otherwise the service's
    /// snapshot matched against the library off the UI thread. A failed read keeps the last
    /// mixes and says so. Never throws.
    /// </summary>
    public async Task RefreshAsync(bool force)
    {
        var generation = ++_generation;
        var user = ActiveUser;
        if (user == null)
        {
            Clear();
            _running = false;
            Status = string.Empty;
            RaiseVisibility();
            return;
        }

        _running = true;
        if (Mixes.Count == 0) Status = Loc.T("Mobile.LastFm.Loading");
        RaiseVisibility();
        try
        {
            var result = await _service!.GetAsync(user, forceRefresh: force);
            if (generation != _generation) return;

            var snapshot = result.Snapshot;
            if (snapshot != null && (force || _needsRebuild || !ReferenceEquals(snapshot, _builtFrom)))
            {
                // What may be suggested: not snoozed (hidden from suggestions), not in the Inbox.
                var library = _shell.Library.Service.Tracks;
                var mixes = await Task.Run(() => LastFmTasteMixes.Build(
                    library.Where(t => !t.IsSnoozed && !t.IsInInbox).ToList(), snapshot));
                if (generation != _generation) return;
                _builtFrom = snapshot;
                _needsRebuild = false;
                Apply(mixes);
            }
            else if (snapshot == null)
            {
                Clear();
            }

            Status = result.Error switch
            {
                LastFmTasteError.UserNotFound => Loc.T("Mobile.LastFm.UserNotFound"),
                LastFmTasteError.Private => Loc.T("Mobile.LastFm.Private"),
                { } when snapshot != null => Loc.T("Mobile.LastFm.ShowingSaved"),
                { } => Loc.T("Mobile.LastFm.Unreachable"),
                null when snapshot != null && Mixes.Count == 0 => Loc.T("Mobile.LastFm.NoMatches"),
                _ => string.Empty,
            };
        }
        catch (Exception ex)
        {
            DebugLog.WriteOnce("Home", "lastfm-taste-failed:" + ex.GetType().FullName, $"Last.fm mixes failed: {ex.GetType().Name}");
            if (generation == _generation) Status = Loc.T("Mobile.LastFm.Unreachable");
        }
        finally
        {
            if (generation == _generation)
            {
                _running = false;
                RaiseVisibility();
                if (_again)
                {
                    _again = false;
                    _ = RefreshAsync(force: false);
                }
            }
        }
    }

    private void Apply(LastFmTasteMixSet mixes)
    {
        var tiles = new List<RailItem>(3);
        void Add(string key, IReadOnlyList<Track> tracks)
        {
            if (tracks.Count > 0) tiles.Add(RailItem.ForMix(new LastFmMix(key, tracks.Select(t => t.Id).ToList()), tracks));
        }
        Add("Mobile.LastFm.TopSongs", mixes.TopSongs);
        Add("Mobile.LastFm.Loved", mixes.Loved);
        Add("Mobile.LastFm.TopArtists", mixes.TopArtists);
        MobileLibrary.ReplaceIfChanged(Mixes, tiles);
        MobileLibrary.ReplaceIfChanged(Discover, mixes.Discover.Select(LastFmDiscoverEntry.From).ToList());
    }

    private void Clear()
    {
        _builtFrom = null;
        if (Mixes.Count > 0) Mixes.Clear();
        if (Discover.Count > 0) Discover.Clear();
    }

    private void RaiseVisibility()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsSectionVisible));
        OnPropertyChanged(nameof(IsDiscoverVisible));
    }

    /// <summary>Opens a Discover entry's Last.fm page in the browser; any other address is refused.</summary>
    [RelayCommand]
    private void OpenDiscover(LastFmDiscoverEntry? entry)
    {
        if (entry == null || !LastFmLinks.IsLastFmPage(entry.Url)) return;
        try { _shell.Links?.Open(entry.Url); }
        catch (Exception ex) { DebugLog.Write("Home", $"Last.fm page failed to open: {ex.GetType().Name}"); }
    }
}
