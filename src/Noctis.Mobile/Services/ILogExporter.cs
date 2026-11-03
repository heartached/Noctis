namespace Noctis.Mobile.Services;

/// <summary>Saves the DebugLog snapshot where the user picks (Android: a create-document
/// picker). False when the user backed out; a failed write throws, so it is not reported as
/// a cancel.</summary>
public interface ILogExporter
{
    Task<bool> ExportAsync(string suggestedFileName, string text);
}
