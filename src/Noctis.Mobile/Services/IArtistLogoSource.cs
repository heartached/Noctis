using System.Globalization;
using System.Text.Json;
using Avalonia.Media;
using Noctis.Mobile.ViewModels;
using Noctis.Services;
using SkiaSharp;

namespace Noctis.Mobile.Services;

/// <summary>
/// What a logo is made of, for drawing it legibly without touching its colours: how its ink
/// (the opaque pixels) spreads over relative luminance, and its accent colour when a good share
/// of the ink is one saturated colour (Doja Cat's neon blue, BLACKPINK's pink), which Apple's page
/// gives its Play button. <see cref="Width"/> × <see cref="Height"/> is the trimmed logo's size.
/// </summary>
public sealed record ArtistLogoInk(IReadOnlyList<double> Luminance, Color? Accent, int Width, int Height)
{
    /// <summary>The luminance histogram's bins (each the ink's share, together 1).</summary>
    public const int Bins = 16;

    /// <summary>The share of saturated ink above which the logo has an accent.</summary>
    public const double AccentShare = 0.3;

    /// <summary>The sidecar's format: bumped when the reading changes, so older ones are read again.</summary>
    internal const int SidecarVersion = 2;

    /// <summary>The ink's mean relative luminance.</summary>
    public double MeanLuminance => Luminance.Select((share, i) => share * BinLuminance(i)).Sum();

    /// <summary>The ink is mostly light (white logos), so a dark halo lifts it off a light page.</summary>
    public bool IsLight => MeanLuminance >= 0.4;

    public double Aspect => Height > 0 ? (double)Width / Height : 1;

    /// <summary>The share of the ink that reads on <paramref name="background"/> (3:1, WCAG's
    /// bar for large graphics).</summary>
    public double LegibleShare(Color background)
    {
        var bg = DominantColorExtractor.GetRelativeLuminance(background);
        double share = 0;
        for (var i = 0; i < Luminance.Count; i++)
        {
            var l = BinLuminance(i);
            if ((Math.Max(l, bg) + 0.05) / (Math.Min(l, bg) + 0.05) >= 3) share += Luminance[i];
        }
        return share;
    }

    private static double BinLuminance(int bin) => (bin + 0.5) / Bins;

    internal string ToJson() => JsonSerializer.Serialize(new Stored
    {
        V = SidecarVersion,
        L = Luminance.ToArray(),
        A = Accent is { } a ? $"#{a.R:X2}{a.G:X2}{a.B:X2}" : null,
        W = Width,
        H = Height,
    });

    internal static ArtistLogoInk? FromJson(string json)
    {
        var s = JsonSerializer.Deserialize<Stored>(json);
        if (s is not { V: SidecarVersion, L: { Length: Bins } l } || s.W <= 0 || s.H <= 0) return null;
        Color? accent = s.A != null && Color.TryParse(s.A, out var c) ? c : null;
        return new ArtistLogoInk(l, accent, s.W, s.H);
    }

    private sealed class Stored
    {
        public int V { get; set; }
        public double[]? L { get; set; }
        public string? A { get; set; }
        public int W { get; set; }
        public int H { get; set; }
    }

    /// <summary>Reads a logo's ink: every pixel at least a quarter opaque counts, weighted by its
    /// opacity. Null when the file cannot be decoded or has no ink.</summary>
    public static ArtistLogoInk? Analyse(string path)
    {
        using var bitmap = DecodeUnpremul(File.ReadAllBytes(path));
        return bitmap == null ? null : Analyse(bitmap);
    }

    internal static ArtistLogoInk? Analyse(SKBitmap bitmap)
    {
        var bins = new double[Bins];
        double total = 0, saturated = 0, r = 0, g = 0, b = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var p = bitmap.GetPixel(x, y);
            if (p.Alpha < 64) continue;
            var w = p.Alpha / 255.0;
            var lum = DominantColorExtractor.GetRelativeLuminance(Color.FromRgb(p.Red, p.Green, p.Blue));
            bins[Math.Clamp((int)(lum * Bins), 0, Bins - 1)] += w;
            total += w;
            var max = Math.Max(p.Red, Math.Max(p.Green, p.Blue));
            var min = Math.Min(p.Red, Math.Min(p.Green, p.Blue));
            // Coloured ink: clearly away from grey (BLACKPINK's pastel pink, chroma 77, counts;
            // an off-white or a chrome logo's tinted greys do not).
            if (max >= 90 && max - min >= 64 && (max - min) / (double)max >= 0.25)
            {
                saturated += w;
                r += p.Red * w;
                g += p.Green * w;
                b += p.Blue * w;
            }
        }
        if (total <= 0) return null;
        for (var i = 0; i < Bins; i++) bins[i] /= total;
        Color? accent = saturated / total >= AccentShare
            ? Color.FromRgb((byte)Math.Round(r / saturated), (byte)Math.Round(g / saturated), (byte)Math.Round(b / saturated))
            : null;
        return new ArtistLogoInk(bins, accent, bitmap.Width, bitmap.Height);
    }

    internal static SKBitmap? DecodeUnpremul(byte[] png)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(png));
        if (codec == null) return null;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        // A logo is drawn at most a phone's width: anything far larger is not a logo.
        if (info.Width <= 0 || info.Height <= 0 || info.Width * (long)info.Height > 16_000_000) return null;
        var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            return null;
        }
        return bitmap;
    }

    /// <summary>
    /// The logo with its transparent margins cut off (a few pixels kept), so the page sizes the
    /// letters, not the canvas: TheAudioDB's and fanart.tv's logos sit on an 800 × 310 canvas
    /// with up to a third of it empty. The ink itself is untouched. Null when there is nothing
    /// to cut or the image cannot be read (the original is then kept).
    /// </summary>
    public static byte[]? Trim(byte[] png)
    {
        using var bitmap = DecodeUnpremul(png);
        if (bitmap == null) return null;
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, y).Alpha < 16) continue;
            if (x < left) left = x;
            if (x > right) right = x;
            if (y < top) top = y;
            if (y > bottom) bottom = y;
        }
        if (right < 0) return null;
        const int pad = 4;
        var crop = new SKRectI(Math.Max(0, left - pad), Math.Max(0, top - pad),
            Math.Min(bitmap.Width, right + pad + 1), Math.Min(bitmap.Height, bottom + pad + 1));
        if (crop.Width >= bitmap.Width - 2 && crop.Height >= bitmap.Height - 2) return null;
        using var subset = new SKBitmap();
        if (!bitmap.ExtractSubset(subset, crop)) return null;
        using var image = SKImage.FromBitmap(subset);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }
}

/// <summary>An artist's logo on the phone: the file and its ink.</summary>
public sealed record ArtistLogo(string Path, ArtistLogoInk Ink);

/// <summary>
/// Where the artist page's logo comes from (Apple Music's artist logos: the artist's own
/// wordmark over the photo instead of the name). Asked when an artist page opens, never for a
/// list. Tests inject a fake; the Android head injects <see cref="ArtistLogoSource"/> when its
/// build carries a key. Without a logo the page shows the name, as before.
/// </summary>
public interface IArtistLogoSource
{
    /// <summary>The logo already on the phone, or null. Never asks the network.</summary>
    ArtistLogo? CachedLogo(string artistName);

    /// <summary>The logo: the cached one, else a lookup. Null when there is none or the lookup
    /// failed; never throws for a failed lookup (cancelling may throw).</summary>
    Task<ArtistLogo?> GetLogoAsync(string artistName, CancellationToken ct);
}

/// <summary>
/// The shared <see cref="ArtistLogoService"/> (fanart.tv / TheAudioDB by MusicBrainz id, cached
/// as <c>artist_logos/&lt;hash&gt;.png</c> under the data directory) with the phone's part: the
/// logo is trimmed before it is saved, and its ink is read once and kept beside it
/// (<c>&lt;hash&gt;.png.ink</c>), so a cached logo is drawn with its halo and accent from the
/// first frame.
/// </summary>
public sealed class ArtistLogoSource : IArtistLogoSource
{
    private readonly ArtistLogoService _service;
    private readonly Dictionary<string, ArtistLogo?> _known = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public ArtistLogoSource(ArtistLogoService service) => _service = service;

    /// <summary>The source over the phone's own cache, checking same-name artists against the
    /// artist's songs in <paramref name="library"/>.</summary>
    public static ArtistLogoSource Create(string dataDirectory, HttpClient http, ILibraryService library, ArtistLogoKeys keys) =>
        new(new ArtistLogoService(http, Path.Combine(dataDirectory, "artist_logos"), keys,
            name => MobileLibrary.SongsBy(library, name).Select(t => t.Title), ArtistLogoInk.Trim));

    public ArtistLogo? CachedLogo(string artistName)
    {
        if (_service.CachedLogo(artistName) is not { } path) return null;
        lock (_lock)
        {
            if (_known.TryGetValue(path, out var known)) return known;
        }
        // The sidecar is a few hundred bytes: read on the caller's thread, like the cache index.
        var logo = ReadSidecar(path) is { } ink ? new ArtistLogo(path, ink) : null;
        if (logo != null) Remember(path, logo);
        return logo;
    }

    public async Task<ArtistLogo?> GetLogoAsync(string artistName, CancellationToken ct)
    {
        if (CachedLogo(artistName) is { } cached) return cached;
        var path = await _service.GetLogoAsync(artistName, ct).ConfigureAwait(false);
        if (path == null) return null;
        // Decoding is off the UI thread: the page awaits this from it.
        var logo = await Task.Run(() =>
        {
            try
            {
                if (ArtistLogoInk.Analyse(path) is not { } ink) return null;
                TryWrite(path + ".ink", ink.ToJson());
                return new ArtistLogo(path, ink);
            }
            catch (Exception ex)
            {
                DebugLog.Write("Artist", $"Reading an artist logo failed: {ex.Message}");
                return null;
            }
        }, ct).ConfigureAwait(false);
        Remember(path, logo);
        return logo;
    }

    private void Remember(string path, ArtistLogo? logo)
    {
        lock (_lock) _known[path] = logo;
    }

    private static ArtistLogoInk? ReadSidecar(string path)
    {
        try
        {
            var sidecar = path + ".ink";
            if (File.Exists(sidecar) && ArtistLogoInk.FromJson(File.ReadAllText(sidecar)) is { } ink) return ink;
            // A logo saved without its sidecar (the app stopped in between): read it now.
            if (ArtistLogoInk.Analyse(path) is not { } read) return null;
            TryWrite(sidecar, read.ToJson());
            return read;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Artist", $"Reading an artist logo failed: {ex.Message}");
            return null;
        }
    }

    private static void TryWrite(string path, string text)
    {
        try
        {
            File.WriteAllText(path, text);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Artist", $"Saving an artist logo's ink failed: {ex.Message}");
        }
    }
}
