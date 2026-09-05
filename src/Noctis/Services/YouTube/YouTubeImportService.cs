using Noctis.Models;

namespace Noctis.Services.YouTube;

public sealed record YouTubeImportProgress(string Stage, double Fraction);

public sealed record YouTubeImportResult(string FilePath, string Artist, string Title, bool AddedToLibrary);

public interface IYouTubeImportService
{
    YtDlpTool Tool { get; }

    /// <summary>Destination folder for downloads (may not exist yet); empty when no music folder is configured.</summary>
    string ResolveDownloadFolder();

    Task<List<YouTubeTrackInfo>> SearchAsync(string query, CancellationToken ct);

    /// <summary>Full metadata for a pasted link or a search result (search entries are shallow).</summary>
    Task<YouTubeTrackInfo?> ResolveAsync(string urlOrId, CancellationToken ct);

    /// <summary>Download → tag → move into the library folder → import. Returns the library file.</summary>
    Task<YouTubeImportResult> ImportAsync(YouTubeTrackInfo info, IProgress<YouTubeImportProgress>? progress, CancellationToken ct);
}

/// <summary>
/// Turns a YouTube video into a properly tagged file in the user's own library folder. No
/// streaming section, no YouTube browsing inside the app — the result is a normal local
/// track like any other import, with title/artist/album/year/cover written into the file.
/// </summary>
public sealed class YouTubeImportService : IYouTubeImportService
{
    private readonly IAudioConverterService _ffmpeg;
    private readonly IMetadataService _metadata;
    private readonly ILibraryService _library;
    private readonly HttpClient _http;
    private readonly Func<AppSettings> _settings;

    public YtDlpTool Tool { get; }

    public YouTubeImportService(YtDlpTool tool, IAudioConverterService ffmpeg, IMetadataService metadata, ILibraryService library, HttpClient http, Func<AppSettings> settings)
    {
        Tool = tool;
        _ffmpeg = ffmpeg;
        _metadata = metadata;
        _library = library;
        _http = http;
        _settings = settings;
    }

    public string ResolveDownloadFolder()
    {
        AppSettings s;
        try { s = _settings(); } catch { return string.Empty; }
        if (!string.IsNullOrWhiteSpace(s.YouTubeDownloadFolder)) return s.YouTubeDownloadFolder.Trim();
        var first = s.MusicFolders.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f));
        return string.IsNullOrWhiteSpace(first) ? string.Empty : Path.Combine(first.Trim(), "YouTube");
    }

    public Task<List<YouTubeTrackInfo>> SearchAsync(string query, CancellationToken ct) => Tool.SearchAsync(query, 12, ct);

    public Task<YouTubeTrackInfo?> ResolveAsync(string urlOrId, CancellationToken ct)
    {
        var url = YtDlpParsing.LooksLikeYouTubeUrl(urlOrId) ? urlOrId.Trim() : YtDlpParsing.WatchUrl(urlOrId.Trim());
        return Tool.GetInfoAsync(url, ct);
    }

    public async Task<YouTubeImportResult> ImportAsync(YouTubeTrackInfo info, IProgress<YouTubeImportProgress>? progress, CancellationToken ct)
    {
        var folder = ResolveDownloadFolder();
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException("Add a music folder first (Settings → Library), or choose a download folder.");
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
        var downloadProgress = new Progress<double>(f => progress?.Report(new YouTubeImportProgress("Downloading", 0.05 + 0.75 * f)));
        var produced = await Tool.DownloadAsync(info.Url, folder, _ffmpeg.GetFfmpegPath(), downloadProgress, ct).ConfigureAwait(false);

        try
        {
            progress?.Report(new YouTubeImportProgress("Tagging", 0.85));
            var (artist, title) = YtDlpParsing.InferTags(info);
            var ext = Path.GetExtension(produced);
            var final = UniquePath(Path.Combine(folder, YtDlpParsing.BuildFileName(artist, title, ext)));
            File.Move(produced, final);
            YtDlpTool.CleanupScratch(produced);

            var tags = new Track
            {
                FilePath = final,
                Title = title,
                Artist = artist,
                AlbumArtist = artist,
                Album = string.IsNullOrWhiteSpace(info.Album) ? title : info.Album!,
                Year = info.Year ?? 0,
                Comment = info.Url,
                TrackNumber = 0,
                DiscNumber = 0,
            };
            await Task.Run(() =>
            {
                try { _metadata.WriteTrackMetadata(tags, final); }
                catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.State, "YouTube.TagFailed", ex.Message); }
            }, ct).ConfigureAwait(false);

            var cover = await FetchCoverAsync(info, ct).ConfigureAwait(false);
            if (cover is not null)
            {
                await Task.Run(() =>
                {
                    try { _metadata.WriteAlbumArt(final, cover); }
                    catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.State, "YouTube.CoverFailed", ex.Message); }
                }, ct).ConfigureAwait(false);
            }

            progress?.Report(new YouTubeImportProgress("Adding to library", 0.95));
            var added = false;
            try
            {
                await _library.ImportFilesAsync(new[] { final }, ct).ConfigureAwait(false);
                added = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                DebugLogger.Warn(DebugLogger.Category.State, "YouTube.ImportFailed", ex.Message);
            }
            progress?.Report(new YouTubeImportProgress("Done", 1));
            DebugLogger.Info(DebugLogger.Category.State, "YouTube.Imported", $"{artist} - {title} → {Path.GetFileName(final)}");
            return new YouTubeImportResult(final, artist, title, added);
        }
        catch
        {
            YtDlpTool.CleanupScratch(produced);
            throw;
        }
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
                var bytes = await HttpSafety.ReadBytesBoundedAsync(response.Content, HttpSafety.MaxImageBytes, ct).ConfigureAwait(false);
                if (bytes.Length > 1024 && HttpSafety.LooksLikeImage(bytes)) return bytes;
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
