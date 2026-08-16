using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Net.Codecrete.QrCodeGenerator;
using Noctis.Services;

namespace Noctis.Helpers;

/// <summary>Renders text as a black-on-white QR code bitmap for on-screen scanning.</summary>
public static class QrCodeBitmap
{
    /// <summary>Modules of white quiet zone around the code — the spec minimum for
    /// reliable camera pickup, and what keeps the code scannable on dark themes.</summary>
    private const int QuietZoneModules = 4;

    public static WriteableBitmap? TryRender(string text, int pixelsPerModule = 8)
    {
        try
        {
            var qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
            var sizePx = (qr.Size + 2 * QuietZoneModules) * pixelsPerModule;
            var bitmap = new WriteableBitmap(
                new PixelSize(sizePx, sizePx), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);

            using var fb = bitmap.Lock();
            var row = new int[sizePx];
            for (int y = 0; y < sizePx; y++)
            {
                // GetModule returns false (light) outside the code, which paints
                // the quiet zone without special-casing it.
                var moduleY = y / pixelsPerModule - QuietZoneModules;
                for (int x = 0; x < sizePx; x++)
                {
                    var moduleX = x / pixelsPerModule - QuietZoneModules;
                    row[x] = qr.GetModule(moduleX, moduleY)
                        ? unchecked((int)0xFF000000)
                        : unchecked((int)0xFFFFFFFF);
                }
                Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, sizePx);
            }
            return bitmap;
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "QrCode.RenderFailed", ex.Message);
            return null;
        }
    }
}
