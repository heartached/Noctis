using System.Text;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.Services.Account;

/// <summary>
/// The lyrics page's file access, with desktop songs added: for a <c>noctis-remote://</c> path
/// the "sidecars" are the texts the desktop found beside the song, and the audio (for SYLT) is
/// the downloaded copy if there is one; any other path goes to the platform's own access. Paired
/// with <see cref="ForLoad"/>, which hands the loader the desktop's stored lyrics for the song, so
/// <see cref="LyricsLoader"/> keeps its one priority order (sidecars, stored synced, LRC in the
/// plain field, SYLT, plain) for desktop songs as for local ones.
///
/// Blocking: a lookup may ask the desktop (at most <see cref="Timeout"/>, then the phone's saved
/// copy). Only for the loader's background thread, never the UI thread. Never throws.
/// </summary>
public sealed class RemoteLyricsFileAccess : ITrackFileAccess
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    private readonly ITrackFileAccess _inner;
    private readonly IRemoteLyricsSource _source;

    public RemoteLyricsFileAccess(ITrackFileAccess inner, IRemoteLyricsSource source, TimeSpan? timeout = null)
    {
        _inner = inner;
        _source = source;
        Timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>Longest wait for the desktop per lookup.</summary>
    public TimeSpan Timeout { get; }

    public SidecarFile? ReadSidecar(string trackPath, IReadOnlyList<string> extensions)
    {
        if (!NoctisRemoteIds.TryParsePath(trackPath, out var id)) return _inner.ReadSidecar(trackPath, extensions);
        var lyrics = Lyrics(id);
        if (lyrics is null) return null;
        foreach (var extension in extensions)
        {
            // The loader decodes bytes (LyricsTextDecoder); UTF-8 without a BOM reads back exactly.
            if (lyrics.SidecarText(extension) is { } text) return new SidecarFile(extension, Encoding.UTF8.GetBytes(text));
        }
        return null;
    }

    public Stream? OpenAudio(string trackPath)
    {
        if (!NoctisRemoteIds.TryParsePath(trackPath, out var id)) return _inner.OpenAudio(trackPath);
        try
        {
            return _source.DownloadedPath(id) is { } path
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
                : null;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Lyrics", $"Downloaded song unreadable for SYLT: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>A desktop song's remote id has no extension for TagLib; its download's name does.</summary>
    public string? AudioFileName(string trackPath)
    {
        if (!NoctisRemoteIds.TryParsePath(trackPath, out var id)) return _inner.AudioFileName(trackPath);
        try
        {
            return _source.DownloadedPath(id) is { } path ? Path.GetFileName(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The track the loader should read: for a desktop song whose phone copy has no synced or
    /// plain lyrics, a stand-in (same id and path) carrying the desktop's; otherwise the track
    /// itself. The library's track is never changed. Blocking (see the class remarks).
    /// </summary>
    public Track ForLoad(Track track)
    {
        try
        {
            if (track.SourceType != SourceType.NoctisServer || !NoctisRemoteIds.TryParsePath(track.FilePath, out var id)) return track;
            var synced = track.SyncedLyrics;
            var plain = track.Lyrics;
            if (!string.IsNullOrWhiteSpace(synced) && !string.IsNullOrWhiteSpace(plain)) return track;
            var lyrics = Lyrics(id);
            if (lyrics is null || (lyrics.Synced is null && lyrics.Plain is null)) return track;
            return new Track
            {
                Id = track.Id,
                FilePath = track.FilePath,
                SourceType = track.SourceType,
                Title = track.Title,
                Artist = track.Artist,
                Album = track.Album,
                Duration = track.Duration,
                SyncedLyrics = string.IsNullOrWhiteSpace(synced) ? lyrics.Synced ?? string.Empty : synced,
                Lyrics = string.IsNullOrWhiteSpace(plain) ? lyrics.Plain ?? string.Empty : plain,
            };
        }
        catch (Exception ex)
        {
            DebugLog.Write("Lyrics", $"Desktop lyrics lookup failed: {ex.GetType().Name}");
            return track;
        }
    }

    private RemoteLyrics? Lyrics(Guid id)
    {
        try
        {
            return _source.GetLyrics(id, Timeout);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Lyrics", $"Desktop lyrics lookup failed: {ex.GetType().Name}");
            return null;
        }
    }
}
