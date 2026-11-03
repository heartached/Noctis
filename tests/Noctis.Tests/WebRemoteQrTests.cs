using Avalonia.Headless.XUnit;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

public class WebRemoteQrTests
{
    [AvaloniaFact]
    public void TryRender_ProducesScannableBlackOnWhiteBitmap()
    {
        using var bmp = QrCodeBitmap.TryRender("http://192.168.0.197:9420/?k=6384e0b2c35392bc");

        Assert.NotNull(bmp);
        Assert.Equal(bmp!.PixelSize.Width, bmp.PixelSize.Height);
        // Smallest QR is 21 modules; plus the 4-module quiet zone on each side.
        Assert.True(bmp.PixelSize.Width >= (21 + 8) * 8);

        // Pixels from the pure renderer: the headless platform's bitmap hands out a fresh,
        // uninitialised buffer on every Lock, so reading the bitmap back here returned
        // heap garbage (a flaky -1095755320 for the white quiet zone).
        var pixels = QrCodeBitmap.RenderPixels("http://192.168.0.197:9420/?k=6384e0b2c35392bc", 8, out var sizePx);
        Assert.Equal(bmp.PixelSize.Width, sizePx);
        int Pixel(int x, int y) => pixels[y * sizePx + x];

        // Quiet zone is opaque white (scannability on dark themes depends on it) and
        // the finder pattern's outer ring just past it is black.
        Assert.Equal(unchecked((int)0xFFFFFFFF), Pixel(2, 2));
        Assert.Equal(unchecked((int)0xFF000000), Pixel(4 * 8 + 4, 4 * 8 + 4));
    }
}
