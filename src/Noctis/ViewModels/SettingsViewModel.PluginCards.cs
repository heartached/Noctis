using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Services;
using Noctis.Services.Plugins;

namespace Noctis.ViewModels;

/// <summary>
/// Settings → Plugins as a card grid: "Official Noctis plugins" (listed under author Noctis, the
/// verified mark) and "Community plugins" (other listings plus whatever the user installed that is
/// not a verified official copy). A search box filters both; a card opens the plugin's own page.
/// </summary>
public partial class SettingsViewModel
{
    /// <summary>Official cards that match the search, in list order.</summary>
    public ObservableCollection<PluginCardItem> OfficialCards { get; } = new();
    /// <summary>Community cards that match the search: listed ones first, then installed ones.</summary>
    public ObservableCollection<PluginCardItem> CommunityCards { get; } = new();

    /// <summary>Every card by key, matching the search or not (the detail page may outlive a filter).</summary>
    private readonly Dictionary<string, PluginCardItem> _pluginCards = new(StringComparer.Ordinal);
    private readonly List<PluginCardItem> _pluginCardOrder = new();

    /// <summary>The search box above both sections.</summary>
    [ObservableProperty] private string _pluginSearchText = string.Empty;

    /// <summary>Cards per row, set by the view from the page width (SettingsView.LayoutPluginGrid).</summary>
    [ObservableProperty] private int _pluginGridColumns = 3;

    /// <summary>The plugin whose page is open, or null for the grid.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPluginDetailOpen), nameof(IsPluginGridVisible))]
    private PluginCardItem? _selectedPluginCard;

    public bool IsPluginDetailOpen => SelectedPluginCard is not null;
    public bool IsPluginGridVisible => SelectedPluginCard is null;

    public bool HasOfficialCards => OfficialCards.Count > 0;
    public bool HasCommunityCards => CommunityCards.Count > 0;
    private bool IsSearchingPlugins => PluginSearchText.Trim().Length > 0;
    /// <summary>No listing came back at all (not a search miss).</summary>
    public bool ShowOfficialEmpty => !IsSearchingPlugins && HasNoOfficialPlugins && !_pluginCardOrder.Any(c => c.IsOfficial);
    /// <summary>Nothing installed or listed from the community yet (not a search miss).</summary>
    public bool ShowCommunityEmpty => !IsSearchingPlugins && !_pluginCardOrder.Any(c => !c.IsOfficial);
    /// <summary>A search that matched nothing in either section.</summary>
    public bool ShowNoPluginMatches => IsSearchingPlugins && OfficialCards.Count == 0 && CommunityCards.Count == 0;
    public bool ShowOfficialSection => HasOfficialCards || ShowOfficialEmpty;
    public bool ShowCommunitySection => HasCommunityCards || ShowCommunityEmpty;
    public string OfficialCountText => OfficialCards.Count.ToString(CultureInfo.CurrentCulture);
    public string CommunityCountText => CommunityCards.Count.ToString(CultureInfo.CurrentCulture);

    partial void OnPluginSearchTextChanged(string value) => ApplyPluginFilter();

    partial void OnIsOfficialPluginsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowOfficialEmpty));
        OnPropertyChanged(nameof(ShowOfficialSection));
    }

    [RelayCommand]
    private void OpenPluginCard(PluginCardItem? card)
    {
        if (card is null) return;
        SelectedPluginCard = card;
    }

    [RelayCommand]
    private void ClosePluginDetail() => SelectedPluginCard = null;

    [RelayCommand]
    private void ClearPluginSearch() => PluginSearchText = string.Empty;

    [RelayCommand]
    private void OpenPluginHomepage(PluginCardItem? card)
    {
        if (card?.Homepage is { Length: > 0 } url) PlatformHelper.OpenUrl(url);
    }

    /// <summary>
    /// One card per listing (keyed by id) and one per installed plugin that no listing took (an
    /// unverified copy of an official id gets its own community card). Cards are reused across
    /// refreshes so an open detail page and the card containers survive a list reload.
    /// </summary>
    private void SyncPluginCards()
    {
        var wanted = new List<PluginCardItem>();
        foreach (var listing in OfficialPlugins)
        {
            var card = CardFor("l:" + listing.Entry.Id.ToLowerInvariant());
            card.SetSource(listing, null);
            wanted.Add(card);
        }
        foreach (var plugin in CustomPlugins)
        {
            var card = CardFor("p:" + plugin.Id.ToLowerInvariant() + "|" + plugin.Directory);
            card.SetSource(null, plugin);
            wanted.Add(card);
        }

        foreach (var stale in _pluginCards.Values.Except(wanted).ToList())
        {
            stale.SetSource(null, null);
            _pluginCards.Remove(stale.Key);
        }
        _pluginCardOrder.Clear();
        _pluginCardOrder.AddRange(wanted);

        foreach (var card in wanted) EnsureCardIcon(card);
        if (SelectedPluginCard is { } open && !_pluginCards.ContainsKey(open.Key)) SelectedPluginCard = null;
        ApplyPluginFilter();
    }

    private PluginCardItem CardFor(string key)
    {
        if (!_pluginCards.TryGetValue(key, out var card)) _pluginCards[key] = card = new PluginCardItem(key);
        return card;
    }

    /// <summary>Puts the matching cards in each section, in place (moves, no Clear).</summary>
    private void ApplyPluginFilter()
    {
        var terms = PluginSearchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        SyncList(OfficialCards, _pluginCardOrder.Where(c => c.IsOfficial && c.Matches(terms)).ToList());
        // Community: listed ones first (the list's order), then what the user installed (the host's order).
        SyncList(CommunityCards, _pluginCardOrder.Where(c => !c.IsOfficial && c.IsListed && c.Matches(terms))
            .Concat(_pluginCardOrder.Where(c => !c.IsOfficial && !c.IsListed && c.Matches(terms))).ToList());
        foreach (var name in new[]
                 {
                     nameof(HasOfficialCards), nameof(HasCommunityCards), nameof(ShowOfficialEmpty), nameof(ShowCommunityEmpty),
                     nameof(ShowNoPluginMatches), nameof(ShowOfficialSection), nameof(ShowCommunitySection),
                     nameof(OfficialCountText), nameof(CommunityCountText),
                 })
            OnPropertyChanged(name);
    }

    private static void SyncList<T>(ObservableCollection<T> target, IList<T> wanted) where T : class
    {
        for (var i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = target.IndexOf(wanted[i]);
            if (at == i) continue;
            if (at < 0) target.Insert(i, wanted[i]);
            else target.Move(at, i);
        }
    }

    // ── Icons ──

    /// <summary>
    /// The card's picture: the installed copy's plugin.json "icon" first, then the listing's (built
    /// into the app, else fetched once), else the letter tile the card draws itself.
    /// </summary>
    private void EnsureCardIcon(PluginCardItem card)
    {
        var installedIcon = card.Installed?.IconPath;
        var entry = card.Listing?.Entry;
        string? key = installedIcon is not null ? "f:" + installedIcon + "|" + SafeWriteTicks(installedIcon)
            : entry is { IconPath: not null } or { IconUri: not null } ? "l:" + entry.Id + "|" + entry.Version + "|" + (entry.IconPath ?? entry.IconUri!.AbsoluteUri)
            : null;
        if (key == card.IconKey) return;
        card.IconKey = key;
        card.SetIcon(null);
        if (key is null) return;

        if (installedIcon is not null)
        {
            card.SetIcon(PluginIconImages.LoadFile(installedIcon));
            return;
        }
        if (entry!.IconPath is { } rel && PluginCatalog.ReadEmbeddedIcon(rel) is { } builtIn)
        {
            card.SetIcon(PluginIconImages.Decode(builtIn));
            return;
        }
        if (PluginCatalogClient is not { } client) return;
        _ = Task.Run(async () =>
        {
            var bytes = await client.LoadIconAsync(entry).ConfigureAwait(false);
            if (bytes is null) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (card.IconKey == key) card.SetIcon(PluginIconImages.Decode(bytes));
            });
        });
    }

    private static long SafeWriteTicks(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}

/// <summary>A decoded plugin icon and the colour its card is tinted with.</summary>
internal sealed record PluginIconImage(Bitmap Bitmap, Color? Average);

/// <summary>Decodes plugin icons (PNG, at most 512 KB) for the cards; any failure is "no icon".</summary>
internal static class PluginIconImages
{
    /// <summary>Decoded width: the detail page shows the icon at 88 DIP, up to 2x on a HiDPI screen.</summary>
    internal const int DecodeWidth = 176;

    public static PluginIconImage? LoadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > PluginCatalog.MaxIconBytes) return null;
            return Decode(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static PluginIconImage? Decode(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > PluginCatalog.MaxIconBytes || !PluginCatalogClient.IsPng(bytes)) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            var bitmap = Bitmap.DecodeToWidth(ms, DecodeWidth, BitmapInterpolationMode.HighQuality);
            return new PluginIconImage(bitmap, AverageColor(bytes));
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.State, "Plugins.Icon", $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The icon's mean colour over its opaque pixels (an 8 px decode), for the card's tint.</summary>
    private static Color? AverageColor(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var small = Bitmap.DecodeToWidth(ms, 8, BitmapInterpolationMode.MediumQuality);
            var size = small.PixelSize;
            var stride = size.Width * 4;
            var buffer = Marshal.AllocHGlobal(stride * size.Height);
            try
            {
                small.CopyPixels(new PixelRect(size), buffer, stride * size.Height, stride);
                var data = new byte[stride * size.Height];
                Marshal.Copy(buffer, data, 0, data.Length);
                var rgba = small.Format == PixelFormats.Rgba8888;
                double r = 0, g = 0, b = 0, n = 0;
                for (var i = 0; i + 3 < data.Length; i += 4)
                {
                    var a = data[i + 3];
                    if (a < 128) continue;
                    var c0 = data[i] * 255.0 / a;
                    var c2 = data[i + 2] * 255.0 / a;
                    r += rgba ? c0 : c2;
                    g += data[i + 1] * 255.0 / a;
                    b += rgba ? c2 : c0;
                    n++;
                }
                if (n == 0) return null;
                return Color.FromRgb((byte)Math.Clamp(r / n, 0, 255), (byte)Math.Clamp(g / n, 0, 255), (byte)Math.Clamp(b / n, 0, 255));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// The letter tile a plugin without an icon gets: its initials on a gradient picked from its name,
/// so the same plugin always has the same colour (FNV-1a, not string.GetHashCode, which changes
/// every run).
/// </summary>
public static class PluginMonogram
{
    /// <summary>(top, bottom) gradient stops, one hue each, all dark enough for white letters.</summary>
    internal static readonly (Color Top, Color Bottom)[] Palette =
    {
        (Color.Parse("#F87171"), Color.Parse("#B91C1C")),
        (Color.Parse("#FB923C"), Color.Parse("#C2410C")),
        (Color.Parse("#EAB308"), Color.Parse("#A16207")),
        (Color.Parse("#4ADE80"), Color.Parse("#15803D")),
        (Color.Parse("#2DD4BF"), Color.Parse("#0F766E")),
        (Color.Parse("#60A5FA"), Color.Parse("#1D4ED8")),
        (Color.Parse("#A78BFA"), Color.Parse("#6D28D9")),
        (Color.Parse("#F472B6"), Color.Parse("#BE185D")),
    };

    public static int PaletteIndex(string name)
    {
        uint hash = 2166136261;
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return (int)(hash % (uint)Palette.Length);
    }

    /// <summary>"Album Town" → "AT", "Mixxx" → "M", "" → "?".</summary>
    public static string Initials(string name)
    {
        var words = name.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w, 0)).ToList();
        if (words.Count == 0) return "?";
        var first = StringInfo.GetNextTextElement(words[0]).ToUpperInvariant();
        return words.Count > 1 ? first + StringInfo.GetNextTextElement(words[1]).ToUpperInvariant() : first;
    }

    public static IBrush Brush(string name)
    {
        var (top, bottom) = Palette[PaletteIndex(name)];
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.2, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.8, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
        };
    }
}

/// <summary>
/// One card on the Plugins page: a listing (with its installed copy, if verified for an official
/// listing) or an installed plugin no listing took. Everything the card and the detail page show.
/// </summary>
public sealed partial class PluginCardItem : ObservableObject
{
    private OfficialPluginItem? _watchedListing;
    private LoadedPlugin? _watchedPlugin;

    internal PluginCardItem(string key) => Key = key;

    internal string Key { get; }
    /// <summary>Where the current picture came from; starts as "never set" so a card without any
    /// icon still gets its letter-tile tint on the first sync.</summary>
    internal string? IconKey { get; set; } = "\0";

    public OfficialPluginItem? Listing { get; private set; }
    /// <summary>The installed plugin of a card that has no listing.</summary>
    public LoadedPlugin? Local { get; private set; }

    /// <summary>The installed copy this card manages: its switch, settings, views and Remove.</summary>
    public LoadedPlugin? Installed => Listing is not null ? Listing.Installed : Local;
    public bool IsInstalled => Installed is not null;
    public bool IsListed => Listing is not null;
    /// <summary>The verified mark: an official listing (author Noctis on plugins/index.json). An
    /// installed zip is never official on its own say, see PluginHost.IsVerifiedOfficial.</summary>
    public bool IsOfficial => Listing?.IsOfficial == true;

    public string Id => Listing?.Entry.Id ?? Local?.Id ?? "";
    public string Name => Listing?.Name ?? Local?.Name ?? "";
    public string Author => Listing is not null ? Listing.Entry.Author : Local?.Author ?? "";
    /// <summary>"by Noctis" / "by Jafezy JD"; a plugin that names nobody reads "Community plugin".</summary>
    public string ByLine => Author.Trim().Length > 0 ? Loc.T("Plugins.Get.By", Author.Trim()) : Loc.T("Plugins.Card.NoAuthor");
    public string Description => Listing is { Description.Length: > 0 } l ? l.Description : Local?.Description ?? "";
    public bool HasDescription => Description.Length > 0;
    public string Version => Listing?.Version ?? Local?.Version ?? "";
    public string VersionText => Version.Length > 0 ? Loc.T("Plugins.Detail.Version", Version) : "";
    public bool HasVersion => Version.Length > 0;
    public string? Homepage => Installed?.Homepage ?? Listing?.Entry.Homepage;
    public bool HasHomepage => !string.IsNullOrEmpty(Homepage);
    public bool IsRestricted => Installed?.IsRestricted == true;
    /// <summary>"by Jafezy JD · v1.2.1": the header's one line under the name.</summary>
    public string MetaLine => HasVersion ? ByLine + " · " + VersionText : ByLine;
    /// <summary>The short line under the page's switch, only when something is wrong: why it can't
    /// run, or the problem. A plugin that runs fine has none (owner 10-29: no long subtitle).</summary>
    public string EnableLine => Installed is not { } p ? ""
        : p.IsRestricted ? Loc.T("Plugins.Detail.RestrictedLine")
        : p.ShowStatusBadge ? p.StatusText
        : "";
    public bool HasEnableLine => EnableLine.Length > 0;
    /// <summary>The header has a primary action: Install / Update / Replace, or the plugin's own views.</summary>
    public bool HasHeaderActions => Listing?.ShowButton == true || Installed?.HasViews == true;
    /// <summary>The header's "..." menu has something in it: the homepage and/or the plugin's folder.</summary>
    public bool HasMoreMenu => HasHomepage || IsInstalled;

    /// <summary>The permissions line is open to the full list.</summary>
    [ObservableProperty] private bool _isPermissionsExpanded;

    [RelayCommand]
    private void TogglePermissions() => IsPermissionsExpanded = !IsPermissionsExpanded;

    /// <summary>The small pill in the card's corner: Update, On, Off, or a problem (Failed, ...).
    /// Empty for a plugin that is not installed (its card is just the plugin).</summary>
    public string ChipText
    {
        get
        {
            if (Listing is { IsBusy: true }) return Loc.T("Plugins.Card.Installing");
            if (Listing is { State: OfficialPluginState.Update }) return Loc.T("Plugins.Get.Update");
            if (Installed is not { } p) return "";
            if (p.ShowStatusBadge) return p.StatusText;
            return p.IsEnabled && !p.IsRestricted ? Loc.T("Plugins.Card.On") : Loc.T("Plugins.Card.Off");
        }
    }
    public bool HasChip => ChipText.Length > 0;
    /// <summary>On and Update read in the accent; Off and problems stay quiet.</summary>
    public bool ChipIsAccent => Listing is { State: OfficialPluginState.Update } || Installed is { IsEnabled: true, IsRestricted: false, ShowStatusBadge: false };

    /// <summary>What a screen reader says for the card: name, maker and what kind of plugin it is.</summary>
    public string AccessibleName => IsOfficial
        ? Loc.T("Plugins.Card.A11yOfficial", Name)
        : Author.Trim().Length > 0 ? Loc.T("Plugins.Card.A11yCommunity", Name, Author.Trim()) : Loc.T("Plugins.Card.A11yCommunityNoAuthor", Name);

    // ── Picture ──

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasIcon), nameof(ShowMonogram))]
    private Bitmap? _icon;

    [ObservableProperty] private IBrush _tintBrush = Brushes.Transparent;

    public bool HasIcon => Icon is not null;
    public bool ShowMonogram => Icon is null;
    public string Monogram => PluginMonogram.Initials(Name);
    public IBrush MonogramBrush => PluginMonogram.Brush(Name);
    /// <summary>Which palette entry the letter tile uses (tests).</summary>
    public int MonogramPaletteIndex => PluginMonogram.PaletteIndex(Name);

    internal void SetIcon(PluginIconImage? image)
    {
        Icon = image?.Bitmap;
        var tint = image?.Average ?? PluginMonogram.Palette[PluginMonogram.PaletteIndex(Name)].Top;
        // The integrations-list look: a faint wash of the plugin's colour from the top corner.
        TintBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.85, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x26, tint.R, tint.G, tint.B), 0),
                new GradientStop(Color.FromArgb(0x00, tint.R, tint.G, tint.B), 0.75),
            },
        };
    }

    internal bool Matches(IReadOnlyList<string> terms)
    {
        foreach (var term in terms)
            if (!(Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                  || Author.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                  || Description.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                  || Id.Contains(term, StringComparison.OrdinalIgnoreCase)))
                return false;
        return true;
    }

    internal void SetSource(OfficialPluginItem? listing, LoadedPlugin? local)
    {
        Listing = listing;
        Local = local;
        Rewire();
        RaiseAll();
    }

    private void Rewire()
    {
        if (_watchedListing is not null) _watchedListing.PropertyChanged -= OnSourceChanged;
        if (_watchedPlugin is not null) _watchedPlugin.PropertyChanged -= OnSourceChanged;
        _watchedListing = Listing;
        _watchedPlugin = Installed;
        if (_watchedListing is not null) _watchedListing.PropertyChanged += OnSourceChanged;
        if (_watchedPlugin is not null) _watchedPlugin.PropertyChanged += OnSourceChanged;
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The listing's installed copy changed: follow the new one.
        if (ReferenceEquals(sender, _watchedListing) && e.PropertyName == nameof(OfficialPluginItem.Installed)) Rewire();
        RaiseAll();
    }

    private static readonly string[] Derived =
    {
        nameof(Listing), nameof(Local), nameof(Installed), nameof(IsInstalled), nameof(IsListed), nameof(IsOfficial),
        nameof(Id), nameof(Name), nameof(Author), nameof(ByLine), nameof(Description), nameof(HasDescription),
        nameof(Version), nameof(VersionText), nameof(HasVersion), nameof(Homepage), nameof(HasHomepage), nameof(IsRestricted), nameof(EnableLine),
        nameof(MetaLine), nameof(HasEnableLine), nameof(HasMoreMenu), nameof(HasHeaderActions),
        nameof(ChipText), nameof(HasChip), nameof(ChipIsAccent), nameof(AccessibleName),
        nameof(Monogram), nameof(MonogramBrush), nameof(MonogramPaletteIndex),
    };

    private void RaiseAll()
    {
        foreach (var name in Derived) OnPropertyChanged(name);
    }
}
