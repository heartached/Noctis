using Noctis.Mobile.Services;
using Noctis.Localization;

namespace Noctis.Android.Services;

/// <summary>IFolderPicker over the activity's ACTION_OPEN_DOCUMENT_TREE round-trip.</summary>
public sealed class AndroidFolderPicker : IFolderPicker
{
    public Task<string?> PickFolderAsync()
        => MainActivity.Current?.PickFolderAsync() ?? Task.FromResult<string?>(null);

    public bool HasAccess(string folder) => SafGrants.CanRead(global::Android.App.Application.Context, folder);

    public void ReleaseAccess(string folder) => SafGrants.Release(global::Android.App.Application.Context, folder);

    /// <summary>The system's own name for the volume a tree is on ("SD card"): the document id
    /// starts with the volume's UUID ("A00C-1602:Music"), or "primary" for the phone's storage.
    /// A card that is not in the phone now is still called an SD card.</summary>
    public string? StorageName(string folder)
    {
        var tree = folder.IndexOf("/tree/", StringComparison.Ordinal);
        if (tree < 0) return null;
        var id = Uri.UnescapeDataString(folder[(tree + "/tree/".Length)..]);
        var colon = id.IndexOf(':');
        if (colon <= 0) return null;
        var volume = id[..colon];
        if (volume == "primary" || volume == "home") return null;
        try
        {
            var context = global::Android.App.Application.Context;
            var storage = (global::Android.OS.Storage.StorageManager?)context.GetSystemService(global::Android.Content.Context.StorageService);
            var match = storage?.StorageVolumes.FirstOrDefault(v => string.Equals(v.Uuid, volume, StringComparison.OrdinalIgnoreCase));
            if (match?.GetDescription(context) is { Length: > 0 } name) return name;
        }
        catch (Exception)
        {
            // No volume list: the generic name below.
        }
        return Loc.T("Mobile.Folders.SdCard");
    }
}
