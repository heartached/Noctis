using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Noctis.Helpers;

/// <summary>
/// Shared Ctrl+Click / Ctrl+A multi-select logic for views.
/// Album tiles get "ctrl-selected" class on the Button.
/// Track rows get "ctrl-selected" class on the ListBoxItem.
/// </summary>
public static class MultiSelectHelper
{
    private const string SelectedClass = "ctrl-selected";

    /// <summary>
    /// Handles Ctrl+Click on an album-tile Button.
    /// Returns true if the event was consumed (Ctrl was held), false otherwise.
    /// </summary>
    public static bool HandleAlbumTileClick(Button tileButton, PointerPressedEventArgs e, HashSet<Button> selectedTiles)
    {
        // Only act on left-click; ignore right-click so context menus don't clear selection
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            return false;

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Normal left-click — clear all selections
            ClearAlbumSelections(selectedTiles);
            return false;
        }

        // Ctrl+Left-Click — toggle selection
        e.Handled = true;
        if (selectedTiles.Contains(tileButton))
        {
            tileButton.Classes.Remove(SelectedClass);
            selectedTiles.Remove(tileButton);
        }
        else
        {
            tileButton.Classes.Add(SelectedClass);
            selectedTiles.Add(tileButton);
        }
        return true;
    }

    /// <summary>
    /// Handles Ctrl+A to toggle-select all album-tile Buttons within a container.
    /// If all are already selected, deselects all. Otherwise selects all.
    /// Returns true if the event was consumed.
    /// </summary>
    public static bool HandleAlbumSelectAll(KeyEventArgs e, List<Button> allTiles, HashSet<Button> selectedTiles)
    {
        if (e.Key != Key.A || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return false;

        e.Handled = true;

        // Toggle: if all visible tiles are already selected, deselect all
        if (allTiles.Count > 0 && allTiles.All(t => selectedTiles.Contains(t)))
        {
            ClearAlbumSelections(selectedTiles);
        }
        else
        {
            // Clear stale references first, then select all currently visible tiles
            selectedTiles.Clear();
            foreach (var tile in allTiles)
            {
                tile.Classes.Add(SelectedClass);
                selectedTiles.Add(tile);
            }
        }
        return true;
    }

    /// <summary>
    /// Clears all Ctrl-selected album tiles.
    /// </summary>
    public static void ClearAlbumSelections(HashSet<Button> selectedTiles)
    {
        foreach (var tile in selectedTiles)
            tile.Classes.Remove(SelectedClass);
        selectedTiles.Clear();
    }

    /// <summary>
    /// Handles Ctrl+Click on a track row within a ListBox.
    /// Returns true if the event was consumed (Ctrl was held), false otherwise.
    /// </summary>
    public static bool HandleTrackRowClick(ListBoxItem item, PointerPressedEventArgs e, HashSet<ListBoxItem> selectedItems)
    {
        // Only act on left-click; ignore right-click so context menus don't clear selection
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            return false;

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ClearTrackSelections(selectedItems);
            return false;
        }

        e.Handled = true;
        if (selectedItems.Contains(item))
        {
            item.Classes.Remove(SelectedClass);
            selectedItems.Remove(item);
        }
        else
        {
            item.Classes.Add(SelectedClass);
            selectedItems.Add(item);
        }
        return true;
    }

    /// <summary>
    /// Handles Ctrl+A to toggle-select all track rows in a ListBox.
    /// If all are already selected, deselects all. Otherwise selects all.
    /// Returns true if the event was consumed.
    /// </summary>
    public static bool HandleTrackSelectAll(KeyEventArgs e, ListBox listBox, HashSet<ListBoxItem> selectedItems)
    {
        if (e.Key != Key.A || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return false;

        e.Handled = true;

        var allItems = new List<ListBoxItem>();
        foreach (var child in listBox.GetVisualDescendants())
        {
            if (child is ListBoxItem item)
                allItems.Add(item);
        }

        // Toggle: if all visible items are already selected, deselect all
        if (allItems.Count > 0 && allItems.All(i => selectedItems.Contains(i)))
        {
            ClearTrackSelections(selectedItems);
        }
        else
        {
            selectedItems.Clear();
            foreach (var item in allItems)
            {
                item.Classes.Add(SelectedClass);
                selectedItems.Add(item);
            }
        }
        return true;
    }

    /// <summary>
    /// Clears all Ctrl-selected track rows.
    /// </summary>
    public static void ClearTrackSelections(HashSet<ListBoxItem> selectedItems)
    {
        foreach (var item in selectedItems)
            item.Classes.Remove(SelectedClass);
        selectedItems.Clear();
    }

    /// <summary>
    /// Extracts data items of type T from selected album-tile Buttons via DataContext.
    /// </summary>
    public static List<T> GetSelectedData<T>(HashSet<Button> selectedTiles) where T : class
    {
        var result = new List<T>();
        foreach (var tile in selectedTiles)
        {
            if (tile.DataContext is T item)
                result.Add(item);
        }
        return result;
    }

    /// <summary>
    /// Extracts data items of type T from selected ListBoxItems via DataContext.
    /// </summary>
    public static List<T> GetSelectedTrackData<T>(HashSet<ListBoxItem> selectedItems) where T : class
    {
        var result = new List<T>();
        foreach (var item in selectedItems)
        {
            if (item.DataContext is T data)
                result.Add(data);
        }
        return result;
    }
}
