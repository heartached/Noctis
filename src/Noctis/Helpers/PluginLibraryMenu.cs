using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using Noctis.Plugins;
using Noctis.Services.Plugins;

namespace Noctis.Helpers;

/// <summary>A plugin's button in a page's top bar (API 1.7, <see cref="LibraryCommandPlaces.SongsPage"/>).</summary>
public sealed record PluginPageButton(string Label, string PluginName, Geometry Icon, ICommand Command);

/// <summary>
/// Plugin library commands (API 1.7) where the pages show them: entries at the end of the
/// artist page's "…" menu, and icon buttons in the Songs page's top bar. Read on every open,
/// so enabling or disabling a plugin shows up the next time.
/// </summary>
public static class PluginLibraryMenu
{
    private static readonly object PluginItemTag = new();

    /// <summary>The running plugins' commands for a place; set once by MainWindowViewModel.</summary>
    public static Func<LibraryCommandPlaces, IReadOnlyList<PluginLibraryCommand>>? Source { get; set; }

    public static IReadOnlyList<PluginLibraryCommand> Commands(LibraryCommandPlaces place)
    {
        try { return Source?.Invoke(place) ?? Array.Empty<PluginLibraryCommand>(); }
        catch { return Array.Empty<PluginLibraryCommand>(); }
    }

    /// <summary>Rebuilds the plugin entries at the end of <paramref name="menu"/> (after a
    /// separator of their own) for an artist page; the menu's own entries stay.</summary>
    public static void BindArtistMenu(MenuFlyout menu, string? artist)
    {
        foreach (var old in menu.Items.OfType<Control>().Where(c => ReferenceEquals(c.Tag, PluginItemTag)).ToList())
            menu.Items.Remove(old);
        if (string.IsNullOrWhiteSpace(artist)) return;
        var commands = Commands(LibraryCommandPlaces.ArtistPage);
        if (commands.Count == 0) return;
        menu.Items.Add(new Separator { Tag = PluginItemTag });
        foreach (var command in commands)
        {
            var c = command;
            var item = new MenuItem
            {
                Header = c.Label,
                Tag = PluginItemTag,
                Command = new RelayCommand(() => c.Execute(LibraryCommandPlaces.ArtistPage, artist)),
            };
            ToolTip.SetTip(item, c.PluginName);
            if (TryParseIcon(c.Icon) is { } geometry)
                item.Icon = new PathIcon { Width = 14, Height = 14, Data = geometry };
            menu.Items.Add(item);
        }
    }

    /// <summary>The Songs page's plugin buttons (commands without an icon are left out: a top-bar
    /// button is only its icon).</summary>
    public static IReadOnlyList<PluginPageButton> SongsPageButtons()
    {
        var list = new List<PluginPageButton>();
        foreach (var command in Commands(LibraryCommandPlaces.SongsPage))
        {
            if (TryParseIcon(command.Icon) is not { } geometry) continue;
            var c = command;
            list.Add(new PluginPageButton(c.Label, c.PluginName, geometry,
                new RelayCommand(() => c.Execute(LibraryCommandPlaces.SongsPage))));
        }
        return list;
    }

    private static Geometry? TryParseIcon(string? pathData)
    {
        if (string.IsNullOrWhiteSpace(pathData)) return null;
        try { return Geometry.Parse(pathData); }
        catch { return null; }
    }
}
