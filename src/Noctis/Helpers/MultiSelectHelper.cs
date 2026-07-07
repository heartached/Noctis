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

    // ── Data-tracked variants ──
    //
    // The container-tracked variants above suffer a virtualization bug: scrolling
    // recycles ListBoxItems, so a HashSet<ListBoxItem> ends up holding stale
    // references whose DataContext now points at a *different* track. The
    // ctrl-selected visual class then sticks to whatever track happens to
    // recycle into that container.
    //
    // The variants below track selection by the underlying data object (e.g.
    // Track) and re-apply/strip the visual class as containers prepare/clear,
    // so scrolling and tab-switching no longer corrupt the selection.

    /// <summary>Toggle Ctrl-selection on a track row by its data object.</summary>
    public static bool HandleTrackRowClickByData<T>(ListBoxItem item, T data, PointerPressedEventArgs e, HashSet<T> selectedData) where T : class
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            return false;

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ClearTrackSelectionsByData(selectedData);
            return false;
        }

        e.Handled = true;
        if (selectedData.Contains(data))
        {
            selectedData.Remove(data);
            item.Classes.Remove(SelectedClass);
        }
        else
        {
            selectedData.Add(data);
            item.Classes.Add(SelectedClass);
        }
        return true;
    }

    /// <summary>Ctrl+A toggle for data-tracked track selection.</summary>
    public static bool HandleTrackSelectAllByData<T>(KeyEventArgs e, ListBox listBox, HashSet<T> selectedData) where T : class
    {
        if (e.Key != Key.A || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return false;

        e.Handled = true;

        if (listBox.ItemsSource is not System.Collections.IEnumerable items) return true;
        var allData = items.OfType<T>().ToList();

        if (allData.Count > 0 && allData.All(d => selectedData.Contains(d)))
        {
            ClearTrackSelectionsByData(selectedData);
            // Strip the class from any currently realized rows.
            foreach (var child in listBox.GetVisualDescendants())
                if (child is ListBoxItem li) li.Classes.Remove(SelectedClass);
        }
        else
        {
            selectedData.Clear();
            foreach (var d in allData) selectedData.Add(d);
            foreach (var child in listBox.GetVisualDescendants())
                if (child is ListBoxItem li) li.Classes.Add(SelectedClass);
        }
        return true;
    }

    /// <summary>Sync the ctrl-selected class on a container based on whether its
    /// DataContext is in the data selection set. Call from ContainerPrepared so
    /// recycled containers reflect the right state after scroll/rebind.</summary>
    public static void SyncContainerVisual<T>(ListBoxItem item, HashSet<T> selectedData) where T : class
    {
        if (item.DataContext is T data && selectedData.Contains(data))
            item.Classes.Add(SelectedClass);
        else
            item.Classes.Remove(SelectedClass);
    }

    /// <summary>Clear all data-tracked track selections.</summary>
    public static void ClearTrackSelectionsByData<T>(HashSet<T> selectedData) where T : class
    {
        selectedData.Clear();
    }

    /// <summary>Clear data-tracked track selections and strip the ctrl-selected
    /// class from any realized rows in the given ListBox.</summary>
    public static void ClearTrackSelectionsByData<T>(HashSet<T> selectedData, ListBox listBox) where T : class
    {
        selectedData.Clear();
        foreach (var child in listBox.GetVisualDescendants())
            if (child is ListBoxItem li) li.Classes.Remove(SelectedClass);
    }

    // ── Data-tracked album-tile variants ──
    //
    // Album grids virtualize rows (outer ListBox), so scrolling destroys/recycles
    // the tile Buttons. A HashSet<Button> then holds stale references and the
    // ctrl-selected visual is lost when a fresh Button scrolls back in. These
    // variants track selection by the underlying data object (Album/FavoriteItem)
    // and re-apply the visual class from Button.Loaded as tiles are realized, so
    // scrolling no longer drops the selection.

    /// <summary>Toggle Ctrl-selection on an album tile by its data object.
    /// Returns true if Ctrl was held (event consumed). On a plain left-click the
    /// caller should clear the set and strip realized tiles.</summary>
    public static bool HandleAlbumTileClickByData<T>(Button tile, T data, PointerPressedEventArgs e, HashSet<T> selectedData) where T : class
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            return false;

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return false;

        e.Handled = true;
        if (selectedData.Contains(data))
        {
            selectedData.Remove(data);
            tile.Classes.Remove(SelectedClass);
        }
        else
        {
            selectedData.Add(data);
            tile.Classes.Add(SelectedClass);
        }
        return true;
    }

    /// <summary>Ctrl+A toggle for data-tracked album selection over the full item set.</summary>
    public static bool HandleAlbumSelectAllByData<T>(KeyEventArgs e, IEnumerable<T> allData, IEnumerable<Button> realizedTiles, HashSet<T> selectedData) where T : class
    {
        if (e.Key != Key.A || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return false;

        e.Handled = true;

        var all = allData.ToList();
        var tiles = realizedTiles.ToList();
        if (all.Count > 0 && all.All(d => selectedData.Contains(d)))
        {
            selectedData.Clear();
            foreach (var tile in tiles) tile.Classes.Remove(SelectedClass);
        }
        else
        {
            selectedData.Clear();
            foreach (var d in all) selectedData.Add(d);
            foreach (var tile in tiles) SyncAlbumTileVisual(tile, selectedData);
        }
        return true;
    }

    /// <summary>Re-apply/strip the ctrl-selected class on a tile based on the data
    /// selection set. Call from Button.Loaded so recycled tiles reflect the right
    /// state after a scroll.</summary>
    public static void SyncAlbumTileVisual<T>(Button tile, HashSet<T> selectedData) where T : class
    {
        if (tile.DataContext is T data && selectedData.Contains(data))
            tile.Classes.Add(SelectedClass);
        else
            tile.Classes.Remove(SelectedClass);
    }

    /// <summary>Clear all data-tracked album selections and strip the class from realized tiles.</summary>
    public static void ClearAlbumSelectionsByData<T>(HashSet<T> selectedData, IEnumerable<Button> realizedTiles) where T : class
    {
        selectedData.Clear();
        foreach (var tile in realizedTiles) tile.Classes.Remove(SelectedClass);
    }
}
