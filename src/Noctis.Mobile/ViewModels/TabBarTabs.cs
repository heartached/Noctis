namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Settings › Tab Bar (Discord #mobile-beta 11-17, "the songs tab at the bottom bar instead of
/// playlists"): which tab roots are on the bottom bar and in what order, kept as their names in
/// AppSettings.MobileTabBar. Two to five tabs. Library is always on the bar: every list (Playlists,
/// Artists, Albums, Songs, Favorites, Downloaded) is a row there, so a tab taken off the bar stays
/// in reach, and every tab root carries the profile button into Settings.
/// </summary>
public static class TabBarTabs
{
    public const int Min = 2, Max = 5;

    /// <summary>The bar as it has always been: Home, Favorites, Playlists, Library, Search.</summary>
    public static readonly IReadOnlyList<MobileTab> Defaults = new[]
    {
        MobileTab.Home, MobileTab.Favorites, MobileTab.Playlists, MobileTab.Library, MobileTab.Search,
    };

    /// <summary>Every tab the bar can hold, in the editor's order for those not on it.</summary>
    public static readonly IReadOnlyList<MobileTab> All = Enum.GetValues<MobileTab>();

    /// <summary>The one tab that cannot leave the bar.</summary>
    public const MobileTab Locked = MobileTab.Library;

    /// <summary>
    /// The saved list read back: known names in their order (a repeat taken once), Library added at
    /// the end if missing, cut to <see cref="Max"/> (Library kept). Empty, or fewer than
    /// <see cref="Min"/> tabs, = <see cref="Defaults"/>.
    /// </summary>
    public static IReadOnlyList<MobileTab> Decode(IReadOnlyList<string>? keys)
    {
        var tabs = new List<MobileTab>();
        foreach (var raw in keys ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            // Names only: Enum.TryParse would also take "3" or "Songs,Albums".
            var name = Enum.GetNames<MobileTab>().FirstOrDefault(n => string.Equals(n, raw.Trim(), StringComparison.OrdinalIgnoreCase));
            if (name == null) continue;
            var tab = Enum.Parse<MobileTab>(name);
            if (!tabs.Contains(tab)) tabs.Add(tab);
        }
        return Normalize(tabs);
    }

    /// <summary>Saved as names; the default bar as an empty list, so a later default reaches it.</summary>
    public static List<string> Encode(IReadOnlyList<MobileTab> tabs) =>
        tabs.SequenceEqual(Defaults) ? new List<string>() : tabs.Select(t => t.ToString()).ToList();

    /// <summary>The rules on any list: no repeats, Library on, at most <see cref="Max"/> (the last
    /// others dropped), at least <see cref="Min"/> (else the default bar).</summary>
    public static IReadOnlyList<MobileTab> Normalize(IEnumerable<MobileTab> tabs)
    {
        var list = tabs.Where(Enum.IsDefined).Distinct().ToList();
        if (list.Count == 0) return Defaults;
        if (!list.Contains(Locked)) list.Add(Locked);
        while (list.Count > Max) list.RemoveAt(list.FindLastIndex(t => t != Locked));
        return list.Count < Min ? Defaults : list;
    }

    /// <summary>The tab's label on the bar (narrow: Playlists has its own short key).</summary>
    public static string LabelKey(MobileTab tab) => tab switch
    {
        MobileTab.Home => "Nav.Home",
        MobileTab.Favorites => "Nav.Favorites",
        MobileTab.Playlists => "Mobile.Tab.Playlists",
        MobileTab.Library => "Tab.Library",
        MobileTab.Search => "Mobile.Search",
        MobileTab.Songs => "Nav.Songs",
        MobileTab.Albums => "Nav.Albums",
        _ => "Nav.Artists",
    };
}
