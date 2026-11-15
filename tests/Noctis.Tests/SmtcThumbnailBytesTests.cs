using Noctis.Services;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Windows media overlay gets a 768 px copy of a big cover, not the whole file: the file was
/// copied into native memory on every song change (profiles' covers average 7.7 MB, up to 55 MB).
/// </summary>
public class SmtcThumbnailBytesTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _dir = Directory.CreateTempSubdirectory("noctis-smtc-thumb").FullName;

    public SmtcThumbnailBytesTests(ITestOutputHelper o) => _o = o;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string WriteImage(string name, int size, SKEncodedImageFormat format)
    {
        var rnd = new Random(3);
        using var bmp = new SKBitmap(size, size);
        // Noise, so the PNG is as big as a real photo cover (several MB at 3000 px).
        var pixels = new SKColor[size * size];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
        bmp.Pixels = pixels;
        using var data = bmp.Encode(format, 90);
        var path = Path.Combine(_dir, name);
        using var fs = File.Create(path);
        data.SaveTo(fs);
        return path;
    }

    [Fact]
    public void BigCover_IsSentAsA768pxPng()
    {
        var path = WriteImage("big.png", 3000, SKEncodedImageFormat.Png);
        var fileBytes = new FileInfo(path).Length;
        Assert.True(fileBytes > SmtcService.ThumbnailPassThroughBytes);

        var bytes = SmtcService.ThumbnailBytes(path);

        Assert.NotNull(bytes);
        _o.WriteLine($"cover file {fileBytes:N0} B -> overlay copy {bytes!.Length:N0} B");
        using var decoded = SKBitmap.Decode(bytes);
        Assert.Equal(SmtcService.ThumbnailWidth, decoded.Width);
        Assert.Equal(SmtcService.ThumbnailWidth, decoded.Height);
        Assert.True(bytes.Length * 4 < fileBytes);
    }

    [Fact]
    public void SmallCover_IsSentAsItIs()
    {
        var path = WriteImage("small.jpg", 300, SKEncodedImageFormat.Jpeg);
        Assert.True(new FileInfo(path).Length <= SmtcService.ThumbnailPassThroughBytes);
        Assert.Equal(File.ReadAllBytes(path), SmtcService.ThumbnailBytes(path));
    }

    [Fact]
    public void MissingOrUndecodableCover()
    {
        Assert.Null(SmtcService.ThumbnailBytes(Path.Combine(_dir, "missing.jpg")));
        var junk = Path.Combine(_dir, "junk.jpg");
        File.WriteAllBytes(junk, new byte[(int)SmtcService.ThumbnailPassThroughBytes + 10]);
        // Not an image: passed through as before (Windows shows no art for it either way).
        Assert.Equal(new FileInfo(junk).Length, SmtcService.ThumbnailBytes(junk)!.Length);
    }
}
