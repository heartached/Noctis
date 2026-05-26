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

    /// <summary>Strip characters that are illegal in Windows/macOS/Linux filenames
    /// from a single name segment (does not touch '/' so the pattern can express folders).</summary>
    private static string SanitizeFilenameSegment(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                sb.Append('-');
            else if (c < 32) continue;
            else sb.Append(c);
        }
        return sb.ToString().Trim().TrimEnd('.');
    }
}
