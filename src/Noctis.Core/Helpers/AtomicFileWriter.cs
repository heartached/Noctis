namespace Noctis.Helpers;

/// <summary>
/// Writes a file that readers may open at any moment so they only ever see the old bytes or
/// the new ones (GitHub #224). The cached covers (<c>artwork/&lt;albumId&gt;.jpg</c>) used
/// <see cref="File.WriteAllBytes(string, byte[])"/>, which truncates the file and writes it
/// over in place while sharing reads; the covers are decoded through Skia's own file open,
/// which shares writes, so a decode that started during the write read a half-written JPEG.
/// SkiaSharp decodes a truncated JPEG without complaint (the rest of the picture is grey), and
/// that picture was what the grid kept showing.
/// </summary>
public static class AtomicFileWriter
{
    // Waits between replace attempts: a decode holding the old file open (Skia opens without
    // sharing delete, so the rename is refused until it closes) takes a few ms to ~100 ms.
    private static readonly int[] RetryDelaysMs = [25, 50, 100, 200, 400, 800];

    /// <summary>Writes <paramref name="bytes"/> beside <paramref name="path"/> and renames it into place.
    /// Throws when the file could not be written or replaced; nothing is left behind then.</summary>
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) dir = ".";
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           && ex is not FileNotFoundException and not DirectoryNotFoundException
                                           && attempt < RetryDelaysMs.Length)
                {
                    Thread.Sleep(RetryDelaysMs[attempt]);
                }
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch { /* best effort */ }
        }
    }
}
