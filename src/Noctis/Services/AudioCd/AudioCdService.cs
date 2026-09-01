using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Noctis.Models;

namespace Noctis.Services.AudioCd;

/// <summary>
/// The MRL / path conventions for audio CDs. Playback paths carry the track as a
/// fragment (<c>cdda:///D:/#3</c>) so the rest of the app can keep treating a track
/// as "a string the player knows how to open"; the player splits it back apart.
/// </summary>
public static class AudioCdPaths
{
    public const string Scheme = "cdda://";

    public static bool IsAudioCdPath(string? path) =>
        path != null && path.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>libvlc's cdda MRL for a drive: <c>cdda:///D:/</c> on Windows, <c>cdda:///dev/sr0</c> on Linux.</summary>
    public static string BuildDiscMrl(string driveRoot, bool isWindows)
    {
        if (isWindows)
        {
            var letter = driveRoot.TrimEnd('\\', '/');
            if (letter.Length == 1) letter += ":";
            return $"{Scheme}/{letter}/";
        }
        return $"{Scheme}{driveRoot}";
    }

    public static string BuildTrackPath(string discMrl, int trackNumber) => $"{discMrl}#{trackNumber}";

    /// <summary>Split <c>cdda:///D:/#3</c> into its disc MRL and 1-based track number.</summary>
    public static bool TryParseTrackPath(string? path, out string discMrl, out int trackNumber)
    {
        discMrl = string.Empty;
        trackNumber = 0;
        if (!IsAudioCdPath(path)) return false;
        var hash = path!.LastIndexOf('#');
        if (hash <= Scheme.Length) return false;
        if (!int.TryParse(path.AsSpan(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
            return false;
        discMrl = path[..hash];
        trackNumber = n;
        return true;
    }

    /// <summary>Hash of the track lengths — stable for a given pressing, independent of drive letter.</summary>
    public static string ComputeDiscId(IReadOnlyList<AudioCdTrackInfo> tracks)
    {
        var sb = new StringBuilder();
        foreach (var t in tracks)
            sb.Append((long)t.Duration.TotalMilliseconds).Append(';');
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// Watches optical drives, reads the loaded disc once, and exposes its tracks as
/// playable <see cref="Track"/>s. Reads are serialized and stamped with a generation
/// so an eject during a read cannot publish a stale disc.
/// </summary>
public sealed class AudioCdService : IAudioCdService
{
    private const int PollIntervalMs = 2000;

    private readonly IAudioCdDriveProbe _probe;
    private readonly IAudioCdReader _reader;
    private readonly bool _isWindows;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly object _stateLock = new();
    private Timer? _poll;
    private bool _pollBusy;
    private int _generation;
    private bool _lastReady;
    private bool _disposed;

    private IReadOnlyList<string> _drives = Array.Empty<string>();
    private AudioCdDisc? _disc;
    private IReadOnlyList<Track> _tracks = Array.Empty<Track>();
    private bool _isReading;

    public AudioCdService(IAudioCdDriveProbe probe, IAudioCdReader reader, bool? isWindows = null, bool? isSupported = null)
    {
        _probe = probe;
        _reader = reader;
        _isWindows = isWindows ?? OperatingSystem.IsWindows();
        IsSupported = isSupported ?? (OperatingSystem.IsWindows() || OperatingSystem.IsLinux());
        if (IsSupported) RefreshDrives();
    }

    public bool IsSupported { get; }
    public bool HasDrive => _drives.Count > 0;
    public IReadOnlyList<string> Drives => _drives;
    public AudioCdDisc? CurrentDisc => _disc;
    public IReadOnlyList<Track> CurrentTracks => _tracks;
    public bool IsReading => _isReading;

    public event EventHandler? DriveStateChanged;
    public event EventHandler? DiscChanged;

    public void StartWatching()
    {
        if (!IsSupported || _poll != null || _disposed) return;
        _poll = new Timer(_ => Poll(), null, PollIntervalMs, PollIntervalMs);
    }

    /// <summary>One poll tick: drives, then (Windows) the cheap ready flag → read/eject transitions.</summary>
    internal void Poll()
    {
        if (_disposed || _pollBusy) return;
        _pollBusy = true;
        try
        {
            var drivesChanged = RefreshDrives();
            if (!HasDrive)
            {
                if (_disc != null || _isReading) ClearDisc();
                return;
            }
            if (!_probe.SupportsReadyProbe)
                return; // Linux: nothing cheap to poll; reads happen on demand.

            var ready = _drives.Any(_probe.IsDiscReady);
            if (ready && !_lastReady)
            {
                _lastReady = true;
                _ = RefreshAsync();
            }
            else if (!ready && _lastReady)
            {
                _lastReady = false;
                ClearDisc();
            }
            else if (drivesChanged && ready && _disc == null && !_isReading)
            {
                _ = RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "AudioCd.Poll", ex.Message);
        }
        finally
        {
            _pollBusy = false;
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!IsSupported || _disposed) return;
        RefreshDrives();
        if (!HasDrive)
        {
            ClearDisc();
            return;
        }

        var generation = Interlocked.Increment(ref _generation);
        await _readGate.WaitAsync(ct);
        try
        {
            if (generation != _generation) return; // a newer refresh is queued behind us
            SetReading(true);

            AudioCdDisc? found = null;
            foreach (var drive in _drives)
            {
                if (_probe.SupportsReadyProbe && !_probe.IsDiscReady(drive)) continue;
                var mrl = AudioCdPaths.BuildDiscMrl(drive, _isWindows);
                found = await _reader.ReadAsync(drive, mrl, ct);
                if (found != null) break;
            }

            if (generation != _generation) return;
            _lastReady = found != null || _lastReady;
            PublishDisc(found);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "AudioCd.Read", ex.Message);
            PublishDisc(null);
        }
        finally
        {
            SetReading(false);
            _readGate.Release();
        }
    }

    private bool RefreshDrives()
    {
        IReadOnlyList<string> drives;
        try { drives = _probe.GetOpticalDriveRoots(); }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "AudioCd.Drives", ex.Message);
            drives = Array.Empty<string>();
        }

        bool changed;
        lock (_stateLock)
        {
            changed = !drives.SequenceEqual(_drives, StringComparer.OrdinalIgnoreCase);
            if (changed) _drives = drives;
        }
        if (changed)
        {
            DebugLogger.Info(DebugLogger.Category.Playback, "AudioCd.Drives", $"count={drives.Count}");
            DriveStateChanged?.Invoke(this, EventArgs.Empty);
        }
        return changed;
    }

    private void SetReading(bool reading)
    {
        if (_isReading == reading) return;
        _isReading = reading;
        DiscChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearDisc()
    {
        Interlocked.Increment(ref _generation);
        PublishDisc(null);
    }

    private void PublishDisc(AudioCdDisc? disc)
    {
        var tracks = disc == null ? Array.Empty<Track>() : MapTracks(disc);
        lock (_stateLock)
        {
            if (ReferenceEquals(_disc, disc) && _disc == null) return;
            _disc = disc;
            _tracks = tracks;
        }
        DebugLogger.Info(DebugLogger.Category.Playback, "AudioCd.Disc",
            disc == null ? "none" : $"tracks={disc.Tracks.Count}, titled={disc.Tracks.Count(t => !string.IsNullOrWhiteSpace(t.Title))}");
        DiscChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Playable tracks for a disc. Pure, so tests can pin the mapping without a drive.</summary>
    public static IReadOnlyList<Track> MapTracks(AudioCdDisc disc)
    {
        var discId = disc.DiscId;
        var album = FirstNonEmpty(disc.Title, "Audio CD");
        var albumArtist = FirstNonEmpty(disc.Artist, "Unknown Artist");
        var list = new List<Track>(disc.Tracks.Count);
        foreach (var info in disc.Tracks)
        {
            var title = FirstNonEmpty(info.Title, $"Track {info.Number}");
            var artist = FirstNonEmpty(info.Artist, albumArtist);
            var track = new Track
            {
                Id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"audiocd:{discId}:{info.Number}"))),
                FilePath = AudioCdPaths.BuildTrackPath(disc.Mrl, info.Number),
                Title = title,
                Artist = artist,
                AlbumArtist = albumArtist,
                Album = FirstNonEmpty(info.Album, album),
                TrackNumber = info.Number,
                DiscNumber = 1,
                Duration = info.Duration < TimeSpan.Zero ? TimeSpan.Zero : info.Duration,
                Codec = "CDDA",
                SampleRate = 44100,
                BitsPerSample = 16,
                Bitrate = 1411,
                LastModified = DateTime.UtcNow,
                DateAdded = DateTime.UtcNow,
                SourceType = SourceType.AudioCd,
                SourceTrackId = info.Number.ToString(CultureInfo.InvariantCulture),
                SourceConnectionId = discId,
            };
            track.AlbumId = Track.ComputeAlbumId(track.AlbumArtist, track.Album);
            list.Add(track);
        }
        return list;
    }

    private static string FirstNonEmpty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll?.Dispose();
        _poll = null;
        _readGate.Dispose();
    }
}
