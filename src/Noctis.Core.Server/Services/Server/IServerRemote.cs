namespace Noctis.Services.Server;

/// <summary>A song as the remote shows it: the playing one, or one of Up Next.</summary>
public sealed record RemoteTrack(Guid Id, Guid AlbumId, string Title, string Artist, string Album, long DurationMs);

/// <summary>
/// What the desktop's player is doing, read on its UI thread in one go. <see cref="UpNext"/> holds
/// at most the songs asked for; <see cref="UpNextTotal"/> counts them all.
/// </summary>
public sealed record RemoteSnapshot(
    RemoteTrack? Track,
    bool Playing,
    long PositionMs,
    long DurationMs,
    int Volume,
    bool Shuffle,
    RemoteRepeat Repeat,
    IReadOnlyList<RemoteTrack> UpNext,
    int UpNextTotal);

public enum RemoteRepeat { Off, All, One }

public enum RemoteAction { Toggle, Play, Pause, Next, Previous, Seek, Volume, Shuffle, Repeat, Jump }

/// <param name="PositionMs">Seek: where to.</param>
/// <param name="Volume">Volume: 0-100.</param>
/// <param name="Shuffle">Shuffle: on or off (null toggles).</param>
/// <param name="Repeat">Repeat: the mode (null cycles).</param>
/// <param name="Index">Jump: the Up Next row (0 = the next song).</param>
/// <param name="TrackId">Jump: the song the phone saw at <paramref name="Index"/>; a queue that
/// changed since is refused rather than playing another song.</param>
public sealed record RemoteCommand(
    RemoteAction Action,
    long PositionMs = 0,
    int Volume = 0,
    bool? Shuffle = null,
    RemoteRepeat? Repeat = null,
    int Index = 0,
    Guid TrackId = default);

/// <summary>
/// The desktop player as the phone's remote drives it (getNoctisRemote / noctisRemoteControl).
/// The desktop implements it over its player view model; a host without a player (the headless
/// server) has none, and the calls then answer error 50.
/// </summary>
public interface IServerRemote
{
    /// <summary>The player now, with up to <paramref name="upNextLimit"/> Up Next songs (0: none).</summary>
    Task<RemoteSnapshot> SnapshotAsync(int upNextLimit);

    /// <summary>Runs <paramref name="command"/> as the matching click would. False: a Jump whose row
    /// no longer holds that song, or a Seek with nothing playing.</summary>
    Task<bool> ControlAsync(RemoteCommand command);
}
