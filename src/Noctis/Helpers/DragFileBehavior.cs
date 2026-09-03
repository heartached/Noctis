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
/// The same drag carries the <see cref="Track"/> objects under <see cref="TracksFormat"/>
/// so in-app drop targets (the sidebar playlists) can accept it, and a sidebar playlist
/// row starts a playlist-only drag (<see cref="PlaylistFormat"/>) for reorder / move.
/// </summary>
public static class DragFileBehavior
{
    /// <summary>Custom format marker so the main window can distinguish internal drags from external file drops.</summary>
    public const string InternalDragFormat = "Noctis.InternalDrag";

    /// <summary>Data format carrying the dragged <see cref="Track"/> list (in-app drops).</summary>
    public const string TracksFormat = "Noctis.Tracks";

    /// <summary>Data format carrying a dragged sidebar playlist's id (reorder / move to folder).</summary>
    public const string PlaylistFormat = "Noctis.Playlist";

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

        var topLevel = TopLevel.GetTopLevel(ctl);
        if (topLevel == null) return;

        try
        {
            // Pre-11.3 IDataObject/DataFormats drag API; suppress obsolete-usage
            // warnings rather than rewriting working code for the new DataTransfer API.
#pragma warning disable CS0618 // Type or member is obsolete
            var data = new DataObject();
            data.Set(InternalDragFormat, true);

            // Sidebar playlist row: an in-app reorder / move-to-folder drag. No file
            // payload — nothing outside the app should receive it.
            if (ctl.DataContext is PlaylistNavItem { IsFolder: false, PlaylistId: { } playlistId })
            {
                data.Set(PlaylistFormat, playlistId);
                await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
                return;
            }

            var tracks = GetTracks(ctl.DataContext);
            if (tracks == null || tracks.Count == 0) return;

            // The Track objects ride along so in-app drop targets (sidebar playlists)
            // can add them without a path round-trip through the library.
            data.Set(TracksFormat, tracks);

            var items = new List<IStorageItem>();
            foreach (var t in tracks)
            {
                // String overload, not new Uri(path). Constructing a Uri from a raw
                // filesystem path misparses common filenames: new Uri(@"C:\Music\Song
                // #1.mp3") treats "#1.mp3" as a URI fragment (LocalPath becomes
                // "C:\Music\Song "), and a literal "%20" is un-escaped to a space. Either
                // way the lookup returned null and the drag silently did nothing — or
                // exported the wrong file.
                var file = await topLevel.StorageProvider.TryGetFileFromPathAsync(t.FilePath);
                if (file != null) items.Add(file);
            }
            if (items.Count > 0)
                data.Set(DataFormats.Files, items);

            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
#pragma warning restore CS0618 // Type or member is obsolete
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DragFile] {ex.Message}");
        }
    }

#pragma warning disable CS0618 // Type or member is obsolete
    /// <summary>The tracks carried by an in-app drag, or null when the payload isn't one.</summary>
    public static IReadOnlyList<Track>? GetDraggedTracks(IDataObject data)
        => data.Contains(TracksFormat) ? data.Get(TracksFormat) as IReadOnlyList<Track> : null;

    /// <summary>The playlist id carried by a sidebar playlist drag, or null.</summary>
    public static Guid? GetDraggedPlaylistId(IDataObject data)
        => data.Contains(PlaylistFormat) && data.Get(PlaylistFormat) is Guid id ? id : null;
#pragma warning restore CS0618 // Type or member is obsolete

    private static List<Track>? GetTracks(object? dc)
    {
        return dc switch
        {
            Track t when !string.IsNullOrEmpty(t.FilePath) => new List<Track> { t },
            TopSongRow r when !string.IsNullOrEmpty(r.Track.FilePath) => new List<Track> { r.Track },
            Album a when a.Tracks?.Count > 0 => a.Tracks
                .Where(t => !string.IsNullOrEmpty(t.FilePath))
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
