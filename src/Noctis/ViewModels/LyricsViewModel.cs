using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the Lyrics view that displays synchronized lyrics
/// alongside album art and playback controls.
/// Supports: embedded lyrics, .lrc files with timestamp syncing.
/// </summary>
public partial class LyricsViewModel : ViewModelBase, IDisposable
{
    private readonly PlayerViewModel _player;
    private readonly ILrcLibService _lrcLib;
    private readonly INetEaseService _netEase;
    private readonly IMetadataService _metadata;
    private readonly IPersistenceService _persistence;
    private string? _selectedColorHex;
    private LyricLine? _currentActiveLine;
    private bool _hasSyncedLyrics;
    private Track? _currentTrack;
    private LrcLibResult? _currentOnlineResult;
    private LrcLibResult? _alternateOnlineResult;
    private string? _alternateSource;
    private int _searchGeneration;

    private static readonly string LyricsCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Noctis", "lyrics_cache");

    private static readonly Color DefaultAdaptiveColor = Color.FromRgb(0x0D, 0x1B, 0x2A);

    // Dedicated lyrics sync timer — bypasses the fragile PropertyChanged chain.
    // The old approach (PlayerVM.PropertyChanged → UpdateActiveLine) failed because
    // CommunityToolkit.Mvvm only fires PropertyChanged when the value actually
    // changes, and the 4-step dispatch chain (Timer→Event→Post→Setter) has
    // too many points where updates can be lost.
    private readonly DispatcherTimer _lyricsSyncTimer;

    public PlayerViewModel Player => _player;

    /// <summary>Lyrics lines for the current track.</summary>
    public ObservableCollection<LyricLine> LyricLines { get; } = new();

    /// <summary>Whether the current lyrics have timestamp sync.</summary>
    [ObservableProperty]
    private bool _isSynced;

    /// <summary>Whether the Synchronized tab is selected.</summary>
    [ObservableProperty]
    private bool _isSyncTabSelected = true;

    /// <summary>Whether the Unsynchronized tab is selected.</summary>
    [ObservableProperty]
    private bool _isUnsyncTabSelected;

    /// <summary>Whether synced lyrics are available (controls Sync tab visibility).</summary>
    [ObservableProperty]
    private bool _hasSyncedLyricsAvailable;

    /// <summary>Plain text lyrics without timestamps for the Unsync tab.</summary>
    public ObservableCollection<LyricLine> UnsyncedLines { get; } = new();

    /// <summary>Index of the currently active lyric line (for auto-scroll).</summary>
    [ObservableProperty]
    private int _activeLineIndex = -1;

    /// <summary>Album metadata line (e.g. "2021 · Alternative · 17 tracks").</summary>
    [ObservableProperty]
    private string _albumInfoText = string.Empty;

    /// <summary>Adaptive gradient brush for the left panel (darker tint).</summary>
    [ObservableProperty]
    private IBrush _leftPanelBrush = CreateDefaultGradient();

    /// <summary>Adaptive gradient brush for the right/lyrics panel (subdued).</summary>
    [ObservableProperty]
    private IBrush _lyricsBackgroundBrush = CreateDefaultSubduedGradient();

    /// <summary>Unified horizontal gradient spanning both panels — removes the hard seam.</summary>
    [ObservableProperty]
    private IBrush _fullBackgroundBrush = CreateDefaultUnifiedBrush();

    /// <summary>Whether the "Search Lyrics" button should be shown (no local lyrics found).</summary>
    [ObservableProperty]
    private bool _showSearchButton;

    /// <summary>Message shown above the Search Lyrics button after a failed search.</summary>
    [ObservableProperty]
    private string _searchFailedMessage = string.Empty;

    /// <summary>Whether a lyrics search is in progress.</summary>
    [ObservableProperty]
    private bool _isSearching;

    /// <summary>Whether online lyrics are currently displayed (enables "Save to File").</summary>
    [ObservableProperty]
    private bool _canSaveToFile;

    /// <summary>Whether lyrics can be removed (true for online-fetched or cached service lyrics).</summary>
    [ObservableProperty]
    private bool _canRemoveLyrics;

    /// <summary>Status text for save operation feedback.</summary>
    [ObservableProperty]
    private string _saveStatusText = string.Empty;

    /// <summary>Whether auto-follow has been paused by user manual scroll.</summary>
    [ObservableProperty]
    private bool _isAutoFollowPaused;

    /// <summary>Name of the lyrics source currently displayed (e.g. "LRCLIB", "NetEase", "Local").</summary>
    [ObservableProperty]
    private string _lyricsSourceName = string.Empty;

    /// <summary>Whether an alternate lyrics source is available to switch to.</summary>
    [ObservableProperty]
    private bool _hasAlternateLyrics;

    /// <summary>Label for the alternate lyrics button (e.g. "Try NetEase", "Try LRCLIB").</summary>
    [ObservableProperty]
    private string _alternateLyricsLabel = string.Empty;

    private Action<string>? _viewArtistAction;
    private Action<Track>? _viewAlbumAction;

    public LyricsViewModel(PlayerViewModel player, ILrcLibService lrcLib, INetEaseService netEase, IMetadataService metadata, IPersistenceService persistence)
    {
        _player = player;
        _lrcLib = lrcLib;
        _netEase = netEase;
        _metadata = metadata;
        _persistence = persistence;

        // Dedicated sync timer: reads player position directly every 16ms (~60fps).
        // This is the SOLE mechanism for lyrics highlighting — no PropertyChanged dependency.
        _lyricsSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _lyricsSyncTimer.Tick += (_, _) =>
        {
            if (_hasSyncedLyrics && _player.State == Models.PlaybackState.Playing)
                UpdateActiveLine(_player.Position);
        };

        // Subscribe to track changes to update lyrics
        _player.TrackStarted += OnTrackStarted;

        // Subscribe to state changes to start/stop the sync timer
        _player.PropertyChanged += OnPlayerPropertyChanged;

        // Load lyrics for current track if one is playing
        if (_player.CurrentTrack != null)
        {
            LoadLyricsForTrack(_player.CurrentTrack);
            UpdateAdaptiveBackground(_player.AlbumArt);
            if (_hasSyncedLyrics && IsSyncTabSelected)
                _lyricsSyncTimer.Start();
        }

        // Load saved background color preference
        _ = LoadSavedBackgroundColorAsync();
    }

    private static LinearGradientBrush CreateDefaultGradient()
    {
        return DominantColorExtractor.CreateGradientFromColor(DefaultAdaptiveColor);
    }

    private static LinearGradientBrush CreateDefaultSubduedGradient()
    {
        var (_, right) = DominantColorExtractor.GenerateAdaptiveBrushes(DefaultAdaptiveColor);
        return right;
    }

    private static LinearGradientBrush CreateDefaultUnifiedBrush()
        => DominantColorExtractor.GenerateUnifiedBrush(DefaultAdaptiveColor);

    /// <summary>
    /// Extracts the dominant color from the current album art and updates
    /// both left and right panel brushes. Called on track change.
    /// </summary>
    private void UpdateAdaptiveBackground(Bitmap? albumArt)
    {
        // Don't override when a custom background color is selected
        if (_selectedColorHex != null) return;

        if (albumArt == null)
        {
            LeftPanelBrush = CreateDefaultGradient();
            LyricsBackgroundBrush = CreateDefaultSubduedGradient();
            FullBackgroundBrush = CreateDefaultUnifiedBrush();
            return;
        }

        try
        {
            var dominant = DominantColorExtractor.ExtractDominantColor(albumArt);
            var (left, right) = DominantColorExtractor.GenerateAdaptiveBrushes(dominant);
            LeftPanelBrush = left;
            LyricsBackgroundBrush = right;
            FullBackgroundBrush = DominantColorExtractor.GenerateUnifiedBrush(dominant);
        }
        catch
        {
            LeftPanelBrush = CreateDefaultGradient();
            LyricsBackgroundBrush = CreateDefaultSubduedGradient();
            FullBackgroundBrush = CreateDefaultUnifiedBrush();
        }
    }

    [RelayCommand]
    private void ResumeAutoFollow()
    {
        IsAutoFollowPaused = false;
    }

    [RelayCommand]
    private async Task SetBackgroundColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            _selectedColorHex = null;
            UpdateAdaptiveBackground(_player.AlbumArt);
        }
        else
        {
            _selectedColorHex = hex;
            try
            {
                var color = Color.Parse(hex);
                FullBackgroundBrush = DominantColorExtractor.GenerateUnifiedBrush(color);
            }
            catch
            {
                _selectedColorHex = null;
                UpdateAdaptiveBackground(_player.AlbumArt);
            }
        }

        // Persist preference
        try
        {
            var settings = await _persistence.LoadSettingsAsync();
            settings.LyricsBackgroundColorHex = _selectedColorHex ?? "";
            await _persistence.SaveSettingsAsync(settings);
        }
        catch { }
    }

    private async Task LoadSavedBackgroundColorAsync()
    {
        try
        {
            var settings = await _persistence.LoadSettingsAsync();
            if (!string.IsNullOrEmpty(settings.LyricsBackgroundColorHex))
            {
                _selectedColorHex = settings.LyricsBackgroundColorHex;
                var color = Color.Parse(_selectedColorHex);
                FullBackgroundBrush = DominantColorExtractor.GenerateUnifiedBrush(color);
            }
        }
        catch { }
    }

    [RelayCommand]
    private void SelectSyncTab()
    {
        IsSyncTabSelected = true;
        IsUnsyncTabSelected = false;
        // Restart sync timer if playing synced lyrics
        if (_hasSyncedLyrics && _player.State == Models.PlaybackState.Playing)
            _lyricsSyncTimer.Start();
    }

    [RelayCommand]
    private void SelectUnsyncTab()
    {
        IsSyncTabSelected = false;
        IsUnsyncTabSelected = true;
        // Stop sync timer — unsync tab doesn't need it
        _lyricsSyncTimer.Stop();
    }

    /// <summary>Sets the action to navigate to an artist's discography.</summary>
    public void SetViewArtistAction(Action<string> action) => _viewArtistAction = action;

    /// <summary>Sets the action to navigate to the current track's album.</summary>
    public void SetViewAlbumAction(Action<Track> action) => _viewAlbumAction = action;

    [RelayCommand]
    private void ViewArtist()
    {
        var artist = _player.CurrentTrack?.Artist;
        if (!string.IsNullOrWhiteSpace(artist))
            _viewArtistAction?.Invoke(artist);
    }

    [RelayCommand]
    private void ViewAlbum()
    {
        var track = _player.CurrentTrack;
        if (track != null)
            _viewAlbumAction?.Invoke(track);
    }

    [RelayCommand]
    private async Task SearchLyrics()
    {
        var track = _currentTrack;
        if (track == null) return;

        DebugLogger.Info(DebugLogger.Category.Lyrics, "SearchLyrics", $"artist={track.Artist}, title={track.Title}");
        var generation = ++_searchGeneration;
        IsSearching = true;
        ShowSearchButton = false;
        SearchFailedMessage = string.Empty;
        SaveStatusText = string.Empty;
        HasAlternateLyrics = false;
        LyricsSourceName = string.Empty;
        _alternateOnlineResult = null;
        _alternateSource = null;

        try
        {
            // Load settings to check which providers are enabled
            var settings = await _persistence.LoadSettingsAsync();
            var lrcLibEnabled = settings.LrcLibEnabled;
            var netEaseEnabled = settings.NetEaseEnabled;

            var artist = track.Artist ?? "";
            var title = track.Title ?? "";
            var duration = track.Duration.TotalSeconds;

            LrcLibResult? lrcLibResult = null;
            LrcLibResult? netEaseResult = null;

            // Search enabled providers in parallel
            var tasks = new List<Task>();

            if (lrcLibEnabled)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var result = await _lrcLib.GetLyricsAsync(artist, title, duration);
                        if (result == null || !result.HasLyrics)
                        {
                            var results = await _lrcLib.SearchLyricsAsync(artist, title);
                            result = results.FirstOrDefault(r => r.HasSyncedLyrics)
                                  ?? results.FirstOrDefault(r => r.HasLyrics);
                        }
                        lrcLibResult = result;
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Warn(DebugLogger.Category.Lyrics, "LRCLIB:Error", ex.Message);
                    }
                }));
            }

            if (netEaseEnabled)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        netEaseResult = await _netEase.SearchLyricsAsync(artist, title, duration);
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Warn(DebugLogger.Category.Lyrics, "NetEase:Error", ex.Message);
                    }
                }));
            }

            await Task.WhenAll(tasks);

            // Race condition guard
            if (generation != _searchGeneration) return;

            // Pick best result: prefer synced over unsynced, LRCLIB over NetEase when equal
            var (primary, primarySource, alternate, altSource) = PickBestResult(lrcLibResult, netEaseResult);

            if (primary != null && primary.HasLyrics)
            {
                DebugLogger.Info(DebugLogger.Category.Lyrics, "SearchLyrics:Found",
                    $"source={primarySource}, synced={primary.HasSyncedLyrics}");
                DisplayOnlineLyrics(primary);
                LyricsSourceName = primarySource;

                // Store alternate if available
                if (alternate != null && alternate.HasLyrics)
                {
                    _alternateOnlineResult = alternate;
                    _alternateSource = altSource;
                    HasAlternateLyrics = true;
                    AlternateLyricsLabel = $"Try {altSource}";
                }
            }
            else
            {
                DebugLogger.Warn(DebugLogger.Category.Lyrics, "SearchLyrics:NotFound");
                LyricLines.Clear();
                UnsyncedLines.Clear();
                SearchFailedMessage = "No Lyrics found.";
                ShowSearchButton = true;
            }
        }
        catch
        {
            if (generation == _searchGeneration)
            {
                LyricLines.Clear();
                UnsyncedLines.Clear();
                SearchFailedMessage = "Search failed — check your internet connection.";
                ShowSearchButton = true;
            }
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>
    /// Picks the best lyrics result from the two providers.
    /// Prefers synced over unsynced. When both have equal quality, prefers LRCLIB (curated).
    /// Returns (primary, primarySource, alternate, alternateSource).
    /// </summary>
    private static (LrcLibResult? Primary, string PrimarySource, LrcLibResult? Alternate, string? AlternateSource)
        PickBestResult(LrcLibResult? lrcLib, LrcLibResult? netEase)
    {
        var lrcLibHas = lrcLib != null && lrcLib.HasLyrics;
        var netEaseHas = netEase != null && netEase.HasLyrics;

        if (lrcLibHas && netEaseHas)
        {
            // Both have results — pick the one with synced lyrics, or LRCLIB if equal
            if (lrcLib!.HasSyncedLyrics && !netEase!.HasSyncedLyrics)
                return (lrcLib, "LRCLIB", netEase, "NetEase");
            if (!lrcLib.HasSyncedLyrics && netEase!.HasSyncedLyrics)
                return (netEase, "NetEase", lrcLib, "LRCLIB");
            // Both synced or both unsynced — prefer LRCLIB (community curated)
            return (lrcLib, "LRCLIB", netEase, "NetEase");
        }

        if (lrcLibHas)
            return (lrcLib, "LRCLIB", null, null);
        if (netEaseHas)
            return (netEase, "NetEase", null, null);

        return (null, "", null, null);
    }

    /// <summary>
    /// Switches to the alternate lyrics source when the user clicks "Try alternate".
    /// </summary>
    [RelayCommand]
    private void SwitchToAlternateLyrics()
    {
        if (_alternateOnlineResult == null || _alternateSource == null) return;

        // Swap current and alternate
        var prevResult = _currentOnlineResult;
        var prevSource = LyricsSourceName;

        DisplayOnlineLyrics(_alternateOnlineResult);
        LyricsSourceName = _alternateSource;

        _alternateOnlineResult = prevResult;
        _alternateSource = prevSource;
        HasAlternateLyrics = prevResult != null && prevResult.HasLyrics;
        AlternateLyricsLabel = $"Try {prevSource}";
    }

    [RelayCommand]
    private void SaveLyricsToFile()
    {
        if (_currentTrack == null || _currentOnlineResult == null) return;

        // Prefer synced lyrics, fall back to plain
        var lyricsToSave = _currentOnlineResult.SyncedLyrics ?? _currentOnlineResult.PlainLyrics;
        if (string.IsNullOrWhiteSpace(lyricsToSave)) return;

        _currentTrack.Lyrics = lyricsToSave;

        try
        {
            // Root cause fix: writing embedded tags can fail while the media file is in use.
            // Save an LRC sidecar next to the track so it works reliably during playback.
            var lrcPath = Path.ChangeExtension(_currentTrack.FilePath, ".lrc");
            if (string.IsNullOrWhiteSpace(lrcPath))
            {
                SaveStatusText = "Save failed";
                return;
            }

            var lrcContent = NormalizeLyricsForLrc(lyricsToSave);
            File.WriteAllText(lrcPath, lrcContent, new UTF8Encoding(false));

            // Best-effort metadata write (non-blocking for save success).
            try { _metadata.WriteTrackMetadata(_currentTrack); } catch { }

            CanSaveToFile = false;
            SaveStatusText = "Saved Lyrics";
        }
        catch
        {
            SaveStatusText = "Save failed — check file permissions";
        }
    }

    /// <summary>
    /// Removes the currently displayed online lyrics: clears the cached file,
    /// resets lyrics state, and shows the search button so the user can retry.
    /// </summary>
    [RelayCommand]
    private void RemoveLyrics()
    {
        if (_currentTrack == null) return;

        // Remove cached lyrics file
        try
        {
            var cachePath = Path.Combine(LyricsCacheDir, $"{_currentTrack.Id}.lrc");
            if (File.Exists(cachePath))
                File.Delete(cachePath);
        }
        catch { }

        // Reset state
        _currentOnlineResult = null;
        _alternateOnlineResult = null;
        _alternateSource = null;
        _currentActiveLine = null;
        _hasSyncedLyrics = false;
        IsSynced = false;
        HasSyncedLyricsAvailable = false;
        ActiveLineIndex = -1;
        CanSaveToFile = false;
        CanRemoveLyrics = false;
        HasAlternateLyrics = false;
        LyricsSourceName = string.Empty;
        AlternateLyricsLabel = string.Empty;
        _lyricsSyncTimer.Stop();

        LyricLines.Clear();
        UnsyncedLines.Clear();

        // Show "no lyrics" state with search button only
        ShowSearchButton = true;

        SaveStatusText = "Lyrics removed";
    }

    private static string? TryLoadCachedLyrics(Guid trackId)
    {
        try
        {
            var path = Path.Combine(LyricsCacheDir, $"{trackId}.lrc");
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        catch { }
        return null;
    }

    private static void SaveLyricsToCache(Guid trackId, string lyrics)
    {
        try
        {
            Directory.CreateDirectory(LyricsCacheDir);
            var path = Path.Combine(LyricsCacheDir, $"{trackId}.lrc");
            File.WriteAllText(path, NormalizeLyricsForLrc(lyrics), new UTF8Encoding(false));
        }
        catch { }
    }

    private static string NormalizeLyricsForLrc(string lyrics)
    {
        // Keep source timestamps intact when present, just normalize line endings.
        return lyrics
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd()
            .Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// Called from context menus to search lyrics for a specific track.
    /// Loads the track first, and if no local lyrics found, triggers online search.
    /// </summary>
    public void SearchLyricsForTrack(Track track)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LoadLyricsForTrack(track);

            // If no local lyrics were found, trigger online search automatically
            if (ShowSearchButton)
                SearchLyricsCommand.Execute(null);
        });
    }

    private void DisplayOnlineLyrics(LrcLibResult result)
    {
        _currentOnlineResult = result;
        LyricLines.Clear();
        UnsyncedLines.Clear();
        _currentActiveLine = null;
        _hasSyncedLyrics = false;
        IsSynced = false;
        HasSyncedLyricsAvailable = false;
        ActiveLineIndex = -1;

        if (result.HasSyncedLyrics)
        {
            var parsedLines = ParseLrcContent(result.SyncedLyrics!);
            _hasSyncedLyrics = parsedLines.Any(l => l.IsSynced);
            IsSynced = _hasSyncedLyrics;
            HasSyncedLyricsAvailable = _hasSyncedLyrics;

            if (_hasSyncedLyrics)
                InsertIntroPlaceholderIfNeeded(parsedLines);

            foreach (var line in parsedLines)
                LyricLines.Add(line);

            PopulateUnsyncedLines(parsedLines);
        }
        else if (!string.IsNullOrWhiteSpace(result.PlainLyrics))
        {
            var lines = result.PlainLyrics.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                var wrapped = SoftWrapText(line);
                LyricLines.Add(new LyricLine { Text = wrapped, IsActive = true });
                UnsyncedLines.Add(new LyricLine { Text = wrapped, IsActive = true });
            }
        }

        AutoSelectTab();
        CanSaveToFile = true;
        CanRemoveLyrics = true;
        ShowSearchButton = false;

        // Cache the downloaded lyrics for offline use
        if (_currentTrack != null)
        {
            var lyricsToCache = result.SyncedLyrics ?? result.PlainLyrics;
            if (!string.IsNullOrWhiteSpace(lyricsToCache))
                SaveLyricsToCache(_currentTrack.Id, lyricsToCache);
        }

        // Start sync timer if synced lyrics and playing
        if (_hasSyncedLyrics && IsSyncTabSelected && _player.State == Models.PlaybackState.Playing)
            _lyricsSyncTimer.Start();
    }

    /// <summary>Clears all lyrics state when no track is playing.</summary>
    private void ClearLyricsState()
    {
        _currentTrack = null;
        _currentOnlineResult = null;
        _alternateOnlineResult = null;
        _alternateSource = null;
        _currentActiveLine = null;
        _hasSyncedLyrics = false;
        IsSynced = false;
        HasSyncedLyricsAvailable = false;
        ActiveLineIndex = -1;
        CanSaveToFile = false;
        CanRemoveLyrics = false;
        HasAlternateLyrics = false;
        LyricsSourceName = string.Empty;
        AlternateLyricsLabel = string.Empty;
        ShowSearchButton = false;
        IsSearching = false;
        SaveStatusText = string.Empty;
        AlbumInfoText = string.Empty;
        _lyricsSyncTimer.Stop();

        LyricLines.Clear();
        UnsyncedLines.Clear();
    }

    private void OnTrackStarted(object? sender, Track track)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LoadLyricsForTrack(track);
            UpdateAdaptiveBackground(_player.AlbumArt);
            // Start sync timer only if synced lyrics exist and sync tab is active
            if (_hasSyncedLyrics && IsSyncTabSelected)
                _lyricsSyncTimer.Start();
            else
                _lyricsSyncTimer.Stop();
        });
    }

    /// <summary>
    /// Called when the lyrics view becomes visible. Ensures lyrics are loaded
    /// for the currently playing track (handles the case where TrackStarted
    /// fired before the user navigated to this view).
    /// </summary>
    public void EnsureLyricsForCurrentTrack()
    {
        var track = _player.CurrentTrack;
        if (track == null) return;

        if (_currentTrack?.Id != track.Id)
        {
            // Different track — full reload
            LoadLyricsForTrack(track);
            if (_hasSyncedLyrics && IsSyncTabSelected && _player.State == Models.PlaybackState.Playing)
                _lyricsSyncTimer.Start();
        }
        else
        {
            // Same track — re-entering the lyrics view.
            // Always sync to current position immediately so lyrics are visible right away,
            // whether playing or paused.
            if (_hasSyncedLyrics && IsSyncTabSelected)
            {
                // Force full refresh: reset tracked state so UpdateActiveLine treats the
                // current line as a new match (fires PropertyChanged + UpdateLineOpacities).
                _currentActiveLine = null;
                ActiveLineIndex = -1;
                UpdateActiveLine(_player.Position);

                // Restart sync timer if playing
                if (_player.State == Models.PlaybackState.Playing && !_lyricsSyncTimer.IsEnabled)
                    _lyricsSyncTimer.Start();
            }
        }
    }

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Clear lyrics when track becomes null (queue ended)
        if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack) && _player.CurrentTrack == null)
        {
            ClearLyricsState();
            return;
        }

        // Manage the sync timer based on playback state changes.
        // Only run the timer when synced tab is active.
        if (e.PropertyName == nameof(PlayerViewModel.State))
        {
            if (_player.State == Models.PlaybackState.Playing && _hasSyncedLyrics && IsSyncTabSelected)
                _lyricsSyncTimer.Start();
            else
                _lyricsSyncTimer.Stop();
        }
        // Also update on Position PropertyChanged — but ONLY when the timer is NOT
        // running. This catches the final position at end-of-track (when playback
        // stops and the timer is no longer ticking) without duplicating work during
        // normal playback.
        else if (e.PropertyName == nameof(PlayerViewModel.Position) && _hasSyncedLyrics
                 && !_lyricsSyncTimer.IsEnabled)
        {
            UpdateActiveLine(_player.Position);
        }
        // Update adaptive background when album art loads/changes
        else if (e.PropertyName == nameof(PlayerViewModel.AlbumArt))
        {
            UpdateAdaptiveBackground(_player.AlbumArt);
        }
    }

    private void LoadLyricsForTrack(Track track)
    {
        DebugLogger.Info(DebugLogger.Category.Lyrics, "LoadLyricsForTrack", $"title={track.Title}, id={track.Id}");
        _currentTrack = track;
        _currentOnlineResult = null;
        _alternateOnlineResult = null;
        _alternateSource = null;
        ShowSearchButton = false;
        SearchFailedMessage = string.Empty;
        IsSearching = false;
        CanSaveToFile = false;
        CanRemoveLyrics = false;
        SaveStatusText = string.Empty;
        IsAutoFollowPaused = false;
        HasAlternateLyrics = false;
        LyricsSourceName = string.Empty;
        AlternateLyricsLabel = string.Empty;
        _searchGeneration++;

        LyricLines.Clear();
        UnsyncedLines.Clear();
        _currentActiveLine = null;
        _hasSyncedLyrics = false;
        IsSynced = false;
        HasSyncedLyricsAvailable = false;
        ActiveLineIndex = -1;

        // Build album info text: "Genre · Year · N tracks"
        var infoParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(track.Genre)) infoParts.Add(track.Genre);
        if (track.Year > 0) infoParts.Add(track.Year.ToString());
        if (track.TrackCount > 0) infoParts.Add($"{track.TrackCount} tracks");
        AlbumInfoText = string.Join(" \u00B7 ", infoParts);

        // Priority 1: Try to load .lrc file with matching filename
        var lrcLines = TryLoadLrcFile(track.FilePath);
        if (lrcLines != null && lrcLines.Count > 0)
        {
            DebugLogger.Info(DebugLogger.Category.Lyrics, "Source:SidecarLrc", $"lines={lrcLines.Count}");
            _hasSyncedLyrics = lrcLines.Any(l => l.IsSynced);
            IsSynced = _hasSyncedLyrics;
            HasSyncedLyricsAvailable = _hasSyncedLyrics;

            if (!_hasSyncedLyrics)
            {
                foreach (var line in lrcLines)
                    line.IsActive = true;
            }
            else
            {
                InsertIntroPlaceholderIfNeeded(lrcLines);
            }

            foreach (var line in lrcLines)
                LyricLines.Add(line);

            // Populate unsynced lines (plain text of all lyrics)
            PopulateUnsyncedLines(lrcLines);
            AutoSelectTab();
            LyricsSourceName = string.Empty;
            return;
        }

        // Priority 2: Try track metadata (SyncedLyrics + Lyrics fields)
        var hasSyncedField = !string.IsNullOrWhiteSpace(track.SyncedLyrics);
        var hasPlainField = !string.IsNullOrWhiteSpace(track.Lyrics);

        // Legacy check: plain Lyrics field may contain LRC timestamps
        var plainIsActuallyLrc = hasPlainField
                                 && !hasSyncedField
                                 && track.Lyrics.Contains("[")
                                 && LrcTimestampRegex().IsMatch(track.Lyrics);

        if (hasSyncedField || plainIsActuallyLrc || hasPlainField)
        {
            DebugLogger.Info(DebugLogger.Category.Lyrics, "Source:Embedded", $"synced={hasSyncedField}, plain={hasPlainField}, lrcInPlain={plainIsActuallyLrc}");
            // Load synced lyrics
            var syncedSource = hasSyncedField ? track.SyncedLyrics
                             : plainIsActuallyLrc ? track.Lyrics
                             : null;

            if (!string.IsNullOrWhiteSpace(syncedSource))
            {
                var parsedLines = ParseLrcContent(syncedSource);
                _hasSyncedLyrics = parsedLines.Any(l => l.IsSynced);
                IsSynced = _hasSyncedLyrics;
                HasSyncedLyricsAvailable = _hasSyncedLyrics;

                if (_hasSyncedLyrics)
                    InsertIntroPlaceholderIfNeeded(parsedLines);

                foreach (var line in parsedLines)
                    LyricLines.Add(line);

                PopulateUnsyncedLines(parsedLines);
            }

            // Load plain/unsynced lyrics (if available and not already covered by synced)
            if (hasPlainField && !plainIsActuallyLrc)
            {
                var lines = track.Lyrics.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                if (!hasSyncedField)
                {
                    // No synced available — show plain in main view too
                    foreach (var line in lines)
                    {
                        var wrapped = SoftWrapText(line);
                        LyricLines.Add(new LyricLine { Text = wrapped, IsActive = true });
                        UnsyncedLines.Add(new LyricLine { Text = wrapped, IsActive = true });
                    }
                }
                else
                {
                    // Both synced and plain exist — plain goes to unsynced tab only
                    UnsyncedLines.Clear();
                    foreach (var line in lines)
                        UnsyncedLines.Add(new LyricLine { Text = SoftWrapText(line), IsActive = true });
                }
            }

            AutoSelectTab();
            LyricsSourceName = string.Empty;
            return;
        }

        // Priority 3: Try cached lyrics from previous online download
        var cachedContent = TryLoadCachedLyrics(track.Id);
        if (!string.IsNullOrWhiteSpace(cachedContent))
        {
            DebugLogger.Info(DebugLogger.Category.Lyrics, "Source:LrclibCache", $"trackId={track.Id}");
            if (cachedContent.Contains("[") && LrcTimestampRegex().IsMatch(cachedContent))
            {
                var parsedLines = ParseLrcContent(cachedContent);
                _hasSyncedLyrics = parsedLines.Any(l => l.IsSynced);
                IsSynced = _hasSyncedLyrics;
                HasSyncedLyricsAvailable = _hasSyncedLyrics;

                if (_hasSyncedLyrics)
                    InsertIntroPlaceholderIfNeeded(parsedLines);

                foreach (var line in parsedLines)
                    LyricLines.Add(line);

                PopulateUnsyncedLines(parsedLines);
            }
            else
            {
                var lines = cachedContent.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                foreach (var line in lines)
                {
                    var wrapped = SoftWrapText(line);
                    LyricLines.Add(new LyricLine { Text = wrapped, IsActive = true });
                    UnsyncedLines.Add(new LyricLine { Text = wrapped, IsActive = true });
                }
            }
            AutoSelectTab();
            LyricsSourceName = string.Empty;
            CanRemoveLyrics = true; // cached lyrics came from online service — allow removal
            return;
        }

        // No lyrics found — show search button only
        DebugLogger.Warn(DebugLogger.Category.Lyrics, "NoLyricsFound", $"title={track.Title}, artist={track.Artist}");
        ShowSearchButton = true;
        AutoSelectTab();
    }

    private void PopulateUnsyncedLines(List<LyricLine> sourceLyrics)
    {
        foreach (var line in sourceLyrics)
        {
            // Skip intro placeholder "..."
            if (line.Timestamp == TimeSpan.Zero && line.Text == "...") continue;
            UnsyncedLines.Add(new LyricLine { Text = line.Text, IsActive = true });
        }
    }

    private void AutoSelectTab()
    {
        if (_hasSyncedLyrics)
        {
            IsSyncTabSelected = true;
            IsUnsyncTabSelected = false;
        }
        else
        {
            IsSyncTabSelected = false;
            IsUnsyncTabSelected = true;
        }
    }

    /// <summary>
    /// Tries to load a .lrc file matching the track's filename.
    /// Searches for: track.lrc, track.LRC in the same directory.
    /// </summary>
    private static List<LyricLine>? TryLoadLrcFile(string trackFilePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(trackFilePath);
            var nameWithoutExt = Path.GetFileNameWithoutExtension(trackFilePath);
            if (dir == null || nameWithoutExt == null) return null;

            // Try common LRC file name patterns
            string[] extensions = { ".lrc", ".LRC", ".Lrc" };
            foreach (var ext in extensions)
            {
                var lrcPath = Path.Combine(dir, nameWithoutExt + ext);
                if (File.Exists(lrcPath))
                {
                    var content = File.ReadAllText(lrcPath);
                    return ParseLrcContent(content);
                }
            }
        }
        catch
        {
            // Non-critical failure
        }

        return null;
    }

    /// <summary>
    /// If the first synced lyric starts after 2 seconds, inserts a "…" placeholder
    /// at timestamp zero. This matches Apple Music's "waiting for lyrics" behavior
    /// during intros — the placeholder becomes the active line until the first
    /// real lyric is reached.
    /// </summary>
    private static void InsertIntroPlaceholderIfNeeded(List<LyricLine> lines)
    {
        var firstSynced = lines.FirstOrDefault(l => l.IsSynced);
        if (firstSynced?.Timestamp != null && firstSynced.Timestamp.Value.TotalSeconds > 2)
        {
            lines.Insert(0, new LyricLine
            {
                Timestamp = TimeSpan.Zero,
                Text = "...",
                IsIntroPlaceholder = true
            });
        }
    }

    /// <summary>
    /// Splits a long lyric line into balanced halves at the word boundary closest to the midpoint.
    /// Recursively applies to each half if still too long. Produces clean, cinematic two-line wraps.
    /// </summary>
    private static string SoftWrapText(string text, int maxWidth = 25)
    {
        if (text.Length <= maxWidth) return text;

        // Find the space closest to the midpoint for two balanced halves
        var mid = text.Length / 2;
        int bestSpace = -1;
        var bestDist = int.MaxValue;

        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] != ' ') continue;
            var dist = Math.Abs(i - mid);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestSpace = i;
            }
        }

        if (bestSpace <= 0) return text;

        // Single split only — never more than 2 lines per lyric
        var line1 = text[..bestSpace];
        var line2 = text[(bestSpace + 1)..];

        // If either half is still too long for the active font size, split it too
        if (line1.Length > maxWidth)
            line1 = SoftWrapText(line1, maxWidth);
        if (line2.Length > maxWidth)
            line2 = SoftWrapText(line2, maxWidth);

        return line1 + "\n" + line2;
    }

    /// <summary>
    /// Parses LRC format content into LyricLine objects.
    /// Supports: [mm:ss.xx] text, [mm:ss] text, multiple timestamps per line.
    /// Ignores metadata tags like [ar:], [ti:], [al:], etc.
    /// </summary>
    private static List<LyricLine> ParseLrcContent(string content)
    {
        var lines = new List<LyricLine>();
        var rawLines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var offsetMs = ParseLrcOffsetMilliseconds(rawLines);

        foreach (var rawLine in rawLines)
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Offset is handled once globally before parsing timestamps.
            if (OffsetTagRegex().IsMatch(trimmed))
                continue;

            // Skip metadata tags like [ar:Artist], [ti:Title], [al:Album], [offset:], [length:]
            if (MetadataTagRegex().IsMatch(trimmed))
                continue;

            // Extract all timestamps from the line
            var matches = LrcTimestampRegex().Matches(trimmed);
            if (matches.Count > 0)
            {
                // Get the text after all timestamps
                var lastMatch = matches[^1];
                var text = trimmed[(lastMatch.Index + lastMatch.Length)..].Trim();

                // Skip empty timestamp lines — LRC files often end with
                // [03:24.00] (no text) as an end marker. If parsed, this empty
                // line becomes the "active" line and deactivates the previous
                // real lyric, making lyrics appear to stop early.
                if (string.IsNullOrWhiteSpace(text)) continue;

                // Create a LyricLine for each timestamp (handles multi-timestamp lines)
                foreach (Match match in matches)
                {
                    var timestamp = ParseLrcTimestamp(match.Value);
                    if (timestamp.HasValue)
                    {
                        var adjusted = timestamp.Value + TimeSpan.FromMilliseconds(offsetMs);
                        if (adjusted < TimeSpan.Zero)
                            adjusted = TimeSpan.Zero;

                        lines.Add(new LyricLine
                        {
                            Timestamp = adjusted,
                            Text = SoftWrapText(text)
                        });
                    }
                }
            }
            else
            {
                // No timestamp — add as unsynced line
                lines.Add(new LyricLine { Text = SoftWrapText(trimmed) });
            }
        }

        // Sort by timestamp for synced lyrics
        lines.Sort((a, b) =>
        {
            if (a.Timestamp == null && b.Timestamp == null) return 0;
            if (a.Timestamp == null) return 1;
            if (b.Timestamp == null) return -1;
            return a.Timestamp.Value.CompareTo(b.Timestamp.Value);
        });

        return lines;
    }

    private static int ParseLrcOffsetMilliseconds(string[] rawLines)
    {
        foreach (var rawLine in rawLines)
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var match = OffsetTagRegex().Match(trimmed);
            if (match.Success &&
                int.TryParse(match.Groups["offset"].Value, out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    /// <summary>
    /// Parses a single LRC timestamp like [01:23.45] or [01:23] into a TimeSpan.
    /// </summary>
    private static TimeSpan? ParseLrcTimestamp(string timestamp)
    {
        // Remove brackets
        var inner = timestamp.Trim('[', ']').Replace(',', '.');
        var parts = inner.Split(':');
        if (parts.Length < 2 || parts.Length > 3) return null;

        if (!int.TryParse(parts[0], out var minutes)) return null;

        if (parts.Length == 2)
        {
            // Seconds can be "23.45", "23,45", or "23"
            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                return null;

            return TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        }

        // Supports mm:ss:ff and mm:ss:fff variants.
        if (!int.TryParse(parts[1], out var wholeSeconds)) return null;
        if (!int.TryParse(parts[2], out var fractionalUnit)) return null;

        var divisor = Math.Pow(10, parts[2].Length);
        var fractionalSeconds = fractionalUnit / divisor;
        return TimeSpan.FromMinutes(minutes) +
               TimeSpan.FromSeconds(wholeSeconds + fractionalSeconds);
    }

    /// <summary>
    /// Seeks playback to the timestamp of a clicked lyric line.
    /// </summary>
    [RelayCommand]
    private void SeekToLine(LyricLine? line)
    {
        if (line?.Timestamp == null || _player.Duration.TotalSeconds <= 0) return;
        _player.SeekToPositionCommand.Execute(
            line.Timestamp.Value.TotalSeconds / _player.Duration.TotalSeconds);
    }

    /// <summary>
    /// Updates the currently active (highlighted) lyric line based on playback position.
    /// Called from OnPlayerPropertyChanged which fires on UI thread, so no extra dispatch needed.
    /// A 400ms lookahead compensates for VLC position polling latency + UI dispatch delay,
    /// ensuring lyrics highlight at the moment the vocal begins rather than after.
    /// </summary>
    private static readonly TimeSpan LyricsLookahead = TimeSpan.FromMilliseconds(350);

    private void UpdateActiveLine(TimeSpan position)
    {
        if (LyricLines.Count == 0) return;

        // Add lookahead so lyrics appear slightly early to match vocals
        var adjustedPosition = position + LyricsLookahead;

        LyricLine? bestMatch = null;
        int bestIndex = -1;

        for (int i = 0; i < LyricLines.Count; i++)
        {
            var line = LyricLines[i];
            if (line.Timestamp.HasValue && line.Timestamp.Value <= adjustedPosition)
            {
                bestMatch = line;
                bestIndex = i;
            }
        }

        // Safety: if no match found but we're past the start and have a current line,
        // keep the current line active (prevents "all dimmed" state from transient glitches).
        if (bestMatch == null && _currentActiveLine != null && position.TotalSeconds > 1)
            return;

        if (bestMatch != _currentActiveLine)
        {
            // Deactivate previous line
            if (_currentActiveLine != null)
                _currentActiveLine.IsActive = false;

            // Activate new line
            if (bestMatch != null)
                bestMatch.IsActive = true;

            _currentActiveLine = bestMatch;
            ActiveLineIndex = bestIndex;
            UpdateLineOpacities(bestIndex);
        }
        else if (bestMatch != null && !bestMatch.IsActive)
        {
            // Safety: ensure the active line stays active even if something reset it
            bestMatch.IsActive = true;
        }
    }

    /// <summary>
    /// Sets LineOpacity on each lyric line based on distance from the active line.
    /// Active=1.0, adjacent lines fade gradually over ±9 lines, rest=0.0 (hidden).
    /// Pass activeIndex=-1 to restore all lines to full opacity (e.g. unsynced or reset).
    /// </summary>
    private void UpdateLineOpacities(int activeIndex)
    {
        if (activeIndex < 0)
        {
            foreach (var line in LyricLines)
            {
                line.LineOpacity = 1.0;
                line.IsClickable = true;
            }
            return;
        }

        for (int i = 0; i < LyricLines.Count; i++)
        {
            var dist = i - activeIndex;
            var absDist = Math.Abs(dist);
            var opacity = absDist switch
            {
                 0 => 1.0,
                 1 => 0.55,
                 2 => 0.32,
                 3 => 0.18,
                 4 => 0.12,
                 5 => 0.08,
                 6 => 0.06,
                 7 => 0.04,
                 8 => 0.03,
                 9 => 0.02,
                 _ => 0.0
            };
            var line = LyricLines[i];
            // Only set if changed — avoids unnecessary PropertyChanged notifications and re-renders
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (line.LineOpacity != opacity)
                line.LineOpacity = opacity;
            var clickable = opacity > 0.0;
            if (line.IsClickable != clickable)
                line.IsClickable = clickable;
        }
    }

    [GeneratedRegex(@"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]")]
    private static partial Regex LrcTimestampRegex();

    [GeneratedRegex(@"^\[(ar|ti|al|by|offset|re|ve|length|id):")]
    private static partial Regex MetadataTagRegex();

    [GeneratedRegex(@"^\[offset:(?<offset>[+-]?\d+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTagRegex();

    public void Dispose()
    {
        // Stop and dispose timer to prevent memory leak
        _lyricsSyncTimer.Stop();

        // Unsubscribe from player events to prevent memory leak
        _player.TrackStarted -= OnTrackStarted;
        _player.PropertyChanged -= OnPlayerPropertyChanged;
    }
}
