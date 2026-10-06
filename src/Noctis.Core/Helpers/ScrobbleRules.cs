using System;
using System.Collections.Generic;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// "Don't scrobble" choices for one song, album or artist (Discord aaron 2026-10-06: friends'
/// unreleased music should stay off Last.fm without turning scrobbling off everywhere).
/// Keys persist in <see cref="AppSettings.ScrobbleExcludedKeys"/>: "track:{id}",
/// "album:{id}" (AlbumId hashes album artist + album, so it survives rescans) and
/// "artist:{name}" (lowercased; matched against every credited name of the track's
/// artist and album artist, so a feature or a compilation track is caught too).
/// </summary>
public static class ScrobbleExclusionKeys
{
    public static string ForTrack(Guid trackId) => "track:" + trackId.ToString("N");
    public static string ForAlbum(Guid albumId) => "album:" + albumId.ToString("N");
    public static string ForArtist(string artistName) => "artist:" + (artistName ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>True when the track, its album, or any artist credited on it is excluded.</summary>
    public static bool IsExcluded(IReadOnlySet<string> keys, Track track)
    {
        if (keys.Count == 0) return false;
        if (keys.Contains(ForTrack(track.Id)) || keys.Contains(ForAlbum(track.AlbumId))) return true;
        foreach (var name in ArtistCredit.Split(track.Artist))
            if (keys.Contains(ForArtist(name))) return true;
        foreach (var name in ArtistCredit.Split(track.AlbumArtist))
            if (keys.Contains(ForArtist(name))) return true;
        return false;
    }
}

/// <summary>
/// How long the current song has actually been playing, for the scrobble rule. Wall-clock
/// time since the song started used to count paused time too, so 10 s of a song, a long
/// pause and a skip still scrobbled it.
/// </summary>
public sealed class ScrobblePlayClock
{
    private DateTime _startedAt;
    private DateTime? _pausedSince;
    private TimeSpan _pausedTotal;

    public DateTime StartedAt => _startedAt;

    public void Start(DateTime now)
    {
        _startedAt = now;
        _pausedSince = null;
        _pausedTotal = TimeSpan.Zero;
    }

    public void Pause(DateTime now) => _pausedSince ??= now;

    public void Resume(DateTime now)
    {
        if (_pausedSince is not { } since) return;
        if (now > since) _pausedTotal += now - since;
        _pausedSince = null;
    }

    public TimeSpan Played(DateTime now)
    {
        var played = now - _startedAt - _pausedTotal;
        if (_pausedSince is { } since && now > since) played -= now - since;
        return played < TimeSpan.Zero ? TimeSpan.Zero : played;
    }

    /// <summary>Last.fm's rule (last.fm/api/scrobbling), mirrored for ListenBrainz: the song is
    /// longer than 30 s and has played for at least half its length or 4 minutes.</summary>
    public static bool ShouldScrobble(TimeSpan duration, TimeSpan played)
        => duration.TotalSeconds > 30
           && (played.TotalSeconds >= duration.TotalSeconds * 0.5 || played.TotalMinutes >= 4);
}
