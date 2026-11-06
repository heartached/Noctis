using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Mobile.Services;
using Noctis.Models;
using Noctis.Services;
using Noctis.Localization;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Phone lyrics. Loads the current track's lyrics off the UI thread (sidecar TTML/ELRC/LRC
/// through <see cref="ITrackFileAccess"/>, then embedded, then — when the song has nothing of
/// its own — the desktop's automatic online lookup, <see cref="Online"/>), and turns the audible position
/// into the active line and word sweep through the shared Core <see cref="LyricsTimeline"/>,
/// with the desktop's clock smoothing, latency compensation and lookaheads.
/// </summary>
public sealed partial class LyricsPageViewModel : ObservableObject, IDisposable
{
    /// <summary>Line size in DIPs at system font scale 1.0: Apple Music's phone lyrics (the
    /// owner's reference, 10-20: about 34 pt on a 440 pt wide iPhone, three or four words a line).</summary>
    public const double BaseFontSize = 34;

    private const int WordLookaheadMs = 80;

    // The desktop's normal-mode opacity ramp by distance from the active line (LyricsViewModel.UpdateLineOpacities).
    private static readonly double[] OpacityRamp = { 1.0, 0.55, 0.32, 0.18, 0.12, 0.08, 0.06, 0.04, 0.03, 0.02 };

    // Depth blur by distance: the desktop's ramp a step stronger, as Apple Music's phone lyrics
    // (owner's reference, 10-20) blur the next line more than the desktop page does.
    private static readonly double[] BlurRamp = { 0, 5, 8, 10 };
    private const double FarBlur = 12;

    /// <summary>While the user scrolls the lyrics themselves (Apple Music's browse), every line
    /// is sharp and at least this visible, so any of them can be read and tapped.</summary>
    internal const double BrowseOpacityFloor = 0.45;

    private readonly IAudioPlayer _player;
    private readonly NowPlayingViewModel _nowPlaying;
    private readonly ITrackFileAccess _files;
    private readonly IPersistenceService _persistence;
    private readonly Func<Func<LoadedLyrics>, Task<LoadedLyrics>> _runBackground;
    private readonly LyricsTimeline _timeline;
    // The phone's position source may land a poll a few ms behind the last: hold, don't step back.
    private readonly LyricsPlaybackClock _clock = new() { BackwardToleranceMs = BackwardToleranceMs };
    internal const double BackwardToleranceMs = 250;

    /// <summary>The player's last position poll and the wall time it was taken (see OnFrame).</summary>
    private sealed record PolledPosition(double Ms, double AtMs);
    private PolledPosition? _polled;
    private int _generation;
    private bool _settingsLoaded;
    // Settings changed by the user before InitializeAsync read the saved ones (Settings opens
    // while a cold start still loads the library): the load keeps these and saves them.
    private readonly HashSet<string> _setBeforeLoad = new();
    private bool _applyingSaved;
    private int _opacitySaveGeneration;
    private bool _disposed;

    public LyricsPageViewModel(IAudioPlayer player, NowPlayingViewModel nowPlaying, ITrackFileAccess files,
        IPersistenceService persistence, Func<Func<LoadedLyrics>, Task<LoadedLyrics>>? runBackground = null)
    {
        _player = player;
        _nowPlaying = nowPlaying;
        _files = files;
        _persistence = persistence;
        _runBackground = runBackground ?? (work => Task.Run(work));
        _timeline = new LyricsTimeline(Lines, TimeSpan.FromMilliseconds(WordLookaheadMs),
            TimeSpan.FromMilliseconds(WordLookaheadMs + LineMotion.HalfTravelMs));
        _nowPlaying.PropertyChanged += OnNowPlayingChanged;
        _player.PositionChanged += OnPlayerPolled;
    }

    /// <summary>Stamps each poll where it is taken. Now Playing's Position gets the same value
    /// through a dispatcher post, which on a busy phone lands one or more long frames later.</summary>
    private void OnPlayerPolled(object? sender, TimeSpan position) =>
        Volatile.Write(ref _polled, new PolledPosition(position.TotalMilliseconds, NowMs()));

    /// <summary>
    /// Optional: the track the loader reads in place of the playing one (a desktop song's stand-in
    /// carrying the desktop's stored lyrics, see RemoteLyricsFileAccess.ForLoad). Runs inside the
    /// background work, just before <see cref="LyricsLoader.Load"/>, so it may block briefly
    /// (the app's runner is a pool thread, never the UI thread). A throw falls back to the
    /// playing track.
    /// </summary>
    public Func<Track, Track>? PrepareTrack { get; init; }

    /// <summary>Optional: the online lookup for a song with no lyrics of its own (the desktop's
    /// automatic search; the Android head passes LRCLIB). Asked only while
    /// <see cref="FindLyricsOnline"/> is on.</summary>
    public IOnlineLyricsSource? Online { get; init; }

    /// <summary>The lines the page shows: synced, or plain (pre-activated, never swept).</summary>
    public BulkObservableCollection<LyricLine> Lines { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(WantsFrames))] private bool _isSynced;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText), nameof(ShowsEmptyState))] private bool _hasLyrics;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText), nameof(ShowsEmptyState))] private bool _isLoading;
    [ObservableProperty] private int _activeLineIndex = -1;

    /// <summary>The line the page scrolls to: the newest started line, except while an earlier
    /// line is still being sung over it (a duet, an overlapping TTML voice) - then the earliest
    /// of those, so the list never scrolls past a line before its end
    /// (<see cref="LyricsTimeline.ScrollIndex"/>, as the desktop's ScrollLineIndex).</summary>
    [ObservableProperty] private int _scrollLineIndex = -1;
    [ObservableProperty] private bool _hasTranslations;
    [ObservableProperty] private bool _hasRomanizations;
    [ObservableProperty] private bool _showTranslation = true;
    [ObservableProperty] private bool _showRomanization = true;

    /// <summary>The background-vocal (adlib) row under a line, as the desktop's setting.</summary>
    [ObservableProperty] private bool _showBackgroundVocals = true;

    /// <summary>Floor for the dimmed lines, in percent (0–60), as the desktop's Minimum Line Opacity.</summary>
    [ObservableProperty] private int _minLineOpacity = AppSettings.LyricsMinLineOpacityDefault;

    /// <summary>One unbroken word for a word TTML splits into timed syllables (desktop issue #32).</summary>
    [ObservableProperty] private bool _joinSplitWords;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] private bool _isRawFallback;

    /// <summary>The desktop's LRCLIB switch (Settings → Lyrics → Find Lyrics Online): a song with
    /// no lyrics of its own is looked up online.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusDetail))] private bool _findLyricsOnline = true;

    /// <summary>True while the online lookup for the current song runs.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusDetail), nameof(ShowsEmptyState))]
    private bool _isSearchingOnline;

    /// <summary>The online source lists the song as instrumental.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusDetail))] private bool _isInstrumental;

    /// <summary>The online lookup failed (offline, timed out, LRCLIB busy), not missed.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusDetail))] private bool _onlineLookupFailed;

    /// <summary>Where the shown lyrics came from (online ones get a credit under the last line).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SourceCredit))] private LyricsSource _source;

    /// <summary>The desktop's Glow and Lift: sung words float up and held notes swell and glow.</summary>
    [ObservableProperty] private bool _wordMotion = true;

    /// <summary>The user is scrolling the lyrics (the page sets it, and clears it a few seconds
    /// after the last touch): every line sharp and readable, as in Apple Music.</summary>
    [ObservableProperty] private bool _isBrowsing;

    /// <summary>Android's system font scale. Avalonia scales by density only, so the head
    /// passes Configuration.FontScale in and the page sizes lyric text from it.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LineFontSize), nameof(LayerFontSize))]
    private double _fontScale = 1.0;

    /// <summary>The user's lyrics text size (Settings), on top of the system font scale.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LineFontSize), nameof(LayerFontSize))]
    private double _textScale = 1.0;

    public double LineFontSize => BaseFontSize * FontScale * TextScale;

    /// <summary>Translation / romanization / background-vocal rows: 0.6 of the line, the
    /// desktop's background-vocal ratio.</summary>
    public double LayerFontSize => LineFontSize * 0.6;

    /// <summary>True while the page should run its per-frame loop.</summary>
    public bool WantsFrames => IsSynced && _nowPlaying.IsPlaying;

    public string StatusText =>
        IsSearchingOnline ? Loc.T("Mobile.Lyrics.Searching")
        : IsLoading ? Loc.T("Mobile.Lyrics.Loading")
        : IsInstrumental ? Loc.T("Mobile.Lyrics.Instrumental")
        : !HasLyrics ? Loc.T("Mobile.Lyrics.None")
        : IsRawFallback ? Loc.T("Mobile.Lyrics.NotSynced")
        : string.Empty;

    /// <summary>The line under the empty state's title.</summary>
    public string StatusDetail =>
        IsSearchingOnline ? string.Empty
        : IsInstrumental ? Loc.T("Mobile.Lyrics.InstrumentalDetail")
        // LRCLIB busy (503) or no network read as "none were found online" (emulator 10-20).
        : OnlineLookupFailed ? Loc.T("Mobile.Lyrics.OnlineFailedDetail")
        : FindLyricsOnline && Online != null
            ? Loc.T("Mobile.Lyrics.NoneOnlineDetail")
            : Loc.T("Mobile.Lyrics.NoneDetail");

    /// <summary>The empty state shows once the local load is done: no lyrics, searching
    /// online, or instrumental. (The local load takes milliseconds; flashing a "Loading" title
    /// on every track change read as a glitch.)</summary>
    public bool ShowsEmptyState => !HasLyrics && (!IsLoading || IsSearchingOnline);

    /// <summary>Credit under the last line for lyrics fetched online.</summary>
    public string SourceCredit => Source == LyricsSource.Online ? Loc.T("Mobile.Lyrics.FromLrclib") : string.Empty;

    public static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// <summary>A slider's settings save waits this long after its last step, so a drag writes
    /// settings.json once, not once per step.</summary>
    internal static TimeSpan SliderSaveDelay { get; set; } = TimeSpan.FromMilliseconds(350);

    /// <summary>Settings first (layer toggles, opacity floor, split-word joining), then the current track.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var settings = await _persistence.LoadSettingsAsync();
            // A switch flipped while this load ran keeps the user's value, not the file's: the
            // load used to put the saved value back, and the flip was lost (never saved either).
            _applyingSaved = true;
            try
            {
                if (Saved(nameof(ShowTranslation))) ShowTranslation = settings.LyricsShowTranslations;
                if (Saved(nameof(ShowRomanization))) ShowRomanization = settings.LyricsShowRomanization;
                if (Saved(nameof(ShowBackgroundVocals))) ShowBackgroundVocals = settings.LyricsShowBackgroundVocals;
                if (Saved(nameof(MinLineOpacity))) MinLineOpacity = Math.Clamp(settings.LyricsMinLineOpacity, 0, 60);
                if (Saved(nameof(JoinSplitWords))) JoinSplitWords = settings.LyricsJoinSplitWords;
                if (Saved(nameof(TextScale))) TextScale = Math.Clamp(settings.MobileLyricsTextScale, 0.8, 1.6);
                if (Saved(nameof(FindLyricsOnline))) FindLyricsOnline = settings.LrcLibEnabled;
                WordMotion = settings.LyricsGlowAndLift;
            }
            finally
            {
                _applyingSaved = false;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("Lyrics", $"Settings load failed: {ex.Message}");
        }
        _settingsLoaded = true;
        // TextScale is the Settings page's to save (its own slider); the rest are saved here.
        if (_setBeforeLoad.Any(name => name != nameof(TextScale))) PersistLayerToggles();
        await LoadAsync(_nowPlaying.CurrentTrack);
    }

    private bool Saved(string setting) => !_setBeforeLoad.Contains(setting);

    /// <summary>A setting changed before the saved ones were read: true (and remembered) when the
    /// user changed it, so InitializeAsync keeps it.</summary>
    private bool SetBeforeLoad(string setting)
    {
        if (_settingsLoaded) return false;
        if (!_applyingSaved) _setBeforeLoad.Add(setting);
        return true;
    }

    partial void OnShowTranslationChanged(bool value) => PersistLayerToggles(nameof(ShowTranslation));

    partial void OnShowRomanizationChanged(bool value) => PersistLayerToggles(nameof(ShowRomanization));

    partial void OnShowBackgroundVocalsChanged(bool value) => PersistLayerToggles(nameof(ShowBackgroundVocals));

    partial void OnTextScaleChanged(double value) => SetBeforeLoad(nameof(TextScale));

    partial void OnWordMotionChanged(bool value) => _timeline.WordMotion = value;

    /// <summary>Switched on with nothing shown, the current song is looked up straight away.</summary>
    partial void OnFindLyricsOnlineChanged(bool value)
    {
        if (SetBeforeLoad(nameof(FindLyricsOnline))) return;
        PersistLayerToggles();
        if (value && !HasLyrics && !IsLoading && !IsInstrumental) _ = LoadAsync(_nowPlaying.CurrentTrack);
    }

    /// <summary>Browsing lifts the blur and the far lines' fade; ending it puts the ramp back.</summary>
    partial void OnIsBrowsingChanged(bool value)
    {
        if (IsSynced && Lines.Count > 0) ApplyOpacities(ActiveLineIndex);
    }

    /// <summary>Re-floors the shown lines in place (the desktop's RefreshFocusDimming); the save
    /// waits for the slider to settle.</summary>
    partial void OnMinLineOpacityChanged(int value)
    {
        if (IsSynced && Lines.Count > 0) ApplyOpacities(ActiveLineIndex);
        if (SetBeforeLoad(nameof(MinLineOpacity))) return;
        var generation = ++_opacitySaveGeneration;
        PendingSave = SaveAfterSliderSettlesAsync(generation);
    }

    /// <summary>Joining changes how the TTML is parsed, not how it is drawn: the current track
    /// goes back through the load path, as on the desktop.</summary>
    partial void OnJoinSplitWordsChanged(bool value)
    {
        if (SetBeforeLoad(nameof(JoinSplitWords))) return;
        PersistLayerToggles();
        _ = LoadAsync(_nowPlaying.CurrentTrack);
    }

    private async Task SaveAfterSliderSettlesAsync(int generation)
    {
        await Task.Delay(SliderSaveDelay);
        if (generation != Volatile.Read(ref _opacitySaveGeneration)) return;   // a later step saves
        await SaveLayerTogglesAsync();
    }

    /// <summary>The latest lyrics-settings save; tests await it instead of racing the file write.</summary>
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

    /// <summary>settings.json writes so far; tests count them.</summary>
    internal int SaveCount;

    private void PersistLayerToggles(string? setting = null)
    {
        if (setting != null ? SetBeforeLoad(setting) : !_settingsLoaded) return;   // saved once loaded
        PendingSave = SaveLayerTogglesAsync();
    }

    private async Task SaveLayerTogglesAsync()
    {
        try
        {
            // One update, so no other phone save lands between the read and the write (a pin, a
            // player-bar switch: the stale copy written after it undid it). The values are read
            // inside, as the latest: two quick flips saved in either order end on the last one.
            await _persistence.UpdateSettingsAsync(settings =>
            {
                settings.LyricsShowTranslations = ShowTranslation;
                settings.LyricsShowRomanization = ShowRomanization;
                settings.LyricsShowBackgroundVocals = ShowBackgroundVocals;
                settings.LyricsMinLineOpacity = MinLineOpacity;
                settings.LyricsJoinSplitWords = JoinSplitWords;
                settings.LrcLibEnabled = FindLyricsOnline;
            });
            Interlocked.Increment(ref SaveCount);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Lyrics", $"Layer toggle save failed: {ex.Message}");
        }
    }

    private void OnNowPlayingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NowPlayingViewModel.CurrentTrack):
                _ = LoadAsync(_nowPlaying.CurrentTrack);
                break;
            case nameof(NowPlayingViewModel.IsPlaying):
                OnPropertyChanged(nameof(WantsFrames));
                break;
            case nameof(NowPlayingViewModel.Position) when !_nowPlaying.IsPlaying:
                // A seek while paused: no frame loop is running, so follow it here.
                OnFrame(NowMs());
                break;
        }
    }

    private async Task LoadAsync(Track? track)
    {
        var generation = ++_generation;
        _timeline.Reset();
        _clock.Reset();
        ActiveLineIndex = -1;
        ScrollLineIndex = -1;
        Lines.ReplaceAll(Array.Empty<LyricLine>());
        IsSynced = false;
        HasLyrics = false;
        HasTranslations = false;
        HasRomanizations = false;
        IsRawFallback = false;
        IsSearchingOnline = false;
        IsInstrumental = false;
        OnlineLookupFailed = false;
        Source = LyricsSource.None;
        if (track == null)
        {
            IsLoading = false;
            return;
        }

        IsLoading = true;
        var join = JoinSplitWords;
        var prepare = PrepareTrack;
        LoadedLyrics result;
        try
        {
            result = await _runBackground(() => LyricsLoader.Load(Prepared(track, prepare), _files, join));
        }
        catch (Exception ex)
        {
            DebugLog.Write("Lyrics", $"Load failed for {track.Title}: {ex.Message}");
            result = LoadedLyrics.None;
        }

        if (generation != _generation || _disposed) return;   // a newer track won

        // Nothing of its own: the desktop's automatic online lookup (LyricsViewModel goes
        // online in exactly this case).
        if (result.Lines.Count == 0 && FindLyricsOnline && Online is { } online)
        {
            IsSearchingOnline = true;
            var found = await FindOnlineAsync(online, track);
            if (generation != _generation || _disposed) return;
            IsSearchingOnline = false;
            if (found is { Failed: true })
            {
                OnlineLookupFailed = true;
            }
            else if (found is { Instrumental: true })
            {
                IsInstrumental = true;
            }
            else if (found != null)
            {
                try
                {
                    result = await _runBackground(() => LyricsLoader.FromOnlineText(found.Text) ?? LoadedLyrics.None);
                }
                catch (Exception ex)
                {
                    DebugLog.Write("Lyrics", $"Online lyrics parse failed for {track.Title}: {ex.Message}");
                }
                if (generation != _generation || _disposed) return;
            }
        }

        IsLoading = false;
        Source = result.Source;
        Lines.ReplaceAll(result.Lines);
        IsSynced = result.IsSynced;
        HasLyrics = result.Lines.Count > 0;
        IsRawFallback = result.Source == LyricsSource.SidecarUnparsed;
        HasTranslations = result.Lines.Any(l => l.HasTranslation);
        HasRomanizations = result.Lines.Any(l => l.HasTransliteration);
        if (IsSynced)
        {
            ApplyOpacities(-1);
            OnFrame(NowMs());
        }
    }

    private static async Task<OnlineLyrics?> FindOnlineAsync(IOnlineLyricsSource online, Track track)
    {
        try
        {
            return await online.FindAsync(track, CancellationToken.None);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Lyrics", $"Online lookup failed for {track.Title}: {ex.GetType().Name}");
            return OnlineLyrics.Unreachable;
        }
    }

    private static Track Prepared(Track track, Func<Track, Track>? prepare)
    {
        if (prepare == null) return track;
        try
        {
            return prepare(track) ?? track;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Lyrics", $"Prepare failed for {track.Title}: {ex.GetType().Name}");
            return track;
        }
    }

    /// <summary>
    /// Advances the timeline to the audible position at wall time <paramref name="nowMs"/>.
    /// Called per rendered frame by the page while <see cref="WantsFrames"/>, and on paused seeks.
    /// </summary>
    /// <summary>The audible position the last frame drew (tests read it).</summary>
    internal double LastAudibleMs { get; private set; }

    public void OnFrame(double nowMs)
    {
        if (!IsSynced || Lines.Count == 0) return;
        var raw = _nowPlaying.Position.TotalMilliseconds;
        double audible;
        if (_nowPlaying.IsPlaying && !_nowPlaying.IsSeeking)
        {
            // The clock is told when the reading was taken, not when this frame first saw it:
            // a poll delivered behind a 200 ms frame otherwise read as the song being 200 ms
            // behind, the sweep slowed, then sped up at the next poll ("desyncs and syncs").
            var rawAt = Volatile.Read(ref _polled) is { } polled && polled.Ms == raw ? polled.AtMs : nowMs;
            audible = _clock.Sample(raw, rawAt, nowMs) - _player.OutputLatency.TotalMilliseconds;
        }
        else
        {
            // Paused, or a scrub steering the position: follow it verbatim, re-anchor after
            // (as the desktop's GetPlaybackPosition).
            _clock.Reset();
            audible = raw;
        }

        LastAudibleMs = audible;
        var step = _timeline.Update(TimeSpan.FromMilliseconds(Math.Max(0, audible)));
        // Also re-ramp when an overlapping (duet) line joins or leaves the lit set
        // while the active line stays.
        if (!step.ActiveSetChanged) return;
        // The scroll target is resolved before the active index is published, so the page's
        // handler of either change reads a settled pair.
        var scroll = Math.Min(_timeline.ScrollIndex(TimeSpan.FromMilliseconds(Math.Max(0, audible)), TimeSpan.Zero), step.ActiveIndex);
        ScrollLineIndex = scroll;
        ActiveLineIndex = step.ActiveIndex;
        ApplyOpacities(step.ActiveIndex);
    }

    /// <summary>Before the first line (<paramref name="active"/> &lt; 0) every line is at full
    /// opacity and sharp, as the desktop's UpdateLineOpacities(-1) does: nothing is sung yet, so
    /// nothing should read as dimmed. Otherwise the opacity and depth-blur ramps run from the
    /// nearest lit line, so overlapping duet lines all sit in full focus. While
    /// <see cref="IsBrowsing"/>, no line is blurred and none fades below the browse floor.</summary>
    private void ApplyOpacities(int active)
    {
        var lit = _timeline.ActiveIndices;
        var floor = Math.Clamp(MinLineOpacity, 0, 60) / 100.0;
        if (IsBrowsing) floor = Math.Max(floor, BrowseOpacityFloor);
        for (var i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            if (active < 0)
            {
                Set(line, 1.0, 0);
                continue;
            }
            var distance = LyricsTimeline.DistanceToNearestActive(i, active, lit);
            // Lines already sung recede a step faster than the ones to come: in Apple Music
            // the line just sung is a soft ghost under the header.
            if (distance > 0 && i < active) distance++;
            var ramp = distance < OpacityRamp.Length ? OpacityRamp[distance] : 0;
            var blur = IsBrowsing ? 0 : distance < BlurRamp.Length ? BlurRamp[distance] : FarBlur;
            Set(line, Math.Max(ramp, floor), blur);
        }

        // Only on change: each write re-renders the line (and its blur).
        static void Set(LyricLine line, double opacity, double blur)
        {
            if (line.LineOpacity != opacity) line.LineOpacity = opacity;
            if (line.BlurRadius != blur) line.BlurRadius = blur;
        }
    }

    [RelayCommand]
    private void SeekToLine(LyricLine? line)
    {
        if (!IsSynced || line?.Timestamp is not { } timestamp) return;
        _nowPlaying.Seek(timestamp);
        _timeline.Rewind();
        _clock.Reset();
    }

    public void Dispose()
    {
        _disposed = true;
        _nowPlaying.PropertyChanged -= OnNowPlayingChanged;
        _player.PositionChanged -= OnPlayerPolled;
    }
}
