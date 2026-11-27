using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Noctis.Converters;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Beta 6 (#mobile-beta): on Now Playing, Previous / Next stuttered in their press when the tap
/// switched songs, not when Previous only restarted the song. A new cover's Colour Wash (a read of
/// the 768 px cover and a 45-tap blur) was made by a binding converter on the UI thread, for Now
/// Playing and again for the Lyrics page under it, in the frames the press animates. WashImage makes
/// it on the pool, keeps the old wash up meanwhile, and shows a cover washed before at once.
/// </summary>
public class MobileWashImageTests
{
    private static WriteableBitmap Cover(byte shade, int size = 256)
    {
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bitmap.Lock();
        var row = new byte[size * 4];
        for (var x = 0; x < size; x++)
        {
            row[x * 4] = shade;
            row[x * 4 + 1] = (byte)(255 - shade);
            row[x * 4 + 2] = (byte)x;
            row[x * 4 + 3] = 255;
        }
        for (var y = 0; y < size; y++) System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        return bitmap;
    }

    private static void Finish(WashImage image)
    {
        image.PendingBuild.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ANewCover_IsWashedOffTheUiThread_TheOldWashStayingUpMeanwhile()
    {
        var image = new WashImage();
        var first = Cover(40);

        image.Art = first;
        Assert.Null(image.Source);                       // not made inside the property change
        Finish(image);
        var wash = Assert.IsAssignableFrom<Bitmap>(image.Source);
        Assert.NotNull(PreBlurredArtworkConverter.WashTonesOf(wash));   // the converter's "wash"
        Assert.Same(wash, PreBlurredArtworkConverter.CachedWash(first));

        // The next cover: the old wash stays until the new one is made.
        var second = Cover(200);
        image.Art = second;
        Assert.Same(wash, image.Source);
        Finish(image);
        Assert.NotSame(wash, image.Source);
        Assert.Same(PreBlurredArtworkConverter.CachedWash(second), image.Source);

        // A cover washed before (the album's next song, or the other page's): at once.
        image.Art = first;
        Assert.Same(wash, image.Source);
        Assert.True(image.PendingBuild.IsCompleted);

        // No cover: no wash.
        image.Art = null;
        Assert.Null(image.Source);
    }

    [AvaloniaFact]
    public void AWashLandingAfterANewerCover_IsNotShown()
    {
        var image = new WashImage();
        var older = Cover(10);
        var newer = Cover(90);
        image.Art = older;
        var olderBuild = image.PendingBuild;
        image.Art = newer;
        olderBuild.GetAwaiter().GetResult();
        Finish(image);
        Assert.Same(PreBlurredArtworkConverter.CachedWash(newer), image.Source);
    }
}
