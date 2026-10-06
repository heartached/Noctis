using Noctis.Mobile.Services;

namespace Noctis.Android.Services;

/// <summary>IFolderPicker over the activity's ACTION_OPEN_DOCUMENT_TREE round-trip.</summary>
public sealed class AndroidFolderPicker : IFolderPicker
{
    public Task<string?> PickFolderAsync()
        => MainActivity.Current?.PickFolderAsync() ?? Task.FromResult<string?>(null);

    public bool HasAccess(string folder) => SafGrants.CanRead(global::Android.App.Application.Context, folder);
}
