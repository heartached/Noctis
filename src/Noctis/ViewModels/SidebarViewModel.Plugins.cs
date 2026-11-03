using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services.Plugins;

namespace Noctis.ViewModels;

/// <summary>
/// The sidebar's Plugins section (Discord feedback 11-03): one entry per screen a running plugin
/// added ("Album Town", "A-B Loop"), so it is reachable without digging through Settings. A row
/// opens the screen where its plugin page's "Open" does (MainWindowViewModel, like Settings: the
/// row never takes the selection, see <see cref="OpensSheet"/>). A plugin's entries leave with
/// its Show in sidebar switch, the row's Hide from Sidebar, or the plugin stopping.
/// </summary>
public partial class SidebarViewModel
{
    /// <summary>Plugin screens, in plugin load order.</summary>
    public ObservableCollection<PluginNavItem> PluginItems { get; } = new();

    public bool HasPluginItems => PluginItems.Count > 0;

    /// <summary>The row's Plugin Settings: open that plugin's page in Settings.</summary>
    public event EventHandler<PluginNavItem>? PluginSettingsRequested;

    /// <summary>
    /// Brings <see cref="PluginItems"/> in line with the running plugins' views (UI thread). Rows
    /// are kept per view, moved and removed in place, so an untouched row keeps its container.
    /// </summary>
    public void SyncPluginItems(IEnumerable<PluginViewEntry> views)
    {
        var wanted = views.Where(v => !v.IsWithdrawn && v.Plugin.ShowInSidebar).ToList();
        for (var i = PluginItems.Count - 1; i >= 0; i--)
            if (!wanted.Contains(PluginItems[i].View)) PluginItems.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = -1;
            for (var j = 0; j < PluginItems.Count; j++)
                if (ReferenceEquals(PluginItems[j].View, wanted[i])) { at = j; break; }
            if (at == i) continue;
            if (at < 0) PluginItems.Insert(i, new PluginNavItem(wanted[i]));
            else PluginItems.Move(at, i);
        }
        OnPropertyChanged(nameof(HasPluginItems));
    }

    /// <summary>Right-click › Hide from Sidebar: the plugin's Show in sidebar goes off (saved by
    /// the plugin host, which then drops its rows).</summary>
    [RelayCommand]
    private void HidePluginItem(PluginNavItem? item)
    {
        if (item is not null) item.View.Plugin.ShowInSidebar = false;
    }

    [RelayCommand]
    private void OpenPluginItemSettings(PluginNavItem? item)
    {
        if (item is not null) PluginSettingsRequested?.Invoke(this, item);
    }
}

/// <summary>A sidebar entry for a screen a running plugin added.</summary>
public sealed partial class PluginNavItem : NavItem
{
    /// <summary>Nav keys of plugin rows start with this (MainWindowViewModel routes them).</summary>
    public const string KeyPrefix = "plugin-view:";

    private static int _nextKey;

    internal PluginNavItem(PluginViewEntry view)
    {
        View = view;
        Key = KeyPrefix + Interlocked.Increment(ref _nextKey).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Label = view.Name;
        var name = view.PluginName;
        Monogram = PluginMonogram.Initials(name);
        MonogramBrush = PluginMonogram.Brush(name);
        if (view.Plugin.IconPath is { } path) Icon = PluginIconImages.LoadFile(path)?.Bitmap;
    }

    public PluginViewEntry View { get; }

    /// <summary>plugin.json's icon, or null for the letter tile.</summary>
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon is not null;
    public bool ShowMonogram => Icon is null;
    public string Monogram { get; }
    public IBrush MonogramBrush { get; }
}
