using Noctis.Models;
using Noctis.Services;

namespace Noctis.Tests;

#pragma warning disable CS0067 // events declared for the interface, raised selectively

/// <summary>No-op IAudioPlayer that records Play calls and can raise TrackEnded.</summary>
internal sealed class FakeAudioPlayer : IAudioPlayer
{
    public List<string> PlayedPaths { get; } = new();

    public event EventHandler? TrackEnded;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler<string>? PlaybackError;
    public event EventHandler<TimeSpan>? DurationResolved;
    public event EventHandler<string>? OutputModeChanged;

    public PlaybackState State { get; private set; } = PlaybackState.Stopped;
    public TimeSpan Duration => TimeSpan.FromMinutes(3);
    public TimeSpan Position => TimeSpan.Zero;
    public long CurrentSessionId { get; private set; }
    public int Volume { get; set; }
    public int VolumeAdjust { get; set; }
    public long PendingSeekMs { get; set; } = -1;
    public bool IsMuted { get; set; }
    public bool ExclusiveModeActive => false;
    public string OutputDescription => "test";
    public double ReplayGainAppliedDb => 0;
    public string? CurrentMediaPath { get; private set; }

    public void RaiseTrackEnded() => TrackEnded?.Invoke(this, EventArgs.Empty);

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
    public void Seek(TimeSpan position) { }
    public void CommitVolume() { }
    public void SetNormalization(bool enabled) { }
    public void SetExclusiveMode(bool enabled) { }
    public void ApplyReplayGain(string mode, double preampDb) { }
    public void SetCrossfade(bool enabled, int durationSeconds, AutoMixFadeCurve fadeCurve = AutoMixFadeCurve.SmoothEase, bool fadeOut = true, bool overlap = false) { }
    public void SetGapless(bool enabled) { }
    public void PrepareNext(string filePath, long startPositionMs = -1) { }
    public void CancelPreparedNext() { }
    public void SetAdvancedEqualizer(bool enabled, float[] bands, float preampDb) { }
    public void Dispose() { }
}

/// <summary>Empty ILibraryService for constructing ViewModels.</summary>
internal sealed class FakeLibraryService : ILibraryService
{
    public List<Track> TrackList { get; } = new();
    public IReadOnlyList<Track> Tracks => TrackList;
    public IReadOnlyList<Album> Albums { get; } = new List<Album>();
    public IReadOnlyList<Artist> Artists { get; } = new List<Artist>();

    public event EventHandler? LibraryUpdated;
    public event EventHandler<int>? ScanProgress;
    public event EventHandler? FavoritesChanged;

    public Task ScanAsync(IEnumerable<string> folders, CancellationToken ct = default) => Task.CompletedTask;
    public Task PauseActiveScanForShutdownAsync(TimeSpan timeout) => Task.CompletedTask;
    public Task ImportFilesAsync(IEnumerable<string> filePaths, CancellationToken ct = default, IProgress<int>? progress = null) => Task.CompletedTask;
    public Track? GetTrackById(Guid id) => TrackList.FirstOrDefault(t => t.Id == id);
    public Album? GetAlbumById(Guid id) => null;
    public IReadOnlyList<Album> GetAlbumsByArtist(string artistName) => Array.Empty<Album>();
    public Task RemoveTrackAsync(Guid id) => Task.CompletedTask;
    public Task RemoveTracksAsync(IEnumerable<Guid> ids) => Task.CompletedTask;
    public Task<IReadOnlyDictionary<Guid, Guid>> RelocateTracksAsync(IReadOnlyList<(string oldPath, string newPath)> moves, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(new Dictionary<Guid, Guid>());
    public Task LoadAsync() => Task.CompletedTask;
    public Task SaveAsync() => Task.CompletedTask;
    public Task ClearAsync() => Task.CompletedTask;
    public Task RebuildIndexAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void NotifyFavoritesChanged() { }
    public Task SetTracksRatingAsync(IReadOnlyList<Track> tracks, int rating) => Task.CompletedTask;
    public Task SetTracksDislikedAsync(IReadOnlyList<Track> tracks, bool isDisliked) => Task.CompletedTask;
    public Task SetTracksSnoozedAsync(IReadOnlyList<Track> tracks, DateTime? until) => Task.CompletedTask;
    public void NotifyMetadataChanged() { }
}

internal sealed class FakeAnimatedCoverService : IAnimatedCoverService
{
    public string? Resolve(Track track) => null;
    public Task<string> ImportAsync(Track track, string sourcePath, AnimatedCoverScope scope) => Task.FromResult(string.Empty);
    public Task RemoveAsync(Track track, AnimatedCoverScope scope) => Task.CompletedTask;
}

#pragma warning restore CS0067
