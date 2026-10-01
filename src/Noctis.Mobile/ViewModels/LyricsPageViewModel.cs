using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Phone lyrics. Loads the current track's lyrics off the UI thread (sidecar TTML/ELRC/LRC
/// through <see cref="ITrackFileAccess"/>, then embedded), and turns the audible position
/// into the active line and word sweep through the shared Core <see cref="LyricsTimeline"/>,
/// with the desktop's clock smoothing, latency compensation and lookaheads.
/// </summary>
public sealed partial class LyricsPageViewModel : ObservableObject, IDisposable
{
    /// <summary>Line size in DIPs at system font scale 1.0.</summary>
    public const double BaseFontSize = 28;

    private const int WordLookaheadMs = 80;

    // The desktop's normal-mode opacity ramp by distance from the active line (LyricsViewModel.UpdateLineOpacities).
    private static readonly double[] OpacityRamp = { 1.0, 0.55, 0.32, 0.18, 0.12, 0.08, 0.06, 0.04, 0.03, 0.02 };

    private readonly IAudioPlayer _player;
    private readonly NowPlayingViewModel _nowPlaying;
    private readonly ITrackFileAccess _files;
    private readonly IPersistenceService _persistence;
    private readonly Func<Func<LoadedLyrics>, Task<LoadedLyrics>> _runBackground;
    private readonly LyricsTimeline _timeline;
    private readonly LyricsPlaybackClock _clock = new();
    private int _generation;
    private double _minOpacity = AppSettings.LyricsMinLineOpacityDefault / 100.0;
    private bool _joinSplitWords;
    private bool _settingsLoaded;
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
    }

    /// <summary>
    /// Optional: the track the loader reads in place of the playing one (a desktop song's stand-in
    /// carrying the desktop's stored lyrics, see RemoteLyricsFileAccess.ForLoad). Runs inside the
    /// background work, just before <see cref="LyricsLoader.Load"/>, so it may block briefly
    /// (the app's runner is a pool thread, never the UI thread). A throw falls back to the
    /// playing track.
    /// </summary>
    public Func<Track, Track>? PrepareTrack { get; init; }

    /// <summary>The lines the page shows: synced, or plain (pre-activated, never swept).</summary>
    public BulkObservableCollection<LyricLine> Lines { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(WantsFrames))] private bool _isSynced;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] private bool _hasLyrics;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] private bool _isLoading;
    [ObservableProperty] private int _activeLineIndex = -1;
    [ObservableProperty] private bool _hasTranslations;
    [ObservableProperty] private bool _hasRomanizations;
    [ObservableProperty] private bool _showTranslation = true;
    [ObservableProperty] private bool _showRomanization = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] private bool _isRawFallback;

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
        IsLoading ? "Loading lyrics…"
        : !HasLyrics ? "No lyrics"
        : IsRawFallback ? "No synced lyrics"
        : string.Empty;

    public static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// <summary>Settings first (layer toggles, opacity floor, split-word joining), then the current track.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var settings = await _persistence.LoadSettingsAsync();
            ShowTranslation = settings.LyricsShowTranslations;
            ShowRomanization = settings.LyricsShowRomanization;
            _minOpacity = Math.Clamp(settings.LyricsMinLineOpacity, 0, 60) / 100.0;
            _joinSplitWords = settings.LyricsJoinSplitWords;
            TextScale = Math.Clamp(settings.MobileLyricsTextScale, 0.8, 1.6);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Lyrics", $"Settings load failed: {ex.Message}");
        }
        _settingsLoaded = true;
        await LoadAsync(_nowPlaying.CurrentTrack);
    }

    partial void OnShowTranslationChanged(bool value) => PersistLayerToggles();

    partial void OnShowRomanizationChanged(bool value) => PersistLayerToggles();

    /// <summary>The latest layer-toggle save; tests await it instead of racing the file write.</summary>
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

    private void PersistLayerToggles()
    {
        if (!_settingsLoaded) return;   // InitializeAsync assigning the saved values
        PendingSave = SaveLayerTogglesAsync();
    }

    private async Task SaveLayerTogglesAsync()
    {
        try
        {
            var settings = await _persistence.LoadSettingsAsync();
            settings.LyricsShowTranslations = ShowTranslation;
            settings.LyricsShowRomanization = ShowRomanization;
            await _persistence.SaveSettingsAsync(settings);
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
        Lines.ReplaceAll(Array.Empty<LyricLine>());
        IsSynced = false;
        HasLyrics = false;
        HasTranslations = false;
        HasRomanizations = false;
        IsRawFallback = false;
        if (track == null)
        {
            IsLoading = false;
            return;
        }

        IsLoading = true;
        var join = _joinSplitWords;
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
        IsLoading = false;
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
    public void OnFrame(double nowMs)
    {
        if (!IsSynced || Lines.Count == 0) return;
        var raw = _nowPlaying.Position.TotalMilliseconds;
        double audible;
        if (_nowPlaying.IsPlaying)
        {
            audible = _clock.Sample(raw, nowMs) - _player.OutputLatency.TotalMilliseconds;
        }
        else
        {
            _clock.Reset();
            audible = raw;
        }

        var step = _timeline.Update(TimeSpan.FromMilliseconds(Math.Max(0, audible)));
        if (!step.LineChanged) return;
        ActiveLineIndex = step.ActiveIndex;
        ApplyOpacities(step.ActiveIndex);
    }

    /// <summary>Before the first line (<paramref name="active"/> &lt; 0) every line is at full
    /// opacity, as the desktop's UpdateLineOpacities(-1) does: nothing is sung yet, so nothing
    /// should read as dimmed.</summary>
    private void ApplyOpacities(int active)
    {
        for (var i = 0; i < Lines.Count; i++)
        {
            if (active < 0)
            {
                Lines[i].LineOpacity = 1.0;
                continue;
            }
            var distance = Math.Abs(i - active);
            var ramp = distance < OpacityRamp.Length ? OpacityRamp[distance] : 0;
            Lines[i].LineOpacity = Math.Max(ramp, _minOpacity);
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
    }
}
