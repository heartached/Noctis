using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Settings › Integrations › "Import play counts" under Last.fm and ListenBrainz (GitHub #160).
/// One-way: the service's listening history is read and Noctis's play counts are raised to it
/// (or set to it). See <see cref="PlayCountHistoryFetcher"/> / <see cref="PlayCountImportMatcher"/>.
/// The counts are written as user state only — no play-history event, no scrobble.
/// <para>"Keep play counts in sync" (off by default) runs the same import in the background on
/// every startup (GitHub #182), when it is turned on and when the account is connected, reading
/// only what was scrobbled since the last run (<see cref="PlayCountSync"/>); the whole history is
/// read only the first time, for another account, or when the saved state is unreadable. Import
/// always reads the whole history again.</para>
/// </summary>
public partial class SettingsViewModel
{
    private CancellationTokenSource? _playCountImportCts;

    /// <summary>Delay before the startup sync, so it never competes with the first page.</summary>
    internal static TimeSpan PlayCountStartupSyncDelay = TimeSpan.FromSeconds(30);

    /// <summary>Data folder the sync state is kept under (cache/); tests point it elsewhere.</summary>
    internal string PlayCountSyncDataRoot { get; set; } = Helpers.AppPaths.DataRoot;

    /// <summary>The client the import reads with; tests stub it. Default: the app's shared one.</summary>
    internal HttpClient? PlayCountImportHttp { get; set; }

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

    /// <summary>Keep the higher count (default) or replace with the imported one; saved
    /// (<see cref="AppSettings.PlayCountImportMode"/>) because the background sync uses it too.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPlayCountImportModeOption))]
    private PlayCountImportMode _playCountImportMode = PlayCountImportMode.KeepHigher;

    /// <summary>"Keep play counts in sync" under Last.fm; see <see cref="AppSettings.LastFmPlayCountSync"/>.</summary>
    [ObservableProperty] private bool _lastFmPlayCountSync;

    /// <summary>"Keep play counts in sync" under ListenBrainz; see <see cref="AppSettings.ListenBrainzPlayCountSync"/>.</summary>
    [ObservableProperty] private bool _listenBrainzPlayCountSync;

    private DateTime? _lastFmPlayCountSyncedUtc;
    private DateTime? _listenBrainzPlayCountSyncedUtc;

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

    // ── Settings plumbing (LoadAsync / SyncToSettings / ResetSettingsToDefaults) ──

    private void LoadPlayCountImportSettings(AppSettings s)
    {
        PlayCountImportMode = ParsePlayCountImportMode(s.PlayCountImportMode);
        LastFmPlayCountSync = s.LastFmPlayCountSync;
        ListenBrainzPlayCountSync = s.ListenBrainzPlayCountSync;
        _lastFmPlayCountSyncedUtc = s.LastFmPlayCountSyncedUtc;
        _listenBrainzPlayCountSyncedUtc = s.ListenBrainzPlayCountSyncedUtc;
    }

    private void SavePlayCountImportSettings(AppSettings s)
    {
        s.PlayCountImportMode = PlayCountImportMode.ToString();
        s.LastFmPlayCountSync = LastFmPlayCountSync;
        s.ListenBrainzPlayCountSync = ListenBrainzPlayCountSync;
        s.LastFmPlayCountSyncedUtc = _lastFmPlayCountSyncedUtc;
        s.ListenBrainzPlayCountSyncedUtc = _listenBrainzPlayCountSyncedUtc;
    }

    private void ResetPlayCountImportSettings(AppSettings defaults)
    {
        _lastFmPlayCountSyncedUtc = defaults.LastFmPlayCountSyncedUtc;
        _listenBrainzPlayCountSyncedUtc = defaults.ListenBrainzPlayCountSyncedUtc;
        PlayCountImportMode = ParsePlayCountImportMode(defaults.PlayCountImportMode);
        LastFmPlayCountSync = defaults.LastFmPlayCountSync;
        ListenBrainzPlayCountSync = defaults.ListenBrainzPlayCountSync;
        LastFmImportStatus = "";
        ListenBrainzImportStatus = "";
    }

    internal static PlayCountImportMode ParsePlayCountImportMode(string? value)
        => Enum.TryParse<PlayCountImportMode>(value, ignoreCase: true, out var m) && Enum.IsDefined(m)
            ? m : PlayCountImportMode.KeepHigher;

    partial void OnPlayCountImportModeChanged(PlayCountImportMode value) { if (_settingsLoaded) _ = SaveAsync(); }

    partial void OnLastFmPlayCountSyncChanged(bool value)
    {
        if (!_settingsLoaded) return;
        _ = SaveAsync();
        if (value) _ = SyncPlayCountsInBackgroundAsync(PlayCountImportSource.LastFm, "turned on");
    }

    partial void OnListenBrainzPlayCountSyncChanged(bool value)
    {
        if (!_settingsLoaded) return;
        _ = SaveAsync();
        if (value) _ = SyncPlayCountsInBackgroundAsync(PlayCountImportSource.ListenBrainz, "turned on");
    }

    // ── Commands ──

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

    private bool IsPlayCountSyncOn(PlayCountImportSource source) => source == PlayCountImportSource.LastFm
        ? LastFmPlayCountSync && IsLastFmConnected
        : ListenBrainzPlayCountSync && IsListenBrainzConnected;

    /// <summary>
    /// Startup (MainWindowViewModel.InitializeAsync, after the library has loaded): after a short
    /// delay, syncs each service whose "Keep play counts in sync" is on, every startup (GitHub
    /// #182: an incremental read is cheap). One service after the other; UI thread.
    /// </summary>
    public async Task SyncPlayCountsOnStartupAsync()
    {
        try
        {
            if (!LastFmPlayCountSync && !ListenBrainzPlayCountSync) return;
            await Task.Delay(PlayCountStartupSyncDelay);
            await SyncPlayCountsInBackgroundAsync(PlayCountImportSource.LastFm, "startup");
            await SyncPlayCountsInBackgroundAsync(PlayCountImportSource.ListenBrainz, "startup");
        }
        catch (Exception ex)
        {
            DebugLog.Write("PlayCountImport", $"startup sync failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The background sync for one service, when it is on and connected. Never
    /// overlaps another sync or Import: if one is running, this one is skipped.</summary>
    internal async Task SyncPlayCountsInBackgroundAsync(PlayCountImportSource source, string reason)
    {
        if (!IsPlayCountSyncOn(source)) return;
        if (PlayCountImportActive != null)
        {
            DebugLog.Write("PlayCountImport", $"{source} sync ({reason}) skipped: an import is already running");
            return;
        }
        DebugLog.Write("PlayCountImport", $"{source} sync ({reason}) starting");
        await ImportPlayCountsAsync(source, incremental: true);
    }

    /// <summary>
    /// UI thread. Fetching and matching run off it; only the count writes come back.
    /// <paramref name="incremental"/> (the background sync) continues from the saved sync state;
    /// Import reads the whole history again. Both save the state for the next sync.
    /// </summary>
    internal async Task ImportPlayCountsAsync(PlayCountImportSource source, bool incremental = false)
    {
        if (PlayCountImportActive != null) return;
        var lastFm = source == PlayCountImportSource.LastFm;
        var service = lastFm ? "Last.fm" : "ListenBrainz";
        var user = lastFm
            ? (string.IsNullOrWhiteSpace(_lastFm?.Username) ? LastFmUsername : _lastFm!.Username)
            : (string.IsNullOrWhiteSpace(_listenBrainz?.Username) ? ListenBrainzUsername : _listenBrainz!.Username);
        var http = PlayCountImportHttp ?? App.Services?.GetService<HttpClient>();
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
            var sync = new PlayCountSync(new PlayCountHistoryFetcher(http));
            var apiUrl = _listenBrainz?.ApiUrl ?? ListenBrainzApiUrl;
            var token = ListenBrainzToken;
            var statePath = PlayCountSync.StatePath(PlayCountSyncDataRoot, source);
            var previous = incremental ? await PlayCountSync.LoadStateAsync(statePath, cts.Token) : null;
            var run = await Task.Run(() => sync.RunAsync(source, user!, apiUrl, token, previous, progress, cts.Token), cts.Token);

            SetPlayCountImportStatus(source, Loc.T("Settings.PlayCountImport.Matching"));
            var tracks = _library.Tracks;
            var match = await Task.Run(() => PlayCountImportMatcher.Match(tracks, run.State.Entries), cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            // Back on the UI thread: PlayCount is observable, and a play counted meanwhile is
            // read now, so "keep the higher" never undoes it.
            var changed = PlayCountImportMatcher.Apply(match, mode);
            if (changed.Count > 0)
            {
                await _library.SaveTrackUserStateAsync(changed);
                _library.NotifyMetadataChanged(); // Songs/Albums/Home "Most Played" re-rank
            }

            // The totals behind the counts just written: the next sync continues from them.
            try { await PlayCountSync.SaveStateAsync(statePath, run.State); }
            catch (Exception ex) { DebugLog.Write("PlayCountImport", $"{service}: sync state not saved: {ex.Message}"); }
            if (lastFm) _lastFmPlayCountSyncedUtc = DateTime.UtcNow;
            else _listenBrainzPlayCountSyncedUtc = DateTime.UtcNow;
            _ = SaveAsync();

            DebugLog.Write("PlayCountImport",
                $"{service}: {(run.Incremental ? "incremental" : "full")} read, {run.NewListens} listens read, " +
                $"{match.SourceEntries} entries, {match.MatchedEntries} matched ({match.MatchedPlays} plays), " +
                $"{match.UnmatchedEntries} unmatched ({match.UnmatchedPlays} plays), {changed.Count} songs changed, mode {mode}");
            SetPlayCountImportStatus(source, run.Incremental
                ? Loc.T("Settings.PlayCountImport.Synced", N(run.NewListens), N(changed.Count))
                : Loc.T("Settings.PlayCountImport.Done", N(changed.Count), N(match.MatchedEntries), N(match.UnmatchedEntries)));
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
