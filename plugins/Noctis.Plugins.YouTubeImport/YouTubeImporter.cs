using System.Runtime.InteropServices;

namespace Noctis.Plugins.YouTubeImport;

/// <summary>One report of an import: the stage line and where the whole import is (0..1, see
/// <see cref="ImportPhases"/>). 0 = no number yet: the card's bar sweeps instead.</summary>
public sealed record YouTubeImportProgress(string Stage, double Fraction);

/// <summary>
/// Where each stage sits on the card's one bar, so it fills once from start to end: yt-dlp's
/// percent covers the download only (0-100, and again from 0 on a retry), the conversion reports
/// none, and tagging and the library add take a moment each. Before this the bar went 2%
/// (reading details), 5%, then the download from 5% to 80%, and a retry's
/// "Updating yt-dlp and retrying…" set it back to 5% (owner 11-27: "cleaner and smoother").
/// </summary>
public static class ImportPhases
{
    /// <summary>The download fills the bar up to here.</summary>
    public const double DownloadEnd = 0.80;
    /// <summary>ffmpeg converting (MP3, Opus, M4A from another codec).</summary>
    public const double Converting = 0.85;
    public const double Tagging = 0.90;
    public const double Adding = 0.95;

    /// <summary>yt-dlp's download percent (0-100) on the whole bar.</summary>
    public static double Download(double percent)
        => DownloadEnd * Math.Clamp(double.IsFinite(percent) ? percent : 0, 0, 100) / 100.0;
}

public sealed record YouTubeImportResult(string FilePath, string Artist, string Title, bool AddedToLibrary)
{
    /// <summary>The album the library put it in (<see cref="TrackInfo.AlbumId"/>), for "Show"; empty when unknown.</summary>
    public string AlbumId { get; init; } = "";

    /// <summary>The library song (<see cref="TrackInfo.Id"/>), for "Show" (API 1.10 ShowTrack); empty when unknown.</summary>
    public string TrackId { get; init; } = "";

    /// <summary>Nothing was downloaded: the file of an earlier import of the same video is already in the folder.</summary>
    public bool AlreadyDownloaded { get; init; }
}

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
            progress?.Report(new YouTubeImportProgress("Reading details", 0));
            try { info = await Tool.GetInfoAsync(info.Url, ct).ConfigureAwait(false) ?? info; }
            catch (OperationCanceledException) { throw; }
            catch { /* tag from what we have */ }
        }

        // An import from before the plugin kept its index of downloads (ImportState): the file it
        // made is in the folder under the name this one would get, tagged with the video's link.
        if (EarlierImport(folder, info) is { } earlier)
        {
            var (earlierArtist, earlierTitle) = YtDlpParsing.InferTags(info);
            var known = Array.Empty<TrackInfo>() as IReadOnlyList<TrackInfo>;
            try { known = await _library.ImportFilesAsync(new[] { earlier }, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log("YouTube.ImportFailed: " + ex.Message); }
            Log($"YouTube.AlreadyDownloaded: {info.Id} → {Path.GetFileName(earlier)}");
            progress?.Report(new YouTubeImportProgress("Done", 1));
            return new YouTubeImportResult(earlier, earlierArtist, earlierTitle, known.Count > 0)
            {
                AlbumId = known.Count > 0 ? known[0].AlbumId ?? "" : "",
                TrackId = known.Count > 0 ? known[0].Id ?? "" : "",
                AlreadyDownloaded = true,
            };
        }

        // No number until yt-dlp's first percent line (it reads the video's formats first).
        progress?.Report(new YouTubeImportProgress("Downloading", 0));
        // One report per (throttled) yt-dlp line, forwarded in order: percent and speed together,
        // so the row's text doesn't flip between "Downloading" and "Downloading · 4.2 MB/s".
        var downloadDetail = new InlineProgress<DownloadDetail>(d =>
        {
            var stage = d.BytesPerSec is { } speed
                ? $"Downloading · {DownloadFormat.Format(speed, d.Eta)}"
                : "Downloading";
            progress?.Report(new YouTubeImportProgress(stage, ImportPhases.Download(d.Percent)));
        });
        // A retry's status ("Updating yt-dlp and retrying…") carries no number: the bar holds
        // where it is (the card never moves it back) until the second run passes it.
        var produced = await Tool.DownloadAsync(info.Url, folder, ffmpeg, null, ct,
            status => progress?.Report(new YouTubeImportProgress(status, 0)), format, downloadDetail,
            onConverting: () => progress?.Report(new YouTubeImportProgress("Converting", ImportPhases.Converting))).ConfigureAwait(false);

        string? moved = null;
        try
        {
            progress?.Report(new YouTubeImportProgress("Tagging", ImportPhases.Tagging));
            var (artist, title) = YtDlpParsing.InferTags(info);
            var ext = Path.GetExtension(produced);
            var final = UniquePath(Path.Combine(folder, YtDlpParsing.BuildFileName(artist, title, ext)));
            File.Move(produced, final);
            moved = final;
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

            progress?.Report(new YouTubeImportProgress("Adding to library", ImportPhases.Adding));
            // In the library only when Noctis says so: before the move to a plugin, a file the
            // import skipped (a format Noctis doesn't play) still showed "In your library".
            var added = false;
            var albumId = "";
            var trackId = "";
            try
            {
                var tracks = await _library.ImportFilesAsync(new[] { final }, ct).ConfigureAwait(false);
                added = tracks.Count > 0;
                if (added)
                {
                    albumId = tracks[0].AlbumId ?? "";
                    trackId = tracks[0].Id ?? "";
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log("YouTube.ImportFailed: " + ex.Message);
            }
            progress?.Report(new YouTubeImportProgress("Done", 1));
            Log($"YouTube.Imported: {artist} - {title} → {Path.GetFileName(final)} ({format.Key}, in library: {added})");
            return new YouTubeImportResult(final, artist, title, added) { AlbumId = albumId, TrackId = trackId };
        }
        catch (OperationCanceledException)
        {
            // Stopped (Noctis closing, the plugin switched off) after the file reached the music
            // folder: it is untagged and not in the library, so it would turn up half-made on the
            // next rescan. Take it back out.
            if (moved is not null)
            {
                try { File.Delete(moved); }
                catch (Exception ex) { Log("YouTube.CancelCleanupFailed: " + ex.Message); }
            }
            YtDlpTool.CleanupScratch(produced);
            throw;
        }
        catch
        {
            YtDlpTool.CleanupScratch(produced);
            throw;
        }
    }

    /// <summary>The extensions a download can end up with (M4A, MP3, Opus; WebM when yt-dlp could not remux).</summary>
    private static readonly string[] DownloadExtensions = { ".m4a", ".mp3", ".opus", ".webm" };

    /// <summary>
    /// The file an earlier import of this video left in <paramref name="folder"/>, or null: the
    /// name this import would use (any format), whose comment tag is the video's link. A file of
    /// another video with the same name does not count (it gets "(2)" as before).
    /// </summary>
    internal static string? EarlierImport(string folder, YouTubeTrackInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Id)) return null;
        var (artist, title) = YtDlpParsing.InferTags(info);
        foreach (var ext in DownloadExtensions)
        {
            var path = Path.Combine(folder, YtDlpParsing.BuildFileName(artist, title, ext));
            if (!File.Exists(path)) continue;
            try
            {
                using var file = TagLib.File.Create(path);
                if (NamesVideo(file.Tag.Comment, info.Id)) return path;
            }
            catch { /* not readable as audio: not ours to judge */ }
        }
        return null;
    }

    /// <summary>The comment (the link the import wrote) names video <paramref name="id"/>.</summary>
    private static bool NamesVideo(string? comment, string id)
    {
        if (string.IsNullOrWhiteSpace(comment)) return false;
        var m = System.Text.RegularExpressions.Regex.Match(comment, @"(?:[?&]v=|youtu\.be/|shorts/)([A-Za-z0-9_-]+)");
        return m.Success && string.Equals(m.Groups[1].Value, id, StringComparison.Ordinal);
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
/// bundles it there), then PATH and, on macOS, Homebrew/MacPorts. The plugin runs inside the Noctis process, so
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
        // PATH, then on macOS Homebrew/MacPorts (a Finder-launched app's PATH has neither).
        return ToolLocator.Find(exe);
    }
}
