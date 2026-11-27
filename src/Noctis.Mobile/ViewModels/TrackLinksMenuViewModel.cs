using CommunityToolkit.Mvvm.Input;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Now Playing's artist line on a song credited to several artists (beta 6, #mobile-beta: the
/// artist goes straight to the artist page, the title to the album): Apple Music's small glass
/// bubble growing out of the tapped artist, one row per credited name (<see cref="ArtistsOf"/>,
/// the Settings › Library separators the Artists list splits by). A row closes the menu and opens
/// that artist's page, which closes the player (ShellViewModel.Navigate). A song with one artist
/// never shows it: the tap opens the artist at once (ShellViewModel.GoToCurrentArtist).
/// </summary>
public sealed partial class TrackLinksMenuViewModel(ShellViewModel shell, Track track, IReadOnlyList<string> artists)
{
    public Track Track { get; } = track;

    /// <summary>The rows: every credited artist, in the credit's order.</summary>
    public IReadOnlyList<string> Artists { get; } = artists;

    /// <summary>The names in <paramref name="track"/>'s artist credit, split as the library splits
    /// them, each once (first spelling wins).</summary>
    public static IReadOnlyList<string> ArtistsOf(Track track) =>
        ArtistCredit.Split(track.Artist).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    [RelayCommand]
    private void GoToArtist(string? name)
    {
        shell.CloseSheet();
        if (!string.IsNullOrWhiteSpace(name)) shell.OpenArtistCommand.Execute(name);
    }
}
