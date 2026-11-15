using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.MetadataSearch;

namespace Noctis.ViewModels;

// GitHub #222: "Last.fm" as the Statistics page's source, next to "This Library" (the play log).
// One source at a time, never merged: plays made here and scrobbled would count twice. With
// Last.fm the summary, Top Artists / Albums / Songs and the period come from the connected
// account (LastFmStatsService: read-only GETs, cached 1 h on disk); what Last.fm can't answer
// (listening time, streaks, a calendar year, the hour-by-hour Play History) is shown as not
// available instead of as a number. The Library card stays the library's.
public partial class StatisticsViewModel
{
    public const string SourceLibrary = "Library";
    public const string SourceLastFm = "LastFm";

    private SettingsViewModel? _settings;
    private LastFmStatsService? _lastFmStats;
    private CancellationTokenSource? _lastFmCts;
    private bool _pageActive;
    private bool _forceNextLastFm;

    /// <summary>A linked Top Artists portrait was clicked (the library artist's name).</summary>
    public event EventHandler<string>? ArtistOpened;

    /// <summary>A linked Top Albums / Top Songs row was clicked (the library album).</summary>
    public event EventHandler<Album>? AlbumOpened;

    /// <summary>A Last.fm read is running (the source pill shows it).</summary>
    [ObservableProperty] private bool _isLastFmLoading;

    /// <summary>Loading / offline / error line over the Overview; empty when all is well.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastFmStatus))]
    private string _lastFmStatusText = string.Empty;

    /// <summary>The status line offers Try Again (a failed read).</summary>
    [ObservableProperty] private bool _canRetryLastFm;

    public bool HasLastFmStatus => LastFmStatusText.Length > 0;

    private string LastFmUser => (_settings?.LastFmUsername ?? string.Empty).Trim();

    /// <summary>The source choice shows only while a Last.fm account is connected.</summary>
    public bool IsLastFmSourceAvailable => _lastFmStats != null && _settings is { IsLastFmConnected: true }
                                           && LastFmUser.Length > 0;

    /// <summary>The figures come from Last.fm: chosen, and an account is connected. Without one
    /// the page is the library's, whatever was chosen (the choice is kept for a reconnect).</summary>
    public bool IsLastFmSource => IsLastFmSourceAvailable && _settings!.StatisticsSource == SourceLastFm;

    public bool IsLibrarySourceSelected
    {
        get => !IsLastFmSource;
        set { if (value) SetSource(SourceLibrary); }
    }

    public bool IsLastFmSourceSelected
    {
        get => IsLastFmSource;
        set { if (value) SetSource(SourceLastFm); }
    }

    private void SetSource(string source)
    {
        if (_settings == null || _settings.StatisticsSource == source) return;
        _settings.StatisticsSource = source; // saved by Settings; the handler below refreshes
    }

    private void InitLastFm(SettingsViewModel? settings, LastFmStatsService? service)
    {
        _settings = settings;
        _lastFmStats = service;
        if (settings == null) return;
        settings.PropertyChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SettingsViewModel.IsLastFmConnected)
            or nameof(SettingsViewModel.LastFmUsername) or nameof(SettingsViewModel.StatisticsSource))) return;
        if (Dispatcher.UIThread.CheckAccess()) OnSourceInputsChanged();
        else Dispatcher.UIThread.Post(OnSourceInputsChanged);
    }

    private void OnSourceInputsChanged()
    {
        OnPropertyChanged(nameof(IsLastFmSourceAvailable));
        OnPropertyChanged(nameof(IsLastFmSource));
        OnPropertyChanged(nameof(IsLibrarySourceSelected));
        OnPropertyChanged(nameof(IsLastFmSourceSelected));

        if (IsLastFmSource)
        {
            // Play History is the play log's (hours, skips, every play): Overview it is.
            if (SelectedTab == TabHistory) SelectedTab = TabOverview;
            // Last.fm has no calendar-year chart; its period change refreshes.
            if (Period == ListeningPeriod.ThisYear)
            {
                Period = ListeningPeriod.Last30Days;
                return;
            }
        }
        if (_pageActive) Refresh();
        else CancelLastFm();
    }

    /// <summary>The page left the screen: a running Last.fm read stops.</summary>
    public void OnPageLeft()
    {
        _pageActive = false;
        CancelLastFm();
    }

    private void CancelLastFm()
    {
        _lastFmCts?.Cancel();
        _lastFmCts?.Dispose();
        _lastFmCts = null;
        IsLastFmLoading = false;
    }

    [RelayCommand]
    private void RetryLastFm()
    {
        _forceNextLastFm = true;
        Refresh();
    }

    [RelayCommand]
    private void OpenArtist(StatsArtistTile? tile)
    {
        if (tile is { IsLinked: true }) ArtistOpened?.Invoke(this, tile.Name);
    }

    [RelayCommand]
    private void OpenRankRow(StatsRankRow? row)
    {
        if (row is not { IsLinked: true }) return;
        var album = _library.Albums.FirstOrDefault(a => a.Id == row.AlbumId);
        if (album != null) AlbumOpened?.Invoke(this, album);
    }

    /// <summary>The Last.fm chart period for a page period; null for This Year (Last.fm has none).</summary>
    internal static string? ToLastFmPeriod(ListeningPeriod period) => period switch
    {
        ListeningPeriod.Last7Days => LastFmPeriod.SevenDays,
        ListeningPeriod.Last30Days => LastFmPeriod.OneMonth,
        ListeningPeriod.AllTime => LastFmPeriod.Overall,
        _ => null,
    };

    internal sealed record LastFmRequest(string User, ListeningPeriod Period, bool Force, CancellationToken Token);

    /// <summary>Called on the UI thread by every refresh: stops an older read and says whether
    /// (and what) to read from Last.fm this time.</summary>
    private LastFmRequest? BeginLastFmRefresh()
    {
        _pageActive = true;
        CancelLastFm();
        var force = _forceNextLastFm;
        _forceNextLastFm = false;
        if (!IsLastFmSource)
        {
            LastFmStatusText = string.Empty;
            CanRetryLastFm = false;
            return null;
        }
        _lastFmCts = new CancellationTokenSource();
        return new LastFmRequest(LastFmUser, Period, force, _lastFmCts.Token);
    }

    private async Task RefreshLastFmAsync(LastFmRequest req, int generation, IReadOnlyList<Track> tracks,
        IReadOnlyList<Album> albums, IReadOnlyList<Artist> artists, ArtistImageService? images, bool force)
    {
        var ct = req.Token;
        force |= req.Force;
        bool Current() => generation == _refreshGeneration && !ct.IsCancellationRequested;
        Task OnUi(Action action) => Dispatcher.UIThread.InvokeAsync(() => { if (Current()) action(); }).GetTask();

        var period = ToLastFmPeriod(req.Period);
        if (period == null)
        {
            await OnUi(() => ShowLastFmEmpty(Loc.T("Stats.LastFm.NoYear"), retry: false));
            return;
        }

        var service = _lastFmStats!;
        try
        {
            await OnUi(() => IsLastFmLoading = true);
            var cached = await service.PeekAsync(req.User, period, ct).ConfigureAwait(false);
            var fresh = cached != null && service.IsFresh(cached);
            if (cached != null)
            {
                var r = await Task.Run(() => BuildLastFm(cached, req.Period, tracks, albums, artists, images, DateTime.Now), ct)
                    .ConfigureAwait(false);
                await OnUi(() =>
                {
                    ApplyListening(r);
                    LastFmStatusText = fresh && !force ? string.Empty : Loc.T("Stats.LastFm.Updating");
                    CanRetryLastFm = false;
                });
            }
            else
            {
                await OnUi(() => ShowLastFmEmpty(Loc.T("Stats.LastFm.Loading"), retry: false));
            }
            if (fresh && !force) return;

            var result = await service.GetAsync(req.User, period, force, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var snapshot = result.Snapshot;
            if (snapshot != null && !ReferenceEquals(snapshot, cached))
            {
                var r = await Task.Run(() => BuildLastFm(snapshot, req.Period, tracks, albums, artists, images, DateTime.Now), ct)
                    .ConfigureAwait(false);
                await OnUi(() => ApplyListening(r));
            }

            var status = result.Error switch
            {
                LastFmTasteError.UserNotFound => Loc.T("Stats.LastFm.UserNotFound", req.User),
                LastFmTasteError.Private => Loc.T("Stats.LastFm.Private"),
                LastFmTasteError.BadReply => Loc.T("Stats.LastFm.BadReply"),
                { } when snapshot != null => Loc.T("Stats.LastFm.ShowingSaved",
                    snapshot.FetchedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
                { } => Loc.T("Stats.LastFm.Unreachable"),
                null => string.Empty,
            };
            await OnUi(() =>
            {
                if (snapshot == null) ShowLastFmEmpty(status, retry: true);
                LastFmStatusText = status;
                CanRetryLastFm = result.Error != null;
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Page left, or the source / period changed: the newer refresh owns the page.
        }
        catch (Exception ex)
        {
            DebugLog.WriteOnce("Statistics", "lastfm-stats-failed:" + ex.GetType().FullName, $"Last.fm statistics failed: {ex}");
            await OnUi(() =>
            {
                LastFmStatusText = Loc.T("Stats.LastFm.Unreachable");
                CanRetryLastFm = true;
            });
        }
        finally
        {
            await OnUi(() => IsLastFmLoading = false);
        }
    }

    /// <summary>No Last.fm figures to show (loading, failed, This Year): no lists, no numbers, and
    /// no "nothing logged yet" card either; the status line says why.</summary>
    private void ShowLastFmEmpty(string status, bool retry)
    {
        var empty = new StatisticsResult { EmptyTitle = "", EmptyText = "" };
        ApplyListening(empty);
        IsEmptyNoteVisible = false;
        LogCoverageText = Loc.T("Stats.LastFm.CoverageNoDate", LastFmUser);
        LastFmStatusText = status;
        CanRetryLastFm = retry;
    }

    /// <summary>
    /// The page's listening figures from one Last.fm snapshot, with every artist, album and song
    /// the library has linked to it (its artwork, a click opens it); the rest shown as plain text.
    /// Pure, so it runs off the UI thread and in tests.
    /// </summary>
    internal static StatisticsResult BuildLastFm(LastFmStatsSnapshot s, ListeningPeriod period,
        IReadOnlyList<Track> tracks, IReadOnlyList<Album> albums, IReadOnlyList<Artist> artists,
        ArtistImageService? images, DateTime nowLocal)
    {
        var r = new StatisticsResult();
        var plays = s.Scrobbles ?? 0;
        r.HasPlayHistory = plays > 0 || s.TopArtists.Count > 0 || s.TopTracks.Count > 0;
        r.HasPeriodPlays = r.HasPlayHistory;
        r.EmptyTitle = Loc.T("Stats.LastFm.EmptyTitle");
        r.EmptyText = Loc.T("Stats.LastFm.EmptyText", s.User);

        r.PeriodCaption = period switch
        {
            ListeningPeriod.Last7Days => Loc.T("Stats.RangeLast7Days"),
            ListeningPeriod.Last30Days => Loc.T("Stats.RangeLast30Days"),
            _ => s.RegisteredUtc is { } reg
                ? Loc.T("Stats.RangeAllTimeSince", ShortDate(reg.LocalDateTime))
                : Loc.T("Stats.RangeAllTime"),
        };
        r.LogCoverageText = s.RegisteredUtc is { } since
            ? Loc.T("Stats.LastFm.Coverage", s.User, ShortDate(since.LocalDateTime))
            : Loc.T("Stats.LastFm.CoverageNoDate", s.User);

        r.PlaysText = s.Scrobbles is { } n ? Count(n) : "—";
        r.PlaysTip = Loc.T("Stats.LastFm.PlaysTip", s.User);
        if (s.Scrobbles is { } now && s.PreviousScrobbles is { } previous && (previous > 0 || now > 0))
        {
            r.HasDelta = true;
            r.DeltaCaption = period == ListeningPeriod.Last7Days ? Loc.T("Stats.VsPrevious7Days") : Loc.T("Stats.VsPrevious30Days");
            r.DeltaTip = Loc.T("Stats.DeltaTip", Count(now), Count(previous));
            (r.DeltaText, r.IsDeltaUp, r.IsDeltaDown) = FormatDelta(Clamp(now), Clamp(previous));
        }
        else
        {
            r.DeltaCaption = r.PeriodCaption;
        }

        // Last.fm keeps no song lengths per scrobble and no per-day series in these reads.
        r.ListeningTimeText = "—";
        r.ListeningDetailText = Loc.T("Stats.LastFm.NoListeningTime");
        r.StreakText = "—";
        r.LongestStreakText = Loc.T("Stats.LastFm.NoStreak");
        r.UniqueArtistsText = s.ArtistCount is { } ac ? Count(ac) : "—";
        r.UniqueTracksText = s.TrackCount is { } tc
            ? (tc == 1 ? Loc.T("Stats.SongCountOne") : Loc.T("Stats.SongCountMany", Count(tc)))
            : string.Empty;

        // ── Library matches ──
        var artistByKey = new Dictionary<string, Artist>(StringComparer.Ordinal);
        foreach (var a in artists)
        {
            var key = MatchText.Fold(a.Name);
            if (key.Length > 0) artistByKey.TryAdd(key, a);
        }
        var albumByKey = new Dictionary<string, Album>(StringComparer.Ordinal);
        foreach (var a in albums)
        {
            var name = LastFmTasteMixes.AlbumKey(a.Name);
            if (name.Length == 0) continue;
            foreach (var k in LastFmTasteMixes.ArtistNameKeys(a.Artist)) albumByKey.TryAdd(k + "\u001f" + name, a);
        }
        var songs = new LibrarySongIndex(tracks);

        r.TopArtists = s.TopArtists.Select((a, i) =>
        {
            artistByKey.TryGetValue(MatchText.Fold(a.Name), out var artist);
            return new StatsArtistTile
            {
                Rank = i + 1,
                Name = artist?.Name ?? a.Name,
                PlaysText = PlaysLabel(a.PlayCount),
                ImagePath = artist != null ? CachedArtistPhoto(artist, images) : null,
                IsLinked = artist != null,
            };
        }).ToList();

        var albumMax = s.TopAlbums.Count > 0 ? s.TopAlbums.Max(a => a.PlayCount) : 0;
        r.TopAlbums = s.TopAlbums.Select((a, i) =>
        {
            var name = LastFmTasteMixes.AlbumKey(a.Name);
            Album? album = null;
            if (name.Length > 0)
                foreach (var k in LastFmTasteMixes.ArtistNameKeys(a.Artist))
                    if (albumByKey.TryGetValue(k + "\u001f" + name, out album)) break;
            return new StatsRankRow
            {
                Rank = i + 1,
                Title = album?.Name ?? a.Name,
                Subtitle = album?.Artist ?? a.Artist,
                PlaysText = PlaysLabel(a.PlayCount),
                ArtworkPath = NonEmpty(album?.ArtworkPath),
                Fraction = albumMax > 0 ? (double)a.PlayCount / albumMax : 0,
                AlbumId = album?.Id ?? Guid.Empty,
                IsLinked = album != null,
            };
        }).ToList();

        var trackMax = s.TopTracks.Count > 0 ? s.TopTracks.Max(t => t.PlayCount) : 0;
        r.TopTracks = s.TopTracks.Select((t, i) =>
        {
            var track = songs.Find(t.Artist, t.Title);
            var linked = track != null && track.AlbumId != Guid.Empty;
            return new StatsRankRow
            {
                Rank = i + 1,
                Title = track?.Title ?? t.Title,
                Subtitle = track?.Artist ?? t.Artist,
                PlaysText = PlaysLabel(t.PlayCount),
                ArtworkPath = NonEmpty(track?.AlbumArtworkPath),
                Fraction = trackMax > 0 ? (double)t.PlayCount / trackMax : 0,
                AlbumId = linked ? track!.AlbumId : Guid.Empty,
                IsLinked = linked,
            };
        }).ToList();

        DebugLog.Write("Statistics",
            $"Last.fm {s.Period}: {r.TopArtists.Count(a => a.IsLinked)}/{r.TopArtists.Count} artists, " +
            $"{r.TopAlbums.Count(a => a.IsLinked)}/{r.TopAlbums.Count} albums, " +
            $"{r.TopTracks.Count(t => t.IsLinked)}/{r.TopTracks.Count} songs in the library");
        return r;
    }

    private static int Clamp(long n) => n > int.MaxValue ? int.MaxValue : (int)n;
}
