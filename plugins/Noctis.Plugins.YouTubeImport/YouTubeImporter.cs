using System.Runtime.InteropServices;

namespace Noctis.Plugins.YouTubeImport;

public sealed record YouTubeImportProgress(string Stage, double Fraction);

public sealed record YouTubeImportResult(string FilePath, string Artist, string Title, bool AddedToLibrary);

public interface IYouTubeImporter
{
    YtDlpTool Tool { get; }

    /// <summary>Destination folder for downloads (may not exist yet); empty when no music folder is configured.</summary>
    string ResolveDownloadFolder();

    /// <summary>True when ffmpeg was found, so the converted formats (MP3, Opus) can be offered.</summary>
    bool HasFfmpeg { get; }

    Task<List<YouTubeTrackInfo>> SearchAsync(string query, CancellationToken ct);

    /// <summary>Full metadata for a pasted link or a search result (search entries are shallow).</summary>
    Task<YouTubeTrackInfo?> ResolveAsync(string urlOrId, CancellationToken ct);

    /// <summary>Download → tag → move into the download folder → add to the library. Returns the file.</summary>
    Task<YouTubeImportResult> ImportAsync(YouTubeTrackInfo info, AudioFormat format, IProgress<YouTubeImportProgress>? progress, CancellationToken ct);
}

/// <summary>
/// Turns a YouTube video into a properly tagged file in the user's own library folder. No
/// streaming section, no YouTube browsing inside the app — the result is a normal local
/// track like any other import, with title/artist/album/year/cover written into the file.
/// Moved from the app (Services/YouTube/YouTubeImportService) into the plugin on 10-29: the
/// library import goes through plugin API 1.5's "library.import".
/// </summary>
public sealed class YouTubeImporter : IYouTubeImporter
{
    private readonly ILibraryImport _library;
    private readonly HttpClient _http;
    private readonly Func<string> _customFolder;
    private readonly Func<string?> _ffmpegPath;
    private readonly Action<string>? _log;

    public YtDlpTool Tool { get; }

    /// <param name="customFolder">The folder the user chose in the window, or empty for the default.</param>
    /// <param name="ffmpegPath">ffmpeg for conversions, or null (<see cref="FfmpegLocator.Find"/>).</param>
    public YouTubeImporter(YtDlpTool tool, ILibraryImport library, HttpClient http, Func<string> customFolder, Func<string?> ffmpegPath, Action<string>? log = null)
    {
        Tool = tool;
        _library = library;
        _http = http;
        _customFolder = customFolder;
        _ffmpegPath = ffmpegPath;
        _log = log;
    }

    public bool HasFfmpeg => SafeFfmpeg() is not null;

    private string? SafeFfmpeg()
    {
        try { return _ffmpegPath(); } catch { return null; }
    }

    public string ResolveDownloadFolder()
    {
        string custom;
        try { custom = _customFolder() ?? string.Empty; } catch { custom = string.Empty; }
        if (!string.IsNullOrWhiteSpace(custom)) return custom.Trim();
        IReadOnlyList<string> folders;
        try { folders = _library.MusicFolders; } catch { return string.Empty; }
        return DefaultDownloadFolder(folders);
    }

    /// <summary>
    /// The download folder when none is set: "YouTube" inside the deepest folder that holds
    /// every music folder. Owner 10-08: with one music folder per artist (B:\ALAC\Taylor Swift,
    /// B:\ALAC\Drake, …) the old "inside the first music folder" put downloads in
    /// B:\ALAC\Taylor Swift\YouTube; now B:\ALAC\YouTube. A shared folder that is only a drive
    /// root (B:\), or none at all (C:\Music + D:\Songs), keeps the old rule rather than writing
    /// to a drive's top level. Files there stay in the library across rescans even outside the
    /// music folders: the import marks them added individually (LibraryService, GitHub #108).
    /// </summary>
    internal static string DefaultDownloadFolder(IEnumerable<string> musicFolders)
    {
        var roots = musicFolders.Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim().TrimEnd('\\', '/'))
            .Where(f => f.Length > 0)
            .ToList();
        if (roots.Count == 0) return string.Empty;
        var fallback = Path.Combine(roots[0], "YouTube");

        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        static string[] Split(string p) => p.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var common = Split(roots[0]);
        var shared = common.Length;
        foreach (var root in roots.Skip(1))
        {
            var parts = Split(root);
            var n = 0;
            while (n < shared && n < parts.Length && string.Equals(common[n], parts[n], cmp)) n++;
            shared = n;
        }
        // One segment is only the drive ("B:") or, on Unix, a top-level folder: too broad.
        if (shared < 2) return fallback;

        // Keep what the split dropped: a Unix root "/" or a network share's "\\".
        var prefix = roots[0].StartsWith(@"\\") ? @"\\" : roots[0].StartsWith('/') ? "/" : string.Empty;
        var sep = roots[0].Contains('\\') ? "\\" : "/";
        return prefix + string.Join(sep, common.Take(shared)) + sep + "YouTube";
    }

    public Task<List<YouTubeTrackInfo>> SearchAsync(string query, CancellationToken ct) => Tool.SearchAsync(query, 12, ct);

    public Task<YouTubeTrackInfo?> ResolveAsync(string urlOrId, CancellationToken ct)
    {
        var url = YtDlpParsing.LooksLikeYouTubeUrl(urlOrId) ? urlOrId.Trim() : YtDlpParsing.WatchUrl(urlOrId.Trim());
        return Tool.GetInfoAsync(url, ct);
    }

    public async Task<YouTubeImportResult> ImportAsync(YouTubeTrackInfo info, AudioFormat format, IProgress<YouTubeImportProgress>? progress, CancellationToken ct)
    {
        var folder = ResolveDownloadFolder();
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException("Add a music folder first (Settings → Library), or choose a download folder.");
        var ffmpeg = SafeFfmpeg();
        if (format.NeedsFfmpeg && ffmpeg is null)
            throw new InvalidOperationException($"{format.Label} needs ffmpeg, which Noctis could not find. Choose M4A, or put ffmpeg next to Noctis or on PATH.");
        Directory.CreateDirectory(folder);

        // Search entries are shallow (no track/artist/album): fetch the full record first.
        if (info.Track is null && info.Artist is null && info.Album is null)
        {
            progress?.Report(new YouTubeImportProgress("Reading details", 0.02));
            try { info = await Tool.GetInfoAsync(info.Url, ct).ConfigureAwait(false) ?? info; }
            catch (OperationCanceledException) { throw; }
            catch { /* tag from what we have */ }
        }

        progress?.Report(new YouTubeImportProgress("Downloading", 0.05));
        // One report per (throttled) yt-dlp line, forwarded in order: percent and speed together,
        // so the row's text doesn't flip between "Downloading" and "Downloading · 4.2 MB/s".
        var downloadDetail = new InlineProgress<DownloadDetail>(d =>
        {
            var stage = d.BytesPerSec is { } speed
                ? $"Downloading · {DownloadFormat.Format(speed, d.Eta)}"
                : "Downloading";
            progress?.Report(new YouTubeImportProgress(stage, 0.05 + 0.75 * d.Percent / 100.0));
        });
        var produced = await Tool.DownloadAsync(info.Url, folder, ffmpeg, null, ct,
            status => progress?.Report(new YouTubeImportProgress(status, 0.05)), format, downloadDetail).ConfigureAwait(false);

        try
        {
            progress?.Report(new YouTubeImportProgress("Tagging", 0.85));
            var (artist, title) = YtDlpParsing.InferTags(info);
            var ext = Path.GetExtension(produced);
            var final = UniquePath(Path.Combine(folder, YtDlpParsing.BuildFileName(artist, title, ext)));
            File.Move(produced, final);
            YtDlpTool.CleanupScratch(produced);

            var album = string.IsNullOrWhiteSpace(info.Album) ? title : info.Album!;
            await Task.Run(() =>
            {
                try { TagWriter.WriteTags(final, title, artist, album, info.Year ?? 0, info.Url); }
                catch (Exception ex) { Log("YouTube.TagFailed: " + ex.Message); }
            }, ct).ConfigureAwait(false);

            var cover = await FetchCoverAsync(info, ct).ConfigureAwait(false);
            if (cover is not null)
            {
                await Task.Run(() =>
                {
                    try { TagWriter.WriteCover(final, cover); }
                    catch (Exception ex) { Log("YouTube.CoverFailed: " + ex.Message); }
                }, ct).ConfigureAwait(false);
            }

            progress?.Report(new YouTubeImportProgress("Adding to library", 0.95));
            // In the library only when Noctis says so: before the move to a plugin, a file the
            // import skipped (a format Noctis doesn't play) still showed "In your library".
            var added = false;
            try
            {
                var tracks = await _library.ImportFilesAsync(new[] { final }, ct).ConfigureAwait(false);
                added = tracks.Count > 0;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log("YouTube.ImportFailed: " + ex.Message);
            }
            progress?.Report(new YouTubeImportProgress("Done", 1));
            Log($"YouTube.Imported: {artist} - {title} → {Path.GetFileName(final)} ({format.Key}, in library: {added})");
            return new YouTubeImportResult(final, artist, title, added);
        }
        catch
        {
            YtDlpTool.CleanupScratch(produced);
            throw;
        }
    }

    private void Log(string line)
    {
        try { _log?.Invoke(line); } catch { }
    }

    private async Task<byte[]?> FetchCoverAsync(YouTubeTrackInfo info, CancellationToken ct)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.ThumbnailUrl)) candidates.Add(info.ThumbnailUrl!);
        candidates.Add($"https://i.ytimg.com/vi/{info.Id}/maxresdefault.jpg");
        candidates.Add($"https://i.ytimg.com/vi/{info.Id}/hqdefault.jpg");
        foreach (var url in candidates.Distinct())
        {
            try
            {
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                var bytes = await HttpBounded.ReadBytesAsync(response.Content, HttpBounded.MaxImageBytes, ct).ConfigureAwait(false);
                if (bytes.Length > 1024 && HttpBounded.LooksLikeImage(bytes)) return bytes;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* next candidate */ }
        }
        return null;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; n < 1000; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem} {Guid.NewGuid():N}{ext}");
    }
}

/// <summary>
/// Finds ffmpeg the way Noctis does when no custom path is set: next to the app (the installer
/// bundles it there), then PATH. The plugin runs inside the Noctis process, so
/// <see cref="AppContext.BaseDirectory"/> is the app's folder.
/// </summary>
public static class FfmpegLocator
{
    public static string? Find()
    {
        var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
        try
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, exe);
            if (File.Exists(bundled)) return bundled;
        }
        catch { }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* invalid PATH segment */ }
        }
        return null;
    }
}
