using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// Routes a click on the player island's artwork to what Settings → Player → Clicking the
/// artwork picked (GitHub #183). Kept apart from the window so each choice is testable
/// without one: the shell passes its own mini player / lyrics page / full-screen actions.
/// </summary>
public static class IslandArtworkClickRouter
{
    public static void Route(
        IslandArtworkClick action,
        Action toggleMiniPlayer,
        Action toggleLyricsPage,
        Action openFullScreenLyrics)
    {
        switch (action)
        {
            case IslandArtworkClick.LyricsPage:
                toggleLyricsPage();
                break;
            case IslandArtworkClick.FullScreenLyrics:
                openFullScreenLyrics();
                break;
            default:
                toggleMiniPlayer();
                break;
        }
    }
}
