using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Noctis.Controls;
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
    /// <summary>Most-played album's artwork: the card's big cover, and its colour tints the background.</summary>
    public string? ArtworkPath { get; init; }
    public ShareCardFormat Format { get; init; } = ShareCardFormat.Square;
    /// <summary>Most-played album's name and artist, shown with its cover. Null: the top artist leads.</summary>
    public string? TopAlbum { get; init; }
    public string? TopAlbumArtist { get; init; }
    public int TopAlbumPlays { get; init; }
    /// <summary>Covers of the top albums, most played first (the story card's strip shows 2-5).</summary>
    public IReadOnlyList<string?> AlbumCoverPaths { get; init; } = Array.Empty<string?>();
}

/// <summary>
/// Renders Spotify-style share cards (album art header, big bold lyric lines,
/// Noctis wordmark) into PNG bytes using SkiaSharp. Pure CPU rendering — safe
/// to call from any thread, no Avalonia visual tree involved.
/// </summary>
public static class ShareCardRenderer
{
    private const int CanvasWidth = 1080;
    private const float CardRadius = 56f;        // frosted artwork-card corner radius
    private const float FooterHeight = 44f;      // wordmark logo height
    private const float LyricGapTop = 60f;       // gap between header and lyric block
    private const float LyricGapBottom = 60f;    // gap between lyric block and wordmark

    /// <summary>Supersampling factor for the exported still card: geometry stays in
    /// 1080-space and the surface doubles it (2160×2160 / 2160×3840), so lyrics, header
    /// text, artwork and the wordmark export crisp instead of soft.</summary>
    public const float CardExportScale = 2f;

    /// <summary>
    /// Spotify-style renderer: a full-bleed solid color (or an improved blurred-artwork
    /// card) with the content filling the frame — small header at the top, large lyrics
    /// centered in the middle, wordmark pinned to the bottom. Lyric weight is selectable.
    /// <paramref name="scale"/> supersamples the output without touching the layout
    /// (all geometry stays in 1080-space). Pure SkiaSharp; safe to call off the UI thread.
    /// </summary>
    public static byte[] RenderLyricCardStyled(LyricCardSpec spec, float scale = 1f)
    {
        int w = CanvasWidth;
        int h = spec.Format == ShareCardFormat.Story ? 1920 : 1080;

        using var art = LoadArtwork(spec.ArtworkPath);
        using var surface = SKSurface.Create(new SKImageInfo(
            (int)Math.Round(w * scale), (int)Math.Round(h * scale)));
        DrawLyricCard(surface.Canvas, spec, w, h, scale, art);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// The live preview's render: the same card as <see cref="RenderLyricCardStyled"/>, drawn
    /// straight into a BGRA premul bitmap with an already decoded cover (<see cref="LoadCardArtwork"/>).
    /// No PNG round trip and no cover decode per change: those were most of the preview's
    /// lag (a 9:16 PNG encode alone ~130 ms). The caller owns the result.
    /// </summary>
    public static SKBitmap RenderLyricCardBitmap(LyricCardSpec spec, float scale, SKBitmap? art)
    {
        int w = CanvasWidth;
        int h = spec.Format == ShareCardFormat.Story ? 1920 : 1080;
        var bitmap = new SKBitmap(new SKImageInfo(
            (int)Math.Round(w * scale), (int)Math.Round(h * scale), SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        DrawLyricCard(canvas, spec, w, h, scale, art);
        return bitmap;
    }

    /// <summary>The cover as the card draws it (subsampled, at most 1024 px), decoded once by
    /// a caller that renders the same card many times. Null without usable artwork.</summary>
    public static SKBitmap? LoadCardArtwork(string? path) => LoadArtwork(path);

    private static void DrawLyricCard(SKCanvas canvas, LyricCardSpec spec, int w, int h, float scale, SKBitmap? art)
    {
        canvas.Scale(scale);

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
            // Text: Black frosts the cover light instead (it used to be ignored here: white
            // text whatever was picked). Auto stays white over the dark scrim.
            darkText = spec.TextColor == ShareTextColor.Black;
            using (var scrim = new SKPaint { Color = darkText ? new SKColor(255, 255, 255, 0xA0) : new SKColor(0, 0, 0, 0x6E) })
                canvas.DrawRect(0, 0, w, h, scrim);
            fg = darkText ? new SKColor(0x14, 0x14, 0x14) : SKColors.White;
            fgSubtle = fg.WithAlpha((byte)(darkText ? 150 : 205));
            badgeLetter = darkText ? SKColors.White : new SKColor(0x1B, 0x1B, 0x1B);
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
        var bodyFill = artworkBlur && !darkText ? new SKColor(255, 255, 255, 0x22)
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
        var headFill = artworkBlur && !darkText ? new SKColor(0, 0, 0, 0x46)
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

        var (wrapped, lyricSize, lineHeight, _) = FitLyrics(spec, boxContentW, avail, lyricFace);
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
        var fit = FitLyrics(spec, contentW, safeH - FixedH(artSize), lyricFace, ComfortLyricSize);
        float overflow = FixedH(artSize) + fit.naturalRows * fit.lineHeight - safeH;
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
        // The filter gets its own `using`: SKPaint.Dispose does NOT dispose an assigned
        // MaskFilter/ImageFilter/Shader, so these survived until finalization.
        using var shadowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 26f);
        using (var shadow = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, artworkBlur ? (byte)0x66 : (byte)0x52),
            MaskFilter = shadowBlur,
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
        var (wrapped, lyricSize, lineHeight, _) = fit;
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
    internal static void DrawArtworkTile(SKCanvas canvas, SKBitmap? art, SKRect artRect, float radius, SKColor fg)
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

    /// <summary>Held-note glow blur radius, in em — the page's EmphasisGlowBlurConverter.</summary>
    internal const float KaraokeGlowRadiusEm = 0.45f;

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

        using var paint = TextPaint(lyricFace, chrome.LyricSize, chrome.Fg);
        // Row → word-token ranges and glyph positions, resolved once (null range = degrade
        // that line to a line highlight).
        var rows = LayoutKaraokeRows(chrome, karaoke, paint);
        var flow = new KaraokeFlow(karaoke);
        using var glowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, chrome.LyricSize * KaraokeGlowRadiusEm * 0.5f);

        int total = Math.Max(1, (int)Math.Ceiling(fps * timing.DurationSeconds));
        for (int frame = 0; frame < total; frame++)
        {
            ct.ThrowIfCancellationRequested();
            // Same 80 ms word lookahead as the live preview and the lyrics page
            // (PreviewWordLookaheadSeconds) — without it the exported sweep
            // trails the vocal by a frame-visible margin.
            double t = timing.StartSeconds + (double)frame / fps + 0.08;

            canvas.DrawImage(baseImage, 0, 0);
            flow.Advance(t);
            DrawKaraokeRows(canvas, chrome, rows, flow, paint, glowBlur, t);

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
    public sealed class KaraokeCardAnimator : IDisposable, IShareFrameAnimator
    {
        private readonly SKSurface _surface;
        private readonly SKImage _baseImage;
        private readonly CardChrome _chrome;
        private readonly KaraokeRow[] _rows;
        private readonly KaraokeFlow _flow;
        private readonly SKTypeface _lyricFace;
        private readonly SKPaint _paint;
        private readonly SKMaskFilter _glowBlur;
        private readonly float _scale;

        public int PixelWidth { get; }
        public int PixelHeight { get; }

        /// <param name="art">The cover from <see cref="LoadCardArtwork"/> when the caller keeps
        /// one (not disposed here); null decodes <see cref="LyricCardSpec.ArtworkPath"/>.</param>
        public KaraokeCardAnimator(LyricCardSpec spec, IReadOnlyList<KaraokeLine> karaoke, float scale, SKBitmap? art = null)
        {
            _scale = scale;
            int w = CanvasWidth;
            int h = spec.Format == ShareCardFormat.Story ? 1920 : 1080;
            PixelWidth = (int)Math.Round(w * scale);
            PixelHeight = (int)Math.Round(h * scale);

            // BGRA premul to match Avalonia's WriteableBitmap framebuffer — the per-frame
            // readback is then a straight copy.
            _surface = SKSurface.Create(new SKImageInfo(PixelWidth, PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
            var canvas = _surface.Canvas;

            using var loaded = art == null ? LoadArtwork(spec.ArtworkPath) : null;
            using var boldFace = ResolveTypeface(SKFontStyleWeight.Bold);
            using var regularFace = ResolveTypeface(SKFontStyleWeight.Normal);
            _lyricFace = ResolveTypeface(
                spec.LyricWeight == LyricCardWeight.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.SemiBold);

            canvas.Save();
            canvas.Scale(scale);
            _chrome = DrawCardBase(canvas, spec, w, h, art ?? loaded, boldFace, regularFace, _lyricFace);
            canvas.Restore();
            _baseImage = _surface.Snapshot();

            _paint = TextPaint(_lyricFace, _chrome.LyricSize, _chrome.Fg);
            _rows = LayoutKaraokeRows(_chrome, karaoke, _paint);
            _flow = new KaraokeFlow(karaoke);
            _glowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, _chrome.LyricSize * KaraokeGlowRadiusEm * 0.5f);
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
            _flow.Advance(tSeconds);
            DrawKaraokeRows(canvas, _chrome, _rows, _flow, _paint, _glowBlur, tSeconds);
            canvas.Restore();
            _surface.ReadPixels(
                new SKImageInfo(PixelWidth, PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul),
                dest, destRowBytes, 0, 0);
        }

        public void Dispose()
        {
            _paint.Dispose();
            _glowBlur.Dispose();
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

    /// <summary>One wrapped lyric row of a karaoke card, measured once: its tokens, where
    /// each starts (from the row's left edge) and how wide its glyphs are, and which of its
    /// source line's words they are (null Range = no word mapping, line highlight).</summary>
    private sealed record KaraokeRow(
        string Text, int Line, float X, float Width, (int Start, int Count)? Range,
        string[] Tokens, float[] TokenX, float[] TokenWidth);

    private static KaraokeRow[] LayoutKaraokeRows(CardChrome chrome, IReadOnlyList<KaraokeLine> karaoke, SKPaint paint)
    {
        var ranges = MapKaraokeRows(chrome.Wrapped, karaoke);
        var rows = new KaraokeRow[chrome.Wrapped.Count];
        for (int r = 0; r < rows.Length; r++)
        {
            var (text, line) = chrome.Wrapped[r];
            var (tokens, xs, ws) = MeasureRowTokens(paint, text);
            var width = MeasureTextFallback(paint, text);
            rows[r] = new KaraokeRow(text, line, chrome.RowX[r], width, ranges[r], tokens, xs, ws);
        }
        return rows;
    }

    /// <summary>Splits a wrapped row into its space-separated tokens and measures, for each,
    /// where it starts (from the row's left edge, trailing space included) and how wide its
    /// glyphs are. Shared by the karaoke card and the Lyrics Page video layout.</summary>
    internal static (string[] Tokens, float[] TokenX, float[] TokenWidth) MeasureRowTokens(SKPaint paint, string text)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var xs = new float[tokens.Length];
        var ws = new float[tokens.Length];
        for (int k = 0; k < tokens.Length; k++)
        {
            xs[k] = k > 0 ? MeasureTextFallback(paint, string.Join(' ', tokens.Take(k)) + " ") : 0f;
            ws[k] = MeasureTextFallback(paint, tokens[k]);
        }
        return (tokens, xs, ws);
    }

    /// <summary>
    /// Paints the lyric block at time <paramref name="t"/> (<see cref="KaraokeFlow"/>, advanced
    /// to <paramref name="t"/> by the caller): the focused line at full opacity, the others at
    /// the inactive level; on the focused line unsung words at the page's 0.45 and the sung
    /// part lit through the same soft band the page draws (<see cref="KaraokeBand"/>: 0.3 em
    /// half-width, one front per line, lead-in/trail-out at rests); held notes glow beneath
    /// the text on the page's clock. Rows without word mapping light as a line. Nothing moves:
    /// no line scale, no word float (owner 10-25: the card keeps only the word-sync sweep).
    /// Painted as foreground gradients, never OpacityMask (see
    /// ProgressToSweepForegroundConverter: mask layers clipped/boxed the glow on the page).
    /// </summary>
    private static void DrawKaraokeRows(
        SKCanvas canvas, CardChrome chrome, KaraokeRow[] rows, KaraokeFlow flow,
        SKPaint paint, SKMaskFilter glowBlur, double t)
    {
        float size = chrome.LyricSize;
        float baseline = chrome.FirstBaseline;
        var fg = chrome.Fg;
        for (int r = 0; r < rows.Length; r++, baseline += chrome.LineHeight)
        {
            var row = rows[r];
            var lineOpacity = (float)flow.LineOpacity(row.Line, t);
            var focus = (float)flow.FocusAt(row.Line, t);
            var line = flow.Line(row.Line);

            if (row.Range is not { } range || line?.Words is not { } words)
            {
                // Line-level highlight: an unsynced line is always lit; a synced one lights
                // with its focus.
                paint.Shader = null;
                paint.Color = fg.WithAlpha(KaraokeTokenPainter.ToAlpha(fg, lineOpacity));
                DrawTextFallback(canvas, row.Text, row.X, baseline, paint);
                continue;
            }

            // Unsung text: the page's word base 0.45 on the focused line, the line's own
            // opacity elsewhere (the inactive look draws unsung and sung alike).
            var unsung = lineOpacity * (1f - focus * (1f - (float)KaraokeFlow.UnsungOpacity));

            for (int k = 0; k < row.Tokens.Length && range.Start + k < words.Count; k++)
                KaraokeTokenPainter.DrawToken(canvas, row.Tokens[k], row.X + row.TokenX[k], baseline,
                    row.TokenWidth[k], words[range.Start + k], size, fg, lineOpacity, unsung, paint, glowBlur);
        }
        paint.Color = fg;
    }

    /// <summary>Smallest lyric size the fit shrinks to before it drops rows.</summary>
    private const float MinLyricSize = 28f;

    /// <summary>The Poster layout's old floor: below it the artwork shrinks first.</summary>
    private const float ComfortLyricSize = 34f;

    /// <summary>
    /// Greedily shrinks the lyric font (from a format-dependent base) until the wrapped
    /// block fits <paramref name="maxLyricsH"/>; returns the wrapped lines, chosen size and
    /// line height. Shared by the box-sizing and draw passes so the two always agree.
    /// A block still too tall at <see cref="MinLyricSize"/> (eight long lines on a 1:1 card)
    /// keeps the rows that fit and ends the last one with "…": the rows used to be drawn
    /// whatever their height, over the wordmark and off the bottom of the image.
    /// <c>naturalRows</c> is the row count before that cut, so the Poster layout can give
    /// the artwork's pixels to the lyrics first.
    /// </summary>
    private static (List<(string Text, int Line)> wrapped, float lyricSize, float lineHeight, int naturalRows) FitLyrics(
        LyricCardSpec spec, float contentW, float maxLyricsH, SKTypeface lyricFace, float minSize = MinLyricSize)
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
            // +0.5: the draw pass re-fits into the box the sizing pass made (avail is
            // computed back from it), so float noise must not shrink the font a step.
            if (wrapped.Count * lineHeight <= maxLyricsH + 0.5f || lyricSize <= minSize)
                break;
            lyricSize = Math.Max(minSize, lyricSize - 3f);
        }

        int naturalRows = wrapped.Count;
        int fits = Math.Max(1, (int)Math.Floor((maxLyricsH + 0.5f) / lineHeight));
        if (wrapped.Count > fits)
        {
            wrapped = wrapped.Take(fits).ToList();
            var (lastText, lastLine) = wrapped[^1];
            float Measure(string s) => MeasureTextFallback(lyricPaint, s);
            var marked = lastText.TrimEnd() + "…";
            if (Measure(marked) > contentW)
            {
                marked = "…";
                for (int len = lastText.Length - 1; len > 0; len--)
                {
                    var candidate = lastText[..len].TrimEnd() + "…";
                    if (Measure(candidate) <= contentW) { marked = candidate; break; }
                }
            }
            wrapped[^1] = (marked, lastLine);
        }
        return (wrapped, lyricSize, lineHeight, naturalRows);
    }

    /// <summary>Whether <see cref="GetVibrantColorHex"/> would answer from cache (no decode).</summary>
    public static bool HasVibrantColor(string artworkPath) => ArtworkVibrantColor.IsCached(artworkPath);

    /// <summary>
    /// The vibrant background color this renderer derives for <paramref name="artworkPath"/>,
    /// as a "#RRGGBB" hex string — so the dialog's "Auto" swatch and the lyrics page's
    /// Solid·Auto background match the rendered card exactly. Cached per path
    /// (<see cref="ArtworkVibrantColor"/>, shared with the phone): decoding the artwork is the
    /// expensive part and callers may sit on the UI thread.
    /// </summary>
    public static string GetVibrantColorHex(string? artworkPath) => ArtworkVibrantColor.GetHex(artworkPath);

    /// <summary>The card's vibrant colour for decoded artwork (<see cref="ArtworkVibrantColor.Derive"/>).</summary>
    private static SKColor DeriveVibrantColor(SKBitmap? art) => ArtworkVibrantColor.Derive(art);

    /// <summary>
    /// Fills the whole canvas with the cover art, scaled UniformToFill plus a small
    /// overscan and heavily Gaussian-blurred — the lyrics-page backdrop look.
    /// </summary>
    internal static void DrawBlurredCover(SKCanvas canvas, SKBitmap art, int w, int h)
    {
        // Cover the canvas, then a little extra so the blur's soft edges never reveal black.
        float scale = Math.Max((float)w / art.Width, (float)h / art.Height) * 1.18f;
        float dw = art.Width * scale;
        float dh = art.Height * scale;
        var dest = new SKRect((w - dw) / 2f, (h - dh) / 2f, (w + dw) / 2f, (h + dh) / 2f);
        using var backdropBlur = SKImageFilter.CreateBlur(48f, 48f);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.High,
            ImageFilter = backdropBlur,
        };
        canvas.DrawBitmap(art, dest, paint);
    }

    /// <summary>
    /// Renders a Noctis Wrap recap card, Last.fm-report style: brand + period, the most-played
    /// album as a big cover with its name, minutes listened, top artists and songs, a stats
    /// strip and the wordmark. The 9:16 story adds a strip of the next four album covers.
    /// The background is a gradient of the top album's cover colour, as before.
    /// </summary>
    public static byte[] RenderWrapCard(WrapCardSpec spec)
    {
        int w = CanvasWidth;
        bool story = spec.Format == ShareCardFormat.Story;
        int h = story ? 1920 : 1080;

        var heroPath = spec.ArtworkPath ?? (spec.AlbumCoverPaths.Count > 0 ? spec.AlbumCoverPaths[0] : null);
        using var art = LoadArtwork(heroPath);
        var bg = DeriveBackground(art);
        bool darkText = UseDarkText(bg.Red, bg.Green, bg.Blue);
        var fg = darkText ? new SKColor(0x12, 0x12, 0x12) : SKColors.White;
        var fgSubtle = fg.WithAlpha(170);

        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        var canvas = surface.Canvas;

        // Vertical gradient: darker tint of the artwork color at the top.
        bg.ToHsl(out var hue, out var sat, out var light);
        var bgTop = SKColor.FromHsl(hue, sat, Math.Max(6f, light * 0.45f));
        using (var bgShader = SKShader.CreateLinearGradient(
                   new SKPoint(0, 0), new SKPoint(0, h),
                   new[] { bgTop, bg }, null, SKShaderTileMode.Clamp))
        using (var bgPaint = new SKPaint { Shader = bgShader })
        {
            canvas.DrawRect(0, 0, w, h, bgPaint);
        }

        using var boldFace = ResolveTypeface(bold: true);
        using var regularFace = ResolveTypeface(bold: false);

        // The hero: the top album, or (no album could be placed) the top artist.
        var hasAlbum = !string.IsNullOrWhiteSpace(spec.TopAlbum);
        var heroLabel = hasAlbum
            ? spec.TopAlbumPlays > 0 ? $"TOP ALBUM  ·  {WrapPlays(spec.TopAlbumPlays)}" : "TOP ALBUM"
            : "TOP ARTIST";
        var heroName = SanitizeForRender(hasAlbum ? spec.TopAlbum : spec.TopArtists.FirstOrDefault());
        var heroSub = SanitizeForRender(hasAlbum ? spec.TopAlbumArtist : null);

        if (story)
            DrawWrapStory(canvas, spec, art, w, h, fg, fgSubtle, boldFace, regularFace, heroLabel, heroName, heroSub);
        else
            DrawWrapSquare(canvas, spec, art, w, h, fg, fgSubtle, boldFace, regularFace, heroLabel, heroName, heroSub);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>English, like the rest of the card's labels.</summary>
    private static string WrapPlays(int plays) =>
        plays == 1 ? "1 PLAY" : plays.ToString("N0", CultureInfo.InvariantCulture) + " PLAYS";

    /// <summary>1:1 — header, cover + album text + minutes, two top-5 columns, stats strip, wordmark.</summary>
    private static void DrawWrapSquare(SKCanvas canvas, WrapCardSpec spec, SKBitmap? art, int w, int h,
        SKColor fg, SKColor fgSubtle, SKTypeface boldFace, SKTypeface regularFace,
        string heroLabel, string heroName, string heroSub)
    {
        const float pad = 80f;
        float contentW = w - pad * 2;

        float y = 72f;
        DrawWrapHeader(canvas, spec.PeriodLabel, pad, y, 26f, 76f, fg, fgSubtle, boldFace, centered: false, w);
        y += 26f + 12f + 76f + 36f;

        // ── Hero: cover on the left, album and minutes on the right ──
        const float cover = 300f;
        var coverRect = new SKRect(pad, y, pad + cover, y + cover);
        DrawArtworkTile(canvas, art, coverRect, 28f, fg);

        float tx = pad + cover + 40f, tw = w - pad - tx;
        using (var label = TextPaint(boldFace, 22f, fgSubtle))
            DrawTextFallback(canvas, Ellipsize(heroLabel, tw, s => MeasureTextFallback(label, s)), tx, y + 22f, label);

        float nameBase = y + 22f + 18f + 44f;
        using (var name = TextPaint(boldFace, 44f, fg))
        {
            var lines = WrapText(heroName, tw, s => MeasureTextFallback(name, s));
            if (lines.Count > 2)
                lines = new List<string> { lines[0], Ellipsize(string.Join(" ", lines.Skip(1)), tw, s => MeasureTextFallback(name, s)) };
            for (int i = 0; i < lines.Count; i++)
            {
                DrawTextFallback(canvas, lines[i], tx, nameBase, name);
                if (i < lines.Count - 1) nameBase += 52f;
            }
        }
        if (heroSub.Length > 0)
        {
            using var sub = TextPaint(regularFace, 28f, fgSubtle);
            DrawTextFallback(canvas, Ellipsize(heroSub, tw, s => MeasureTextFallback(sub, s)), tx, nameBase + 42f, sub);
        }

        // Minutes listened, sitting on the cover's bottom edge.
        float valueBase = coverRect.Bottom - 4f;
        using (var value = TextPaint(boldFace, 64f, fg))
            canvas.DrawText(spec.TotalMinutes.ToString("N0", CultureInfo.InvariantCulture), tx, valueBase, value);
        using (var minutesLabel = TextPaint(regularFace, 20f, fgSubtle))
            canvas.DrawText("MINUTES LISTENED", tx, valueBase - 64f - 10f, minutesLabel);
        y = coverRect.Bottom + 48f;

        // ── Top artists / top songs ──
        float colW = (contentW - 48f) / 2f;
        DrawWrapColumn(canvas, boldFace, "TOP ARTISTS", spec.TopArtists, pad, y, colW, 24f, 29f, 44f, fg, fgSubtle);
        DrawWrapColumn(canvas, boldFace, "TOP SONGS", spec.TopTracks, pad + colW + 48f, y, colW, 24f, 29f, 44f, fg, fgSubtle);
        y += 24f + 22f + 29f + 4 * 44f + 34f;

        // ── Stats strip ──
        float cellW = (contentW - 2 * 32f) / 3f;
        DrawWrapStat(canvas, boldFace, regularFace, "TRACKS PLAYED", spec.TotalPlays.ToString("N0", CultureInfo.InvariantCulture),
            pad, y, cellW, 20f, 40f, fg, fgSubtle);
        DrawWrapStat(canvas, boldFace, regularFace, "TOP GENRE", spec.TopGenre,
            pad + cellW + 32f, y, cellW, 20f, 40f, fg, fgSubtle);
        DrawWrapStat(canvas, boldFace, regularFace, "LOSSLESS", $"{spec.LosslessPercent:0}%",
            pad + 2 * (cellW + 32f), y, cellW, 20f, 40f, fg, fgSubtle);

        DrawWordmark(canvas, boldFace, fg, pad, h - 68f - FooterHeight);
    }

    /// <summary>9:16 — centred header and big cover, album text, a strip of the next four
    /// covers, a stats row, the two top-5 columns, wordmark.</summary>
    private static void DrawWrapStory(SKCanvas canvas, WrapCardSpec spec, SKBitmap? art, int w, int h,
        SKColor fg, SKColor fgSubtle, SKTypeface boldFace, SKTypeface regularFace,
        string heroLabel, string heroName, string heroSub)
    {
        const float pad = 96f;
        float contentW = w - pad * 2;

        float y = 120f;
        DrawWrapHeader(canvas, spec.PeriodLabel, pad, y, 30f, 96f, fg, fgSubtle, boldFace, centered: true, w);
        y += 30f + 12f + 96f + 50f;

        const float cover = 520f;
        var coverRect = new SKRect((w - cover) / 2f, y, (w + cover) / 2f, y + cover);
        DrawArtworkTile(canvas, art, coverRect, 40f, fg);
        y = coverRect.Bottom + 50f;

        using (var label = TextPaint(boldFace, 24f, fgSubtle))
            DrawCentered(canvas, Ellipsize(heroLabel, contentW, s => MeasureTextFallback(label, s)), w, y, label);
        y += 14f + 50f;
        using (var name = TextPaint(boldFace, 50f, fg))
        {
            var lines = WrapText(heroName, contentW, s => MeasureTextFallback(name, s));
            if (lines.Count > 2)
                lines = new List<string> { lines[0], Ellipsize(string.Join(" ", lines.Skip(1)), contentW, s => MeasureTextFallback(name, s)) };
            for (int i = 0; i < lines.Count; i++)
            {
                DrawCentered(canvas, lines[i], w, y, name);
                if (i < lines.Count - 1) y += 58f;
            }
        }
        if (heroSub.Length > 0)
        {
            y += 44f;
            using var sub = TextPaint(regularFace, 30f, fgSubtle);
            DrawCentered(canvas, Ellipsize(heroSub, contentW, s => MeasureTextFallback(sub, s)), w, y, sub);
        }
        y += 50f;

        // ── The next four albums as a cover strip ──
        var more = spec.AlbumCoverPaths.Skip(1).Take(4).ToList();
        if (more.Count > 0)
        {
            const float gap = 24f;
            float tile = (contentW - 3 * gap) / 4f;
            float x = pad + (contentW - (more.Count * tile + (more.Count - 1) * gap)) / 2f;
            foreach (var path in more)
            {
                using var thumb = LoadArtwork(path);
                DrawArtworkTile(canvas, thumb, new SKRect(x, y, x + tile, y + tile), 20f, fg);
                x += tile + gap;
            }
            y += tile + 46f;
        }

        // ── Stats row ──
        float cellW = (contentW - 2 * 32f) / 3f;
        DrawWrapStat(canvas, boldFace, regularFace, "MINUTES", spec.TotalMinutes.ToString("N0", CultureInfo.InvariantCulture),
            pad, y, cellW, 22f, 52f, fg, fgSubtle);
        DrawWrapStat(canvas, boldFace, regularFace, "TRACKS PLAYED", spec.TotalPlays.ToString("N0", CultureInfo.InvariantCulture),
            pad + cellW + 32f, y, cellW, 22f, 52f, fg, fgSubtle);
        DrawWrapStat(canvas, boldFace, regularFace, "TOP GENRE", spec.TopGenre,
            pad + 2 * (cellW + 32f), y, cellW, 22f, 52f, fg, fgSubtle);
        y += 22f + 8f + 52f + 48f;

        // ── Top artists / top songs ──
        float colW = (contentW - 48f) / 2f;
        DrawWrapColumn(canvas, boldFace, "TOP ARTISTS", spec.TopArtists, pad, y, colW, 26f, 32f, 50f, fg, fgSubtle);
        DrawWrapColumn(canvas, boldFace, "TOP SONGS", spec.TopTracks, pad + colW + 48f, y, colW, 26f, 32f, 50f, fg, fgSubtle);

        using (var wordmarkPaint = TextPaint(boldFace, 38, fg))
        {
            float wordmarkW = FooterHeight + 14 + MeasureTextFallback(wordmarkPaint, "Noctis");
            DrawWordmark(canvas, boldFace, fg, (w - wordmarkW) / 2f, h - 90f - FooterHeight);
        }
    }

    private static void DrawWrapHeader(SKCanvas canvas, string period, float x, float y, float brandSize, float periodSize,
        SKColor fg, SKColor fgSubtle, SKTypeface boldFace, bool centered, int w)
    {
        using var brand = TextPaint(boldFace, brandSize, fgSubtle);
        using var periodPaint = TextPaint(boldFace, periodSize, fg);
        const string brandText = "N O C T I S   W R A P";
        var periodText = Ellipsize(SanitizeForRender(period), w - 2 * x, s => MeasureTextFallback(periodPaint, s));
        if (centered)
        {
            DrawCentered(canvas, brandText, w, y + brandSize, brand);
            DrawCentered(canvas, periodText, w, y + brandSize + 12f + periodSize, periodPaint);
        }
        else
        {
            canvas.DrawText(brandText, x, y + brandSize, brand);
            DrawTextFallback(canvas, periodText, x, y + brandSize + 12f + periodSize, periodPaint);
        }
    }

    private static void DrawCentered(SKCanvas canvas, string text, int w, float baseline, SKPaint paint) =>
        DrawTextFallback(canvas, text, (w - MeasureTextFallback(paint, text)) / 2f, baseline, paint);

    private static void DrawWrapColumn(SKCanvas canvas, SKTypeface boldFace,
        string header, IReadOnlyList<string> entries, float x, float y, float width,
        float headerSize, float entrySize, float entryLine, SKColor fg, SKColor fgSubtle)
    {
        using var headerPaint = TextPaint(boldFace, headerSize, fgSubtle);
        canvas.DrawText(header, x, y + headerSize, headerPaint);

        using var rankPaint = TextPaint(boldFace, entrySize, fgSubtle);
        using var namePaint = TextPaint(boldFace, entrySize, fg);
        float rankW = entrySize * 1.3f;
        float baseline = y + headerSize + 22f + entrySize;
        for (int i = 0; i < Math.Min(5, entries.Count); i++)
        {
            canvas.DrawText($"{i + 1}", x, baseline, rankPaint);
            var name = Ellipsize(SanitizeForRender(entries[i]), width - rankW, s => MeasureTextFallback(namePaint, s));
            DrawTextFallback(canvas, name, x + rankW, baseline, namePaint);
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
        // A long genre ("Hip-Hop/Rap") shrinks to fit before it is cut.
        var text = SanitizeForRender(value);
        while (valuePaint.TextSize > valueSize * 0.6f && MeasureTextFallback(valuePaint, text) > width)
            valuePaint.TextSize -= 2f;
        var fitted = Ellipsize(text, width, s => MeasureTextFallback(valuePaint, s));
        DrawTextFallback(canvas, fitted, x, y + labelSize + 8f + valueSize, valuePaint);
    }

    internal static SKPaint TextPaint(SKTypeface face, float size, SKColor color) => new()
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
    internal static void DrawExplicitBadge(SKCanvas canvas, SKTypeface face, SKColor fg, SKColor cardColor,
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

    // Both volatile: the fast path reads them outside the lock while the writer sets
    // _logo then _logoLoadAttempted inside it. On a weak memory model (this ships arm64
    // macOS) a concurrent render — RefreshPreview starts renders concurrently — could
    // observe _logoLoadAttempted == true with _logo still null and silently fall back to
    // the quarter-note glyph wordmark.
    private static volatile SKBitmap? _logo;
    private static volatile bool _logoLoadAttempted;
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
                var uri = new Uri("avares://Noctis.UI/Assets/Icons/Noctis%20Logo%20Clean.png");
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
    // Memoized results, keyed by (text, primary typeface).
    //
    // The karaoke animator calls this for every row on every rendered frame, and each
    // call allocates a StringBuilder + List and makes one native ContainsGlyph P/Invoke
    // per character. With ~8 wrapped rows that was ~40 calls and 1–2k native probes per
    // frame — for text that does not change between frames.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (string Text, SKTypeface Face), List<(string Run, SKTypeface Face)>> _runCache = new();

    private const int RunCacheMax = 512;

    public static List<(string Run, SKTypeface Face)> SplitFallbackRuns(string text, SKTypeface primary)
    {
        if (string.IsNullOrEmpty(text))
            return new List<(string Run, SKTypeface Face)>();

        if (_runCache.TryGetValue((text, primary), out var cached))
            return cached;

        var computed = SplitFallbackRunsCore(text, primary);

        // Cheap bound: the card text set is small and bursty, so a full clear on
        // overflow is fine and avoids tracking recency.
        if (_runCache.Count >= RunCacheMax) _runCache.Clear();
        _runCache.TryAdd((text, primary), computed);
        return computed;
    }

    private static List<(string Run, SKTypeface Face)> SplitFallbackRunsCore(string text, SKTypeface primary)
    {
        var runs = new List<(string Run, SKTypeface Face)>();
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
    internal static float MeasureTextFallback(SKPaint paint, string text)
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
    internal static void DrawTextFallback(SKCanvas canvas, string text, float x, float y, SKPaint paint)
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

            // Hard-break an oversized word character by character — by text element, so an
            // emoji's surrogate pair or a Thai vowel/tone mark never ends up split across rows.
            var piece = "";
            var elements = StringInfo.GetTextElementEnumerator(word);
            while (elements.MoveNext())
            {
                var ch = elements.GetTextElement();
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
        // Cut on text-element boundaries only: half a surrogate pair draws as a tofu box.
        var starts = StringInfo.ParseCombiningCharacters(text);
        for (int e = starts.Length - 1; e > 0; e--)
        {
            var candidate = text[..starts[e]].TrimEnd() + "…";
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

    internal static SKTypeface ResolveTypeface(SKFontStyleWeight weight)
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

    /// <summary>The cover, subsampled and downscaled to at most 1024 px (<see cref="ArtworkVibrantColor.LoadArtwork"/>).</summary>
    private static SKBitmap? LoadArtwork(string? path) => ArtworkVibrantColor.LoadArtwork(path);

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
