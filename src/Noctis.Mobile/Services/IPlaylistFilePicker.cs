namespace Noctis.Mobile.Services;

/// <summary>A playlist file the user picked: its display name ("Road Trip.m3u8") and bytes.</summary>
public sealed record PickedPlaylistFile(string Name, byte[] Content);

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
}
