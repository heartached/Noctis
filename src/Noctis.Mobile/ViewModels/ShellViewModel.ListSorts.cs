using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// The sort each list was left in (Songs, Favorites, a playlist, the Playlists list), kept in the
/// phone's settings (MobileListSorts) so it is still there when the list is opened again or the
/// app restarts. A song list used to be made fresh on every visit and always came back in its
/// default order (beta 2 feedback).
/// </summary>
public sealed partial class ShellViewModel
{
    private readonly Dictionary<string, string> _listSorts = new(StringComparer.Ordinal);
    // Lists sorted before the saved sorts were read keep the user's choice.
    private readonly HashSet<string> _listSortSetBeforeLoad = new(StringComparer.Ordinal);
    private bool _listSortsLoaded;
    private Task _listSortSaveChain = Task.CompletedTask;

    /// <summary>The tail of the list-sort saves; tests await it.</summary>
    internal Task PendingListSortSave { get; private set; } = Task.CompletedTask;

    /// <summary>A list's saved sort changed (its key), or all of them were read (null): open pages
    /// of that list follow, so the Favorites tab and a Favorites page opened from Library agree.</summary>
    internal event EventHandler<string?>? ListSortChanged;

    /// <summary>The saved sort of the list <paramref name="key"/>, if one is saved and still known.</summary>
    internal bool TryGetListSort<TSort>(string key, out TSort sort, out bool descending) where TSort : struct, Enum
    {
        sort = default;
        descending = false;
        if (!_listSorts.TryGetValue(key, out var saved)) return false;
        var parts = saved.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !Enum.TryParse(parts[0], ignoreCase: true, out sort) || !Enum.IsDefined(sort)) return false;
        descending = parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    /// <summary>Saves the list's sort; the list's own order (ascending) is saved as nothing.</summary>
    internal void SaveListSort<TSort>(string key, TSort sort, bool descending, TSort ownOrder) where TSort : struct, Enum
    {
        string? value = EqualityComparer<TSort>.Default.Equals(sort, ownOrder) && !descending
            ? null
            : descending ? $"{sort} desc" : sort.ToString();
        if (value == null) _listSorts.Remove(key);
        else _listSorts[key] = value;
        if (!_listSortsLoaded) _listSortSetBeforeLoad.Add(key);
        PendingListSortSave = _listSortSaveChain = _listSortSaveChain.ContinueWith(async _ =>
        {
            try
            {
                // Only this list's entry: a save never puts back another list's older sort.
                await Library.Persistence.UpdateSettingsAsync(settings =>
                {
                    if (value == null) settings.MobileListSorts.Remove(key);
                    else settings.MobileListSorts[key] = value;
                });
            }
            catch (Exception ex)
            {
                DebugLog.Write("Settings", $"List sort save failed: {ex.Message}");
            }
        }, TaskScheduler.Default).Unwrap();
        ListSortChanged?.Invoke(this, key);
    }

    private async Task LoadListSortsAsync()
    {
        try
        {
            var settings = await Library.Persistence.LoadSettingsAsync();
            foreach (var (key, value) in settings.MobileListSorts ?? new())
                if (!_listSortSetBeforeLoad.Contains(key) && !string.IsNullOrWhiteSpace(value)) _listSorts[key] = value;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Settings", $"List sorts load failed: {ex.Message}");
        }
        _listSortsLoaded = true;
        _listSortSetBeforeLoad.Clear();
        ListSortChanged?.Invoke(this, null);
    }
}
