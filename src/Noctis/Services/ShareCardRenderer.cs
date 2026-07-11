using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
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

/// <summary>Weight used for the lyric lines on the card.</summary>
public enum LyricCardWeight
{
    SemiBold,
    Bold
}

/// <summary>Arrangement of artwork, title/artist and lyrics on the card.</summary>
public enum ShareCardLayout
{
    /// <summary>Rounded card with a two-tone header panel (artwork + title/artist)
    /// above left-aligned lyrics.</summary>
    Panel,
    /// <summary>Full-bleed poster: large centered artwork, centered title/artist,
    /// centered lyrics.</summary>
    Poster
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
    /// <summary>Weight used for the lyric lines (new full-bleed renderer).</summary>
    public LyricCardWeight LyricWeight { get; init; } = LyricCardWeight.SemiBold;
    /// <summary>How artwork, title/artist and lyrics are arranged on the card.</summary>
    public ShareCardLayout Layout { get; init; } = ShareCardLayout.Panel;
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
    private const float Pad = 96f;               // Wrap-card content padding
    private const float CardRadius = 56f;        // frosted artwork-card corner radius
    private const float FooterHeight = 44f;      // wordmark logo height
    private const float LyricGapTop = 60f;       // gap between header and lyric block
    private const float LyricGapBottom = 60f;    // gap between lyric block and wordmark

    /// <summary>
    /// Spotify-style renderer: a full-bleed solid color (or an improved blurred-artwork
    /// card) with the content filling the frame — small header at the top, large lyrics
    /// centered in the middle, wordmark pinned to the bottom. Lyric weight is selectable.
    /// Pure SkiaSharp; safe to call off the UI thread.
    /// </summary>
    public static byte[] RenderLyricCardStyled(LyricCardSpec spec)
    {
        int w = CanvasWidth;
        int h = spec.Format == ShareCardFormat.Story ? 1920 : 1080;

        using var art = LoadArtwork(spec.ArtworkPath);
        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        var canvas = surface.Canvas;

        using var boldFace = ResolveTypeface(SKFontStyleWeight.Bold);
        using var regularFace = ResolveTypeface(SKFontStyleWeight.Normal);
        using var lyricFace = ResolveTypeface(
            spec.LyricWeight == LyricCardWeight.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.SemiBold);

        var chrome = DrawCardBase(canvas, spec, w, h, art, boldFace, regularFace, lyricFace);

        using var lyricPaint = TextPaint(lyricFace, chrome.LyricSize, chrome.Fg);
        float baseline = chrome.FirstBaseline;
        for (int i = 0; i < chrome.Wrapped.Count; i++)
        {
            DrawTextFallback(canvas, chrome.Wrapped[i].Text, chrome.RowX[i], baseline, lyricPaint);
            baseline += chrome.LineHeight;
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Geometry the lyric block was laid out with, so per-frame redraws land
    /// on exactly the same pixels as the still card. RowX is per wrapped row — the
    /// Poster layout centers each row, the Panel layout left-aligns them all.</summary>
    private sealed record CardChrome(
        SKColor Fg, float[] RowX, float FirstBaseline,
        List<(string Text, int Line)> Wrapped, float LyricSize, float LineHeight);

    /// <summary>
    /// Draws the full card EXCEPT the lyric rows (background, frosted/solid box, header,
    /// wordmark) and returns the lyric-block geometry. Shared by the still card and the
    /// karaoke frame loop so the two always agree pixel-for-pixel.
    /// </summary>
    private static CardChrome DrawCardBase(
        SKCanvas canvas, LyricCardSpec spec, int w, int h, SKBitmap? art,
        SKTypeface boldFace, SKTypeface regularFace, SKTypeface lyricFace)
    {
        bool artworkBlur = spec.Background == ShareBackground.Artwork && art != null;

        // Solid color: an explicit swatch hex if given, else a vibrant color derived from
        // the artwork (hue-dominant — far more faithful than a flat average).
        var solidColor = spec.Background == ShareBackground.Solid
                         && SKColor.TryParse(spec.SolidColorHex, out var parsed)
            ? parsed
            : DeriveVibrantColor(art);

        bool darkText;
        SKColor fg, fgSubtle, badgeLetter;

        if (artworkBlur)
        {
            canvas.Clear(SKColors.Black);
            DrawBlurredCover(canvas, art!, w, h);
            // Lighter scrim than the legacy card so the cover stays vivid behind legible text.
            using (var scrim = new SKPaint { Color = new SKColor(0, 0, 0, 0x6E) })
                canvas.DrawRect(0, 0, w, h, scrim);
            darkText = false;
            fg = SKColors.White;
            fgSubtle = fg.WithAlpha(205);
            badgeLetter = new SKColor(0x1B, 0x1B, 0x1B);
        }
        else
        {
            canvas.Clear(solidColor);
            darkText = spec.TextColor switch
            {
                ShareTextColor.Black => true,
                ShareTextColor.White => false,
                _ => UseDarkText(solidColor.Red, solidColor.Green, solidColor.Blue),
            };
            fg = darkText ? new SKColor(0x14, 0x14, 0x14) : SKColors.White;
            fgSubtle = fg.WithAlpha((byte)(darkText ? 150 : 200));
            badgeLetter = solidColor;
        }

        return spec.Layout == ShareCardLayout.Poster
            ? DrawPosterContent(canvas, spec, w, h, art, artworkBlur, fg, fgSubtle, badgeLetter,
                boldFace, regularFace, lyricFace)
            : DrawPanelContent(canvas, spec, w, h, art, artworkBlur, darkText, fg, fgSubtle, badgeLetter,
                boldFace, regularFace, lyricFace);
    }

    /// <summary>
    /// Panel layout: a content-hugging rounded card floating on the background, with a
    /// deeper-toned header panel (artwork thumbnail + title/artist) above left-aligned
    /// lyrics and the wordmark pinned to the card's bottom.
    /// </summary>
    private static CardChrome DrawPanelContent(
        SKCanvas canvas, LyricCardSpec spec, int w, int h, SKBitmap? art,
        bool artworkBlur, bool darkText, SKColor fg, SKColor fgSubtle, SKColor badgeLetter,
        SKTypeface boldFace, SKTypeface regularFace, SKTypeface lyricFace)
    {
        const float headerPad = 26f, headerArt = 148f, headerTextGap = 30f;
        float headerPanelH = headerArt + 2 * headerPad;

        // Social-safe margins. Story: content stays inside the reels-safe band — clear of
        // the TikTok/Instagram top search bar and bottom caption/actions overlays, and
        // inside the profile grid's 3:4 cover crop (center 1080×1440 ⇒ y 240..1680).
        // Square: Instagram's profile grid crops 1:1 posts to 3:4 (center ~810px wide),
        // so the inner padding is wide enough that title/lyrics start inside the crop.
        float marginX, marginTop, marginBottom, pad;
        if (spec.Format == ShareCardFormat.Story)
        {
            marginX = 60f; marginTop = 240f; marginBottom = 260f; pad = 44f;
        }
        else
        {
            marginX = 72f; marginTop = 60f; marginBottom = 60f; pad = 64f;
        }

        float boxContentW = w - 2 * marginX - 2 * pad;
        float fixedH = headerPanelH + LyricGapTop + LyricGapBottom + FooterHeight;
        float maxBoxH = h - marginTop - marginBottom;
        var fit = FitLyrics(spec, boxContentW, maxBoxH - 2 * pad - fixedH, lyricFace);
        float contentH = fixedH + fit.wrapped.Count * fit.lineHeight;
        float boxH = Math.Min(contentH + 2 * pad, maxBoxH);
        float boxTop = marginTop + (maxBoxH - boxH) / 2f;
        var boxRect = new SKRect(marginX, boxTop, w - marginX, boxTop + boxH);

        // Card body: a translucent overlay tone so the background color stays the star —
        // lighter than the bg for light text, deeper for dark text, frosted over artwork.
        var bodyFill = artworkBlur ? new SKColor(255, 255, 255, 0x22)
                     : darkText ? new SKColor(0, 0, 0, 0x12)
                     : new SKColor(255, 255, 255, 0x16);
        using (var fill = new SKPaint { IsAntialias = true, Color = bodyFill })
            canvas.DrawRoundRect(boxRect, CardRadius, CardRadius, fill);
        using (var border = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            Color = fg.WithAlpha(0x2C),
        })
            canvas.DrawRoundRect(boxRect, CardRadius, CardRadius, border);

        // ── Header panel: a deeper tone than the body, so it reads as its own chip ──
        var headRect = new SKRect(boxRect.Left + pad, boxRect.Top + pad,
                                  boxRect.Right - pad, boxRect.Top + pad + headerPanelH);
        var headFill = artworkBlur ? new SKColor(0, 0, 0, 0x46)
                     : darkText ? new SKColor(0, 0, 0, 0x16)
                     : new SKColor(0, 0, 0, 0x30);
        using (var fill = new SKPaint { IsAntialias = true, Color = headFill })
            canvas.DrawRoundRect(headRect, 30, 30, fill);

        var artRect = new SKRect(headRect.Left + headerPad, headRect.Top + headerPad,
                                 headRect.Left + headerPad + headerArt, headRect.Top + headerPad + headerArt);
        DrawArtworkTile(canvas, art, artRect, 18, fg);

        using var titlePaint = TextPaint(boldFace, 43, fg);
        using var artistPaint = TextPaint(regularFace, 30, fgSubtle);
        float textX = artRect.Right + headerTextGap;
        float textMaxW = headRect.Right - headerPad - textX;
        float titleBaseline = headRect.MidY - 10;
        float badgeReserve = spec.IsExplicit ? titlePaint.TextSize * 0.72f + 14 : 0;
        var titleText = Ellipsize(SanitizeForRender(spec.Title), textMaxW - badgeReserve, s => MeasureTextFallback(titlePaint, s));
        DrawTextFallback(canvas, titleText, textX, titleBaseline, titlePaint);
        if (spec.IsExplicit)
        {
            float badgeX = textX + MeasureTextFallback(titlePaint, titleText) + 14;
            DrawExplicitBadge(canvas, regularFace, fg, badgeLetter,
                badgeX, titleBaseline - titlePaint.TextSize * 0.34f, titlePaint.TextSize);
        }
        DrawTextFallback(canvas, Ellipsize(SanitizeForRender(spec.Artist), textMaxW, s => MeasureTextFallback(artistPaint, s)),
            textX, titleBaseline + 42, artistPaint);

        // ── Footer wordmark, pinned to the card's bottom ────────────────
        float footerTop = boxRect.Bottom - pad - FooterHeight;
        DrawWordmark(canvas, boldFace, fg, headRect.Left, footerTop);

        // ── Lyric geometry: auto-fit, then vertically center between header & footer ──
        float lyTop = headRect.Bottom + LyricGapTop;
        float lyBottom = footerTop - LyricGapBottom;
        float avail = Math.Max(0f, lyBottom - lyTop);

        var (wrapped, lyricSize, lineHeight) = FitLyrics(spec, boxContentW, avail, lyricFace);
        float blockH = wrapped.Count * lineHeight;
        float firstBaseline = lyTop + Math.Max(0f, (avail - blockH) / 2f) + lyricSize * 0.80f;
        var rowX = new float[wrapped.Count];
        Array.Fill(rowX, headRect.Left);
        return new CardChrome(fg, rowX, firstBaseline, wrapped, lyricSize, lineHeight);
    }

    /// <summary>
    /// Poster layout: no card box — a large centered artwork with a soft shadow near the
    /// top, centered title/artist beneath it, centered lyrics, and a centered wordmark.
    /// The whole block is vertically centered on the canvas.
    /// </summary>
    private static CardChrome DrawPosterContent(
        SKCanvas canvas, LyricCardSpec spec, int w, int h, SKBitmap? art,
        bool artworkBlur, SKColor fg, SKColor fgSubtle, SKColor badgeLetter,
        SKTypeface boldFace, SKTypeface regularFace, SKTypeface lyricFace)
    {
        const float gapArtTitle = 54f, gapTitleArtist = 16f, gapArtistLyrics = 62f, gapLyricsFooter = 64f;
        const float titleSize = 50f, artistSize = 32f;
        float artSize = spec.Format == ShareCardFormat.Story ? 460f : 380f;

        // Social-safe margins — same contract as the Panel layout: Story centers the
        // block inside the reels-safe band (clear of the top/bottom app overlays and the
        // profile grid's 3:4 cover crop); Square keeps text inside the grid's 3:4 side
        // crop (center ~810px) via a wider content inset.
        float marginTop, marginBottom, contentInset;
        if (spec.Format == ShareCardFormat.Story)
        {
            marginTop = 240f; marginBottom = 260f; contentInset = 104f;
        }
        else
        {
            marginTop = 72f; marginBottom = 72f; contentInset = 140f;
        }
        float contentW = w - 2 * contentInset;
        float safeH = h - marginTop - marginBottom;

        float FixedH(float a) => a + gapArtTitle + titleSize + gapTitleArtist + artistSize
                                 + gapArtistLyrics + gapLyricsFooter + FooterHeight;
        var fit = FitLyrics(spec, contentW, safeH - FixedH(artSize), lyricFace);
        float overflow = FixedH(artSize) + fit.wrapped.Count * fit.lineHeight - safeH;
        if (overflow > 0)
        {
            // Long selections at the minimum lyric size: give the artwork's pixels to the
            // lyrics instead of letting the block run out of the safe band.
            artSize = Math.Max(240f, artSize - overflow);
            fit = FitLyrics(spec, contentW, safeH - FixedH(artSize), lyricFace);
        }
        float blockH = FixedH(artSize) + fit.wrapped.Count * fit.lineHeight;
        float top = Math.Max(marginTop, marginTop + (safeH - blockH) / 2f);

        // ── Artwork hero: soft drop shadow, rounded tile, hairline edge ──
        var artRect = new SKRect((w - artSize) / 2f, top, (w + artSize) / 2f, top + artSize);
        using (var shadow = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, artworkBlur ? (byte)0x66 : (byte)0x52),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 26f),
        })
            canvas.DrawRoundRect(new SKRect(artRect.Left + 6, artRect.Top + 16, artRect.Right - 6, artRect.Bottom + 16),
                24, 24, shadow);
        DrawArtworkTile(canvas, art, artRect, 24, fg);

        // ── Centered title (+ badge) and artist ─────────────────────────
        using var titlePaint = TextPaint(boldFace, titleSize, fg);
        using var artistPaint = TextPaint(regularFace, artistSize, fgSubtle);
        float badgeSize = spec.IsExplicit ? titleSize * 0.72f : 0f;
        float badgeGap = spec.IsExplicit ? 16f : 0f;
        var titleText = Ellipsize(SanitizeForRender(spec.Title), contentW - badgeSize - badgeGap,
            s => MeasureTextFallback(titlePaint, s));
        float titleW = MeasureTextFallback(titlePaint, titleText);
        float titleX = (w - titleW - badgeGap - badgeSize) / 2f;
        float titleBaseline = artRect.Bottom + gapArtTitle + titleSize * 0.78f;
        DrawTextFallback(canvas, titleText, titleX, titleBaseline, titlePaint);
        if (spec.IsExplicit)
            DrawExplicitBadge(canvas, regularFace, fg, badgeLetter,
                titleX + titleW + badgeGap, titleBaseline - titleSize * 0.34f, titleSize);

        var artistText = Ellipsize(SanitizeForRender(spec.Artist), contentW, s => MeasureTextFallback(artistPaint, s));
        float artistBaseline = artRect.Bottom + gapArtTitle + titleSize + gapTitleArtist + artistSize * 0.78f;
        DrawTextFallback(canvas, artistText, (w - MeasureTextFallback(artistPaint, artistText)) / 2f,
            artistBaseline, artistPaint);

        // ── Centered lyric rows ─────────────────────────────────────────
        float lyTop = artRect.Bottom + gapArtTitle + titleSize + gapTitleArtist + artistSize + gapArtistLyrics;
        var (wrapped, lyricSize, lineHeight) = fit;
        float firstBaseline = lyTop + lyricSize * 0.80f;
        using var lyricMeasure = TextPaint(lyricFace, lyricSize, fg);
        var rowX = new float[wrapped.Count];
        for (int i = 0; i < wrapped.Count; i++)
            rowX[i] = (w - MeasureTextFallback(lyricMeasure, wrapped[i].Text)) / 2f;

        // ── Centered wordmark under the lyric block ─────────────────────
        float footerTop = lyTop + wrapped.Count * lineHeight + gapLyricsFooter;
        using (var wordmarkPaint = TextPaint(boldFace, 38, fg))
        {
            float textW = MeasureTextFallback(wordmarkPaint, "Noctis");
            float wordmarkW = FooterHeight + 14 + textW;
            DrawWordmark(canvas, boldFace, fg, (w - wordmarkW) / 2f, footerTop);
        }

        return new CardChrome(fg, rowX, firstBaseline, wrapped, lyricSize, lineHeight);
    }

    /// <summary>Draws the album artwork (or a placeholder tone) as a rounded tile with a
    /// subtle hairline outline, shared by both layouts.</summary>
    private static void DrawArtworkTile(SKCanvas canvas, SKBitmap? art, SKRect artRect, float radius, SKColor fg)
    {
        if (art != null)
        {
            using var artPaint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            using var rounded = new SKRoundRect(artRect, radius);
            canvas.Save();
            canvas.ClipRoundRect(rounded, antialias: true);
            canvas.DrawBitmap(art, artRect, artPaint);
            canvas.Restore();
        }
        else
        {
            using var placeholder = new SKPaint { IsAntialias = true, Color = fg.WithAlpha(28) };
            canvas.DrawRoundRect(artRect, radius, radius, placeholder);
        }
        // Subtle outline so the tile reads as a distinct element even when the cover
        // color is close to the card color.
        using var artBorder = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            Color = fg.WithAlpha(0x30),
        };
        canvas.DrawRoundRect(artRect, radius, radius, artBorder);
    }

    /// <summary>Alpha of unsung lyric text on karaoke frames (the dim base layer) —
    /// mirrors the lyrics page's active-line word-base opacity of 0.45.</summary>
    private const byte KaraokeDimAlpha = 0x73;
    /// <summary>Sweep feather, as a fraction of the current word's width — mirrors
    /// <c>ProgressToSweepForegroundConverter.Feather</c> on the lyrics page.</summary>
    private const float KaraokeFeather = 0.06f;
    /// <summary>Held-note threshold — mirrors <c>LyricLine.EmphasisMs</c> on the lyrics page.</summary>
    private const double KaraokeEmphasisSeconds = 1.0;
    /// <summary>Glow fade in/out — mirrors the page's 0.45 s word-glow opacity transition.</summary>
    private const double KaraokeGlowFadeSeconds = 0.45;
    /// <summary>Peak glow opacity — mirrors the page's emphasis word-glow opacity of 0.5.</summary>
    private const float KaraokeGlowAlpha = 0.5f;

    /// <summary>
    /// Glow strength for a word at time <paramref name="t"/>: 0 for words shorter than the
    /// held-note threshold, else eased 0→peak over the fade-in, held while sung, and eased
    /// back to 0 after the word ends — the page's emphasis glow timing.
    /// </summary>
    private static float KaraokeGlowLevel(double start, double end, double t)
    {
        if (end - start < KaraokeEmphasisSeconds) return 0f;
        if (t < start || t > end + KaraokeGlowFadeSeconds) return 0f;
        double x = t < start + KaraokeGlowFadeSeconds ? (t - start) / KaraokeGlowFadeSeconds
                 : t <= end ? 1.0
                 : 1.0 - (t - end) / KaraokeGlowFadeSeconds;
        // CubicEaseInOut, like the page's word-glow transition.
        double eased = x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
        return (float)(KaraokeGlowAlpha * eased);
    }

    /// <summary>
    /// Renders the karaoke frame sequence for a clip: the same card as
    /// <see cref="RenderLyricCardStyled"/>, chrome drawn once, then per frame the lyric
    /// block repainted with the word sweep at that frame's absolute track time
    /// (timing.StartSeconds + frame/fps). Frames are written as frame-00000.jpg… into
    /// <paramref name="frameDir"/>; <paramref name="karaoke"/> is parallel to
    /// <see cref="LyricCardSpec.Lines"/>. Lines whose rendered rows can't be mapped back
    /// to their word tokens degrade to a whole-line highlight. CPU-only — call off the
    /// UI thread.
    /// </summary>
    public static void RenderKaraokeFrames(
        LyricCardSpec spec, IReadOnlyList<KaraokeLine> karaoke, ShareClipTiming timing,
        int fps, string frameDir, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        int w = CanvasWidth;
        int h = spec.Format == ShareCardFormat.Story ? 1920 : 1080;

        using var art = LoadArtwork(spec.ArtworkPath);
        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        var canvas = surface.Canvas;

        using var boldFace = ResolveTypeface(SKFontStyleWeight.Bold);
        using var regularFace = ResolveTypeface(SKFontStyleWeight.Normal);
        using var lyricFace = ResolveTypeface(
            spec.LyricWeight == LyricCardWeight.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.SemiBold);

        var chrome = DrawCardBase(canvas, spec, w, h, art, boldFace, regularFace, lyricFace);
        using var baseImage = surface.Snapshot();   // chrome only, no lyric rows

        // Row → word-token ranges, resolved once (null = degrade that line to line highlight).
        var rowRanges = MapKaraokeRows(chrome.Wrapped, karaoke);

        using var dimPaint = TextPaint(lyricFace, chrome.LyricSize, chrome.Fg.WithAlpha(KaraokeDimAlpha));
        using var brightPaint = TextPaint(lyricFace, chrome.LyricSize, chrome.Fg);

        int total = Math.Max(1, (int)Math.Ceiling(fps * timing.DurationSeconds));
        for (int frame = 0; frame < total; frame++)
        {
            ct.ThrowIfCancellationRequested();
            double t = timing.StartSeconds + (double)frame / fps;

            canvas.DrawImage(baseImage, 0, 0);
            DrawKaraokeRows(canvas, chrome, karaoke, rowRanges, brightPaint, dimPaint, t);

            // JPEG, not PNG: encoding is the per-frame bottleneck, and at 60 fps the
            // sequence would otherwise take minutes and gigabytes. The video encode is
            // lossy anyway, so q92 JPEG is visually identical in the final MP4.
            using var img = surface.Snapshot();
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 92);
            using var fs = File.Create(Path.Combine(frameDir, $"frame-{frame:D5}.jpg"));
            data.SaveTo(fs);

            if (frame % 12 == 0 || frame == total - 1)
                progress?.Invoke(frame + 1, total);
        }
    }

    /// <summary>
    /// Live karaoke card renderer for the share dialog's preview: the card chrome is
    /// drawn once at construction (scaled), then <see cref="RenderFrame"/> repaints just
    /// the lyric sweep for a playback time and copies the pixels into a caller-supplied
    /// BGRA framebuffer (an Avalonia WriteableBitmap). Same drawing code as the exported
    /// clip, so the preview is exactly what Save Video produces. Not thread-safe — build
    /// off the UI thread if desired, then render from one thread at a time.
    /// </summary>
    public sealed class KaraokeCardAnimator : IDisposable
    {
        private readonly SKSurface _surface;
        private readonly SKImage _baseImage;
        private readonly CardChrome _chrome;
        private readonly IReadOnlyList<KaraokeLine> _karaoke;
        private readonly (int Start, int Count)?[] _rowRanges;
        private readonly SKTypeface _lyricFace;
        private readonly SKPaint _dimPaint;
        private readonly SKPaint _brightPaint;
        private readonly float _scale;

        public int PixelWidth { get; }
        public int PixelHeight { get; }

        public KaraokeCardAnimator(LyricCardSpec spec, IReadOnlyList<KaraokeLine> karaoke, float scale)
        {
            _karaoke = karaoke;
            _scale = scale;
            int w = CanvasWidth;
            int h = spec.Format == ShareCardFormat.Story ? 1920 : 1080;
            PixelWidth = (int)Math.Round(w * scale);
            PixelHeight = (int)Math.Round(h * scale);

            // BGRA premul to match Avalonia's WriteableBitmap framebuffer — the per-frame
            // readback is then a straight copy.
            _surface = SKSurface.Create(new SKImageInfo(PixelWidth, PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
            var canvas = _surface.Canvas;

            using var art = LoadArtwork(spec.ArtworkPath);
            using var boldFace = ResolveTypeface(SKFontStyleWeight.Bold);
            using var regularFace = ResolveTypeface(SKFontStyleWeight.Normal);
            _lyricFace = ResolveTypeface(
                spec.LyricWeight == LyricCardWeight.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.SemiBold);

            canvas.Save();
            canvas.Scale(scale);
            _chrome = DrawCardBase(canvas, spec, w, h, art, boldFace, regularFace, _lyricFace);
            canvas.Restore();
            _baseImage = _surface.Snapshot();

            _rowRanges = MapKaraokeRows(_chrome.Wrapped, karaoke);
            _dimPaint = TextPaint(_lyricFace, _chrome.LyricSize, _chrome.Fg.WithAlpha(KaraokeDimAlpha));
            _brightPaint = TextPaint(_lyricFace, _chrome.LyricSize, _chrome.Fg);
        }

        /// <summary>Renders the card at absolute track time <paramref name="tSeconds"/>
        /// into <paramref name="dest"/> (BGRA8888 premul, <see cref="PixelWidth"/> ×
        /// <see cref="PixelHeight"/>).</summary>
        public void RenderFrame(double tSeconds, IntPtr dest, int destRowBytes)
        {
            var canvas = _surface.Canvas;
            canvas.DrawImage(_baseImage, 0, 0);
            canvas.Save();
            canvas.Scale(_scale);
            DrawKaraokeRows(canvas, _chrome, _karaoke, _rowRanges, _brightPaint, _dimPaint, tSeconds);
            canvas.Restore();
            _surface.ReadPixels(
                new SKImageInfo(PixelWidth, PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul),
                dest, destRowBytes, 0, 0);
        }

        public void Dispose()
        {
            _dimPaint.Dispose();
            _brightPaint.Dispose();
            _baseImage.Dispose();
            _surface.Dispose();
            _lyricFace.Dispose();
        }
    }

    /// <summary>Resolves each wrapped row's token range in its source line's word list;
    /// a line whose rows don't cleanly re-assemble its tokens maps to null.</summary>
    private static (int Start, int Count)?[] MapKaraokeRows(
        List<(string Text, int Line)> wrapped, IReadOnlyList<KaraokeLine> karaoke)
    {
        var result = new (int Start, int Count)?[wrapped.Count];
        foreach (var group in Enumerable.Range(0, wrapped.Count).GroupBy(i => wrapped[i].Line))
        {
            int line = group.Key;
            var words = line < karaoke.Count ? karaoke[line].Words : null;
            if (words == null || words.Count == 0)
                continue;
            var rowIdx = group.ToList();
            var rows = rowIdx.Select(i => wrapped[i].Text).ToList();
            var ranges = KaraokeSweep.MapRowsToTokenRanges(words.Select(kw => kw.Token).ToList(), rows);
            if (ranges == null)
                continue;
            for (int r = 0; r < rowIdx.Count; r++)
                result[rowIdx[r]] = ranges[r];
        }
        return result;
    }

    /// <summary>
    /// Paints the lyric block at time <paramref name="t"/>: every row dim, then the sung
    /// part bright — per-word sweep with a feathered leading edge when the row has word
    /// mapping, whole-row highlight (lit once the line has started) otherwise. Held-note
    /// words get the page's soft glow, drawn beneath the text layers. Painted as
    /// overlaid foreground text, never OpacityMask (see ProgressToSweepForegroundConverter:
    /// mask layers clipped/boxed the glow on the lyrics page).
    /// </summary>
    private static void DrawKaraokeRows(
        SKCanvas canvas, CardChrome chrome, IReadOnlyList<KaraokeLine> karaoke,
        (int Start, int Count)?[] rowRanges, SKPaint brightPaint, SKPaint dimPaint, double t)
    {
        float baseline = chrome.FirstBaseline;
        using var glowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, chrome.LyricSize * 0.09f);
        for (int r = 0; r < chrome.Wrapped.Count; r++, baseline += chrome.LineHeight)
        {
            var (text, lineIdx) = chrome.Wrapped[r];
            float rowX = chrome.RowX[r];
            var line = lineIdx < karaoke.Count ? karaoke[lineIdx] : null;

            if (rowRanges[r] is not { } range || line?.Words is not { } words)
            {
                DrawTextFallback(canvas, text, rowX, baseline, dimPaint);
                // Line-level highlight: lit once the line has started (always lit if unsynced).
                bool lit = line == null || line.StartSeconds is not { } start || t >= start;
                if (lit)
                    DrawTextFallback(canvas, text, rowX, baseline, brightPaint);
                continue;
            }

            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Held-note glow under the base text (page: word-cell.emphasis word-glow).
            for (int k = 0; k < tokens.Length; k++)
            {
                var word = words[range.Start + k];
                float glow = KaraokeGlowLevel(word.StartSeconds, word.EndSeconds, t);
                if (glow <= 0.01f)
                    continue;
                float glowPrefixW = k > 0
                    ? MeasureTextFallback(brightPaint, string.Join(' ', tokens.Take(k)) + " ")
                    : 0f;
                using var glowPaint = TextPaint(brightPaint.Typeface!, chrome.LyricSize,
                    brightPaint.Color.WithAlpha((byte)(glow * 255)));
                glowPaint.MaskFilter = glowBlur;
                DrawTextFallback(canvas, tokens[k], rowX + glowPrefixW, baseline, glowPaint);
            }

            DrawTextFallback(canvas, text, rowX, baseline, dimPaint);

            // Word sweep: bright width = fully-sung tokens + fraction of the current one.
            float brightW = MeasureTextFallback(brightPaint, text);   // default: every word sung
            float featherW = 0f;
            for (int k = 0; k < tokens.Length; k++)
            {
                var word = words[range.Start + k];
                double p = KaraokeSweep.WordProgress(word.StartSeconds, word.EndSeconds, t);
                if (p >= 1)
                    continue;
                float wordW = MeasureTextFallback(brightPaint, tokens[k]);
                float prefixW = k > 0
                    ? MeasureTextFallback(brightPaint, string.Join(' ', tokens.Take(k)) + " ")
                    : 0f;
                brightW = prefixW + (float)p * wordW;
                // Feather shrinks toward the word's edges, like the lyrics-page converter.
                featherW = (float)Math.Min(KaraokeFeather * wordW, Math.Min(p, 1 - p) * wordW);
                break;
            }

            if (brightW <= 0f)
                continue;
            if (featherW <= 0.5f)
            {
                // Hard edge (word boundary): clip-rect reveal.
                canvas.Save();
                canvas.ClipRect(new SKRect(rowX, baseline - chrome.LyricSize * 1.1f,
                                           rowX + brightW, baseline + chrome.LyricSize * 0.45f));
                DrawTextFallback(canvas, text, rowX, baseline, brightPaint);
                canvas.Restore();
            }
            else
            {
                // Feathered edge mid-word: foreground gradient bright→transparent, same RGB.
                using var shader = SKShader.CreateLinearGradient(
                    new SKPoint(rowX + brightW - featherW, 0),
                    new SKPoint(rowX + brightW + featherW, 0),
                    new[] { brightPaint.Color, brightPaint.Color.WithAlpha(0) },
                    null, SKShaderTileMode.Clamp);
                using var sweep = TextPaint(brightPaint.Typeface!, chrome.LyricSize, brightPaint.Color);
                sweep.Shader = shader;
                DrawTextFallback(canvas, text, rowX, baseline, sweep);
            }
        }
    }

    /// <summary>
    /// Greedily shrinks the lyric font (from a format-dependent base) until the wrapped
    /// block fits <paramref name="maxLyricsH"/>; returns the wrapped lines, chosen size and
    /// line height. Shared by the box-sizing and draw passes so the two always agree.
    /// </summary>
    private static (List<(string Text, int Line)> wrapped, float lyricSize, float lineHeight) FitLyrics(
        LyricCardSpec spec, float contentW, float maxLyricsH, SKTypeface lyricFace)
    {
        var renderLines = spec.Lines.Select(SanitizeForRender).ToList();
        float lyricSize = spec.Format == ShareCardFormat.Story ? 78f : 66f;
        using var lyricPaint = TextPaint(lyricFace, lyricSize, SKColors.White);
        List<(string Text, int Line)> wrapped;
        float lineHeight;
        while (true)
        {
            lyricPaint.TextSize = lyricSize;
            wrapped = WrapAll(renderLines, contentW, s => MeasureTextFallback(lyricPaint, s));
            lineHeight = lyricSize * 1.30f;
            if (wrapped.Count * lineHeight <= maxLyricsH || lyricSize <= 34f)
                break;
            lyricSize -= 3f;
        }
        return (wrapped, lyricSize, lineHeight);
    }

    private const int VibrantCacheMax = 300;
    private static readonly ConcurrentDictionary<string, string> _vibrantHexCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The vibrant background color this renderer derives for <paramref name="artworkPath"/>,
    /// as a "#RRGGBB" hex string — so the dialog's "Auto" swatch and the lyrics page's
    /// Solid·Auto background match the rendered card exactly. Cached per path: decoding
    /// the artwork is the expensive part and callers may sit on the UI thread.
    /// </summary>
    public static string GetVibrantColorHex(string? artworkPath)
    {
        if (string.IsNullOrEmpty(artworkPath))
            return "#1A1A2E";
        if (_vibrantHexCache.TryGetValue(artworkPath, out var cached))
            return cached;

        using var art = LoadArtwork(artworkPath);
        var c = DeriveVibrantColor(art);
        var hex = $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

        if (_vibrantHexCache.Count >= VibrantCacheMax)
            _vibrantHexCache.Clear();
        _vibrantHexCache.TryAdd(artworkPath, hex);
        return hex;
    }

    /// <summary>
    /// A vibrant background color faithful to the artwork: pixels vote for one of 24 hue
    /// buckets weighted by chroma², the winning bucket (plus its neighbours) is averaged,
    /// and lightness is clamped to a legible background range. Unlike a global weighted
    /// average — which blends opposing hues (blue cover + skin tones) into brown mud —
    /// this keeps the hue of the cover's dominant colorful region.
    /// </summary>
    private static SKColor DeriveVibrantColor(SKBitmap? art)
    {
        var fallback = new SKColor(0x1A, 0x1A, 0x2E);
        if (art == null)
            return fallback;

        const int Buckets = 24;
        var bw = new double[Buckets];
        var br = new double[Buckets]; var bg = new double[Buckets]; var bb = new double[Buckets];
        double ar = 0, ag = 0, ab = 0; long an = 0;   // plain average (grey-cover fallback)

        for (int y = 0; y < art.Height; y += 6)
        {
            for (int x = 0; x < art.Width; x += 6)
            {
                var px = art.GetPixel(x, y);
                if (px.Alpha < 32) continue;
                ar += px.Red; ag += px.Green; ab += px.Blue; an++;

                double r = px.Red / 255.0, g = px.Green / 255.0, b = px.Blue / 255.0;
                double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                if (luma < 0.06 || luma > 0.94) continue;  // skip near-black / near-white
                double max = Math.Max(r, Math.Max(g, b));
                double min = Math.Min(r, Math.Min(g, b));
                double chroma = max - min;
                if (chroma < 0.05) continue;               // near-grey pixels don't vote a hue

                px.ToHsl(out var hDeg, out _, out _);
                int bucket = (int)(hDeg / 360f * Buckets) % Buckets;
                double weight = chroma * chroma;
                bw[bucket] += weight;
                br[bucket] += px.Red * weight; bg[bucket] += px.Green * weight; bb[bucket] += px.Blue * weight;
            }
        }

        SKColor baseColor;
        int best = 0;
        for (int i = 1; i < Buckets; i++)
            if (bw[i] > bw[best]) best = i;

        if (bw[best] > 0.25)
        {
            // Merge the winner with its hue neighbours so colors straddling a bucket
            // boundary aren't split.
            int prev = (best + Buckets - 1) % Buckets, next = (best + 1) % Buckets;
            double w = bw[prev] + bw[best] + bw[next];
            baseColor = new SKColor(
                (byte)((br[prev] + br[best] + br[next]) / w),
                (byte)((bg[prev] + bg[best] + bg[next]) / w),
                (byte)((bb[prev] + bb[best] + bb[next]) / w));
        }
        else if (an > 0)
        {
            baseColor = new SKColor((byte)(ar / an), (byte)(ag / an), (byte)(ab / an));
        }
        else
        {
            return fallback;
        }

        baseColor.ToHsl(out var hue, out var s, out var l);
        s = Math.Clamp(Math.Max(s, 30f) * 1.06f, 0f, 96f);
        l = Math.Clamp(l, 20f, 54f);
        return SKColor.FromHsl(hue, s, l);
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

    // ── Font fallback for non-Latin text ─────────────────────────────────
    // SkiaSharp's DrawText paints a .notdef "tofu" box for any glyph the single
    // typeface lacks — there is no per-run fallback like Avalonia's. Korean /
    // Japanese / Chinese lyrics therefore rendered as boxes. These helpers split
    // text into runs per typeface, matching missing scripts through the OS font
    // manager (DirectWrite / CoreText / fontconfig), and are used for BOTH drawing
    // and measuring so wrapping, fitting and the karaoke sweep stay aligned.

    private static readonly Dictionary<(int Block, int Weight), SKTypeface?> _fallbackFaces = new();
    private static readonly object _fallbackLock = new();

    /// <summary>
    /// Splits <paramref name="text"/> into runs drawable with one typeface each: the
    /// primary face wherever it has the glyphs, otherwise an OS-matched fallback per
    /// 256-codepoint block. Whitespace sticks to the current run. Runs concatenate
    /// back to the input; codepoints nothing can draw stay on the primary face.
    /// </summary>
    public static List<(string Run, SKTypeface Face)> SplitFallbackRuns(string text, SKTypeface primary)
    {
        var runs = new List<(string Run, SKTypeface Face)>();
        if (string.IsNullOrEmpty(text))
            return runs;

        var sb = new StringBuilder();
        SKTypeface currentFace = primary;
        for (int i = 0; i < text.Length;)
        {
            int cp = char.ConvertToUtf32(text, i);
            int len = char.IsSurrogatePair(text, i) ? 2 : 1;

            SKTypeface face;
            if (char.IsWhiteSpace(text[i]) || primary.ContainsGlyph(cp))
                face = sb.Length > 0 && char.IsWhiteSpace(text[i]) ? currentFace : primary;
            else
                face = MatchFallbackFace(primary, cp) ?? primary;

            if (sb.Length > 0 && !ReferenceEquals(face, currentFace))
            {
                runs.Add((sb.ToString(), currentFace));
                sb.Clear();
            }
            currentFace = face;
            sb.Append(text, i, len);
            i += len;
        }
        if (sb.Length > 0)
            runs.Add((sb.ToString(), currentFace));
        return runs;
    }

    /// <summary>OS-matched typeface for a codepoint the primary face can't draw,
    /// cached per 256-codepoint block + weight. Null when no installed font has it.</summary>
    private static SKTypeface? MatchFallbackFace(SKTypeface primary, int codepoint)
    {
        var key = (codepoint >> 8, (int)primary.FontStyle.Weight);
        lock (_fallbackLock)
        {
            if (_fallbackFaces.TryGetValue(key, out var cached))
                return cached;
            var matched = SKFontManager.Default.MatchCharacter(null, primary.FontStyle, null, codepoint);
            _fallbackFaces[key] = matched;   // cached for the process lifetime, never disposed
            return matched;
        }
    }

    /// <summary>Width of <paramref name="text"/> under <paramref name="paint"/> with
    /// font fallback — the sum of each run measured with its own typeface.</summary>
    private static float MeasureTextFallback(SKPaint paint, string text)
    {
        var runs = SplitFallbackRuns(text, paint.Typeface!);
        if (runs.Count == 1 && ReferenceEquals(runs[0].Face, paint.Typeface))
            return paint.MeasureText(text);

        float width = 0;
        var saved = paint.Typeface;
        foreach (var (run, face) in runs)
        {
            paint.Typeface = face;
            width += paint.MeasureText(run);
        }
        paint.Typeface = saved;
        return width;
    }

    /// <summary>Draws <paramref name="text"/> with font fallback, advancing x run by
    /// run — the drawing twin of <see cref="MeasureTextFallback"/>.</summary>
    private static void DrawTextFallback(SKCanvas canvas, string text, float x, float y, SKPaint paint)
    {
        var runs = SplitFallbackRuns(text, paint.Typeface!);
        if (runs.Count == 1 && ReferenceEquals(runs[0].Face, paint.Typeface))
        {
            canvas.DrawText(text, x, y, paint);
            return;
        }

        var saved = paint.Typeface;
        foreach (var (run, face) in runs)
        {
            paint.Typeface = face;
            canvas.DrawText(run, x, y, paint);
            x += paint.MeasureText(run);
        }
        paint.Typeface = saved;
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

    /// <summary>
    /// Normalizes text for SkiaSharp's single-typeface <c>DrawText</c>, which does no font
    /// fallback — any glyph the render font lacks is painted as a .notdef "tofu" box. Lyrics
    /// from online sources often join words with exotic spaces (non-breaking U+00A0, narrow
    /// no-break U+202F, thin/figure spaces) or zero-width / formatting marks (ZWSP, word
    /// joiner, BOM) that fonts like Inter can't draw. Every such separator is folded to a
    /// single plain ASCII space — NOT dropped, so "leave​her" becomes "leave her", not
    /// "leaveher" — with runs collapsed and the ends trimmed.
    /// </summary>
    public static string SanitizeForRender(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        bool wroteNonSpace = false;
        bool pendingSpace = false;
        foreach (var ch in text)
        {
            bool isSeparator = ch == ' ' || CharUnicodeInfo.GetUnicodeCategory(ch) is
                UnicodeCategory.SpaceSeparator       // NBSP, narrow/thin/ideographic spaces, …
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Format            // ZWSP, ZWNJ, ZWJ, word joiner, BOM, …
                or UnicodeCategory.Control;          // tab, stray control characters

            if (isSeparator)
            {
                // Defer the space: this collapses runs and drops leading/trailing whitespace
                // (a pending space is only flushed once a following non-space char arrives).
                if (wroteNonSpace)
                    pendingSpace = true;
            }
            else
            {
                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }
                sb.Append(ch);
                wroteNonSpace = true;
            }
        }
        return sb.ToString();
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

    private static List<(string Text, int Line)> WrapAll(IReadOnlyList<string> lines, float maxWidth, Func<string, float> measure)
    {
        var result = new List<(string Text, int Line)>();
        for (int i = 0; i < lines.Count; i++)
            foreach (var row in WrapText(lines[i], maxWidth, measure))
                result.Add((row, i));
        return result;
    }

    private static SKTypeface ResolveTypeface(bool bold) =>
        ResolveTypeface(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal);

    private static SKTypeface ResolveTypeface(SKFontStyleWeight weight)
    {
        var style = new SKFontStyle(weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
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
