namespace Noctis.Services;

/// <summary>Guidance for a pasted streaming link the app cannot fetch itself.</summary>
public sealed record StreamingLinkHint(string Service, string Message, string HelpUrl, string HelpLabel);

/// <summary>
/// Recognises share links from services whose playlists Noctis cannot read directly and
/// tells the user the shortest working path instead of a bare "unsupported". Spotify closed
/// third-party playlist reads to small apps in 2026, Apple Music needs a paid developer
/// token, Amazon has no public API, YouTube Music has none for playlists; TIDAL would need
/// an OAuth app that is not set up. Deezer links are handled by <see cref="DeezerPlaylistLink"/>.
/// </summary>
public static class StreamingLinkHints
{
    private const string Exportify = "https://exportify.net/";
    private const string TuneMyMusic = "https://www.tunemymusic.com/transfer";

    /// <summary>Null when the text is not a link to one of the known services.</summary>
    public static StreamingLinkHint? For(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (!t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();

        // Owner 10-08 "less words": one short line each (they were full how-to sentences), and
        // through Loc like the rest of the Import pop-up.
        if (host is "open.spotify.com" or "spotify.link" || host.EndsWith(".spotify.com"))
            return new StreamingLinkHint("Spotify", T("Import.Hint.Spotify"), Exportify, T("Import.Hint.OpenExportify"));

        if (host == "music.apple.com" || host.EndsWith(".music.apple.com"))
            return new StreamingLinkHint("Apple Music", T("Import.Hint.AppleMusic"), TuneMyMusic, T("Import.Hint.OpenTuneMyMusic"));

        if (host is "tidal.com" or "listen.tidal.com" || host.EndsWith(".tidal.com"))
            return new StreamingLinkHint("TIDAL", T("Import.Hint.Tidal"), TuneMyMusic, T("Import.Hint.OpenTuneMyMusic"));

        if (host == "music.youtube.com" || host == "youtube.com" || host == "www.youtube.com" || host == "youtu.be")
            return new StreamingLinkHint("YouTube Music", T("Import.Hint.YouTubeMusic"), TuneMyMusic, T("Import.Hint.OpenTuneMyMusic"));

        if (host.Contains("music.amazon."))
            return new StreamingLinkHint("Amazon Music", T("Import.Hint.AmazonMusic"), TuneMyMusic, T("Import.Hint.OpenTuneMyMusic"));

        return null;
    }

    private static string T(string key) => Localization.Loc.T(key);
}
