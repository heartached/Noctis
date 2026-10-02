using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The album page's 340 DIP cover asked for a bucketed decode capped at 512: at 125% that is
/// a 512px bitmap shrunk to 425 device px by the renderer, at 150% 512 → 510 (bilinear phase
/// drift over the whole cover), and from 175% up a 512px bitmap stretched over 595–680 px.
/// PixelExact surfaces decode at exactly the device pixels they cover and draw them 1:1.
/// </summary>
[Collection("ArtworkCache")]
public class CachedImagePixelExactTests : IDisposable
{
    private readonly List<int> _requestedWidths = new();

    public CachedImagePixelExactTests()
    {
        ArtworkCache.DisposeGrace = TimeSpan.Zero;
        ArtworkCache.ClearForTests();
        ArtworkCache.DecoderOverride = (path, width) =>
        {
            lock (_requestedWidths) _requestedWidths.Add(width);
            return Make(width);
        };
    }

    public void Dispose()
    {
        ArtworkCache.ClearForTests();
        ArtworkCache.DecoderOverride = null;
        ArtworkCache.DisposeGrace = TimeSpan.FromSeconds(2);
        ArtworkCache.MaxCacheBytes = 128L * 1024 * 1024;
    }

    private static string P(string name) => System.IO.Path.Combine("C:\\", "exact", name + ".jpg");

    private static Bitmap Make(int width)
        => new WriteableBitmap(new PixelSize(width, width), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    private int[] Requested()
    {
        lock (_requestedWidths) return _requestedWidths.ToArray();
    }

    private static async Task Flush(int settleMs = 120)
    {
        var until = DateTime.UtcNow.AddMilliseconds(settleMs);
        do
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20);
        } while (DateTime.UtcNow < until);
        Dispatcher.UIThread.RunJobs();
    }

    [Theory]
    [InlineData(1.00, 340)]
    [InlineData(1.25, 425)]
    [InlineData(1.50, 510)]
    [InlineData(1.75, 595)]
    [InlineData(2.00, 680)]
    [InlineData(3.00, 1020)]
    public void ExactWidth_IsTheAlbumHeaderInDevicePixels_AtEveryScaling(double scaling, int expected)
    {
        Assert.Equal(expected, CachedImage.ExactDecodeWidth(new Size(340, 340), scaling, Stretch.UniformToFill));
    }

    [Fact]
    public void ExactWidth_CoversTheTallerSideForFill_AndTheShorterForUniform()
    {
        // The mini player's lyrics column: a square cover filling a tall slot is drawn at
        // the slot's height, so that is the width it needs.
        Assert.Equal(625, CachedImage.ExactDecodeWidth(new Size(300, 500), 1.25, Stretch.UniformToFill));
        // A whole (uniform) cover in a wide slot is drawn at the slot's height.
        Assert.Equal(375, CachedImage.ExactDecodeWidth(new Size(400, 300), 1.25, Stretch.Uniform));
        Assert.Equal(500, CachedImage.ExactDecodeWidth(new Size(400, 300), 1.25, Stretch.Fill));
    }

    [Fact]
    public void ExactWidth_FloatNoise_DoesNotAddAPixel()
    {
        // 0.1 * 3 = 0.30000000000000004: a whole-pixel slot must not round up to one more.
        Assert.Equal(3, CachedImage.ExactDecodeWidth(new Size(0.1 * 3, 0.1 * 3), 10, Stretch.UniformToFill));
        Assert.Equal(1, CachedImage.ExactDecodeWidth(new Size(0.1, 0.1), 1, Stretch.UniformToFill));
    }

    [AvaloniaFact]
    public async Task PixelExact_DecodesTheSlotsOwnWidth_NotABucketOrTheCap()
    {
        var image = new CachedImage { Width = 340, Height = 340, DecodeWidth = 256, PixelExact = true, Stretch = Stretch.UniformToFill };
        var window = new Window { Width = 600, Height = 600, Content = image };
        window.Show();
        await Flush();

        image.SourcePath = P("hero");
        await Flush();

        Assert.Equal(new[] { 340 }, Requested()); // not the 384 bucket, not the 256 cap
        var shown = Assert.IsAssignableFrom<Bitmap>(image.Source);
        Assert.Equal(340, shown.PixelSize.Width);
        Assert.Same(shown, ArtworkCache.TryGet(P("hero"), 340, exact: true));
        Assert.Null(ArtworkCache.TryGet(P("hero"), 384));
        window.Close();
    }

    [AvaloniaFact]
    public async Task PixelExact_ReDecodesForANewSize_OnceTheResizeSettles()
    {
        var image = new CachedImage { Width = 300, Height = 300, PixelExact = true, Stretch = Stretch.UniformToFill };
        var window = new Window { Width = 800, Height = 800, Content = image };
        window.Show();
        await Flush();
        image.SourcePath = P("lyrics");
        await Flush();
        Assert.Equal(new[] { 300 }, Requested());

        // A window drag: a new size every few frames, then it stops.
        foreach (var size in new[] { 320, 350, 380, 410, 440 })
        {
            image.Width = image.Height = size;
            await Flush(40);
        }
        await Flush((int)CachedImage.ExactResizeDelay.TotalMilliseconds + 300);

        Assert.Equal(new[] { 300, 440 }, Requested()); // one decode for the size it settled at
        var shown = Assert.IsAssignableFrom<Bitmap>(image.Source);
        Assert.Equal(440, shown.PixelSize.Width);

        // Shrinking re-decodes too: a bigger bitmap than the slot is a resample again.
        image.Width = image.Height = 260;
        await Flush((int)CachedImage.ExactResizeDelay.TotalMilliseconds + 300);
        Assert.Equal(new[] { 300, 440, 260 }, Requested());
        window.Close();
    }

    [AvaloniaFact]
    public async Task PixelExact_KeepsTheCoverOnScreen_WhileItsNewSizeDecodes()
    {
        using var gate = new ManualResetEventSlim(false);
        ArtworkCache.DecoderOverride = (path, width) =>
        {
            lock (_requestedWidths) _requestedWidths.Add(width);
            if (width == 400) gate.Wait(5000);
            return Make(width);
        };
        // The album header blanks on a NEW cover (ClearOnSourceChange, the default), but a
        // re-decode of the cover it is already showing must not flash the placeholder.
        var image = new CachedImage { Width = 300, Height = 300, PixelExact = true, Stretch = Stretch.UniformToFill };
        var window = new Window { Width = 800, Height = 800, Content = image };
        window.Show();
        await Flush();
        image.SourcePath = P("hero");
        await Flush();
        var first = Assert.IsAssignableFrom<Bitmap>(image.Source);
        // The cache evicted it meanwhile (still alive: the image holds it), so there is no
        // cached copy of this cover to fall back on.
        ArtworkCache.MaxCacheBytes = 1L * 1024 * 1024;
        for (var i = 0; i < 40; i++)
            ArtworkCache.LoadAndCache(P("filler" + i), 256);
        Assert.Null(ArtworkCache.TryGet(P("hero"), 300, exact: true));

        image.Width = image.Height = 400;
        await Flush((int)CachedImage.ExactResizeDelay.TotalMilliseconds + 200);
        Assert.Contains(400, Requested());
        Assert.NotNull(image.Source); // still a cover up while the 400 decode is parked

        gate.Set();
        await Flush(300);
        var shown = Assert.IsAssignableFrom<Bitmap>(image.Source);
        Assert.Equal(400, shown.PixelSize.Width);
        Assert.NotSame(first, shown);
        window.Close();
    }

    [AvaloniaFact]
    public async Task PixelExact_InsideAViewbox_DecodesForTheSizeItIsDrawnAt()
    {
        // The lyrics page's CD / vinyl / cassette costumes draw the cover on a 200-unit
        // canvas that a Viewbox scales up to the slot: the decode must follow the scaled size.
        var image = new CachedImage { PixelExact = true, Stretch = Stretch.UniformToFill };
        var canvas = new Panel { Width = 200, Height = 200, Children = { image } };
        var viewbox = new Viewbox { Width = 600, Height = 600, Stretch = Stretch.Uniform, Child = canvas };
        var window = new Window { Width = 900, Height = 900, Content = viewbox };
        window.Show();
        await Flush();
        image.SourcePath = P("disc");
        await Flush();
        Assert.Equal(new[] { 600 }, Requested());

        // The page grows the costume: the Viewbox rescales without re-arranging the image.
        viewbox.Width = viewbox.Height = 800;
        await Flush((int)CachedImage.ExactResizeDelay.TotalMilliseconds + 300);
        Assert.Equal(new[] { 600, 800 }, Requested());
        window.Close();
    }

    /// <summary>1px black/white columns: any resampling shows up as grey.</summary>
    private static WriteableBitmap Stripes(int size)
    {
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bitmap.Lock();
        var row = new byte[size * 4];
        for (var x = 0; x < size; x++)
        {
            var v = (byte)(x % 2 == 0 ? 255 : 0);
            row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = v;
            row[x * 4 + 3] = 255;
        }
        for (var y = 0; y < size; y++)
            System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        return bitmap;
    }

    /// <summary>Largest difference of the blue channel from the stripes over a size×size square at (left, top).</summary>
    private static int StripeError(WriteableBitmap frame, int left, int top, int size)
    {
        using var fb = frame.Lock();
        var row = new byte[fb.RowBytes];
        var worst = 0;
        for (var y = top; y < top + size; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, row.Length);
            for (var x = left; x < left + size; x++)
                worst = Math.Max(worst, Math.Abs(row[x * 4] - ((x - left) % 2 == 0 ? 255 : 0)));
        }
        return worst;
    }

    [AvaloniaFact]
    public async Task RealSkia_TheExactDecodeIsDrawnPixelForPixel_ABucketIsResampled()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        ArtworkCache.DecoderOverride = (path, width) => Stripes(width);

        CachedImage Cover(bool exact, double left)
        {
            var image = new CachedImage
            {
                Width = 64, Height = 64, PixelExact = exact, Stretch = Stretch.UniformToFill,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Margin = new Thickness(left, 8, 0, 0),
            };
            // As the big covers set it: Avalonia.Skia draws this trilinear unless enlarging.
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            return image;
        }
        var exact = Cover(exact: true, left: 8);
        var bucket = Cover(exact: false, left: 88); // the 128 bucket, shrunk into 64 px
        var window = new Window { Width = 200, Height = 100, Content = new Panel { Children = { exact, bucket } } };
        window.Show();
        await Flush();
        exact.SourcePath = P("stripes");
        bucket.SourcePath = P("stripes");
        await Flush();

        var frame = window.CaptureRenderedFrame()!;
        Assert.Equal(0, StripeError(frame, 8, 8, 64));
        Assert.True(StripeError(frame, 88, 8, 64) > 100, "the bucket decode should be resampled to grey");
        window.Close();
    }

    [AvaloniaFact]
    public void ExactEntries_AreKeyedAtTheirOwnWidth_AndNeverStandInAsSufficient()
    {
        var exact = ArtworkCache.LoadAndCache(P("x"), 425, exact: true)!;
        Assert.Equal(425, exact.PixelSize.Width);
        Assert.Same(exact, ArtworkCache.TryGet(P("x"), 425, exact: true));
        Assert.Null(ArtworkCache.TryGet(P("x"), 512)); // the bucket the old header used is not filled

        // A bucket decode can paint while an exact one runs, but never replaces it.
        ArtworkCache.LoadAndCache(P("y"), 512);
        var fallback = ArtworkCache.TryGetAnyWidth(P("y"), 425, out var sufficient, exact: true);
        Assert.NotNull(fallback);
        Assert.False(sufficient);

        ArtworkCache.Invalidate(P("x"));
        Assert.Null(ArtworkCache.TryGet(P("x"), 425, exact: true));
    }
}
