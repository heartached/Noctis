using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// One "From your Last.fm" mix on Home: the whole mix (what Play queues) and the first few
/// songs shown as pills. The commands play the WHOLE mix from the clicked song.
/// </summary>
public sealed partial class LastFmMixRow : ObservableObject
{
    /// <summary>Pills shown per mix (two rows of three, as the play-history rows).</summary>
    public const int ShownCount = 6;

    private readonly PlayerViewModel? _player;

    public LastFmMixRow(PlayerViewModel? player) => _player = player;

    /// <summary>The whole mix, in order.</summary>
    public IReadOnlyList<Track> All { get; private set; } = Array.Empty<Track>();

    /// <summary>The first <see cref="ShownCount"/> songs.</summary>
    public BulkObservableCollection<Track> Shown { get; } = new();

    /// <summary>Songs in the whole mix, for the header ("50 songs").</summary>
    [ObservableProperty] private int _count;

    internal void Replace(IReadOnlyList<Track> all)
    {
        var same = All.Count == all.Count;
        for (var i = 0; same && i < all.Count; i++) same = ReferenceEquals(All[i], all[i]);
        if (same) return;
        All = all;
        Count = all.Count;
        HomeViewModel.ReplaceRowIfChanged(Shown, all.Take(ShownCount).ToList());
    }

    [RelayCommand]
    private void Play(Track? track)
    {
        if (_player == null || All.Count == 0) return;
        var index = track == null ? 0 : Math.Max(0, IndexOf(track));
        _player.ReplaceQueueAndPlay(All.ToList(), index);
    }

    /// <summary>The Play / Pause over a pill's art: pauses or resumes the playing song, else plays the mix from it.</summary>
    [RelayCommand]
    private void TogglePlay(Track? track)
    {
        if (track is null || _player == null) return;
        if (track.IsNowPlaying) { _player.PlayPauseCommand.Execute(null); return; }
        Play(track);
    }

    [RelayCommand]
    private void Shuffle(Track? _)
    {
        if (_player == null || All.Count == 0) return;
        _player.PlayShuffled(All.ToList());
    }

    private int IndexOf(Track track)
    {
        for (var i = 0; i < All.Count; i++)
            if (ReferenceEquals(All[i], track)) return i;
        return -1;
    }
}

/// <summary>One Discover entry on Home: an artist or album to look up on Last.fm.</summary>
public sealed class LastFmDiscoverRow
{
    public LastFmDiscoverRow(LastFmDiscoverItem item)
    {
        Item = item;
        Title = item.Name;
        Detail = item.Reason switch
        {
            LastFmDiscoverReason.SimilarTo => Loc.T("Home.LastFm.SimilarTo", string.Join(", ", item.Because.Take(2))),
            LastFmDiscoverReason.TopAlbumMissing => Loc.T("Home.LastFm.AlbumYouPlay", item.Artist ?? ""),
            _ => Loc.T("Home.LastFm.ArtistYouPlay"),
        };
        Glyph = item.IsAlbum ? "♪" : "★";
    }

    public LastFmDiscoverItem Item { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Glyph { get; }
    public string Url => Item.Url;
}

// GitHub #177: "From your Last.fm" (mixes of library songs from the connected account's charts)
// and "Discover on Last.fm" (artists/albums the library lacks, linking out to Last.fm only).
public partial class HomeViewModel
{
    private LastFmTasteService? _lastFmTaste;
    private LastFmTasteSnapshot? _lastFmBuiltFrom;
    private bool _lastFmNeedsRebuild = true;
    private int _lastFmGeneration;

    /// <summary>Your Last.fm Top Songs (user.getTopTracks, last 6 months).</summary>
    public LastFmMixRow LastFmTopSongs { get; private set; } = new(null);

    /// <summary>Loved on Last.fm (user.getLovedTracks).</summary>
    public LastFmMixRow LastFmLoved { get; private set; } = new(null);

    /// <summary>Songs by the artists you play most on Last.fm.</summary>
    public LastFmMixRow LastFmArtists { get; private set; } = new(null);

    public BulkObservableCollection<LastFmDiscoverRow> LastFmDiscover { get; } = new();

    [ObservableProperty] private bool _isLastFmExpanded = true;
    [ObservableProperty] private bool _isLastFmDiscoverExpanded = true;
    [ObservableProperty] private bool _isLastFmRefreshing;

    /// <summary>The line under the section title: what the mixes are, or why they're missing / old.</summary>
    [ObservableProperty] private string _lastFmStatus = string.Empty;

    /// <summary>A Last.fm account is connected (its name known) and the Settings toggle is on.</summary>
    public bool IsLastFmActive => _lastFmTaste != null && _settings is { IsLastFmConnected: true, LastFmTasteEnabled: true }
                                  && !string.IsNullOrWhiteSpace(LastFmUser);

    /// <summary>The section shows while active: with mixes, or with the line saying why there are none.</summary>
    public bool IsLastFmSectionVisible => IsLastFmActive;

    public bool IsLastFmDiscoverVisible => IsLastFmActive && LastFmDiscover.Count > 0;

    public bool HasLastFmMixes => LastFmTopSongs.Count > 0 || LastFmLoved.Count > 0 || LastFmArtists.Count > 0;

    private string? LastFmUser => _settings?.LastFmUsername;

    /// <summary>Called by the constructor: wires the service and the settings that switch the section.</summary>
    private void InitLastFm(LastFmTasteService? service)
    {
        _lastFmTaste = service;
        LastFmTopSongs = new LastFmMixRow(_player);
        LastFmLoved = new LastFmMixRow(_player);
        LastFmArtists = new LastFmMixRow(_player);
        LastFmDiscover.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsLastFmDiscoverVisible));
        if (_settings == null) return;
        IsLastFmExpanded = _settings.HomeLastFmExpanded;
        IsLastFmDiscoverExpanded = _settings.HomeLastFmDiscoverExpanded;
        _settings.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(SettingsViewModel.IsLastFmConnected):
                case nameof(SettingsViewModel.LastFmUsername):
                case nameof(SettingsViewModel.LastFmTasteEnabled):
                    _lastFmNeedsRebuild = true;
                    RaiseLastFmVisibility();
                    _ = RefreshLastFmAsync(force: false);
                    break;
                case nameof(SettingsViewModel.HomeLastFmExpanded):
                case nameof(SettingsViewModel.HomeLastFmDiscoverExpanded):
                    _adoptingPersistedState = true;
                    try
                    {
                        IsLastFmExpanded = _settings.HomeLastFmExpanded;
                        IsLastFmDiscoverExpanded = _settings.HomeLastFmDiscoverExpanded;
                    }
                    finally { _adoptingPersistedState = false; }
                    break;
            }
        };
    }

    partial void OnIsLastFmExpandedChanged(bool value)
    {
        if (_settings != null && !_adoptingPersistedState) _settings.HomeLastFmExpanded = value;
    }

    partial void OnIsLastFmDiscoverExpandedChanged(bool value)
    {
        if (_settings != null && !_adoptingPersistedState) _settings.HomeLastFmDiscoverExpanded = value;
    }

    private void RaiseLastFmVisibility()
    {
        OnPropertyChanged(nameof(IsLastFmActive));
        OnPropertyChanged(nameof(IsLastFmSectionVisible));
        OnPropertyChanged(nameof(IsLastFmDiscoverVisible));
        OnPropertyChanged(nameof(HasLastFmMixes));
    }

    /// <summary>The section's Refresh: reads Last.fm again even when the cached charts are fresh.</summary>
    [RelayCommand]
    private Task RefreshLastFm() => RefreshLastFmAsync(force: true);

    /// <summary>Opens a Discover entry's Last.fm page in the browser (nothing else is linked).</summary>
    [RelayCommand]
    private void OpenLastFmPage(LastFmDiscoverRow? row)
    {
        if (row == null || !LastFmLinks.IsLastFmPage(row.Url)) return;
        try { PlatformHelper.OpenUrl(row.Url); }
        catch (Exception ex) { DebugLog.Write("Home", $"Last.fm page failed to open: {ex.GetType().Name}"); }
    }

    /// <summary>
    /// Brings the Last.fm rows up to date: hidden and emptied when inactive; otherwise the
    /// service's snapshot (cached for 12 h, so a Home visit normally costs no request) matched
    /// against the library off the UI thread. A failed read keeps the last mixes and says so.
    /// </summary>
    internal async Task RefreshLastFmAsync(bool force)
    {
        var generation = ++_lastFmGeneration;
        if (!IsLastFmActive)
        {
            ClearLastFmRows();
            IsLastFmRefreshing = false;
            LastFmStatus = string.Empty;
            RaiseLastFmVisibility();
            UpdateEmptyLibraryState();
            return;
        }

        var user = LastFmUser!.Trim();
        IsLastFmRefreshing = force || !HasLastFmMixes;
        if (!HasLastFmMixes) LastFmStatus = Loc.T("Home.LastFm.Loading");
        try
        {
            var result = await _lastFmTaste!.GetAsync(user, forceRefresh: force);
            if (generation != _lastFmGeneration) return;

            var snapshot = result.Snapshot;
            if (snapshot != null && (force || _lastFmNeedsRebuild || !ReferenceEquals(snapshot, _lastFmBuiltFrom)))
            {
                // What may be suggested: not snoozed (hidden from suggestions), not in the Inbox.
                var library = _library.Tracks;
                var mixes = await Task.Run(() => LastFmTasteMixes.Build(
                    library.Where(t => !t.IsSnoozed && !t.IsInInbox).ToList(), snapshot));
                if (generation != _lastFmGeneration) return;
                _lastFmBuiltFrom = snapshot;
                _lastFmNeedsRebuild = false;
                LastFmTopSongs.Replace(mixes.TopSongs);
                LastFmLoved.Replace(mixes.Loved);
                LastFmArtists.Replace(mixes.TopArtists);
                ReplaceDiscoverIfChanged(mixes.Discover);
            }
            else if (snapshot == null)
            {
                ClearLastFmRows();
            }

            LastFmStatus = result.Error switch
            {
                LastFmTasteError.UserNotFound => Loc.T("Home.LastFm.Error.UserNotFound"),
                LastFmTasteError.Private => Loc.T("Home.LastFm.Error.Private"),
                { } when snapshot != null => Loc.T("Home.LastFm.Error.ShowingSaved"),
                { } => Loc.T("Home.LastFm.Error.Unreachable"),
                null when snapshot != null && !HasLastFmMixes => Loc.T("Home.LastFm.NoMatches"),
                _ => Loc.T("Home.LastFm.Subtitle"),
            };
        }
        catch (Exception ex)
        {
            DebugLog.WriteOnce("Home", "lastfm-taste-failed:" + ex.GetType().FullName, $"Last.fm mixes failed: {ex}");
            if (generation == _lastFmGeneration) LastFmStatus = Loc.T("Home.LastFm.Error.Unreachable");
        }
        finally
        {
            if (generation == _lastFmGeneration)
            {
                IsLastFmRefreshing = false;
                RaiseLastFmVisibility();
                UpdateEmptyLibraryState();
            }
        }
    }

    private void ClearLastFmRows()
    {
        _lastFmBuiltFrom = null;
        LastFmTopSongs.Replace(Array.Empty<Track>());
        LastFmLoved.Replace(Array.Empty<Track>());
        LastFmArtists.Replace(Array.Empty<Track>());
        if (LastFmDiscover.Count > 0) LastFmDiscover.Clear();
    }

    private void ReplaceDiscoverIfChanged(IReadOnlyList<LastFmDiscoverItem> items)
    {
        if (LastFmDiscover.Count == items.Count && LastFmDiscover.Zip(items).All(p => p.First.Item == p.Second))
            return;
        LastFmDiscover.ReplaceAll(items.Select(i => new LastFmDiscoverRow(i)).ToList());
    }
}
