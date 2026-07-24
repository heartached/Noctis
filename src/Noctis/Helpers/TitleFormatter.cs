using System.Text;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// Expands foobar-style %token% patterns against a Track for use as filenames
/// or display strings. Only the tokens listed in <see cref="SupportedTokens"/>
/// are recognised; unknown tokens are left literal so users notice typos.
/// </summary>
public static class TitleFormatter
{
    public static readonly string[] SupportedTokens =
    {
        "%artist%", "%albumartist%", "%album%", "%title%",
        "%tracknumber%", "%tracknumber2%",
        "%discnumber%", "%year%", "%genre%", "%composer%",
    };

    /// <summary>Expand %tokens% in <paramref name="pattern"/> for the given track.</summary>
    /// <param name="sanitizeForFilename">When true, replaces characters that are illegal in
    /// filenames (\\ / : * ? \" &lt; &gt; |) with '-'. Path separators inside the *pattern itself*
    /// (used as folder boundaries) are preserved.</param>
    public static string Expand(string pattern, Track t, bool sanitizeForFilename)
    {
        if (string.IsNullOrEmpty(pattern)) return string.Empty;

        var sb = new StringBuilder(pattern.Length + 32);
        int i = 0;
        while (i < pattern.Length)
        {
            if (pattern[i] != '%') { sb.Append(pattern[i++]); continue; }

            int end = pattern.IndexOf('%', i + 1);
            if (end < 0) { sb.Append(pattern[i++]); continue; }

            var token = pattern.Substring(i, end - i + 1).ToLowerInvariant();
            string value = token switch
            {
                "%artist%" => t.Artist,
                "%albumartist%" => t.AlbumArtist,
                "%album%" => t.Album,
                "%title%" => t.Title,
                "%tracknumber%" => t.TrackNumber > 0 ? t.TrackNumber.ToString() : string.Empty,
                "%tracknumber2%" => t.TrackNumber > 0 ? t.TrackNumber.ToString("D2") : string.Empty,
                "%discnumber%" => t.DiscNumber > 0 ? t.DiscNumber.ToString() : string.Empty,
                "%year%" => t.Year > 0 ? t.Year.ToString() : string.Empty,
                "%genre%" => t.Genre,
                "%composer%" => t.Composer,
                _ => pattern.Substring(i, end - i + 1),
            };

            if (sanitizeForFilename) value = SanitizeFilenameSegment(value);
            sb.Append(value);
            i = end + 1;
        }
        return sb.ToString();
    }

    /// <summary>Public wrapper over the filename sanitizer for callers outside pattern expansion.</summary>
    public static string SanitizeForFilename(string? value) => SanitizeFilenameSegment(value ?? string.Empty);

    /// <summary>
    /// Strip characters that are illegal in Windows/macOS/Linux filenames from a single
    /// name segment.
    ///
    /// '/' is replaced rather than preserved. It used to be kept so a pattern could express
    /// folders, but the substituted value is a *tag*, not part of the pattern: a Title of
    /// "../../../../Documents/taxes" expanded to a path that escaped the output directory
    /// entirely, and ffmpeg's -y then truncated whatever was there. Any pattern that needs
    /// a directory separator can put one in the literal text between tokens, which is not
    /// routed through here. Leading dots are stripped for the same reason ("." and ".."
    /// are directory references, not names).
    /// </summary>
    private static string SanitizeFilenameSegment(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                sb.Append('-');
            else if (c < 32) continue;
            else sb.Append(c);
        }

        var result = sb.ToString().Trim().TrimEnd('.').Trim();

        // A segment of only dots ("." / "..") sanitizes to empty above only if it ends in
        // a dot; guard explicitly, and drop a leading dot so the result can never be read
        // as a relative path reference or a hidden file.
        result = result.TrimStart('.').Trim();

        // A leading '-' makes the value look like a command-line option to any tool the
        // path is passed to positionally (ffmpeg reads "-y" as a flag, not an output path).
        return result.TrimStart('-').Trim();
    }
}
