using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// Attached behavior that enables dragging audio files out of the application.
/// Set helpers:DragFileBehavior.EnableFileDrag="True" on any control whose
/// DataContext is a <see cref="Track"/> or <see cref="Album"/>.
/// </summary>
public static class DragFileBehavior
{
    public static readonly AttachedProperty<bool> EnableFileDragProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("EnableFileDrag", typeof(DragFileBehavior));

    private static readonly ConditionalWeakTable<Control, DragState> _states = new();

    static DragFileBehavior()
    {
        EnableFileDragProperty.Changed.AddClassHandler<Control>(OnEnableChanged);
    }

    public static bool GetEnableFileDrag(Control c) => c.GetValue(EnableFileDragProperty);
    public static void SetEnableFileDrag(Control c, bool v) => c.SetValue(EnableFileDragProperty, v);

    private static void OnEnableChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            // Use handledEventsToo so the handler fires even on Buttons that mark events handled
            control.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Bubble, true);
            control.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Bubble, true);
        }
        else
        {
            control.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
            control.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
        }
    }

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control ctl) return;
        if (!e.GetCurrentPoint(ctl).Properties.IsLeftButtonPressed) return;

        var state = _states.GetOrCreateValue(ctl);
        state.StartPoint = e.GetPosition(ctl);
        state.Started = false;
    }

    private static async void OnMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control ctl) return;
        if (!_states.TryGetValue(ctl, out var state) || state.Started) return;
        if (!e.GetCurrentPoint(ctl).Properties.IsLeftButtonPressed) return;

        var pos = e.GetPosition(ctl);
        if (Math.Abs(pos.X - state.StartPoint.X) < 6 && Math.Abs(pos.Y - state.StartPoint.Y) < 6)
            return;

        state.Started = true;

        var paths = GetFilePaths(ctl.DataContext);
        if (paths == null || paths.Count == 0) return;

        var topLevel = TopLevel.GetTopLevel(ctl);
        if (topLevel == null) return;

        try
        {
            var items = new List<IStorageItem>();
            foreach (var p in paths)
            {
                var file = await topLevel.StorageProvider.TryGetFileFromPathAsync(new Uri(p));
                if (file != null) items.Add(file);
            }

            if (items.Count > 0)
            {
                var data = new DataObject();
                data.Set(DataFormats.Files, items);
                await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DragFile] {ex.Message}");
        }
    }

    private static List<string>? GetFilePaths(object? dc)
    {
        return dc switch
        {
            Track t when !string.IsNullOrEmpty(t.FilePath) => new List<string> { t.FilePath },
            Album a when a.Tracks?.Count > 0 => a.Tracks
                .Where(t => !string.IsNullOrEmpty(t.FilePath))
                .Select(t => t.FilePath)
                .ToList(),
            _ => null
        };
    }

    private sealed class DragState
    {
        public Point StartPoint;
        public bool Started;
    }
}
