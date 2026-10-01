namespace Noctis.Mobile.Services;

/// <summary>Saves the DebugLog snapshot where the user picks (Android: a create-document
/// picker). False when cancelled or failed.</summary>
public interface ILogExporter
{
    Task<bool> ExportAsync(string suggestedFileName, string text);
}
