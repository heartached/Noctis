using Noctis.Models;

namespace Noctis.Services.Plugins;

/// <summary>
/// Where a plugin's <see cref="IPluginHost.ShowAlbum"/> (API 1.3) and <see cref="IPluginHost.ShowTrack"/>
/// (1.10) lead: the album page in the main window (for a song, scrolled to it and washed, as the
/// app's own "go to song" does), then Settings closes and the window comes forward. A plugin's
/// view is usually shown inside the Settings sheet (its Open button and its sidebar entry,
/// SettingsViewModel.ShowPluginViewInPlace): the page opened under the sheet, which stayed up,
/// so Add from YouTube's Show looked like it did nothing (owner 11-27). The page swaps first and
/// the sheet closes after, as Settings' Statistics link does, so the old page never flashes.
/// </summary>
internal sealed class PluginLibraryNavigator
{
    private readonly ILibraryService _library;
    private readonly Action<Album, Guid?> _openAlbum;
    private readonly Action _leaveSettings;
    private readonly Action _bringForward;

    /// <param name="openAlbum">Opens the album page, scrolled to the song when one is given.</param>
    /// <param name="leaveSettings">Closes the Settings sheet when it is open.</param>
    /// <param name="bringForward">Restores and activates the main window.</param>
    public PluginLibraryNavigator(ILibraryService library, Action<Album, Guid?> openAlbum, Action leaveSettings, Action bringForward)
    {
        _library = library;
        _openAlbum = openAlbum;
        _leaveSettings = leaveSettings;
        _bringForward = bringForward;
    }

    public bool ShowAlbum(Guid albumId)
        => _library.GetAlbumById(albumId) is { } album && Open(album, null);

    /// <summary>The song's album as the library has it now (an album edit or regrouping since the
    /// import moves the song, its id stays), scrolled to the song. False for a song that is not
    /// shown (unknown, hidden folder, still in the Inbox).</summary>
    public bool ShowTrack(Guid trackId)
        => _library.GetTrackById(trackId) is { } track && _library.GetAlbumById(track.AlbumId) is { } album && Open(album, track.Id);

    private bool Open(Album album, Guid? revealTrackId)
    {
        _openAlbum(album, revealTrackId);
        _leaveSettings();
        _bringForward();
        return true;
    }
}
