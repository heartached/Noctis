using System.Text.Json;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Services.LocalApi;

/// <summary>
/// JSON shapes of the Local API (see docs/LOCAL-API.md). Everything here is built on the
/// UI thread from live view-model state and is a plain snapshot: no file paths, no
/// model objects, nothing a client can use to reach the filesystem. Artwork is exposed
/// only as an API URL that the server resolves itself.
/// </summary>
public static class LocalApiDto
{
    /// <summary>Major version of the /api/v1 surface. Additive changes don't bump it.</summary>
    public const int ApiVersion = 1;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Json);

    public static string StateName(PlaybackState state) => state switch
    {
        PlaybackState.Playing => "playing",
        PlaybackState.Paused => "paused",
        _ => "stopped",
    };

    public static string RepeatName(RepeatMode mode) => mode switch
    {
        RepeatMode.All => "all",
        RepeatMode.One => "one",
        _ => "off",
    };

    public static string ArtworkUrl(Guid trackId) => $"/api/v1/artwork/{trackId:D}";

    public sealed record TrackDto(
        Guid Id,
        string Title,
        string Artist,
        IReadOnlyList<string> Artists,
        string Album,
        string AlbumArtist,
        long DurationMs,
        int TrackNumber,
        int DiscNumber,
        int Year,
        string Genre,
        bool IsFavorite,
        string ArtworkUrl);

    public sealed record AlbumDto(Guid Id, string Name, string Artist, int Year, int TrackCount, string? ArtworkUrl);

    public sealed record ArtistDto(Guid Id, string Name, int AlbumCount, int TrackCount);

    public sealed record PlaybackDto(
        string State,
        bool Playing,
        long PositionMs,
        long DurationMs,
        int Volume,
        bool Muted,
        bool Shuffle,
        string Repeat);

    public sealed record NowPlayingDto(
        string State,
        bool Playing,
        TrackDto? Track,
        string Title,
        IReadOnlyList<string> Artists,
        string Album,
        string AlbumArtist,
        long DurationMs,
        long PositionMs,
        bool Shuffle,
        string Repeat,
        int Volume,
        bool Muted,
        Guid? TrackId,
        string? ArtworkUrl);

    public static TrackDto Track(Track t)
    {
        var artists = ArtistCredit.Split(t.Artist);
        return new TrackDto(
            t.Id,
            t.Title,
            t.Artist,
            artists.Length > 0 ? artists : new[] { t.Artist },
            t.Album,
            t.AlbumArtist,
            (long)t.Duration.TotalMilliseconds,
            t.TrackNumber,
            t.DiscNumber,
            t.DisplayYear,
            t.Genre,
            t.IsFavorite,
            ArtworkUrl(t.Id));
    }

    public static AlbumDto Album(Album a)
    {
        var first = a.Tracks.Count > 0 ? a.Tracks[0] : null;
        return new AlbumDto(a.Id, a.Name, a.Artist, a.Year, a.TrackCount > 0 ? a.TrackCount : a.Tracks.Count,
            first != null ? ArtworkUrl(first.Id) : null);
    }

    public static ArtistDto Artist(Artist a) => new(a.Id, a.Name, a.AlbumCount, a.TrackCount);

    /// <summary>UI thread only.</summary>
    public static PlaybackDto Playback(PlayerViewModel p) => new(
        StateName(p.State),
        p.State == PlaybackState.Playing,
        (long)p.Position.TotalMilliseconds,
        (long)p.Duration.TotalMilliseconds,
        p.Volume,
        p.IsMuted,
        p.IsShuffleEnabled,
        RepeatName(p.RepeatMode));

    /// <summary>UI thread only.</summary>
    public static NowPlayingDto NowPlaying(PlayerViewModel p)
    {
        var t = p.CurrentTrack;
        var track = t != null ? Track(t) : null;
        return new NowPlayingDto(
            StateName(p.State),
            p.State == PlaybackState.Playing,
            track,
            track?.Title ?? string.Empty,
            track?.Artists ?? Array.Empty<string>(),
            track?.Album ?? string.Empty,
            track?.AlbumArtist ?? string.Empty,
            // The player's Duration is the decoder's figure; before it resolves, fall
            // back to the tag duration so an overlay's progress bar has a denominator.
            p.Duration > TimeSpan.Zero ? (long)p.Duration.TotalMilliseconds : track?.DurationMs ?? 0,
            (long)p.Position.TotalMilliseconds,
            p.IsShuffleEnabled,
            RepeatName(p.RepeatMode),
            p.Volume,
            p.IsMuted,
            t?.Id,
            t != null ? ArtworkUrl(t.Id) : null);
    }

    public static object Error(string code, string message) =>
        new { error = new { code, message } };
}

/// <summary>A word with its own timing (ELRC / TTML karaoke lyrics).</summary>
public sealed record LocalApiLyricWord(long StartMs, long? EndMs, string Text);

/// <summary>One lyric line. StartMs is null for plain (unsynced) lyrics.</summary>
public sealed record LocalApiLyricLine(long? StartMs, long? EndMs, string Text, IReadOnlyList<LocalApiLyricWord>? Words);

/// <summary>Lyrics of one track as the app currently shows them.</summary>
public sealed record LocalApiLyrics(
    Guid TrackId,
    bool Synced,
    bool WordLevel,
    IReadOnlyList<LocalApiLyricLine> Lines,
    string PlainText);

/// <summary>
/// Where the Local API reads the current track's lyrics from. Both members are used on
/// the UI thread only. <see cref="Changed"/> fires when the loaded lyrics change (a new
/// track's lyrics arrived, an online search finished, the user edited them).
/// </summary>
public interface ILocalApiLyricsSource
{
    LocalApiLyrics? Snapshot();
    event EventHandler? Changed;
}
