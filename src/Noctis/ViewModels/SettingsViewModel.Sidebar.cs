using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>One row of Settings › General › Sidebar › Pages: a sidebar page with its Show
/// switch and move buttons (GitHub #199).</summary>
public sealed partial class SidebarPageOption : ObservableObject
{
    public SidebarPageOption(string key)
    {
        Key = key;
        _label = Loc.T(SidebarViewModel.LabelKey(key));
    }

    public string Key { get; }

    /// <summary>Settings always shows: its switch is off-limits.</summary>
    public bool CanHide => SidebarViewModel.CanHidePage(Key);

    [ObservableProperty] private string _label;
    [ObservableProperty] private bool _isShown = true;

    /// <summary>False while the page's feature is off (Inbox, Visualizer, Server, Audio CD):
    /// the row is left out of the list and the moves step over it.</summary>
    [ObservableProperty] private bool _isAvailable = true;

    [ObservableProperty] private bool _canMoveUp;
    [ObservableProperty] private bool _canMoveDown;

    internal Action<SidebarPageOption>? ShownChanged;

    partial void OnIsShownChanged(bool value) => ShownChanged?.Invoke(this);

    public override string ToString() => Label;
}

public partial class SettingsViewModel
{
    /// <summary>Every sidebar page in the user's order (Settings › General › Sidebar).</summary>
    /// <summary>Filled by the settings load (and Reset).</summary>
    public ObservableCollection<SidebarPageOption> SidebarPages { get; } = new();

    /// <summary>Which pages exist right now, mirrored from the sidebar. Starts like the rail:
    /// core pages and the Visualizer.</summary>
    private readonly Dictionary<string, bool> _sidebarPageAvailability =
        SidebarViewModel.DefaultPageOrder.ToDictionary(k => k, k => k is not ("inbox" or "server" or "cd"));

    /// <summary>Start page picker: Home plus every shown, available page but Settings (a sheet).</summary>
    public ObservableCollection<SidebarPageOption> StartPageOptions { get; } = new();

    /// <summary>Page key Noctis opens on (<see cref="AppSettings.StartPage"/>).</summary>
    [ObservableProperty] private string _startPage = "home";

    /// <summary>The user hid, showed or moved a sidebar page (or the layout was loaded/reset).</summary>
    public event EventHandler? SidebarLayoutChanged;

    private bool _rebuildingStartPageOptions;

    public IReadOnlyList<string> SidebarPageOrderKeys => SidebarPages.Select(p => p.Key).ToList();

    public IReadOnlyList<string> SidebarHiddenPageKeys =>
        SidebarPages.Where(p => !p.IsShown && p.CanHide).Select(p => p.Key).ToList();

    /// <summary>The picker's entry for <see cref="StartPage"/>; Home while that page is not offered.</summary>
    public SidebarPageOption? StartPageChoice
    {
        get => StartPageOptions.FirstOrDefault(o => o.Key == StartPage)
               ?? StartPageOptions.FirstOrDefault(o => o.Key == "home");
        set
        {
            if (_rebuildingStartPageOptions || value == null) return;
            StartPage = value.Key;
        }
    }

    partial void OnStartPageChanged(string value)
    {
        OnPropertyChanged(nameof(StartPageChoice));
        if (_settingsLoaded && !_suspendSettingPersistence) _ = SaveAsync();
    }

    /// <summary>Gets the navigation key for the start page: the chosen one, or Home when it is
    /// hidden or its feature is off.</summary>
    public string GetDefaultPageKey() => SidebarViewModel.ResolveStartPage(StartPage, SidebarHiddenPageKeys, key =>
    {
        var available = _sidebarPageAvailability.TryGetValue(key, out var a) && a;
        return key switch
        {
            // The sidebar learns these from settings after the load; read them straight away.
            "visualizer" => VisualizerEnabled,
            "inbox" => available || InboxEnabled,
            _ => available,
        };
    });

    /// <summary>A page came or went with its feature (MainWindowViewModel mirrors the sidebar).</summary>
    public void SetSidebarPageAvailable(string key, bool available)
    {
        if (!_sidebarPageAvailability.ContainsKey(key)) return;
        _sidebarPageAvailability[key] = available;
        if (SidebarPages.FirstOrDefault(p => p.Key == key) is not { } page || page.IsAvailable == available) return;
        page.IsAvailable = available;
        RefreshSidebarPageState();
    }

    [RelayCommand]
    private void MoveSidebarPageUp(SidebarPageOption? page) => MoveSidebarPage(page, -1);

    [RelayCommand]
    private void MoveSidebarPageDown(SidebarPageOption? page) => MoveSidebarPage(page, +1);

    /// <summary>Moves a page one place among the listed (available) pages.</summary>
    internal void MoveSidebarPage(SidebarPageOption? page, int step)
    {
        if (page == null) return;
        var from = SidebarPages.IndexOf(page);
        if (from < 0) return;
        var to = from + step;
        while (to >= 0 && to < SidebarPages.Count && !SidebarPages[to].IsAvailable) to += step;
        if (to < 0 || to >= SidebarPages.Count) return;
        SidebarPages.Move(from, to);
        OnSidebarLayoutEdited();
    }

    private void OnSidebarPageShownChanged(SidebarPageOption page)
    {
        // A hidden start page would open a page the user took off the sidebar: back to Home.
        if (!page.IsShown && StartPage == page.Key) StartPage = "home";
        OnSidebarLayoutEdited();
    }

    private void OnSidebarLayoutEdited()
    {
        RefreshSidebarPageState();
        SidebarLayoutChanged?.Invoke(this, EventArgs.Empty);
        if (_settingsLoaded && !_suspendSettingPersistence) _ = SaveAsync();
    }

    /// <summary>Reads the layout from <paramref name="settings"/> (load and Reset).</summary>
    private void LoadSidebarLayout(AppSettings settings)
    {
        var hidden = new HashSet<string>(settings.SidebarHiddenPages ?? new());
        SidebarPages.Clear();
        foreach (var key in SidebarViewModel.MergePageOrder(settings.SidebarPageOrder))
        {
            var page = new SidebarPageOption(key)
            {
                IsShown = !hidden.Contains(key) || !SidebarViewModel.CanHidePage(key),
                IsAvailable = _sidebarPageAvailability.TryGetValue(key, out var a) && a,
            };
            page.ShownChanged = OnSidebarPageShownChanged;
            SidebarPages.Add(page);
        }
        var start = settings.StartPage;
        StartPage = string.IsNullOrEmpty(start) || start == "settings" || !SidebarViewModel.DefaultPageOrder.Contains(start)
            ? "home" : start;
        RefreshSidebarPageState();
        SidebarLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes the layout onto <see cref="_settings"/> (SyncToSettings).</summary>
    private void SaveSidebarLayout()
    {
        var order = SidebarPageOrderKeys;
        _settings.SidebarPageOrder = order.SequenceEqual(SidebarViewModel.DefaultPageOrder) ? new() : order.ToList();
        _settings.SidebarHiddenPages = SidebarHiddenPageKeys.ToList();
        _settings.StartPage = StartPage;
    }

    /// <summary>Move-button states and the start page picker follow the list.</summary>
    private void RefreshSidebarPageState()
    {
        for (var i = 0; i < SidebarPages.Count; i++)
        {
            var page = SidebarPages[i];
            page.CanMoveUp = SidebarPages.Take(i).Any(p => p.IsAvailable);
            page.CanMoveDown = SidebarPages.Skip(i + 1).Any(p => p.IsAvailable);
        }

        _rebuildingStartPageOptions = true;
        try
        {
            StartPageOptions.Clear();
            foreach (var page in SidebarPages)
                if (page.Key == "home" || (page.IsAvailable && page.IsShown && page.Key != "settings"))
                    StartPageOptions.Add(page);
        }
        finally { _rebuildingStartPageOptions = false; }
        OnPropertyChanged(nameof(StartPageChoice));
    }

    /// <summary>Page names follow a language switch.</summary>
    private void RelabelSidebarPages()
    {
        foreach (var page in SidebarPages) page.Label = Loc.T(SidebarViewModel.LabelKey(page.Key));
    }
}
