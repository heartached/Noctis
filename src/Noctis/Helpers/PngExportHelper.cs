using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace Noctis.Helpers;

/// <summary>
/// Shared save-PNG / copy-PNG plumbing for share-card dialogs.
/// Returns a short status string for the dialog's status text, or null
/// when the user cancelled the picker.
/// </summary>
public static class PngExportHelper
{
    // The status text the dialogs show as is: in the app language (it was fixed English).
    internal static string SavedStatus => Localization.Loc.T("ShareLyrics.Saved");
    internal static string CopiedStatus => Localization.Loc.T("Export.Copied");
    internal static string SaveFailedStatus(string? reason = null) =>
        reason is null ? Localization.Loc.T("ShareLyrics.SaveFailed") : $"{Localization.Loc.T("ShareLyrics.SaveFailed")}: {reason}";
    internal static string CopyFailedStatus(string reason) => $"{Localization.Loc.T("ShareLyrics.CopyFailed")}: {reason}";

    public static Task<string?> SavePngAsync(TopLevel topLevel, byte[] png, string suggestedFileName)
        => SavePngAsync(topLevel, Task.FromResult<byte[]?>(png), suggestedFileName);

    /// <summary>As above for an image still rendering: the picker opens at once and the bytes
    /// are awaited only once a file is chosen (null bytes: the render failed).</summary>
    public static async Task<string?> SavePngAsync(TopLevel topLevel, Task<byte[]?> pngTask, string suggestedFileName)
    {
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Localization.Loc.T("Export.SaveImageTitle"),
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "png",
            FileTypeChoices = new[] { FilePickerFileTypes.ImagePng },
        });
        if (file == null)
            return null;

        try
        {
            if (await pngTask is not { } png)
                return SaveFailedStatus();
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(png);
            return SavedStatus;
        }
        catch (Exception ex)
        {
            return SaveFailedStatus(ex.Message);
        }
    }

    public static async Task<string?> CopyPngAsync(TopLevel topLevel, byte[] png, string fileName)
    {
        if (topLevel.Clipboard is not { } clipboard)
            return Localization.Loc.T("Export.ClipboardUnavailable");

        try
        {
            var transfer = new DataTransfer();
            // Raw PNG bytes under the platform "PNG" format — understood by
            // most image-aware apps (Discord, GIMP, Paint.NET, browsers).
            transfer.Add(DataTransferItem.Create(DataFormat.CreateBytesPlatformFormat("PNG"), png));

            // Also put a temp .png file on the clipboard so chat apps and
            // file managers can paste it as an attachment.
            //
            // Written into a fresh per-invocation subdirectory with FileMode.CreateNew.
            // The old code combined the caller's suggested name — "{Artist} - {Title}
            // lyrics.png", fully predictable — directly with GetTempPath(), which on
            // Linux/macOS is the world-writable /tmp: another local user could pre-create
            // that path as a symlink and have this write clobber a file the victim owns.
            // The file was also never cleaned up, so every Copy leaked a multi-MB PNG.
            var tempDir = Path.Combine(Path.GetTempPath(), "noctis-share-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var tempPath = Path.Combine(tempDir, Path.GetFileName(fileName));
            await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None))
            {
                await fs.WriteAsync(png);
            }

            var tempFile = await topLevel.StorageProvider.TryGetFileFromPathAsync(tempPath);
            if (tempFile != null)
                transfer.Add(DataTransferItem.CreateFile(tempFile));

            await clipboard.SetDataAsync(transfer);
            return CopiedStatus;
        }
        catch (Exception ex)
        {
            return CopyFailedStatus(ex.Message);
        }
    }

    /// <summary>
    /// Deletes leftover share/clip temp directories older than a few hours. A crash or a
    /// kill mid-export orphaned them, and nothing ever swept them — a karaoke clip export
    /// alone writes up to 3600 JPEG frames.
    /// </summary>
    public static void SweepStaleTempDirs()
    {
        try
        {
            var root = Path.GetTempPath();
            var cutoff = DateTime.UtcNow.AddHours(-6);
            foreach (var prefix in new[] { "noctis-share-", "noctis-karaoke-", "noctis-clip-" })
            {
                foreach (var dir in Directory.EnumerateDirectories(root, prefix + "*"))
                {
                    try
                    {
                        if (Directory.GetCreationTimeUtc(dir) < cutoff)
                            Directory.Delete(dir, recursive: true);
                    }
                    catch { /* in use or not ours — leave it */ }
                }
            }

            // The still clip's frame is a file in the temp root, not a directory
            // (ShareClipRenderer.RenderAsync: noctis-clip-{guid}.png).
            foreach (var file in Directory.EnumerateFiles(root, "noctis-clip-*.png"))
            {
                try
                {
                    if (File.GetCreationTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch { /* in use or not ours — leave it */ }
            }
        }
        catch { /* sweeping is best effort */ }
    }
}
