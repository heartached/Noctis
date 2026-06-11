using SkiaSharp;

namespace Noctis.Services;

/// <summary>Output aspect for share cards: 1:1 (feed) or 9:16 (story).</summary>
public enum ShareCardFormat
{
    Square,
    Story
}

/// <summary>Everything needed to render a lyric snapshot card.</summary>
public sealed record LyricCardSpec
{
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public string? ArtworkPath { get; init; }
    public required IReadOnlyList<string> Lines { get; init; }
    public ShareCardFormat Format { get; init; } = ShareCardFormat.Square;
}

/// <summary>
/// Renders Spotify-style share cards (album art header, big bold lyric lines,
/// Noctis wordmark) into PNG bytes using SkiaSharp. Pure CPU rendering — safe
/// to call from any thread, no Avalonia visual tree involved.
/// </summary>
public static class ShareCardRenderer
{
    private const int CanvasWidth = 1080;
    private const float Pad = 96f;
    private const float ArtSize = 112f;
    private const float HeaderToLyricsGap = 72f;
    private const float LyricsToFooterGap = 84f;
    private const float FooterHeight = 48f;

    public static byte[] RenderLyricCard(LyricCardSpec spec)
    {
        int w = CanvasWidth;
        int h = spec.Format == ShareCardFormat.Story ? 1920 : 1080;

        using var art = LoadArtwork(spec.ArtworkPath);
        var bg = DeriveBackground(art);
        bool darkText = UseDarkText(bg.Red, bg.Green, bg.Blue);
        var fg = darkText ? new SKColor(0x12, 0x12, 0x12) : SKColors.White;
        var fgSubtle = fg.WithAlpha(185);

        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        var canvas = surface.Canvas;
        canvas.Clear(bg);

        using var boldFace = ResolveTypeface(bold: true);
        using var regularFace = ResolveTypeface(bold: false);

        float contentW = w - Pad * 2;

        using var titlePaint = TextPaint(boldFace, 40, fg);
        using var artistPaint = TextPaint(regularFace, 33, fgSubtle);

        // ── Measure pass: lyric font auto-shrinks until the block fits ──
        float footerBlock = LyricsToFooterGap + FooterHeight;
        float maxLyricsH = h - Pad * 2 - ArtSize - HeaderToLyricsGap - footerBlock;
        float lyricSize = 64;
        List<string> wrapped;
        float lineHeight;
        using var lyricPaint = TextPaint(boldFace, lyricSize, fg);
        while (true)
        {
            lyricPaint.TextSize = lyricSize;
            wrapped = WrapAll(spec.Lines, contentW, s => lyricPaint.MeasureText(s));
            lineHeight = lyricSize * 1.3f;
            if (wrapped.Count * lineHeight <= maxLyricsH || lyricSize <= 34)
                break;
            lyricSize -= 4;
        }

        float lyricsH = wrapped.Count * lineHeight;
        float blockH = ArtSize + HeaderToLyricsGap + lyricsH + footerBlock;

        // Square: anchor at top padding. Story: center the whole block vertically.
        float y = spec.Format == ShareCardFormat.Story
            ? Math.Max(Pad, (h - blockH) / 2f)
            : Pad;

        // ── Header: album art + title/artist ────────────────────────────
        var artRect = new SKRect(Pad, y, Pad + ArtSize, y + ArtSize);
        if (art != null)
        {
            using var artPaint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            using var rounded = new SKRoundRect(artRect, 14);
            canvas.Save();
            canvas.ClipRoundRect(rounded, antialias: true);
            canvas.DrawBitmap(art, artRect, artPaint);
            canvas.Restore();
        }
        else
        {
            using var placeholder = new SKPaint { IsAntialias = true, Color = fg.WithAlpha(28) };
            canvas.DrawRoundRect(artRect, 14, 14, placeholder);
        }

        float textX = Pad + ArtSize + 30;
        float textMaxW = contentW - ArtSize - 30;
        canvas.DrawText(Ellipsize(spec.Title, textMaxW, s => titlePaint.MeasureText(s)),
            textX, y + ArtSize / 2f - 8, titlePaint);
        canvas.DrawText(Ellipsize(spec.Artist, textMaxW, s => artistPaint.MeasureText(s)),
            textX, y + ArtSize / 2f + 36, artistPaint);

        // ── Lyric lines ─────────────────────────────────────────────────
        float baseline = y + ArtSize + HeaderToLyricsGap + lyricSize;
        foreach (var line in wrapped)
        {
            canvas.DrawText(line, Pad, baseline, lyricPaint);
            baseline += lineHeight;
        }

        // ── Footer wordmark ─────────────────────────────────────────────
        float footerY = spec.Format == ShareCardFormat.Story
            ? y + blockH - FooterHeight
            : h - Pad - FooterHeight;
        DrawWordmark(canvas, boldFace, fg, Pad, footerY);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKPaint TextPaint(SKTypeface face, float size, SKColor color) => new()
    {
        IsAntialias = true,
        Typeface = face,
        TextSize = size,
        Color = color,
    };

    /// <summary>Draws a small quarter-note glyph followed by the "Noctis" wordmark.</summary>
    private static void DrawWordmark(SKCanvas canvas, SKTypeface boldFace, SKColor fg, float x, float y)
    {
        using var paint = new SKPaint { IsAntialias = true, Color = fg };

        // Quarter note: filled note-head ellipse + stem.
        float headCx = x + 13;
        float headCy = y + FooterHeight - 12;
        canvas.Save();
        canvas.RotateDegrees(-20, headCx, headCy);
        canvas.DrawOval(headCx, headCy, 13, 10, paint);
        canvas.Restore();
        canvas.DrawRect(headCx + 8.5f, headCy - 38, 4f, 38, paint);

        using var textPaint = TextPaint(boldFace, 38, fg);
        canvas.DrawText("Noctis", x + 42, y + FooterHeight - 8, textPaint);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Pure helpers (unit-tested)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Greedy word-wrap: splits on spaces, hard-breaks single words wider than
    /// <paramref name="maxWidth"/>. Measurement is injected so tests don't need fonts.
    /// </summary>
    public static List<string> WrapText(string text, float maxWidth, Func<string, float> measureWidth)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (measureWidth(candidate) <= maxWidth)
            {
                current = candidate;
                continue;
            }

            if (current.Length > 0)
            {
                result.Add(current);
                current = "";
            }

            // Word alone fits on a fresh line?
            if (measureWidth(word) <= maxWidth)
            {
                current = word;
                continue;
            }

            // Hard-break an oversized word character by character.
            var piece = "";
            foreach (var ch in word)
            {
                if (piece.Length > 0 && measureWidth(piece + ch) > maxWidth)
                {
                    result.Add(piece);
                    piece = "";
                }
                piece += ch;
            }
            current = piece;
        }

        if (current.Length > 0)
            result.Add(current);
        return result;
    }

    /// <summary>Whether a background color is light enough to need dark text.</summary>
    public static bool UseDarkText(byte r, byte g, byte b) =>
        (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0 > 0.6;

    /// <summary>Truncates text with an ellipsis so it fits within maxWidth.</summary>
    public static string Ellipsize(string text, float maxWidth, Func<string, float> measureWidth)
    {
        if (measureWidth(text) <= maxWidth)
            return text;
        for (int len = text.Length - 1; len > 0; len--)
        {
            var candidate = text[..len].TrimEnd() + "…";
            if (measureWidth(candidate) <= maxWidth)
                return candidate;
        }
        return "…";
    }

    private static List<string> WrapAll(IReadOnlyList<string> lines, float maxWidth, Func<string, float> measure)
    {
        var result = new List<string>();
        foreach (var line in lines)
            result.AddRange(WrapText(line, maxWidth, measure));
        return result;
    }

    private static SKTypeface ResolveTypeface(bool bold)
    {
        var style = bold ? SKFontStyle.Bold : SKFontStyle.Normal;
        foreach (var family in new[] { "Inter", "Segoe UI", "Helvetica Neue", "Noto Sans" })
        {
            var face = SKFontManager.Default.MatchFamily(family, style);
            if (face != null)
                return face;
        }
        return SKTypeface.CreateDefault();
    }

    private static SKBitmap? LoadArtwork(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;
        try
        {
            using var raw = SKBitmap.Decode(path);
            // Downscale once so the average-color pass and card drawing stay cheap.
            return raw?.Resize(new SKImageInfo(512, 512), SKFilterQuality.Medium);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Average artwork color with lightness clamped so the card never goes
    /// pure white/black; falls back to the app's dark navy without artwork.
    /// </summary>
    private static SKColor DeriveBackground(SKBitmap? art)
    {
        if (art == null)
            return new SKColor(0x1A, 0x1A, 0x2E);

        long r = 0, g = 0, b = 0, count = 0;
        // Sample a coarse grid — plenty for an average.
        for (int y = 0; y < art.Height; y += 8)
        {
            for (int x = 0; x < art.Width; x += 8)
            {
                var px = art.GetPixel(x, y);
                if (px.Alpha < 32) continue;
                r += px.Red; g += px.Green; b += px.Blue;
                count++;
            }
        }
        if (count == 0)
            return new SKColor(0x1A, 0x1A, 0x2E);

        var avg = new SKColor((byte)(r / count), (byte)(g / count), (byte)(b / count));
        avg.ToHsl(out var hue, out var sat, out var light);
        light = Math.Clamp(light, 12f, 88f);
        return SKColor.FromHsl(hue, sat, light);
    }
}
