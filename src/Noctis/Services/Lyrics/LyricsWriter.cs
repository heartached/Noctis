using System.Text;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services.Lyrics;

/// <summary>
/// The one place bulk features write lyrics for a track, following the lyrics page's own
/// rules: the synced text goes to an <c>.lrc</c> sidecar next to the file (registered as
/// app-written so Remove can take it back), both texts land on the track (store-backed),
/// and the plain text optionally reaches the file's tags through the deferred writer.
/// Removal only touches what this app created — a user's own sidecar is never deleted.
/// </summary>
public sealed class LyricsWriter
{
    private readonly IMetadataService _metadata;
    private readonly IDeferredTagWriter? _tagWriter;
    private readonly AppWrittenSidecarRegistry _registry;
    private readonly string _cacheDir;

    /// <summary>Test hook: moving a sidecar to the trash (defaults to the recycle bin helper).</summary>
    internal Func<string, bool> TrashFile { get; set; } = RecycleBin.TryMoveToTrash;

    public LyricsWriter(IMetadataService metadata, IDeferredTagWriter? tagWriter)
        : this(metadata, tagWriter, AppWrittenSidecarRegistry.Default, Path.Combine(AppPaths.DataRoot, "lyrics_cache")) { }

    internal LyricsWriter(IMetadataService metadata, IDeferredTagWriter? tagWriter, AppWrittenSidecarRegistry registry, string cacheDir)
    {
        _metadata = metadata;
        _tagWriter = tagWriter;
        _registry = registry;
        _cacheDir = cacheDir;
    }

    /// <summary>
    /// Saves lyrics for <paramref name="track"/>. Returns false when nothing was written
    /// (no text, or no file path to anchor a sidecar to).
    /// </summary>
    public bool Save(Track track, string? plain, string? synced, bool embedInTags)
    {
        var hasSynced = !string.IsNullOrWhiteSpace(synced);
        var hasPlain = !string.IsNullOrWhiteSpace(plain);
        if (!hasSynced && !hasPlain) return false;
        if (!hasPlain && hasSynced) plain = LyricsTextHelper.StripTimestamps(synced);

        track.Lyrics = plain ?? string.Empty;
        track.SyncedLyrics = synced ?? string.Empty;

        var path = track.FilePath;
        if (hasSynced && !string.IsNullOrWhiteSpace(path) && track.SourceType == SourceType.Local)
        {
            var lrcPath = Path.ChangeExtension(path, ".lrc");
            // Never overwrite a sidecar the user made themselves — only ours.
            if (!File.Exists(lrcPath) || _registry.Contains(lrcPath))
            {
                File.WriteAllText(lrcPath, Normalize(synced!), new UTF8Encoding(false));
                _registry.Add(lrcPath);
            }
        }

        if (embedInTags && !string.IsNullOrWhiteSpace(path) && track.SourceType == SourceType.Local)
        {
            if (_tagWriter is not null)
                _tagWriter.Enqueue(path, "lyrics", () => _metadata.WriteTrackMetadata(track));
            else
                Task.Run(() => { try { _metadata.WriteTrackMetadata(track); } catch { } });
        }
        return true;
    }

    /// <summary>Removes the app's own lyrics artefacts for the track and clears its fields.</summary>
    public void Remove(Track track, bool clearTags)
    {
        try
        {
            foreach (var ext in new[] { ".lrc", ".lyricsfile" })
            {
                var cachePath = Path.Combine(_cacheDir, $"{track.Id}{ext}");
                if (File.Exists(cachePath)) File.Delete(cachePath);
            }
        }
        catch { /* cache is regenerable */ }

        var path = track.FilePath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var lrcPath = Path.ChangeExtension(path, ".lrc");
            if (_registry.Contains(lrcPath))
            {
                if (!File.Exists(lrcPath) || TrashFile(lrcPath))
                    _registry.Remove(lrcPath);
                else
                    DebugLogger.Error(DebugLogger.Category.Error, "Lyrics.SidecarTrashFailed", lrcPath);
            }
        }

        track.Lyrics = string.Empty;
        track.SyncedLyrics = string.Empty;

        if (clearTags && !string.IsNullOrWhiteSpace(path) && track.SourceType == SourceType.Local)
        {
            if (_tagWriter is not null)
                _tagWriter.Enqueue(path, "lyrics", () => _metadata.WriteTrackMetadata(track));
            else
                Task.Run(() => { try { _metadata.WriteTrackMetadata(track); } catch { } });
        }
    }

    private static string Normalize(string lyrics) =>
        lyrics.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd()
              .Replace("\n", Environment.NewLine, StringComparison.Ordinal);
}
