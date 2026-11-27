namespace Noctis.Mobile.Services;

/// <summary>A playlist file the user picked: its display name ("Road Trip.m3u8") and bytes.</summary>
public sealed record PickedPlaylistFile(string Name, byte[] Content);

/// <summary>A video the user picked: its display name and a way to read it while the pick's
/// grant lasts (it can be tens of MB, so it is streamed, never held whole).</summary>
public sealed record PickedVideoFile(string Name, Func<Stream> Open);

/// <summary>
/// Playlists ⋯ › Import Playlist: the system's document picker (Android's ACTION_OPEN_DOCUMENT,
/// several files at once). Each file is read while its one-time grant lasts, so nothing is
/// kept; an empty list when the user backs out.
/// </summary>
public interface IPlaylistFilePicker
{
    Task<IReadOnlyList<PickedPlaylistFile>> PickPlaylistFilesAsync();

    /// <summary>A playlist's Change Cover: one picture from the same picker, read whole; null when
    /// the user backs out or it cannot be read.</summary>
    Task<PickedPlaylistFile?> PickImageAsync() => Task.FromResult<PickedPlaylistFile?>(null);

    /// <summary>Album ⋯ › Choose Animated Cover: one MP4 or WebM video from the same picker; null
    /// when the user backs out or the host has no picker.</summary>
    Task<PickedVideoFile?> PickVideoAsync() => Task.FromResult<PickedVideoFile?>(null);
}
