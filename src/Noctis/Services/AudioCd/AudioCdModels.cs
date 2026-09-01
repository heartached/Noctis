using Noctis.Models;

namespace Noctis.Services.AudioCd;

/// <summary>One track as reported by the disc's table of contents (plus CD-Text / CDDB when available).</summary>
public sealed record AudioCdTrackInfo(int Number, string? Title, string? Artist, string? Album, TimeSpan Duration, string? Mrl);

/// <summary>An audio CD that was successfully read from a drive.</summary>
public sealed record AudioCdDisc(string DriveRoot, string Mrl, IReadOnlyList<AudioCdTrackInfo> Tracks, string? Title, string? Artist)
{
    /// <summary>
    /// Stable id for this disc built from its track lengths (the same "disc id" idea
    /// CDDB uses). Lets track ids stay the same across insertions of the same CD.
    /// </summary>
    public string DiscId => AudioCdPaths.ComputeDiscId(Tracks);
}

/// <summary>Enumerates optical drives and, where the OS can tell cheaply, whether a disc is loaded.</summary>
public interface IAudioCdDriveProbe
{
    /// <summary>Drive roots: "D:\" on Windows, "/dev/sr0" on Linux. Empty when there is no optical drive.</summary>
    IReadOnlyList<string> GetOpticalDriveRoots();

    /// <summary>
    /// True when <see cref="IsDiscReady"/> is meaningful. Windows answers through
    /// DriveInfo.IsReady; Linux device nodes exist with or without a disc, so the
    /// only way to know there is to read it.
    /// </summary>
    bool SupportsReadyProbe { get; }

    bool IsDiscReady(string driveRoot);
}

/// <summary>Reads a disc's table of contents. Returns null when there is no audio CD in that drive.</summary>
public interface IAudioCdReader
{
    Task<AudioCdDisc?> ReadAsync(string driveRoot, string mrl, CancellationToken ct = default);
}

/// <summary>
/// The app-level Audio CD source: which drives exist, what disc is loaded, and the
/// disc's tracks as playable <see cref="Track"/>s. Tracks never enter the library;
/// they live only while the disc is in the drive (same lifecycle as media-server tracks).
/// </summary>
public interface IAudioCdService : IDisposable
{
    /// <summary>False on platforms without optical-drive support (macOS).</summary>
    bool IsSupported { get; }
    bool HasDrive { get; }
    IReadOnlyList<string> Drives { get; }
    AudioCdDisc? CurrentDisc { get; }
    IReadOnlyList<Track> CurrentTracks { get; }
    bool IsReading { get; }

    /// <summary>The set of optical drives changed (or was first enumerated).</summary>
    event EventHandler? DriveStateChanged;

    /// <summary>The loaded disc changed (inserted, ejected, re-read) or <see cref="IsReading"/> flipped.</summary>
    event EventHandler? DiscChanged;

    /// <summary>Re-enumerate drives and re-read the disc. Safe to call repeatedly; reads are serialized.</summary>
    Task RefreshAsync(CancellationToken ct = default);

    /// <summary>Begin polling for drive/disc changes (no-op when unsupported or already started).</summary>
    void StartWatching();
}
