using Noctis.Models;
using Noctis.Services;

namespace Noctis.Tests;

#pragma warning disable CS0067 // events declared for the interface, raised selectively

/// <summary>No-op IAudioPlayer that records Play calls and can raise TrackEnded.</summary>
internal sealed class FakeAudioPlayer : IAudioPlayer
{
    public List<string> PlayedPaths { get; } = new();
    public List<string> PreparedPaths { get; } = new();

    public event EventHandler? TrackEnded;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler<string>? PlaybackError;
    public event EventHandler<TimeSpan>? DurationResolved;
    public event EventHandler<string>? OutputModeChanged;

    public PlaybackState State { get; private set; } = PlaybackState.Stopped;
    public TimeSpan Duration => TimeSpan.FromMinutes(3);
    public TimeSpan Position => TimeSpan.Zero;
    public TimeSpan OutputLatency => TimeSpan.Zero;
    public long CurrentSessionId { get; private set; }
    public int Volume { get; set; }
    public int VolumeAdjust { get; set; }
    public long PendingSeekMs { get; set; } = -1;
    public bool IsMuted { get; set; }
    public bool ExclusiveModeActive => false;
    public bool EqualizerActive { get; set; }
    public string OutputDescription => "test";
    public double ReplayGainAppliedDb => 0;
    public string? CurrentMediaPath { get; private set; }

    public void RaiseTrackEnded() => TrackEnded?.Invoke(this, EventArgs.Empty);
    public void RaisePlaybackError(string msg) => PlaybackError?.Invoke(this, msg);
    public void RaisePositionChanged(TimeSpan position) => PositionChanged?.Invoke(this, position);

    public void Play(string filePath)
    {
        PlayedPaths.Add(filePath);
        CurrentMediaPath = filePath;
        CurrentSessionId++;
        State = PlaybackState.Playing;
    }

    public void Pause() => State = PlaybackState.Paused;
    public void Resume() => State = PlaybackState.Playing;
    public void Stop() => State = PlaybackState.Stopped;
    public List<TimeSpan> Seeks { get; } = new();
    public void Seek(TimeSpan position) => Seeks.Add(position);
    public void CommitVolume() { }
    public void SetNormalization(bool enabled) { }
    public void SetExclusiveMode(bool enabled) { }
    public void ApplyReplayGain(string mode, double preampDb) { }
    public void SetCrossfade(bool enabled, int durationSeconds, AutoMixFadeCurve fadeCurve = AutoMixFadeCurve.SmoothEase, bool fadeOut = true, bool overlap = false) { }
    public bool GaplessEnabled { get; private set; } = true;
    public void SetGapless(bool enabled) => GaplessEnabled = enabled;
    public (bool Enabled, int DurationMs) PlayPauseFade { get; private set; }
    public void SetPlayPauseFade(bool enabled, int durationMs) => PlayPauseFade = (enabled, durationMs);
    public double PlaybackRate { get; private set; } = 1.0;
    public void SetPlaybackRate(double rate) => PlaybackRate = rate;
    public double PitchSemitones { get; private set; }
    public void SetPitchSemitones(double semitones) => PitchSemitones = semitones;
    public string UpmixMode { get; private set; } = "Off";
    public void SetUpmixMode(string mode) => UpmixMode = mode;
    public void PrepareNext(string filePath, long startPositionMs = -1) => PreparedPaths.Add(filePath);
    public bool PreparesRemoteStreams { get; set; }
    public int CancelledCount { get; private set; }
    public void CancelPreparedNext() => CancelledCount++;
    /// <summary>The last curve pushed by SetAdvancedEqualizer (null until the first call).</summary>
    public (bool Enabled, float[] Bands, float PreampDb)? LastEqualizer { get; private set; }
    public void SetAdvancedEqualizer(bool enabled, float[] bands, float preampDb) =>
        LastEqualizer = (enabled, (float[])bands.Clone(), preampDb);
    public void Dispose() { }
}

/// <summary>Empty ILibraryService for constructing ViewModels.</summary>
internal sealed class FakeLibraryService : ILibraryService
{
    public List<Track> TrackList { get; } = new();
    public IReadOnlyList<Track> Tracks => TrackList;
    public IReadOnlyList<Album> Albums { get; } = new List<Album>();
    public List<Artist> ArtistList { get; } = new();
    public IReadOnlyList<Artist> Artists => ArtistList;

    public event EventHandler? LibraryUpdated;
    public event EventHandler<int>? ScanProgress;

    public void RaiseLibraryUpdated() => LibraryUpdated?.Invoke(this, EventArgs.Empty);
    public bool IsPublishingPartial { get; set; }
    public event EventHandler? FavoritesChanged;
    public event EventHandler<List<string>>? MusicFoldersChanged;
    public event EventHandler<string[]>? ScanAborted;

    /// <summary>When set, ScanAsync behaves like the real service meeting an offline
    /// root: it raises ScanAborted with these roots and leaves the library untouched.</summary>
    public string[]? AbortScanWithRoots { get; set; }

    public List<string> ScannedFolders { get; } = new();

    public Task ScanAsync(IEnumerable<string> folders, CancellationToken ct = default)
    {
        ScannedFolders.AddRange(folders);
        if (AbortScanWithRoots is { } roots)
            ScanAborted?.Invoke(this, roots);
        return Task.CompletedTask;
    }
    public Task PauseActiveScanForShutdownAsync(TimeSpan timeout) => Task.CompletedTask;
    public Task ImportFilesAsync(IEnumerable<string> filePaths, CancellationToken ct = default, IProgress<int>? progress = null) => Task.CompletedTask;
    public Track? GetTrackById(Guid id) => TrackList.FirstOrDefault(t => t.Id == id);
    public Album? GetAlbumById(Guid id) => Albums.FirstOrDefault(a => a.Id == id);
    /// <summary>Albums GetAlbumsByArtist answers from (by Artist); empty unless a test fills it.</summary>
    public List<Album> ArtistAlbums { get; } = new();
    public IReadOnlyList<Album> GetAlbumsByArtist(string artistName) =>
        ArtistAlbums.Where(a => string.Equals(a.Artist, artistName, StringComparison.OrdinalIgnoreCase)).ToList();
    public Task RemoveTrackAsync(Guid id) => Task.CompletedTask;
    public Task RemoveTracksAsync(IEnumerable<Guid> ids) => Task.CompletedTask;
    /// <summary>Old id → new id that RelocateTracksAsync reports (empty by default).</summary>
    public Dictionary<Guid, Guid> RelocateRemap { get; } = new();
    public Task<IReadOnlyDictionary<Guid, Guid>> RelocateTracksAsync(IReadOnlyList<(string oldPath, string newPath)> moves, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(RelocateRemap);
    public Task LoadAsync() => Task.CompletedTask;
    public Task SaveAsync() => Task.CompletedTask;
    public Task SaveTrackUserStateAsync(IReadOnlyCollection<Track> tracks) => Task.CompletedTask;
    public Task ClearAsync() => Task.CompletedTask;
    public Task RebuildIndexAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void NotifyFavoritesChanged() => NotifyFavoritesChanged(null);
    public void NotifyFavoritesChanged(IReadOnlyCollection<Track>? changed)
    {
        foreach (var a in Albums) a.NotifyFavoriteStateChanged();
        FavoritesChanged?.Invoke(this, EventArgs.Empty);
    }
    public Task SetTracksRatingAsync(IReadOnlyList<Track> tracks, int rating) => Task.CompletedTask;
    public Task SetTracksBadgeAsync(IReadOnlyList<Track> tracks, string? badge)
    {
        foreach (var t in tracks) t.Badge = string.IsNullOrWhiteSpace(badge) ? null : badge.Trim();
        return Task.CompletedTask;
    }
    public IReadOnlyList<string> GetBadgeNames() => TrackList.Select(t => t.Badge).Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(b => b).ToList();
    public Task SetTracksDislikedAsync(IReadOnlyList<Track> tracks, bool isDisliked) => Task.CompletedTask;
    public Task SetTracksSnoozedAsync(IReadOnlyList<Track> tracks, DateTime? until) => Task.CompletedTask;
    public void NotifyMetadataChanged() { }
    public Task<int> ApplyMergeFeaturedFromTitlesAsync(bool enabled, CancellationToken ct = default) => Task.FromResult(0);
    public Task<int> BackfillMissingArtworkAsync(CancellationToken ct = default) => Task.FromResult(0);
}

internal sealed class FakeAnimatedCoverService : IAnimatedCoverService
{
    public string? Resolve(Track track) => null;
    public Task<string> ImportAsync(Track track, string sourcePath, AnimatedCoverScope scope) => Task.FromResult(string.Empty);
    public Task RemoveAsync(Track track, AnimatedCoverScope scope) => Task.CompletedTask;
}

#pragma warning restore CS0067
