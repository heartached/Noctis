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
    public static async Task<string?> SavePngAsync(TopLevel topLevel, byte[] png, string suggestedFileName)
    {
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save image",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "png",
            FileTypeChoices = new[] { FilePickerFileTypes.ImagePng },
        });
        if (file == null)
            return null;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(png);
            return "Saved";
        }
        catch (Exception ex)
        {
            return $"Save failed: {ex.Message}";
        }
    }

    public static async Task<string?> CopyPngAsync(TopLevel topLevel, byte[] png, string fileName)
    {
        if (topLevel.Clipboard is not { } clipboard)
            return "Clipboard unavailable";

        try
        {
            var transfer = new DataTransfer();
            // Raw PNG bytes under the platform "PNG" format — understood by
            // most image-aware apps (Discord, GIMP, Paint.NET, browsers).
            transfer.Add(DataTransferItem.Create(DataFormat.CreateBytesPlatformFormat("PNG"), png));

            // Also put a temp .png file on the clipboard so chat apps and
            // file managers can paste it as an attachment.
            var tempPath = Path.Combine(Path.GetTempPath(), fileName);
            await File.WriteAllBytesAsync(tempPath, png);
            var tempFile = await topLevel.StorageProvider.TryGetFileFromPathAsync(tempPath);
            if (tempFile != null)
                transfer.Add(DataTransferItem.CreateFile(tempFile));

            await clipboard.SetDataAsync(transfer);
            return "Copied to clipboard";
        }
        catch (Exception ex)
        {
            return $"Copy failed: {ex.Message}";
        }
    }
}
