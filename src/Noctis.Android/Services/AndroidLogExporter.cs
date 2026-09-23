using Android.Content;
using Noctis.Mobile.Services;
using Noctis.Services;
using AUri = Android.Net.Uri;

namespace Noctis.Android.Services;

/// <summary>Settings → Export logs: the system create-document picker, then the text written
/// through the content resolver. No FileProvider or manifest entry is needed.</summary>
public sealed class AndroidLogExporter : ILogExporter
{
    private readonly Context _context;

    public AndroidLogExporter(Context context) => _context = context;

    public async Task<bool> ExportAsync(string suggestedFileName, string text)
    {
        if (MainActivity.Current is not { } activity) return false;
        var uri = await activity.CreateDocumentAsync(suggestedFileName, "text/plain");
        if (uri == null) return false;
        try
        {
            await using var stream = _context.ContentResolver!.OpenOutputStream(AUri.Parse(uri)!)!;
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(text);
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Android", $"Log export failed: {ex.Message}");
            return false;
        }
    }
}
