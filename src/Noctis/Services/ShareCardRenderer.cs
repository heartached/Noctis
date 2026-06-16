using SkiaSharp;

namespace Noctis.Services;

/// <summary>Output aspect for share cards: 1:1 (feed) or 9:16 (story).</summary>
public enum ShareCardFormat
{
    Square,
    Story
}

/// <summary>How the lyric text color is chosen on the card.</summary>
public enum ShareTextColor
{
    /// <summary>Pick dark or light text automatically from the card color.</summary>
    Auto,
    /// <summary>Force white lyrics.</summary>
    White,
    /// <summary>Force black lyrics.</summary>
    Black
}

/// <summary>How the card background color is chosen.</summary>
public enum ShareBackground
{
    /// <summary>Derive the background from the album artwork's average color.</summary>
    Artwork,
    /// <summary>Use a solid color chosen by the user (<see cref="LyricCardSpec.SolidColorHex"/>).</summary>
    Solid
}

/// <summary>Everything needed to render a lyric snapshot card.</summary>
public sealed record LyricCardSpec
{
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public string? ArtworkPath { get; init; }
    public required IReadOnlyList<string> Lines { get; init; }
    public ShareCardFormat Format { get; init; } = ShareCardFormat.Square;
    public ShareTextColor TextColor { get; init; } = ShareTextColor.Auto;
    /// <summary>Draws an "E" explicit badge after the title when true.</summary>
    public bool IsExplicit { get; init; }
    /// <summary>Whether the background is derived from artwork or a solid color.</summary>
    public ShareBackground Background { get; init; } = ShareBackground.Artwork;
    /// <summary>Solid background color (e.g. "#1A1A2E"); used when <see cref="Background"/> is Solid.</summary>
    public string? SolidColorHex { get; init; }
}

/// <summary>Everything needed to render a Noctis Wrap recap card.</summary>
public sealed record WrapCardSpec
{
    public required string PeriodLabel { get; init; }
    public required IReadOnlyList<string> TopArtists { get; init; }
    public required IReadOnlyList<string> TopTracks { get; init; }
    public required long TotalMinutes { get; init; }
    public required int TotalPlays { get; init; }
    public required double LosslessPercent { get; init; }
    public required string TopGenre { get; init; }
    /// <summary>Most-played album's artwork — only used to tint the background.</summary>
    public string? ArtworkPath { get; init; }
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

    // Spotify-style floating card geometry.
    private const float CardMargin = 90f;       // gap between canvas edge and the inner card
    private const float CardRadius = 56f;        // inner card corner radius
    private const float CardPad = 84f;           // padding inside the inner card
    private const float ArtSize = 96f;           // small album thumbnail on the card
    private const float ArtTextGap = 28f;        // gap between thumbnail and title/artist
    private const float HeaderToLyricsGap = 64f; // gap between header row and lyrics block
    private const float LyricsToFooterGap = 64f; // gap between lyrics block and wordmark
    private const float FooterHeight = 44f;

    public static byte[] RenderLyricCard(LyricCardSpec spec)
    {
        int w = CanvasWidth;
        int h = spec.Format == ShareCardFormat.Story ? 1920 : 1080;

        using var art = LoadArtwork(spec.ArtworkPath);
        // Artwork mode fills the whole card with the heavily-blurred cover (matching the
        // lyrics page); falls back to a solid surround only when there's no artwork to blur.
        bool photoBg = spec.Background == ShareBackground.Artwork && art != null;

        var surround = spec.Background == ShareBackground.Solid
                       && SKColor.TryParse(spec.SolidColorHex, out var solid)
            ? solid
            : DeriveBackground(art);
        // The card floats slightly lighter than the surround, Spotify-style.
        var cardColor = Lighten(surround, 0.12f);

        bool darkText = spec.TextColor switch
        {
            ShareTextColor.Black => true,
            ShareTextColor.White => false,
            // Auto: the blurred-photo background carries a dark scrim, so white stays
            // legible; on a flat card, pick from the card's own lightness.
            _ => !photoBg && UseDarkText(cardColor.Red, cardColor.Green, cardColor.Blue),
        };
        var fg = darkText ? new SKColor(0x12, 0x12, 0x12) : SKColors.White;
        var fgSubtle = fg.WithAlpha((byte)(darkText ? 160 : 195));

        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        var canvas = surface.Canvas;
        if (photoBg)
        {
            canvas.Clear(SKColors.Black);
            DrawBlurredCover(canvas, art!, w, h);
            // Dark scrim keeps lyrics readable over bright covers (same as the lyrics page).
            using var scrim = new SKPaint { Color = new SKColor(0, 0, 0, 0x99) };
            canvas.DrawRect(0, 0, w, h, scrim);
        }
        else
        {
            canvas.Clear(surround);
        }

        using var boldFace = ResolveTypeface(bold: true);
        using var regularFace = ResolveTypeface(bold: false);

        // ── Inner card horizontal bounds ────────────────────────────────
        float cardLeft = CardMargin;
        float cardRight = w - CardMargin;
        float cardW = cardRight - cardLeft;
        float contentW = cardW - CardPad * 2;
        float contentX = cardLeft + CardPad;

        using var titlePaint = TextPaint(boldFace, 38, fg);
        using var artistPaint = TextPaint(regularFace, 31, fgSubtle);

        // ── Measure pass: lyric font auto-shrinks until the block fits ──
        float headerH = ArtSize;
        float footerBlock = LyricsToFooterGap + FooterHeight;
        // Available height for lyrics depends on the card height, which differs by format.
        float cardMaxH = h - CardMargin * 2;
        float maxLyricsH = cardMaxH - CardPad * 2 - headerH - HeaderToLyricsGap - footerBlock;
        float lyricSize = 60;
        List<string> wrapped;
        float lineHeight;
        using var lyricPaint = TextPaint(boldFace, lyricSize, fg);
        while (true)
        {
            lyricPaint.TextSize = lyricSize;
            wrapped = WrapAll(spec.Lines, contentW, s => lyricPaint.MeasureText(s));
            lineHeight = lyricSize * 1.32f;
            if (wrapped.Count * lineHeight <= maxLyricsH || lyricSize <= 32)
                break;
            lyricSize -= 4;
        }

        float lyricsH = wrapped.Count * lineHeight;
        float contentH = headerH + HeaderToLyricsGap + lyricsH + footerBlock;

        // ── Inner card vertical bounds ──────────────────────────────────
        // Story: the card hugs its content and floats in the vertical center.
        // Square: the card fills the area between the margins.
        float cardTop, cardH;
        if (spec.Format == ShareCardFormat.Story)
        {
            cardH = Math.Min(contentH + CardPad * 2, cardMaxH);
            cardTop = (h - cardH) / 2f;
        }
        else
        {
            cardTop = CardMargin;
            cardH = cardMaxH;
        }

        var cardRect = new SKRect(cardLeft, cardTop, cardRight, cardTop + cardH);
        if (photoBg)
        {
            // Frosted panel: a translucent lighter card lifts off the blurred backdrop so the
            // Spotify-style square still reads, while the artwork stays visible around it.
            using var fill = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0x30) };
            canvas.DrawRoundRect(cardRect, CardRadius, CardRadius, fill);
            using var border = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f,
                Color = new SKColor(255, 255, 255, 0x42),
            };
            canvas.DrawRoundRect(cardRect, CardRadius, CardRadius, border);
        }
        else
        {
            using var cardPaint = new SKPaint { IsAntialias = true, Color = cardColor };
            canvas.DrawRoundRect(cardRect, CardRadius, CardRadius, cardPaint);
        }

        // Center the whole content block vertically within the card.
        float contentTop = cardTop + (cardH - contentH) / 2f;

        // ── Header: album art + title/artist ────────────────────────────
        var artRect = new SKRect(contentX, contentTop, contentX + ArtSize, contentTop + ArtSize);
        if (art != null)
        {
            using var artPaint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            using var rounded = new SKRoundRect(artRect, 12);
            canvas.Save();
            canvas.ClipRoundRect(rounded, antialias: true);
            canvas.DrawBitmap(art, artRect, artPaint);
            canvas.Restore();
        }
        else
        {
            using var placeholder = new SKPaint { IsAntialias = true, Color = fg.WithAlpha(28) };
            canvas.DrawRoundRect(artRect, 12, 12, placeholder);
        }

        float textX = contentX + ArtSize + ArtTextGap;
        float textMaxW = contentW - ArtSize - ArtTextGap;
        float titleBaseline = contentTop + ArtSize / 2f - 6;
        // Reserve room for the explicit badge so it never collides with the title.
        float badgeReserve = spec.IsExplicit ? titlePaint.TextSize * 0.72f + 14 : 0;
        var titleText = Ellipsize(spec.Title, textMaxW - badgeReserve, s => titlePaint.MeasureText(s));
        canvas.DrawText(titleText, textX, titleBaseline, titlePaint);
        if (spec.IsExplicit)
        {
            float badgeX = textX + titlePaint.MeasureText(titleText) + 14;
            DrawExplicitBadge(canvas, regularFace, fg, cardColor,
                badgeX, titleBaseline - titlePaint.TextSize * 0.34f, titlePaint.TextSize);
        }
        canvas.DrawText(Ellipsize(spec.Artist, textMaxW, s => artistPaint.MeasureText(s)),
            textX, contentTop + ArtSize / 2f + 32, artistPaint);

        // ── Lyric lines ─────────────────────────────────────────────────
        float baseline = contentTop + headerH + HeaderToLyricsGap + lyricSize;
        foreach (var line in wrapped)
        {
            canvas.DrawText(line, contentX, baseline, lyricPaint);
            baseline += lineHeight;
        }

        // ── Footer wordmark ─────────────────────────────────────────────
        float footerY = contentTop + headerH + HeaderToLyricsGap + lyricsH + LyricsToFooterGap;
        DrawWordmark(canvas, boldFace, fg, contentX, footerY);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Fills the whole canvas with the cover art, scaled UniformToFill plus a small
    /// overscan and heavily Gaussian-blurred — the lyrics-page backdrop look.
    /// </summary>
    private static void DrawBlurredCover(SKCanvas canvas, SKBitmap art, int w, int h)
    {
        // Cover the canvas, then a little extra so the blur's soft edges never reveal black.
        float scale = Math.Max((float)w / art.Width, (float)h / art.Height) * 1.18f;
        float dw = art.Width * scale;
        float dh = art.Height * scale;
        var dest = new SKRect((w - dw) / 2f, (h - dh) / 2f, (w + dw) / 2f, (h + dh) / 2f);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.High,
            ImageFilter = SKImageFilter.CreateBlur(48f, 48f),
        };
        canvas.DrawBitmap(art, dest, paint);
    }

    /// <summary>Blends a color toward white by <paramref name="amount"/> (0–1).</summary>
    private static SKColor Lighten(SKColor c, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        byte Mix(byte v) => (byte)(v + (255 - v) * amount);
        return new SKColor(Mix(c.Red), Mix(c.Green), Mix(c.Blue));
    }

    /// <summary>
    /// Renders a Noctis Wrap recap card: brand header, period, top-artist and
    /// top-song columns, then a 2×2 stats grid (minutes, top genre, plays, lossless).
    /// </summary>
    public static byte[] RenderWrapCard(WrapCardSpec spec)
    {
        int w = CanvasWidth;
        int h = spec.Format == ShareCardFormat.Story ? 1920 : 1080;

        using var art = LoadArtwork(spec.ArtworkPath);
        var bg = DeriveBackground(art);
        bool darkText = UseDarkText(bg.Red, bg.Green, bg.Blue);
        var fg = darkText ? new SKColor(0x12, 0x12, 0x12) : SKColors.White;
        var fgSubtle = fg.WithAlpha(170);

        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        var canvas = surface.Canvas;

        // Vertical gradient: darker tint of the artwork color at the top.
        bg.ToHsl(out var hue, out var sat, out var light);
        var bgTop = SKColor.FromHsl(hue, sat, Math.Max(6f, light * 0.45f));
        using (var bgPaint = new SKPaint())
        {
            bgPaint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(0, h),
                new[] { bgTop, bg }, null, SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, w, h, bgPaint);
        }

        using var boldFace = ResolveTypeface(bold: true);
        using var regularFace = ResolveTypeface(bold: false);
        float contentW = w - Pad * 2;

        // Fixed content metrics; the story format centers the same block vertically.
        const float brandSize = 30, periodSize = 96, colHeaderSize = 28, entrySize = 34,
            entryLine = 52, statLabelSize = 22, statValueSize = 56;
        float colsH = colHeaderSize + 28 + 5 * entryLine;
        float statsH = 2 * (statLabelSize + 8 + statValueSize + 26);
        float blockH = brandSize + 18 + periodSize + 44 + colsH + 44 + statsH + 36 + FooterHeight;

        float y = spec.Format == ShareCardFormat.Story
            ? Math.Max(Pad, (h - blockH) / 2f)
            : Math.Max(Pad / 2f, (h - blockH) / 2f);

        using (var brandPaint = TextPaint(boldFace, brandSize, fgSubtle))
        {
            brandPaint.TextSkewX = 0;
            canvas.DrawText("N O C T I S   W R A P", Pad, y + brandSize, brandPaint);
        }
        using (var periodPaint = TextPaint(boldFace, periodSize, fg))
        {
            canvas.DrawText(spec.PeriodLabel, Pad, y + brandSize + 18 + periodSize, periodPaint);
        }

        // ── Two top-list columns ────────────────────────────────────────
        float colsTop = y + brandSize + 18 + periodSize + 44;
        float colW = (contentW - 48) / 2f;
        DrawWrapColumn(canvas, boldFace, regularFace, "TOP ARTISTS", spec.TopArtists,
            Pad, colsTop, colW, colHeaderSize, entrySize, entryLine, fg, fgSubtle);
        DrawWrapColumn(canvas, boldFace, regularFace, "TOP SONGS", spec.TopTracks,
            Pad + colW + 48, colsTop, colW, colHeaderSize, entrySize, entryLine, fg, fgSubtle);

        // ── 2×2 stats grid ──────────────────────────────────────────────
        float statsTop = colsTop + colsH + 44;
        float cellH = statLabelSize + 8 + statValueSize + 26;
        DrawWrapStat(canvas, boldFace, regularFace, "MINUTES LISTENED",
            spec.TotalMinutes.ToString("N0"), Pad, statsTop, colW, statLabelSize, statValueSize, fg, fgSubtle);
        DrawWrapStat(canvas, boldFace, regularFace, "TOP GENRE",
            spec.TopGenre, Pad + colW + 48, statsTop, colW, statLabelSize, statValueSize, fg, fgSubtle);
        DrawWrapStat(canvas, boldFace, regularFace, "TRACKS PLAYED",
            spec.TotalPlays.ToString("N0"), Pad, statsTop + cellH, colW, statLabelSize, statValueSize, fg, fgSubtle);
        DrawWrapStat(canvas, boldFace, regularFace, "LOSSLESS",
            $"{spec.LosslessPercent:0}%", Pad + colW + 48, statsTop + cellH, colW, statLabelSize, statValueSize, fg, fgSubtle);

        DrawWordmark(canvas, boldFace, fg, Pad, statsTop + statsH + 36);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void DrawWrapColumn(SKCanvas canvas, SKTypeface boldFace, SKTypeface regularFace,
        string header, IReadOnlyList<string> entries, float x, float y, float width,
        float headerSize, float entrySize, float entryLine, SKColor fg, SKColor fgSubtle)
    {
        using var headerPaint = TextPaint(boldFace, headerSize, fgSubtle);
        canvas.DrawText(header, x, y + headerSize, headerPaint);

        using var rankPaint = TextPaint(boldFace, entrySize, fgSubtle);
        using var namePaint = TextPaint(boldFace, entrySize, fg);
        float baseline = y + headerSize + 28 + entrySize;
        for (int i = 0; i < Math.Min(5, entries.Count); i++)
        {
            canvas.DrawText($"{i + 1}", x, baseline, rankPaint);
            var name = Ellipsize(entries[i], width - 44, s => namePaint.MeasureText(s));
            canvas.DrawText(name, x + 44, baseline, namePaint);
            baseline += entryLine;
        }
    }

    private static void DrawWrapStat(SKCanvas canvas, SKTypeface boldFace, SKTypeface regularFace,
        string label, string value, float x, float y, float width,
        float labelSize, float valueSize, SKColor fg, SKColor fgSubtle)
    {
        using var labelPaint = TextPaint(regularFace, labelSize, fgSubtle);
        canvas.DrawText(label, x, y + labelSize, labelPaint);
        using var valuePaint = TextPaint(boldFace, valueSize, fg);
        var fitted = Ellipsize(value, width, s => valuePaint.MeasureText(s));
        canvas.DrawText(fitted, x, y + labelSize + 8 + valueSize, valuePaint);
    }

    private static SKPaint TextPaint(SKTypeface face, float size, SKColor color) => new()
    {
        IsAntialias = true,
        Typeface = face,
        TextSize = size,
        Color = color,
    };

    /// <summary>Draws the Noctis app logo followed by the "Noctis" wordmark.</summary>
    private static void DrawWordmark(SKCanvas canvas, SKTypeface boldFace, SKColor fg, float x, float y)
    {
        float logoSize = FooterHeight;
        float textX;

        // Nudge the logo vertically relative to the "Noctis" text.
        // Negative = move logo UP, positive = move logo DOWN. (Pixels at 1080px render width.)
        const float logoNudgeY = -3f;

        var logo = LoadLogo();
        if (logo != null)
        {
            float logoTop = y + logoNudgeY;
            var dest = new SKRect(x, logoTop, x + logoSize, logoTop + logoSize);
            using var p = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            canvas.DrawBitmap(logo, dest, p);
            textX = x + logoSize + 14;
        }
        else
        {
            // Fallback: quarter-note glyph (used when the logo asset can't be loaded).
            using var paint = new SKPaint { IsAntialias = true, Color = fg };
            float headCx = x + 13;
            float headCy = y + FooterHeight - 12;
            canvas.Save();
            canvas.RotateDegrees(-20, headCx, headCy);
            canvas.DrawOval(headCx, headCy, 13, 10, paint);
            canvas.Restore();
            canvas.DrawRect(headCx + 8.5f, headCy - 38, 4f, 38, paint);
            textX = x + 42;
        }

        using var textPaint = TextPaint(boldFace, 38, fg);
        canvas.DrawText("Noctis", textX, y + logoSize - 13, textPaint);
    }

    /// <summary>Draws a small rounded "E" explicit badge centered vertically on <paramref name="centerY"/>.</summary>
    private static void DrawExplicitBadge(SKCanvas canvas, SKTypeface face, SKColor fg, SKColor cardColor,
        float x, float centerY, float refSize)
    {
        float size = refSize * 0.72f;
        var rect = new SKRect(x, centerY - size / 2f, x + size, centerY + size / 2f);
        using var bgPaint = new SKPaint { IsAntialias = true, Color = fg.WithAlpha(215) };
        canvas.DrawRoundRect(rect, 4, 4, bgPaint);

        using var letterPaint = TextPaint(face, size * 0.66f, cardColor);
        letterPaint.TextAlign = SKTextAlign.Center;
        letterPaint.FakeBoldText = true;
        var metrics = letterPaint.FontMetrics;
        float baseline = centerY - (metrics.Ascent + metrics.Descent) / 2f;
        canvas.DrawText("E", rect.MidX, baseline, letterPaint);
    }

    private static SKBitmap? _logo;
    private static bool _logoLoadAttempted;
    private static readonly object _logoLock = new();

    /// <summary>Loads and caches the app logo from Avalonia resources. Null if unavailable.</summary>
    private static SKBitmap? LoadLogo()
    {
        if (_logoLoadAttempted) return _logo;
        lock (_logoLock)
        {
            if (_logoLoadAttempted) return _logo;
            try
            {
                var uri = new Uri("avares://Noctis/Assets/Icons/Noctis%20Logo%20Clean.png");
                using var stream = Avalonia.Platform.AssetLoader.Open(uri);
                var raw = SKBitmap.Decode(stream);
                if (raw != null)
                {
                    var resized = raw.Resize(new SKImageInfo(160, 160), SKFilterQuality.High);
                    if (resized != null) { _logo = resized; raw.Dispose(); }
                    else _logo = raw;
                }
            }
            catch
            {
                _logo = null;
            }
            _logoLoadAttempted = true;
        }
        return _logo;
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
