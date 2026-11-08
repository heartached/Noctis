using Noctis.Models;
using Noctis.Services;

namespace Noctis.Tests;

#pragma warning disable CS0067 // events declared for the interface, raised selectively

/// <summary>No-op IAudioPlayer that records Play calls and can raise TrackEnded.</summary>
internal sealed class FakeAudioPlayer : IAudioPlayer
{
    public List<string> PlayedPaths { get; } = new();
    /// <summary>The fallback passed with each Play (null for a plain Play), index-aligned with PlayedPaths.</summary>
    public List<string?> PlayedFallbacks { get; } = new();
    public List<string> PreparedPaths { get; } = new();
    public List<TimeSpan> Seeks { get; } = new();
    /// <summary>Play calls on a <see cref="MissingPaths"/> path (not in PlayedPaths).</summary>
    public List<string> MissingPlayAttempts { get; } = new();

    public event EventHandler? TrackEnded;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler<string>? PlaybackError;
    public event EventHandler<TimeSpan>? DurationResolved;
    public event EventHandler<string>? OutputModeChanged;

    public PlaybackState State { get; private set; } = PlaybackState.Stopped;
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(3);
    public TimeSpan Position { get; set; } = TimeSpan.Zero;
    public TimeSpan OutputLatency { get; set; } = TimeSpan.Zero;
    public long CurrentSessionId { get; private set; }
    public int Volume { get; set; }
    public int VolumeAdjust { get; set; }
    public long PendingSeekMs { get; set; } = -1;
    public bool IsMuted { get; set; }
    public bool ExclusiveModeActive => false;
    /// <summary>Settable so a test can play an output chain that taps the meters itself.</summary>
    public bool OutputTapsMeters { get; set; }
    public bool EqualizerActive { get; set; }
    public string OutputDescription => "test";
    public double ReplayGainAppliedDb => 0;
    /// <summary>Settable so a test can play the engine falling back to the song file.</summary>
    public string? CurrentMediaPath { get; set; }

    public void RaiseTrackEnded() => TrackEnded?.Invoke(this, EventArgs.Empty);
    public void RaisePlaybackError(string msg) => PlaybackError?.Invoke(this, msg);
    public void RaisePositionChanged(TimeSpan position) => PositionChanged?.Invoke(this, position);
    public void RaiseDurationResolved(TimeSpan duration) => DurationResolved?.Invoke(this, duration);

    public void Play(string filePath) => Play(filePath, null);

    /// <summary>Paths that are "not on disk": <see cref="SourceExists"/> says so and Play
    /// reports "File not found" through PlaybackError, as VlcAudioPlayer does.</summary>
    public HashSet<string> MissingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool SourceExists(string filePath) => !MissingPaths.Contains(filePath);

    public void Play(string filePath, string? fallbackPath)
    {
        if (MissingPaths.Contains(filePath))
        {
            MissingPlayAttempts.Add(filePath);
            PlaybackError?.Invoke(this, $"File not found: {filePath}");
            return;
        }
        PlayedPaths.Add(filePath);
        PlayedFallbacks.Add(fallbackPath);
        CurrentMediaPath = filePath;
        CurrentSessionId++;
        State = PlaybackState.Playing;
    }

    public void Pause() => State = PlaybackState.Paused;
    public void Resume() => State = PlaybackState.Playing;
    public void Stop() => State = PlaybackState.Stopped;
    public void Seek(TimeSpan position) => Seeks.Add(position);
    public void CommitVolume() { }
    public void SetNormalization(bool enabled) { }
    /// <summary>Every SetExclusiveMode call, in order.</summary>
    public List<bool> ExclusiveRequests { get; } = new();
    public void SetExclusiveMode(bool enabled) => ExclusiveRequests.Add(enabled);
    public bool ExclusiveModePaused { get; set; }
    public void RaiseOutputModeChanged(string status) => OutputModeChanged?.Invoke(this, status);
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
    public bool HeadphoneSurroundActive { get; set; }
    public bool MultichannelOutputActive { get; set; }
    public (bool Mono, double Balance) ChannelMix { get; private set; }
    public void SetChannelMix(bool mono, double balance) => ChannelMix = (mono, balance);
    public bool ChannelMixAvailable { get; set; } = true;
    /// <summary>Every SetPlaybackFadeGain call, in order.</summary>
    public List<(double Gain, int RampMs)> FadeGainCalls { get; } = new();
    public double FadeGain => FadeGainCalls.Count == 0 ? 1.0 : FadeGainCalls[^1].Gain;
    public void SetPlaybackFadeGain(double gain, int rampMs) => FadeGainCalls.Add((gain, rampMs));
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
    /// <summary>The visible songs: <see cref="TrackList"/> minus those hidden by path (as
    /// LibraryService's Hide Folder / Hide Song), the same list when nothing is hidden.</summary>
    public IReadOnlyList<Track> Tracks => HiddenList.Count == 0
        ? TrackList
        : TrackList.Where(t => !Noctis.Helpers.FolderPathMatch.IsInOrSameAsAny(t.FilePath, HiddenList)).ToList();
    public IReadOnlyList<Track> AllTracks => TrackList;
    public List<string> HiddenList { get; } = new();
    public IReadOnlyList<string> HiddenFolders => HiddenList;
    public Task SetFolderHiddenAsync(string folderPath, bool hidden)
    {
        var isHidden = HiddenList.Any(f => Noctis.Helpers.FolderPathMatch.IsSame(f, folderPath));
        if (isHidden == hidden) return Task.CompletedTask;
        if (hidden) HiddenList.Add(folderPath);
        else HiddenList.RemoveAll(f => Noctis.Helpers.FolderPathMatch.IsSame(f, folderPath));
        LibraryUpdated?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
    public IReadOnlyList<Album> Albums { get; } = new List<Album>();
    public List<Artist> ArtistList { get; } = new();
    public IReadOnlyList<Artist> Artists => ArtistList;

    public event EventHandler? LibraryUpdated;
    public event EventHandler<int>? ScanProgress;

    public void RaiseLibraryUpdated() => LibraryUpdated?.Invoke(this, EventArgs.Empty);

    /// <summary>What <see cref="ReadReplayGainSoon"/> was asked for (GitHub #158).</summary>
    public List<Track> ReplayGainSoonRequests { get; } = new();
    public void ReadReplayGainSoon(IEnumerable<Track> tracks)
    {
        lock (ReplayGainSoonRequests) ReplayGainSoonRequests.AddRange(tracks);
    }
    public void RaiseScanProgress(int count) => ScanProgress?.Invoke(this, count);
    public bool IsPublishingPartial { get; set; }
    public event EventHandler? FavoritesChanged;
    public event EventHandler<List<string>>? MusicFoldersChanged;
    public event EventHandler<string[]>? ScanAborted;
    public event EventHandler<IReadOnlyList<Track>>? TracksAdded;
    public void RaiseTracksAdded(IReadOnlyList<Track> tracks) => TracksAdded?.Invoke(this, tracks);

    /// <summary>When set, ScanAsync behaves like the real service meeting an offline
    /// root: it raises ScanAborted with these roots and leaves the library untouched.</summary>
    public string[]? AbortScanWithRoots { get; set; }

    public List<string> ScannedFolders { get; } = new();

    /// <summary>Each scan's folders, one entry per ScanAsync call.</summary>
    public List<List<string>> ScanPasses { get; } = new();

    /// <summary>When set, a scan stays running until the test completes it.</summary>
    public TaskCompletionSource? HoldScan { get; set; }

    public Task ScanAsync(IEnumerable<string> folders, CancellationToken ct = default)
    {
        var list = folders.ToList();
        ScannedFolders.AddRange(list);
        ScanPasses.Add(list);
        if (AbortScanWithRoots is { } roots)
            ScanAborted?.Invoke(this, roots);
        if (HoldScan is { } hold)
        {
            HoldScan = null;
            return hold.Task;
        }
        return Task.CompletedTask;
    }
    public Task PauseActiveScanForShutdownAsync(TimeSpan timeout) => Task.CompletedTask;
    /// <summary>Every ImportFilesAsync call's paths, in order.</summary>
    public List<List<string>> ImportCalls { get; } = new();

    /// <summary>When set, an import adds a track per new path (as the real service does for a file it can read).</summary>
    public bool ImportAddsTracks { get; set; }

    /// <summary>Whether each import call ran on the UI thread.</summary>
    public List<bool> ImportOnUiThread { get; } = new();

    public Task ImportFilesAsync(IEnumerable<string> filePaths, CancellationToken ct = default, IProgress<int>? progress = null)
    {
        var list = filePaths.ToList();
        ImportCalls.Add(list);
        ImportOnUiThread.Add(Avalonia.Threading.Dispatcher.UIThread.CheckAccess());
        if (ImportAddsTracks)
            foreach (var path in list.Where(p => !TrackList.Any(t => string.Equals(t.FilePath, p, StringComparison.OrdinalIgnoreCase))))
                TrackList.Add(new Track { Title = Path.GetFileNameWithoutExtension(path), Artist = "Artist", Album = "Album", FilePath = path });
        return Task.CompletedTask;
    }
    public Track? GetTrackById(Guid id) => TrackList.FirstOrDefault(t => t.Id == id);
    public Album? GetAlbumById(Guid id) => Albums.FirstOrDefault(a => a.Id == id);
    /// <summary>Albums GetAlbumsByArtist answers from (by Artist); empty unless a test fills it.</summary>
    public List<Album> ArtistAlbums { get; } = new();
    public IReadOnlyList<Album> GetAlbumsByArtist(string artistName) =>
        ArtistAlbums.Where(a => string.Equals(a.Artist, artistName, StringComparison.OrdinalIgnoreCase)).ToList();
    /// <summary>Every RemoveTrackAsync id, in order.</summary>
    public List<Guid> RemovedTrackIds { get; } = new();
    public Task RemoveTrackAsync(Guid id)
    {
        RemovedTrackIds.Add(id);
        return Task.CompletedTask;
    }
    public Task RemoveTracksAsync(IEnumerable<Guid> ids) => Task.CompletedTask;
    /// <summary>Old id → new id that RelocateTracksAsync reports (empty by default).</summary>
    public Dictionary<Guid, Guid> RelocateRemap { get; } = new();
    public Task<IReadOnlyDictionary<Guid, Guid>> RelocateTracksAsync(IReadOnlyList<(string oldPath, string newPath)> moves, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(RelocateRemap);
    public Task LoadAsync() => Task.CompletedTask;
    /// <summary>Number of full library saves requested.</summary>
    public int SaveCount { get; private set; }
    public Task SaveAsync() { SaveCount++; return Task.CompletedTask; }
    public Task SaveTrackUserStateAsync(IReadOnlyCollection<Track> tracks) => Task.CompletedTask;
    public Task ClearAsync() => Task.CompletedTask;
    public Task RebuildIndexAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void NotifyFavoritesChanged() => NotifyFavoritesChanged(null);
    public void NotifyFavoritesChanged(IReadOnlyCollection<Track>? changed)
    {
        foreach (var a in Albums) a.NotifyFavoriteStateChanged();
        FavoritesChanged?.Invoke(this, EventArgs.Empty);
    }
    public Task SetTracksRatingAsync(IReadOnlyList<Track> tracks, int rating)
    {
        foreach (var t in tracks) t.Rating = Math.Clamp(rating, 0, 5);
        return Task.CompletedTask;
    }
    public Task SetTracksBadgeAsync(IReadOnlyList<Track> tracks, string? badge)
    {
        foreach (var t in tracks) t.Badge = string.IsNullOrWhiteSpace(badge) ? null : badge.Trim();
        return Task.CompletedTask;
    }
    public IReadOnlyList<string> GetBadgeNames() => TrackList.Select(t => t.Badge).Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(b => b).ToList();
    public Task SetTracksDislikedAsync(IReadOnlyList<Track> tracks, bool isDisliked) => Task.CompletedTask;
    /// <summary>Every SetTracksSnoozedAsync call, in order (tracks copied, so later edits don't alter it).</summary>
    public List<(List<Track> Tracks, DateTime? Until)> SnoozeCalls { get; } = new();
    public Task SetTracksSnoozedAsync(IReadOnlyList<Track> tracks, DateTime? until)
    {
        SnoozeCalls.Add((tracks.ToList(), until));
        foreach (var t in tracks) t.SnoozedUntil = until;
        return Task.CompletedTask;
    }
    public int MetadataChangedCount { get; private set; }
    public void NotifyMetadataChanged() => MetadataChangedCount++;

    /// <summary>What GroupIntoAlbumAsync was asked for (GitHub #178).</summary>
    public List<(IReadOnlyList<Track> Tracks, string Album, string AlbumArtist)> GroupCalls { get; } = new();
    public AlbumGroupResult GroupResult { get; set; } = new(Guid.Empty, 0, Array.Empty<string>());
    public Task<AlbumGroupResult> GroupIntoAlbumAsync(IReadOnlyList<Track> tracks, string album, string albumArtist,
        CancellationToken ct = default)
    {
        GroupCalls.Add((tracks, album, albumArtist));
        return Task.FromResult(GroupResult);
    }

    /// <summary>What Settings asked of the album grouping (GitHub #193 / #190).</summary>
    public List<bool> AlbumGroupingCalls { get; } = new();
    public List<bool> AlbumGroupingWriteTagsCalls { get; } = new();
    public int AlbumGroupingMoved { get; set; }
    public AlbumGroupResult AlbumGroupingWriteResult { get; set; } = new(Guid.Empty, 0, Array.Empty<string>());
    public Task<int> SetAlbumGroupingAsync(bool enabled, CancellationToken ct = default)
    {
        AlbumGroupingCalls.Add(enabled);
        return Task.FromResult(AlbumGroupingMoved);
    }
    public Task<AlbumGroupResult> SetAlbumGroupingWriteTagsAsync(bool enabled, CancellationToken ct = default)
    {
        AlbumGroupingWriteTagsCalls.Add(enabled);
        return Task.FromResult(AlbumGroupingWriteResult);
    }
    public event EventHandler<AlbumGroupResult>? AlbumGroupingWritten;
    public void RaiseAlbumGroupingWritten(AlbumGroupResult result) => AlbumGroupingWritten?.Invoke(this, result);
    public Task<int> ApplyMergeFeaturedFromTitlesAsync(bool enabled, CancellationToken ct = default) => Task.FromResult(0);
    public Task<int> ApplyArtistCreditJoinAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task<int> BackfillMissingArtworkAsync(CancellationToken ct = default) => Task.FromResult(0);
}

/// <summary>In-memory ITrackFileAccess: sidecars by extension (any path), optional audio bytes.</summary>
internal sealed class FakeTrackFiles : ITrackFileAccess
{
    public Dictionary<string, string> Sidecars { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[]? Audio { get; set; }
    public int SidecarReads { get; private set; }

    public SidecarFile? ReadSidecar(string trackPath, IReadOnlyList<string> extensions)
    {
        SidecarReads++;
        foreach (var ext in extensions)
            if (Sidecars.TryGetValue(ext, out var text))
                return new SidecarFile(ext, System.Text.Encoding.UTF8.GetBytes(text));
        return null;
    }

    public Stream? OpenAudio(string trackPath) => Audio == null ? null : new MemoryStream(Audio);
}

internal sealed class FakeAnimatedCoverService : IAnimatedCoverService
{
    public string? Resolve(Track track) => null;
    public Task<string> ImportAsync(Track track, string sourcePath, AnimatedCoverScope scope) => Task.FromResult(string.Empty);
    public Task RemoveAsync(Track track, AnimatedCoverScope scope) => Task.CompletedTask;
}

/// <summary>In-memory play log. RecordPlay appends like PlayHistoryService; Seed adds an
/// event at a chosen time so tests can build history.</summary>
internal sealed class FakeHistoryLog : IPlayHistoryService
{
    public List<PlayHistoryEvent> EventList { get; } = new();
    public IReadOnlyList<PlayHistoryEvent> Events => EventList;
    public Task PreloadAsync() => Task.CompletedTask;
    public void RecordPlay(Track track) => Seed(track, DateTime.UtcNow);
    public void RecordSkip(Track track) { }
    public void SetListened(Track track, TimeSpan listened)
    {
        var e = EventList.LastOrDefault(x => x.TrackId == track.Id);
        if (e != null) e.ListenedMs = (long)listened.TotalMilliseconds;
    }
    public Task FlushAsync() => Task.CompletedTask;
    public int PruneOlderThan(DateTime cutoffUtc) => EventList.RemoveAll(e => e.PlayedAtUtc < cutoffUtc);
    public Task ClearAsync() { EventList.Clear(); return Task.CompletedTask; }

    public void Seed(Track track, DateTime playedAtUtc) => EventList.Add(new PlayHistoryEvent
    {
        TrackId = track.Id, Title = track.Title, Artist = track.Artist, PlayedAtUtc = playedAtUtc,
    });
}

#pragma warning restore CS0067
