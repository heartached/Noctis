using Noctis.Models;
using SkiaSharp;

namespace Noctis.Services.LyricsPage;

/// <summary>
/// Paints one frame of the Lyrics Page video: the background, the header (artwork tile, title,
/// E badge, artist — recorded once), then the lyric list as the lyrics page shows it at that
/// moment — scrolled and cascaded by <see cref="LyricsPageMotion"/>, each line at its distance
/// opacity and blur, the sung words lit through the karaoke band, adlib rows under their line,
/// the interlude dots filling — with the list's top and bottom edges faded out. The safe-zone
/// overlay (<see cref="ShowSafeZones"/>) is for the preview only.
///
/// Drawn in the 1080-wide canvas space under <c>scale</c>, so the half-size preview and the
/// export are the same picture. Feed times in playback order (the motion is stateful). The
/// background may block for a clip frame (<see cref="ClipBackground"/> in blocking mode): call
/// <see cref="Draw"/>/<see cref="RenderFrame"/> off the UI thread, from one thread at a time,
/// and dispose on that thread once the last draw returned.
/// </summary>
public sealed class LyricsPageAnimator : IShareFrameAnimator
{
    /// <summary>Gap between the artwork tile and the title / artist text, in canvas px.</summary>
    private const float HeaderTextGap = 28f;

    /// <summary>The page's dots row at a 46 px font: 12 px dots 8 px apart, 10 px in from the
    /// left and 8 px down (LyricsView.axaml's margin 10,8,0,8).</summary>
    private const float DotSize = 12f, DotPitch = 20f, DotsLeft = 10f, DotsTop = 8f;

    /// <summary>The artwork tile's outline width (ShareCardRenderer.DrawArtworkTile, 1.5 px).</summary>
    private const float TileOutlineInset = 1.5f;

    private static readonly SKColor BadgeLetter = new(0x1B, 0x1B, 0x1B);
    private static readonly SKColor SafeZoneFill = new(0xFF, 0xFF, 0xFF, 0x22);

    private readonly LyricsPageSpec _spec;
    private readonly float _scale;
    private readonly SKSurface _surface;
    private readonly IPageBackground _background;
    private readonly SKPicture _header;
    private readonly SKBitmap? _loadedArt;
    private readonly SKTypeface _boldFace;
    private readonly List<LyricLine> _lines;
    private readonly IReadOnlyList<PageLineLayout> _layout;
    private readonly SKPaint _mainPaint, _backgroundPaint, _dotPaint;
    private readonly SKMaskFilter _mainGlow, _backgroundGlow;
    private readonly SKShader _edgeFade;

    /// <param name="art">The decoded cover when the caller keeps one (not disposed here); null
    /// decodes <see cref="LyricsPageSpec.ArtworkPath"/>.</param>
    /// <param name="blockingBackground">Wait for each clip-background frame (export) instead of
    /// showing the newest decoded one (preview).</param>
    public LyricsPageAnimator(LyricsPageSpec spec, float scale, SKBitmap? art = null,
        bool blockingBackground = true, int backgroundFps = LyricsPageFrames.Fps)
    {
        _spec = spec;
        _scale = scale;
        Geometry = LyricsPageLayout.Measure(spec.Format, spec.SafeZones);
        PixelWidth = (int)Math.Round(Geometry.Width * scale);
        PixelHeight = (int)Math.Round(Geometry.Height * scale);

        // BGRA premul to match Avalonia's WriteableBitmap framebuffer, as KaraokeCardAnimator.
        _surface = SKSurface.Create(new SKImageInfo(PixelWidth, PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));

        if (art == null) _loadedArt = ShareCardRenderer.LoadCardArtwork(spec.ArtworkPath);
        var cover = art ?? _loadedArt;

        _boldFace = ShareCardRenderer.ResolveTypeface(SKFontStyleWeight.Bold);
        _lines = LyricsPageEngine.BuildLines(spec.Lines);
        _layout = LyricsPageLineLayout.Build(_lines, Geometry, _boldFace);
        Motion = new LyricsPageMotion(_lines, _layout.Select(l => l.Top).ToList(), _layout.Select(l => l.Height).ToList(),
            Geometry.AnchorY, Geometry.Viewport.Height, Geometry.Scale, Geometry.Viewport.Top, scale);

        _background = PageBackgrounds.Create(spec, PixelWidth, PixelHeight, backgroundFps, blockingBackground, cover);
        _header = RecordHeader(spec, Geometry, cover, _boldFace);

        _mainPaint = ShareCardRenderer.TextPaint(_boldFace, Geometry.LyricSize, SKColors.White);
        _backgroundPaint = ShareCardRenderer.TextPaint(_boldFace, Geometry.BackgroundVocalSize, SKColors.White);
        _dotPaint = new SKPaint { IsAntialias = true, Color = SKColors.White };
        _mainGlow = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Geometry.LyricSize * ShareCardRenderer.KaraokeGlowRadiusEm * 0.5f);
        _backgroundGlow = SKMaskFilter.CreateBlur(SKBlurStyle.Normal,
            Geometry.BackgroundVocalSize * ShareCardRenderer.KaraokeGlowRadiusEm * 0.5f);

        _edgeFade = EdgeFadeShader(Geometry.Viewport, Geometry.EdgeFade);
    }

    /// <summary>
    /// The list's top and bottom fade: over <paramref name="fadeHeight"/> at each edge the lyrics
    /// ease in from transparent on a smoothstep (eased stops, not a linear ramp, so a line passing
    /// the edge dissolves instead of reading as sliced).
    /// </summary>
    private static SKShader EdgeFadeShader(SKRect viewport, float fadeHeight)
    {
        float fade = Math.Clamp(fadeHeight / Math.Max(1f, viewport.Height), 0f, 0.5f);
        float[] steps = { 0f, 0.25f, 0.5f, 0.75f, 1f };
        var colors = new List<SKColor>();
        var positions = new List<float>();
        foreach (var x in steps)
        {
            colors.Add(SKColors.Black.WithAlpha((byte)Math.Round(255 * x * x * (3 - 2 * x))));
            positions.Add(x * fade);
        }
        foreach (var x in steps.Reverse())
        {
            colors.Add(SKColors.Black.WithAlpha((byte)Math.Round(255 * x * x * (3 - 2 * x))));
            positions.Add(1f - x * fade);
        }
        return SKShader.CreateLinearGradient(new SKPoint(0, viewport.Top), new SKPoint(0, viewport.Bottom),
            colors.ToArray(), positions.ToArray(), SKShaderTileMode.Clamp);
    }

    /// <summary>Paints the social apps' covered areas over the frame (preview only, never exported).</summary>
    public bool ShowSafeZones { get; set; }

    /// <summary>The readability strength (0-100) over the background from the next frame on, without
    /// rebuilding the background (a clip's ffmpeg keeps running). Any thread.</summary>
    public int BackgroundStrength { set => _background.Strength = value; }

    public int PixelWidth { get; }
    public int PixelHeight { get; }

    internal LyricsPageMotion Motion { get; }
    internal LyricsPageGeometry Geometry { get; }

    /// <summary>Renders the frame at track time <paramref name="tSeconds"/> into <paramref name="dest"/>
    /// (BGRA8888 premul, <see cref="PixelWidth"/> × <see cref="PixelHeight"/>).</summary>
    public void RenderFrame(double tSeconds, IntPtr dest, int destRowBytes)
    {
        Draw(tSeconds);
        _surface.ReadPixels(new SKImageInfo(PixelWidth, PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul),
            dest, destRowBytes, 0, 0);
    }

    /// <summary>Draws the frame at track time <paramref name="tSeconds"/> (word lookahead included)
    /// onto the animator's surface.</summary>
    internal void Draw(double tSeconds)
    {
        var canvas = _surface.Canvas;
        canvas.Clear(SKColors.Black);
        _background.Draw(canvas, Math.Max(0, tSeconds - LyricsPageFrames.WordLookaheadSeconds - _spec.ClipStartSeconds));

        canvas.Save();
        canvas.Scale(_scale);
        canvas.DrawPicture(_header);

        Motion.Advance(tSeconds);
        DrawLyrics(canvas, tSeconds);

        if (ShowSafeZones)
        {
            using var zonePaint = new SKPaint { Color = SafeZoneFill };
            foreach (var zone in SocialSafeZones.Zones(_spec.Format, true))
                canvas.DrawRect(zone, zonePaint);
        }
        canvas.Restore();
        canvas.Flush();
    }

    /// <summary>The surface's current frame.</summary>
    internal SKImage Snapshot() => _surface.Snapshot();

    private void DrawLyrics(SKCanvas canvas, double t)
    {
        var g = Geometry;
        var vp = g.Viewport;
        double offset = Motion.ScrollOffset(t);
        var lags = Motion.LineLags(t);
        float margin = g.LyricSize;   // blur and glow reach past a line's band
        // Clipped top and bottom only: blur and glow run on into the side margins (a soft halo is
        // not text in a safe zone), where a side clip would cut them off hard.
        var band = new SKRect(0, vp.Top, g.Width, vp.Bottom);

        canvas.SaveLayer(band, null);
        canvas.ClipRect(band);
        for (int i = 0; i < _lines.Count; i++)
        {
            var opacity = Motion.Opacity(i, t);
            if (opacity <= 0.004) continue;
            var layout = _layout[i];
            // At rest the target line sits on a whole device pixel: Motion rounds its target
            // offsets, so the glide lands there with no extra snap after it.
            float y = (float)(vp.Top + layout.Top - offset + lags[i]);
            if (y + layout.Height + margin < vp.Top || y - margin > vp.Bottom) continue;

            var blur = Motion.BlurRadius(i, t);
            using var filter = blur > 0 ? SKImageFilter.CreateBlur((float)blur / 2, (float)blur / 2) : null;
            using var layerPaint = new SKPaint
            {
                Color = SKColors.White.WithAlpha(KaraokeTokenPainter.ToAlpha(SKColors.White, (float)opacity)),
                ImageFilter = filter,
            };
            canvas.SaveLayer(new SKRect(band.Left, y - margin, band.Right, y + layout.Height + margin), layerPaint);

            var line = _lines[i];
            if (layout.IsDots)
            {
                DrawDots(canvas, i, y, line.InterludeProgress, t);
            }
            else
            {
                float dim = 1f - (float)Motion.SweepFocus(i, t) * (1f - (float)KaraokeFlow.UnsungOpacity);
                DrawRows(canvas, layout.Rows, line.Words, y, g.RowHeight, g.LyricSize, dim, _mainPaint, _mainGlow);
                DrawRows(canvas, layout.BackgroundRows, line.BackgroundWords, y + layout.BackgroundTop,
                    g.BackgroundVocalSize * 1.2f, g.BackgroundVocalSize, dim, _backgroundPaint, _backgroundGlow);
            }
            canvas.Restore();
        }

        // Full width, so halos in the side margins fade with their lines too.
        using (var fadePaint = new SKPaint { Shader = _edgeFade, BlendMode = SKBlendMode.DstIn })
            canvas.DrawRect(band, fadePaint);
        canvas.Restore();
    }

    /// <summary>Rows from <paramref name="top"/>, <paramref name="rowHeight"/> apart: word-mapped rows
    /// token by token through the karaoke band (lit 1, unsung <paramref name="dim"/>), the rest lit whole.</summary>
    private static void DrawRows(SKCanvas canvas, PageRow[] rows, IReadOnlyList<WordTiming>? words, float top,
        float rowHeight, float size, float dim, SKPaint paint, SKMaskFilter glow)
    {
        for (int r = 0; r < rows.Length; r++)
        {
            var row = rows[r];
            float baseline = top + r * rowHeight + 0.9f * size;
            if (row.Range is not { } range || words == null)
            {
                paint.Shader = null;
                paint.Color = SKColors.White;
                ShareCardRenderer.DrawTextFallback(canvas, row.Text, row.X, baseline, paint);
                continue;
            }
            for (int k = 0; k < row.Tokens.Length && range.Start + k < words.Count; k++)
                KaraokeTokenPainter.DrawToken(canvas, row.Tokens[k], row.X + row.TokenX[k], baseline, row.TokenWidth[k],
                    words[range.Start + k], size, SKColors.White, 1f, dim, paint, glow);
        }
        paint.Color = SKColors.White;
    }

    /// <summary>The three dots filling with the gap (<see cref="InterludeDots.DotLevel"/>), breathing
    /// about their left-centre (<see cref="LyricsPageMotion.DotsScale"/>).</summary>
    private void DrawDots(SKCanvas canvas, int line, float top, double progress, double t)
    {
        var g = Geometry;
        float s = g.Scale;
        float pivotX = g.Viewport.Left + DotsLeft * s;
        float centerY = top + (DotsTop + DotSize / 2f) * s;
        float breath = (float)Motion.DotsScale(line, t);
        canvas.Save();
        canvas.Scale(breath, breath, pivotX, centerY);
        for (int k = 0; k < InterludeDots.Count; k++)
        {
            _dotPaint.Color = SKColors.White.WithAlpha(
                KaraokeTokenPainter.ToAlpha(SKColors.White, (float)InterludeDots.DotLevel(progress, k)));
            canvas.DrawCircle(pivotX + (DotSize / 2f + k * DotPitch) * s, centerY, DotSize / 2f * s, _dotPaint);
        }
        canvas.Restore();
    }

    /// <summary>The header in canvas space: artwork tile, title (ellipsized, E badge when explicit)
    /// and artist at 0.7. No player controls and no service branding.</summary>
    private static SKPicture RecordHeader(LyricsPageSpec spec, LyricsPageGeometry g, SKBitmap? art, SKTypeface boldFace)
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0, 0, g.Width, g.Height));
        var header = g.Header;
        var artRect = new SKRect(header.Left, header.Top, header.Left + g.ArtSize, header.Top + g.ArtSize);
        // The tile's outline is stroked on its edge, and at the half-size preview it is thinner
        // than a pixel, so Skia draws it as a 1-device-px hairline. Inset by the full stroke width
        // so either way it stays inside the header and off the safe zones the header borders.
        var tileRect = SKRect.Inflate(artRect, -TileOutlineInset, -TileOutlineInset);
        ShareCardRenderer.DrawArtworkTile(canvas, art, tileRect, 16 * g.Scale, SKColors.White);

        using var regularFace = ShareCardRenderer.ResolveTypeface(SKFontStyleWeight.Normal);
        using var titlePaint = ShareCardRenderer.TextPaint(boldFace, g.TitleSize, SKColors.White);
        using var artistPaint = ShareCardRenderer.TextPaint(regularFace, g.ArtistSize,
            SKColors.White.WithAlpha(KaraokeTokenPainter.ToAlpha(SKColors.White, 0.7f)));
        float textX = artRect.Right + HeaderTextGap;
        float textMaxW = Math.Max(0, header.Right - textX);
        float titleBaseline = header.MidY - 6;
        // The card's badge spacing (DrawPanelContent): 0.72 em square, 14 px from the title.
        float badgeReserve = spec.IsExplicit ? g.TitleSize * 0.72f + 14 : 0;
        var title = ShareCardRenderer.Ellipsize(ShareCardRenderer.SanitizeForRender(spec.Title), textMaxW - badgeReserve,
            s => ShareCardRenderer.MeasureTextFallback(titlePaint, s));
        ShareCardRenderer.DrawTextFallback(canvas, title, textX, titleBaseline, titlePaint);
        if (spec.IsExplicit)
        {
            float badgeX = textX + ShareCardRenderer.MeasureTextFallback(titlePaint, title) + 14;
            ShareCardRenderer.DrawExplicitBadge(canvas, regularFace, SKColors.White, BadgeLetter,
                badgeX, titleBaseline - g.TitleSize * 0.34f, g.TitleSize);
        }
        var artist = ShareCardRenderer.Ellipsize(ShareCardRenderer.SanitizeForRender(spec.Artist), textMaxW,
            s => ShareCardRenderer.MeasureTextFallback(artistPaint, s));
        ShareCardRenderer.DrawTextFallback(canvas, artist, textX, titleBaseline + g.ArtistSize * 1.25f, artistPaint);
        return recorder.EndRecording();
    }

    public void Dispose()
    {
        _background.Dispose();
        _header.Dispose();
        _edgeFade.Dispose();
        _mainGlow.Dispose();
        _backgroundGlow.Dispose();
        _mainPaint.Dispose();
        _backgroundPaint.Dispose();
        _dotPaint.Dispose();
        _boldFace.Dispose();
        _surface.Dispose();
        _loadedArt?.Dispose();
    }
}
