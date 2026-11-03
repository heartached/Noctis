using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Localization;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Settings › Integrations › "Import play counts" under Last.fm and ListenBrainz (GitHub #160).
/// One-way: the service's listening history is read and Noctis's play counts are raised to it
/// (or set to it). See <see cref="PlayCountHistoryFetcher"/> / <see cref="PlayCountImportMatcher"/>.
/// The counts are written as user state only — no play-history event, no scrobble.
/// </summary>
public partial class SettingsViewModel
{
    private CancellationTokenSource? _playCountImportCts;

    /// <summary>The service an import is running for; null when none is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlayCountImportRunning), nameof(IsLastFmImportRunning), nameof(IsListenBrainzImportRunning))]
    [NotifyCanExecuteChangedFor(nameof(ImportLastFmPlayCountsCommand), nameof(ImportListenBrainzPlayCountsCommand))]
    private PlayCountImportSource? _playCountImportActive;

    public bool IsPlayCountImportRunning => PlayCountImportActive != null;
    public bool IsLastFmImportRunning => PlayCountImportActive == PlayCountImportSource.LastFm;
    public bool IsListenBrainzImportRunning => PlayCountImportActive == PlayCountImportSource.ListenBrainz;

    /// <summary>Progress, then the summary (or why it stopped), under each service.</summary>
    [ObservableProperty] private string _lastFmImportStatus = "";
    [ObservableProperty] private string _listenBrainzImportStatus = "";

    /// <summary>Keep the higher count (default) or replace with the imported one; this session only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPlayCountImportModeOption))]
    private PlayCountImportMode _playCountImportMode = PlayCountImportMode.KeepHigher;

    private IReadOnlyList<LyricsChoiceOption>? _playCountImportModeOptions;

    public IReadOnlyList<LyricsChoiceOption> PlayCountImportModeOptions
    {
        get
        {
            if (_playCountImportModeOptions == null)
            {
                _playCountImportModeOptions = BuildPlayCountImportModeOptions();
                Loc.Instance.CultureChanged += (_, _) =>
                {
                    _playCountImportModeOptions = BuildPlayCountImportModeOptions();
                    OnPropertyChanged(nameof(PlayCountImportModeOptions));
                    OnPropertyChanged(nameof(SelectedPlayCountImportModeOption));
                };
            }
            return _playCountImportModeOptions;
        }
    }

    private static IReadOnlyList<LyricsChoiceOption> BuildPlayCountImportModeOptions() =>
        Enum.GetValues<PlayCountImportMode>()
            .Select(m => new LyricsChoiceOption(m.ToString(), Loc.T($"Settings.PlayCountImport.Mode.{m}")))
            .ToList();

    public LyricsChoiceOption? SelectedPlayCountImportModeOption
    {
        get => PlayCountImportModeOptions.FirstOrDefault(o => o.Value == PlayCountImportMode.ToString());
        set
        {
            if (value != null && Enum.TryParse<PlayCountImportMode>(value.Value, out var mode))
                PlayCountImportMode = mode;
        }
    }

    private bool CanImportPlayCounts() => PlayCountImportActive == null;

    [RelayCommand(CanExecute = nameof(CanImportPlayCounts))]
    private Task ImportLastFmPlayCounts() => ImportPlayCountsAsync(PlayCountImportSource.LastFm);

    [RelayCommand(CanExecute = nameof(CanImportPlayCounts))]
    private Task ImportListenBrainzPlayCounts() => ImportPlayCountsAsync(PlayCountImportSource.ListenBrainz);

    [RelayCommand]
    private void CancelPlayCountImport() => _playCountImportCts?.Cancel();

    private void SetPlayCountImportStatus(PlayCountImportSource source, string text)
    {
        if (source == PlayCountImportSource.LastFm) LastFmImportStatus = text;
        else ListenBrainzImportStatus = text;
    }

    private static string N(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>UI thread. Fetching and matching run off it; only the count writes come back.</summary>
    private async Task ImportPlayCountsAsync(PlayCountImportSource source)
    {
        if (PlayCountImportActive != null) return;
        var lastFm = source == PlayCountImportSource.LastFm;
        var service = lastFm ? "Last.fm" : "ListenBrainz";
        var user = lastFm
            ? (string.IsNullOrWhiteSpace(_lastFm?.Username) ? LastFmUsername : _lastFm!.Username)
            : (string.IsNullOrWhiteSpace(_listenBrainz?.Username) ? ListenBrainzUsername : _listenBrainz!.Username);
        var http = App.Services?.GetService<HttpClient>();
        if (string.IsNullOrWhiteSpace(user) || http == null)
        {
            SetPlayCountImportStatus(source, Loc.T("Settings.PlayCountImport.NotConnected", service));
            return;
        }

        var cts = new CancellationTokenSource();
        _playCountImportCts = cts;
        PlayCountImportActive = source;
        SetPlayCountImportStatus(source, Loc.T("Settings.PlayCountImport.Starting"));
        var mode = PlayCountImportMode;
        // Created here, on the UI thread: reports post back to it.
        var progress = new Progress<PlayCountFetchProgress>(p => SetPlayCountImportStatus(source, p.Total > 0
            ? Loc.T("Settings.PlayCountImport.ReadingOf", N(Math.Min(p.Listens, p.Total)), N(p.Total))
            : Loc.T("Settings.PlayCountImport.Reading", N(p.Listens))));
        try
        {
            var fetcher = new PlayCountHistoryFetcher(http);
            var apiUrl = _listenBrainz?.ApiUrl;
            var token = ListenBrainzToken;
            var history = await Task.Run(() => fetcher.FetchAsync(source, user!, apiUrl, token, progress, cts.Token), cts.Token);

            SetPlayCountImportStatus(source, Loc.T("Settings.PlayCountImport.Matching"));
            var tracks = _library.Tracks;
            var match = await Task.Run(() => PlayCountImportMatcher.Match(tracks, history), cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            // Back on the UI thread: PlayCount is observable, and a play counted meanwhile is
            // read now, so "keep the higher" never undoes it.
            var changed = PlayCountImportMatcher.Apply(match, mode);
            if (changed.Count > 0)
            {
                await _library.SaveTrackUserStateAsync(changed);
                _library.NotifyMetadataChanged(); // Songs/Albums/Home "Most Played" re-rank
            }
            DebugLog.Write("PlayCountImport",
                $"{service}: {match.SourceEntries} entries, {match.MatchedEntries} matched ({match.MatchedPlays} plays), " +
                $"{match.UnmatchedEntries} unmatched ({match.UnmatchedPlays} plays), {changed.Count} songs changed, mode {mode}");
            SetPlayCountImportStatus(source, Loc.T("Settings.PlayCountImport.Done",
                N(changed.Count), N(match.MatchedEntries), N(match.UnmatchedEntries)));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SetPlayCountImportStatus(source, Loc.T("Settings.PlayCountImport.Cancelled"));
        }
        catch (PlayCountImportException ex)
        {
            DebugLog.Write("PlayCountImport", $"{service}: {ex.Kind} {ex.Message}");
            SetPlayCountImportStatus(source, Loc.T($"Settings.PlayCountImport.Error.{ex.Kind}", service));
        }
        catch (Exception ex)
        {
            DebugLog.Write("PlayCountImport", $"{service}: failed: {ex.GetType().Name}: {ex.Message}");
            SetPlayCountImportStatus(source, Loc.T("Settings.PlayCountImport.Error.BadReply", service));
        }
        finally
        {
            if (ReferenceEquals(_playCountImportCts, cts)) _playCountImportCts = null;
            cts.Dispose();
            PlayCountImportActive = null;
        }
    }
}
