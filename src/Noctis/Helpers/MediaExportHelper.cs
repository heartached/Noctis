using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Noctis.Helpers;

/// <summary>Save-picker plumbing for exported media (MP4 lyric clips).</summary>
public static class MediaExportHelper
{
    /// <summary>
    /// Shows a "Save video" picker and returns the chosen local filesystem path (ffmpeg
    /// writes directly to it), or null if the user cancelled.
    /// </summary>
    public static async Task<string?> PickMp4PathAsync(TopLevel topLevel, string suggestedFileName)
    {
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save video",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "mp4",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("MP4 video")
                {
                    Patterns = new[] { "*.mp4" },
                    MimeTypes = new[] { "video/mp4" },
                },
            },
        });
        return file?.Path.LocalPath;
    }
}
