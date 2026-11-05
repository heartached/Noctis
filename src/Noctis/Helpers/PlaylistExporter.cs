using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Helpers;

/// <summary>
/// Export Playlist (.m3u8 / .m3u): the Playlists grid's right-click menu and the playlist
/// page's "…" menu (Discord, DendyAly 11-04: the page had no way to export) share this.
/// </summary>
public static class PlaylistExporter
{
    /// <summary>The songs a playlist exports, in its saved order (smart playlists: by their rules).</summary>
    public static List<Track> TracksOf(Playlist playlist, ILibraryService library) =>
        playlist.IsSmartPlaylist
            ? SmartPlaylistEvaluator.Evaluate(playlist, library.Tracks)
            : playlist.TrackIds.Select(library.GetTrackById).OfType<Track>().ToList();

    /// <summary>Asks where to save, then writes the playlist there. False when nothing was written.</summary>
    public static async Task<bool> ExportAsync(string playlistName, IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return false;

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow == null) return false;
        var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
        if (topLevel == null) return false;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("LibraryPlaylists.ExportPlaylist"),
            SuggestedFileName = SanitizeFileName(playlistName),
            DefaultExtension = "m3u8",
            FileTypeChoices = new[]
            {
                new FilePickerFileType(Noctis.Localization.Loc.T("Picker.M3uPlaylist")) { Patterns = new[] { "*.m3u8", "*.m3u" } }
            }
        });
        if (file == null) return false;

        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return false;

        var interop = App.Services!.GetRequiredService<IPlaylistInteropService>();
        try
        {
            await interop.ExportM3uAsync(path, tracks);
            return true;
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.State, "Playlist.Export", ex.Message);
            return false;
        }
    }

    internal static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "Playlist" : name.Trim();
    }
}
