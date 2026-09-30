using System.Text;

namespace Noctis.Helpers;

/// <summary>
/// Decodes lyric sidecar bytes, falling back off UTF-8 when the bytes are not valid UTF-8.
/// Shift-JIS / GB18030 / CP1251 .lrc files are common in the wild; decoded as UTF-8 their
/// bytes become U+FFFD, which LyricsTextHelper.CleanDisplayText then strips, leaving
/// timestamped but empty lines with no error anywhere. Takes bytes rather than a path
/// because Android SAF documents have no path.
/// </summary>
public static class LyricsTextDecoder
{
    public static string Decode(byte[] bytes)
    {
        // A BOM is authoritative — let the framework handle it.
        if (bytes.Length >= 2 &&
            ((bytes[0] == 0xFF && bytes[1] == 0xFE) ||
             (bytes[0] == 0xFE && bytes[1] == 0xFF) ||
             (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)))
        {
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        // Strict UTF-8 first: throwOnInvalidBytes turns "not UTF-8" into a signal rather
        // than a string full of replacement characters.
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8. Use the OS default ANSI code page, which is the right guess for a
            // file authored on the user's own machine; Latin1 elsewhere (and where the code
            // page lookup does not exist) so every byte maps to something.
            try
            {
                return Encoding.GetEncoding(0).GetString(bytes);
            }
            catch
            {
                return Encoding.Latin1.GetString(bytes);
            }
        }
    }
}
