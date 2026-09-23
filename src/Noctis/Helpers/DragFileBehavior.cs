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
    // Application-scoped string formats carrying a per-drag token. The objects
    // themselves (the Track list, the playlist id) live in the slot below for the
    // duration of one drag: a DataTransferItem only carries bytes/strings/files/
    // bitmaps for custom formats, so in-process objects cannot ride inside it.

    /// <summary>Data format marking a drag that carries <see cref="Track"/>s (in-app drops).</summary>
    public static readonly DataFormat<string> TracksFormat = DataFormat.CreateStringApplicationFormat("noctis.tracks");

    /// <summary>Data format marking a sidebar playlist drag (reorder / move to folder).</summary>
    public static readonly DataFormat<string> PlaylistFormat = DataFormat.CreateStringApplicationFormat("noctis.playlist");

    private static readonly object _slotLock = new();
    private static string? _token;
    private static IReadOnlyList<Track>? _tracks;
    private static Guid? _playlistId;

    /// <summary>What the floating drag chip shows while tracks / an album are dragged:
    /// artwork, a title line, a subtitle line, and how many songs ride along.</summary>
    public sealed record DragPreview(string Title, string Subtitle, string? ArtworkPath, int Count);

    /// <summary>A track / album drag began in <see cref="TopLevel"/> (the OS drag shows no
    /// picture of what is being dragged; MainWindow draws a chip that follows the pointer).</summary>
    public static event Action<TopLevel, DragPreview>? DragPreviewStarted;

    /// <summary>The drag started by <see cref="DragPreviewStarted"/> ended (dropped or cancelled).</summary>
    public static event Action<TopLevel>? DragPreviewEnded;

    /// <summary>Chip content for a dragged row / tile: an album shows its name, artist and
    /// song count; a track shows its title and artist.</summary>
    public static DragPreview? BuildPreview(object? dataContext, IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return null;
        if (dataContext is Album album)
            return new DragPreview(
                album.Name,
                album.Artist,
                album.ArtworkPath ?? tracks.FirstOrDefault(t => t.HasAlbumArt)?.AlbumArtworkPath,
                tracks.Count);
        var track = tracks[0];
        return new DragPreview(track.TitleDisplay, track.Artist, track.AlbumArtworkPath, tracks.Count);
    }

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
        state.Pressed = e;   // DoDragDropAsync wants the press that began the gesture
    }

    private static async void OnMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control ctl) return;
        if (!_states.TryGetValue(ctl, out var state) || state.Started || state.Pressed is not { } pressed) return;
        if (!e.GetCurrentPoint(ctl).Properties.IsLeftButtonPressed) return;

        var pos = e.GetPosition(ctl);
        if (Math.Abs(pos.X - state.StartPoint.X) < 6 && Math.Abs(pos.Y - state.StartPoint.Y) < 6)
            return;

        state.Started = true;

        var topLevel = TopLevel.GetTopLevel(ctl);
        if (topLevel == null) return;

        try
        {
            var tracks = GetTracks(ctl.DataContext);
            if (tracks == null || tracks.Count == 0) return;

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
            // The Track objects ride along (via the slot) so in-app drop targets (sidebar
            // playlists) can add them without a path round-trip through the library.
            using var data = BuildTracksTransfer(tracks, items);
            var preview = BuildPreview(ctl.DataContext, tracks);
            if (preview != null) DragPreviewStarted?.Invoke(topLevel, preview);
            try
            {
                await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Copy);
            }
            finally
            {
                if (preview != null) DragPreviewEnded?.Invoke(topLevel);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DragFile] {ex.Message}");
        }
    }

    private static string NewToken(IReadOnlyList<Track>? tracks, Guid? playlistId)
    {
        lock (_slotLock)
        {
            _token = Guid.NewGuid().ToString("N");
            _tracks = tracks;
            _playlistId = playlistId;
            return _token;
        }
    }

    /// <summary>A track drag: the token item plus one file item per exportable track.</summary>
    public static DataTransfer BuildTracksTransfer(IReadOnlyList<Track> tracks, IReadOnlyList<IStorageItem> files)
    {
        var token = NewToken(tracks, null);
        var transfer = new DataTransfer();
        var item = new DataTransferItem();
        item.Set(TracksFormat, token);
        transfer.Add(item);
        foreach (var file in files)
            transfer.Add(DataTransferItem.CreateFile(file));
        return transfer;
    }

    /// <summary>A sidebar playlist drag: token only, nothing an external app can consume.</summary>
    public static DataTransfer BuildPlaylistTransfer(Guid playlistId)
    {
        var token = NewToken(null, playlistId);
        var transfer = new DataTransfer();
        var item = new DataTransferItem();
        item.Set(PlaylistFormat, token);
        transfer.Add(item);
        return transfer;
    }

    /// <summary>True for a drag started inside the app (tracks or playlist), so the
    /// main window's file-import overlay leaves it alone.</summary>
    public static bool IsInternalDrag(IDataTransfer data)
        => data.Contains(TracksFormat) || data.Contains(PlaylistFormat);

    /// <summary>The tracks carried by an in-app drag, or null when the payload isn't one
    /// (or belongs to an earlier drag).</summary>
    public static IReadOnlyList<Track>? GetDraggedTracks(IDataTransfer data)
    {
        if (!data.Contains(TracksFormat)) return null;
        var token = ReadToken(data, TracksFormat);
        lock (_slotLock)
            return token is not null && token == _token ? _tracks : null;
    }

    /// <summary>The playlist id carried by a sidebar playlist drag, or null.</summary>
    public static Guid? GetDraggedPlaylistId(IDataTransfer data)
    {
        if (!data.Contains(PlaylistFormat)) return null;
        var token = ReadToken(data, PlaylistFormat);
        lock (_slotLock)
            return token is not null && token == _token ? _playlistId : null;
    }

    private static string? ReadToken(IDataTransfer data, DataFormat<string> format)
        => data.TryGetValue(format);

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
        public PointerPressedEventArgs? Pressed;
    }
}
