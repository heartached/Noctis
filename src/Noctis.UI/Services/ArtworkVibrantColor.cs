using System.Collections.Concurrent;
using Noctis.Helpers;
using SkiaSharp;

namespace Noctis.Services;

/// <summary>
/// The cover's vibrant colour: the hue of its dominant colourful region (not its edge, not a
/// flat average). One derivation for every head: the desktop's share card and lyrics page
/// backgrounds, the desktop's "Accent follows album art" and the phone's Settings › Accent ›
/// Match Artwork. SkiaSharp only, so it is safe on a worker thread; cached per path.
/// </summary>
public static class ArtworkVibrantColor
{
    /// <summary>The colour without usable artwork (the app's dark navy).</summary>
    public const string FallbackHex = "#1A1A2E";

    private static readonly SKColor Fallback = new(0x1A, 0x1A, 0x2E);

    /// <summary>Largest source dimension we will decode at full size (see <see cref="LoadArtwork"/>).</summary>
    private const int MaxDecodeDimension = 8192;

    private const int CacheMax = 300;

    /// <summary>One cover's result: the card colour, and the colour an accent may take from it
    /// (null without artwork; the plain average when no hue won, which the accent's grey check
    /// then turns down).</summary>
    private readonly record struct Entry(string Hex, string? AccentSourceHex);

    private static readonly ConcurrentDictionary<string, Entry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <see cref="GetHex"/> / <see cref="GetAccentHex"/> would answer from cache (no decode).</summary>
    public static bool IsCached(string artworkPath) => _cache.ContainsKey(artworkPath);

    /// <summary>
    /// The vibrant colour of <paramref name="artworkPath"/> as "#RRGGBB", lightness clamped for a
    /// background (<see cref="FallbackHex"/> without artwork). Decodes on a miss: call it off the
    /// UI thread unless <see cref="IsCached"/>.
    /// </summary>
    public static string GetHex(string? artworkPath) => Get(artworkPath)?.Hex ?? FallbackHex;

    /// <summary>
    /// The cover's colour as an accent (<see cref="ArtworkAccent.TameForAccent"/>: real chroma at
    /// mid lightness), or null when there is no readable artwork or the cover is grey (no hue
    /// region won), so the caller keeps the user's own accent. Decodes on a miss.
    /// </summary>
    public static string? GetAccentHex(string? artworkPath) =>
        ArtworkAccent.TameForAccent(GetAccentSourceHex(artworkPath));

    /// <summary>
    /// The colour <see cref="GetAccentHex"/> tames, untamed: the vibrant colour when a hue region
    /// won, the plain average of a grey cover (which <see cref="ArtworkAccent.TameForAccent"/>
    /// turns down), null without readable artwork. The phone hands this to its theme host, which
    /// tames it the same way. Decodes on a miss.
    /// </summary>
    public static string? GetAccentSourceHex(string? artworkPath) => Get(artworkPath)?.AccentSourceHex;

    private static Entry? Get(string? artworkPath)
    {
        if (string.IsNullOrEmpty(artworkPath))
            return null;
        if (_cache.TryGetValue(artworkPath, out var cached))
            return cached;

        using var art = LoadArtwork(artworkPath);
        var (card, accentSource) = Analyse(art);
        var entry = new Entry(Hex(card), accentSource is { } a ? Hex(a) : null);

        if (_cache.Count >= CacheMax)
            _cache.Clear();
        _cache.TryAdd(artworkPath, entry);
        return entry;
    }

    private static string Hex(SKColor c) => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

    /// <summary>
    /// The cover subsampled on the way in and downscaled to at most 1024 px (what the share card
    /// draws); null when missing or unreadable.
    /// </summary>
    public static SKBitmap? LoadArtwork(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;
        try
        {
            // Read the header first and subsample absurd images on the way in. A plain
            // SKBitmap.Decode allocates the file's *native* resolution before the resize
            // below ever runs, so a 20000×20000 cover next to a track — or embedded in a
            // downloaded one — meant a ~1.6 GB allocation and an OOM kill, on a path that
            // runs at every track change.
            using var raw = SkiaArtworkDecoder.DecodeSubsampled(path, MaxDecodeDimension);
            if (raw == null) return null;

            // Downscale once so the color passes and card drawing stay cheap. 1024px keeps
            // the artwork sharp on the 2× supersampled export (poster art draws at ~920px).
            return SkiaArtworkDecoder.Downscale(raw, new SKImageInfo(1024, 1024));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A vibrant background color faithful to the artwork: pixels vote for one of 24 hue
    /// buckets weighted by chroma², the winning bucket (plus its neighbours) is averaged,
    /// and lightness is clamped to a legible background range. Unlike a global weighted
    /// average — which blends opposing hues (blue cover + skin tones) into brown mud —
    /// this keeps the hue of the cover's dominant colorful region.
    /// </summary>
    public static SKColor Derive(SKBitmap? art) => Analyse(art).Card;

    /// <summary><see cref="Derive"/>'s colour, and the colour an accent may take: the same when a
    /// hue won; the plain (unboosted) average when none did, so a grey cover stays grey for the
    /// accent's grey check instead of the background's saturation floor tinting it red (hue 0);
    /// null without artwork.</summary>
    private static (SKColor Card, SKColor? AccentSource) Analyse(SKBitmap? art)
    {
        if (art == null)
            return (Fallback, null);

        const int Buckets = 24;
        var bw = new double[Buckets];
        var br = new double[Buckets]; var bg = new double[Buckets]; var bb = new double[Buckets];
        double ar = 0, ag = 0, ab = 0; long an = 0;   // plain average (grey-cover fallback)

        // Walk the pixel span directly. GetPixel is a native P/Invoke per call, and at a
        // stride of 6 over a 1024² bitmap that was ~29,000 interop transitions plus a
        // ToHsl call each — per artwork, on the UI thread, at every track change.
        var pixels = art.Pixels;
        var width = art.Width;
        for (int y = 0; y < art.Height; y += 6)
        {
            var row = y * width;
            for (int x = 0; x < width; x += 6)
            {
                var px = pixels[row + x];
                if (px.Alpha < 32) continue;
                ar += px.Red; ag += px.Green; ab += px.Blue; an++;

                double r = px.Red / 255.0, g = px.Green / 255.0, b = px.Blue / 255.0;
                double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                if (luma < 0.06 || luma > 0.94) continue;  // skip near-black / near-white
                double max = Math.Max(r, Math.Max(g, b));
                double min = Math.Min(r, Math.Min(g, b));
                double chroma = max - min;
                if (chroma < 0.05) continue;               // near-grey pixels don't vote a hue

                // Hue inline from the RGB we already have, rather than a second native
                // call per pixel (SKColor.ToHsl).
                double hDeg;
                if (chroma <= 0) hDeg = 0;
                else if (max == r) hDeg = 60.0 * (((g - b) / chroma) % 6.0);
                else if (max == g) hDeg = 60.0 * (((b - r) / chroma) + 2.0);
                else hDeg = 60.0 * (((r - g) / chroma) + 4.0);
                if (hDeg < 0) hDeg += 360.0;

                int bucket = (int)(hDeg / 360.0 * Buckets) % Buckets;
                double weight = chroma * chroma;
                bw[bucket] += weight;
                br[bucket] += px.Red * weight; bg[bucket] += px.Green * weight; bb[bucket] += px.Blue * weight;
            }
        }

        SKColor baseColor;
        bool hueWon;
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
            hueWon = true;
        }
        else if (an > 0)
        {
            baseColor = new SKColor((byte)(ar / an), (byte)(ag / an), (byte)(ab / an));
            hueWon = false;
        }
        else
        {
            return (Fallback, null);
        }

        baseColor.ToHsl(out var hue, out var s, out var l);
        s = Math.Clamp(Math.Max(s, 30f) * 1.06f, 0f, 96f);
        l = Math.Clamp(l, 20f, 54f);
        var card = SKColor.FromHsl(hue, s, l);
        return (card, hueWon ? card : baseColor);
    }
}
