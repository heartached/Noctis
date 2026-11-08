using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Mobile.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>One row of Settings › Hidden: a hidden song (its title and artist) or folder, by the
/// path in AppSettings.HiddenLibraryFolders that Unhide takes out again.</summary>
public sealed record HiddenItem(string Path, string Title, string Subtitle)
{
    public string UnhideName => Loc.T("Mobile.Hidden.UnhideName", Title);
}

/// <summary>
/// Settings › Library › Hidden (Discord #mobile-beta: audio that is not music): what Hide Song
/// left out of the library, and any folder hidden the desktop's way (the same list,
/// AppSettings.HiddenLibraryFolders), each with Unhide.
/// </summary>
public sealed partial class SettingsPageViewModel
{
    private IReadOnlyList<HiddenItem>? _hiddenRows;

    public IReadOnlyList<HiddenItem> HiddenRows => _hiddenRows ??= BuildHiddenRows();

    public bool HasHidden => HiddenCount > 0;

    private int HiddenCount => Shell.Library.Service.HiddenFolders.Count;

    /// <summary>The Hidden row's value: how many, or nothing when none.</summary>
    public string HiddenValue => HasHidden ? HiddenCount.ToString(Loc.Instance.Culture) : string.Empty;

    public string HiddenRowName => Named(Loc.T("Mobile.Settings.Hidden"), HiddenValue);

    public bool ShowsHiddenPane => ShownSubPane == SettingsPane.Hidden;

    private IReadOnlyList<HiddenItem> BuildHiddenRows()
    {
        var hidden = Shell.Library.Service.HiddenFolders;
        if (hidden.Count == 0) return Array.Empty<HiddenItem>();
        var all = Shell.Library.Service.AllTracks;
        var rows = new List<HiddenItem>(hidden.Count);
        foreach (var path in hidden)
        {
            var song = all.FirstOrDefault(t => FolderPathMatch.IsSame(t.FilePath, path));
            if (song != null)
            {
                rows.Add(new HiddenItem(path, song.Title, song.Artist));
                continue;
            }
            // A song the library no longer has (an index entry, a desktop song, a file in a picked
            // folder: SAF names it ".../document/..."), else a folder (a picked tree, a path).
            var isSong = DeviceMusicScan.IsDeviceMusicPath(path)
                         || path.StartsWith("noctis-remote:", StringComparison.Ordinal)
                         || path.Contains("/document/", StringComparison.Ordinal);
            rows.Add(isSong
                ? new HiddenItem(path, FolderDisplay(path), Loc.T("Mobile.Hidden.NotFound"))
                : new HiddenItem(path, FolderDisplay(path), Loc.T("Mobile.Hidden.Folder")));
        }
        return rows;
    }

    [RelayCommand]
    private async Task UnhideAsync(HiddenItem? item)
    {
        if (item == null) return;
        await Shell.SetHiddenAsync(new[] { item.Path }, hidden: false);
        HiddenChanged();
    }

    /// <summary>The hidden list changed (Unhide here, Hide Song in a menu): the row and the page follow.</summary>
    private void HiddenChanged()
    {
        _hiddenRows = null;
        OnPropertyChanged(nameof(HiddenRows));
        OnPropertyChanged(nameof(HasHidden));
        OnPropertyChanged(nameof(HiddenValue));
        OnPropertyChanged(nameof(HiddenRowName));
    }

    private void OnLibraryRefreshed(object? sender, EventArgs e) => HiddenChanged();
}
