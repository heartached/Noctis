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

        if (host is "open.spotify.com" or "spotify.link" || host.EndsWith(".spotify.com"))
            return new StreamingLinkHint("Spotify",
                "Spotify no longer lets apps read playlists. Export it with Exportify (log in, click Export next to the playlist), then choose the CSV file here.",
                Exportify, "Open Exportify");

        if (host == "music.apple.com" || host.EndsWith(".music.apple.com"))
            return new StreamingLinkHint("Apple Music",
                "Apple Music playlists need Apple's paid developer access. Convert it with TuneMyMusic (export to file), then choose the file here.",
                TuneMyMusic, "Open TuneMyMusic");

        if (host is "tidal.com" or "listen.tidal.com" || host.EndsWith(".tidal.com"))
            return new StreamingLinkHint("TIDAL",
                "TIDAL links can't be fetched yet. Convert it with TuneMyMusic (export to file), then choose the file here.",
                TuneMyMusic, "Open TuneMyMusic");

        if (host == "music.youtube.com" || host == "youtube.com" || host == "www.youtube.com" || host == "youtu.be")
            return new StreamingLinkHint("YouTube Music",
                "YouTube Music has no playlist API. Convert it with TuneMyMusic (export to file), then choose the file here.",
                TuneMyMusic, "Open TuneMyMusic");

        if (host.Contains("music.amazon."))
            return new StreamingLinkHint("Amazon Music",
                "Amazon Music has no public API. Convert it with TuneMyMusic (export to file), then choose the file here.",
                TuneMyMusic, "Open TuneMyMusic");

        return null;
    }
}
