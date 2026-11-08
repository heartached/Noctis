using Noctis.Localization;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// The user's sidebar layout (GitHub #199): pages they hid and the order they put them in
/// (Settings › General › Sidebar). Optional pages (Inbox, Visualizer, Server, Audio CD) still
/// come and go with their features; the layout only decides where a present page sits and
/// whether it shows. Settings always shows.
/// </summary>
public partial class SidebarViewModel
{
    /// <summary>Every sidebar page in its built-in order.</summary>
    public static IReadOnlyList<string> DefaultPageOrder { get; } = new[]
    {
        "home", "songs", "albums", "artists", "folders", "inbox", "playlists",
        "visualizer", "lyricsstudio", "server", "cd", "settings",
    };

    /// <summary>Pages that only exist while their feature does.</summary>
    private static readonly HashSet<string> OptionalPages = new() { "inbox", "visualizer", "server", "cd" };

    /// <summary>Icon resource key for a page, by nav key.</summary>
    public static string PageIcon(string key) => key switch
    {
        "home" => "HomeIcon", "songs" => "SongsIcon", "albums" => "AlbumsIcon", "artists" => "ArtistsIcon",
        "folders" => "FoldersIcon", "playlists" => "PlaylistsIcon", "visualizer" => "SidebarVisualizerIcon",
        "lyricsstudio" => "SidebarLyricsStudioIcon", "settings" => "SettingsIcon", "inbox" => "SidebarInboxIcon",
        "server" => "ServerIcon", "cd" => "CdIcon",
        _ => "",
    };

    /// <summary>True for a page the user may hide (all but Settings).</summary>
    public static bool CanHidePage(string key) => key != "settings";

    /// <summary>
    /// Every known page in the user's order: the saved keys first (unknown and repeated ones
    /// dropped), then each page the saved list lacks, right after the page it follows in the
    /// built-in order (or first when nothing before it is placed).
    /// </summary>
    public static List<string> MergePageOrder(IEnumerable<string>? userOrder)
    {
        var result = new List<string>();
        foreach (var key in userOrder ?? Enumerable.Empty<string>())
            if (DefaultPageOrder.Contains(key) && !result.Contains(key)) result.Add(key);

        for (var i = 0; i < DefaultPageOrder.Count; i++)
        {
            var key = DefaultPageOrder[i];
            if (result.Contains(key)) continue;
            var insertAt = 0;
            for (var j = i - 1; j >= 0; j--)
            {
                var at = result.IndexOf(DefaultPageOrder[j]);
                if (at >= 0) { insertAt = at + 1; break; }
            }
            result.Insert(insertAt, key);
        }
        return result;
    }

    /// <summary>The pages the sidebar shows, in order: present, not hidden (Settings always).</summary>
    public static List<string> VisiblePages(IEnumerable<string> present, IEnumerable<string>? userOrder, IEnumerable<string>? hidden)
    {
        var presentSet = new HashSet<string>(present);
        var hiddenSet = new HashSet<string>(hidden ?? Enumerable.Empty<string>());
        return MergePageOrder(userOrder)
            .Where(k => presentSet.Contains(k) && (!hiddenSet.Contains(k) || !CanHidePage(k)))
            .ToList();
    }

    /// <summary>
    /// The page to open on launch: the chosen one when it is a known page (not Settings, a sheet),
    /// not hidden and available; otherwise Home.
    /// </summary>
    public static string ResolveStartPage(string? key, IEnumerable<string>? hidden, Func<string, bool> isAvailable)
    {
        if (string.IsNullOrEmpty(key) || key == "settings" || !DefaultPageOrder.Contains(key)) return "home";
        if (hidden != null && hidden.Contains(key)) return "home";
        return isAvailable(key) ? key : "home";
    }

    /// <summary>Pages that exist right now (core pages always, optional ones while their feature is on).
    /// The Visualizer starts present, as the rail's built-in list has it.</summary>
    private readonly HashSet<string> _presentPages = new(DefaultPageOrder.Where(k => !OptionalPages.Contains(k) || k == "visualizer"));

    private List<string> _userPageOrder = new();
    private List<string> _userHiddenPages = new();

    /// <summary>One NavItem per page, kept while it is hidden so it comes back as the same row.</summary>
    private readonly Dictionary<string, NavItem> _pageItems = new();

    /// <summary>A page came or went with its feature (key, present).</summary>
    public event Action<string, bool>? PageAvailabilityChanged;

    /// <summary>Raised after <see cref="NavItems"/> was rearranged, so the view can put the
    /// highlight back on the selected row.</summary>
    public event Action? NavItemsLayoutChanged;

    /// <summary>True while the page exists (whether or not the user hid it).</summary>
    public bool IsPagePresent(string key) => _presentPages.Contains(key);

    /// <summary>Applies the user's order and hidden pages (UI thread).</summary>
    public void ApplyUserLayout(IEnumerable<string>? order, IEnumerable<string>? hidden)
    {
        _userPageOrder = order?.ToList() ?? new();
        _userHiddenPages = hidden?.ToList() ?? new();
        SyncNavItems();
    }

    private void SetPagePresent(string key, bool present)
    {
        var changed = present ? _presentPages.Add(key) : _presentPages.Remove(key);
        SyncNavItems();
        if (changed) PageAvailabilityChanged?.Invoke(key, present);
    }

    private NavItem GetPageItem(string key)
    {
        if (_pageItems.TryGetValue(key, out var item)) return item;
        item = NavItems.FirstOrDefault(i => i.Key == key)
               ?? new NavItem { Key = key, Label = Loc.T(LabelKey(key)), IconGlyph = PageIcon(key) };
        _pageItems[key] = item;
        return item;
    }

    /// <summary>Brings <see cref="NavItems"/> in line with the wanted pages, moving rows in place.</summary>
    private void SyncNavItems()
    {
        foreach (var existing in NavItems) _pageItems.TryAdd(existing.Key, existing);
        var wanted = VisiblePages(_presentPages, _userPageOrder, _userHiddenPages);
        var changed = false;
        for (var i = NavItems.Count - 1; i >= 0; i--)
            if (!wanted.Contains(NavItems[i].Key)) { NavItems.RemoveAt(i); changed = true; }
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = -1;
            for (var j = 0; j < NavItems.Count; j++)
                if (NavItems[j].Key == wanted[i]) { at = j; break; }
            if (at < 0) { NavItems.Insert(i, GetPageItem(wanted[i])); changed = true; }
            else if (at != i) { NavItems.Move(at, i); changed = true; }
        }
        if (changed) NavItemsLayoutChanged?.Invoke();
    }
}
