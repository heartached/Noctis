using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Plugins;

namespace Noctis.Services.Plugins;

/// <summary>
/// "library.import" (API 1.5): a plugin adds audio files it saved (the YouTube download plugin's
/// downloads) to the library through the same additive import a drop uses
/// (<see cref="ILibraryService.ImportFilesAsync"/>): no rescan, the files marked as added one by
/// one so they survive rescans outside the music folders. Only existing files of a format Noctis
/// plays get through; imports from a stopped plugin are dropped.
/// </summary>
internal sealed class PluginLibraryImporter : ILibraryImport
{
    /// <summary>Most paths one call takes; the rest are ignored.</summary>
    internal const int MaxPaths = 500;

    private readonly ILibraryService? _library;
    private readonly Func<AppSettings> _settings;
    private readonly Func<bool> _isActive;
    private readonly string _pluginId;

    public PluginLibraryImporter(ILibraryService? library, Func<AppSettings> settings, Func<bool> isActive, string pluginId)
    {
        _library = library;
        _settings = settings;
        _isActive = isActive;
        _pluginId = pluginId;
    }

    public IReadOnlyList<string> MusicFolders
    {
        get
        {
            try { return (_settings().MusicFolders ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).ToArray(); }
            catch { return Array.Empty<string>(); }
        }
    }

    public async Task<IReadOnlyList<TrackInfo>> ImportFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (_library is not { } library || !_isActive()) return Array.Empty<TrackInfo>();
        var files = Accept(paths);
        if (files.Count == 0) return Array.Empty<TrackInfo>();

        // On the UI thread: ImportFilesAsync raises LibraryUpdated on the thread that awaits it,
        // and the views listening to it must hear it there.
        await Dispatcher.UIThread.InvokeAsync(() => _isActive() ? library.ImportFilesAsync(files, ct) : Task.CompletedTask);

        var wanted = new HashSet<string>(files, PathComparison.Comparer);
        var tracks = await Dispatcher.UIThread.InvokeAsync(() => library.Tracks
            .Where(t => !string.IsNullOrEmpty(t.FilePath) && wanted.Contains(Normalize(t.FilePath) ?? ""))
            .Select(PluginHost.ToTrackInfo)
            .ToList());
        DebugLogger.Info(DebugLogger.Category.State, "Plugins", $"{_pluginId} imported {tracks.Count} of {files.Count} files");
        return tracks;
    }

    /// <summary>Absolute paths of existing files Noctis plays, normalized, without repeats (at most <see cref="MaxPaths"/>).</summary>
    internal static List<string> Accept(IReadOnlyList<string> paths)
    {
        var seen = new HashSet<string>(PathComparison.Comparer);
        var result = new List<string>();
        foreach (var raw in paths.Take(MaxPaths))
        {
            if (string.IsNullOrWhiteSpace(raw) || !Path.IsPathFullyQualified(raw)) continue;
            var path = Normalize(raw);
            if (path is null || !File.Exists(path)) continue;
            if (!MetadataService.SupportedExtensions.Contains(Path.GetExtension(path))) continue;
            if (seen.Add(path)) result.Add(path);
        }
        return result;
    }

    private static string? Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return null; }
    }
}
