using System.Text;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services.LyricsStudio;

namespace Noctis.Services.Lyrics;

/// <summary>
/// The one place bulk features write lyrics for a track, following the lyrics page's own
/// rules: the synced text goes to an <c>.lrc</c> sidecar next to the file (registered as
/// app-written so Remove can take it back), both texts land on the track (store-backed),
/// and the plain text optionally reaches the file's tags through the deferred writer.
/// Removal only touches what this app created — a user's own sidecar is never deleted.
/// </summary>
/// <summary>What a save actually did on disk.</summary>
/// <param name="Wrote">Some text was saved (to the track and possibly a sidecar).</param>
/// <param name="SidecarWritten">The .lrc next to the file now holds the synced text.</param>
/// <param name="ReplacedForeignSidecar">A sidecar the app had not written was moved to the trash first.</param>
/// <param name="KeptForeignSidecar">A sidecar the app had not written could not be trashed, so it was left in place.</param>
public sealed record LyricsSaveOutcome(bool Wrote, bool SidecarWritten, bool ReplacedForeignSidecar, bool KeptForeignSidecar)
{
    public static readonly LyricsSaveOutcome Nothing = new(false, false, false, false);
}

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
    public bool Save(Track track, string? plain, string? synced, bool embedInTags) =>
        SaveDetailed(track, plain, synced, embedInTags, replaceForeignSidecar: false).Wrote;

    /// <summary>
    /// Saves lyrics and reports what reached the disk. Bulk features pass
    /// <paramref name="replaceForeignSidecar"/> = false so a user's own .lrc is never touched.
    /// Lyrics Studio passes true: the user picked the song, reviewed the timings and pressed
    /// Save, and the lyrics page reads the sidecar before the stored text — leaving an old
    /// line-timed .lrc in place would silently hide the word timings they just made. The old
    /// file goes to the recycle bin, not into the void; when the trash refuses it stays put.
    /// </summary>
    /// <param name="ttml">Also written as a .ttml sidecar when given (Lyrics Studio's "Also save as TTML").
    /// The lyrics page reads .lyricsfile and .ttml before .elrc and .lrc, so without one a leftover
    /// .ttml or .lyricsfile is removed like a stale .elrc — it would hide what was just saved.</param>
    public LyricsSaveOutcome SaveDetailed(Track track, string? plain, string? synced, bool embedInTags, bool replaceForeignSidecar, string? ttml = null)
    {
        var hasSynced = !string.IsNullOrWhiteSpace(synced);
        var hasPlain = !string.IsNullOrWhiteSpace(plain);
        if (!hasSynced && !hasPlain) return LyricsSaveOutcome.Nothing;
        if (!hasPlain && hasSynced) plain = LyricsTextHelper.StripTimestamps(synced);

        track.Lyrics = plain ?? string.Empty;
        track.SyncedLyrics = synced ?? string.Empty;

        var sidecarWritten = false;
        var replaced = false;
        var kept = false;
        var path = track.FilePath;
        if (hasSynced && !string.IsNullOrWhiteSpace(path) && track.SourceType == SourceType.Local)
        {
            var lrcPath = Path.ChangeExtension(path, ".lrc");
            var elrcPath = Path.ChangeExtension(path, ".elrc");
            var isWordLevel = LyricsFormatDetector.Detect(null, synced) == LyricsFormat.Elrc;
            if (isWordLevel)
            {
                // Word timings live in .elrc; the .lrc keeps a line-level projection so
                // players that only know LRC still work. ELRC is additive, never a replacement.
                WriteSidecar(elrcPath, synced!, replaceForeignSidecar, ref sidecarWritten, ref replaced, ref kept);
                WriteSidecar(lrcPath, LineLevelProjection(synced!), replaceForeignSidecar, ref sidecarWritten, ref replaced, ref kept);
            }
            else
            {
                WriteSidecar(lrcPath, synced!, replaceForeignSidecar, ref sidecarWritten, ref replaced, ref kept);
                // A leftover .elrc would out-rank the new .lrc on the lyrics page.
                foreach (var stale in SidecarsOnDisk(path, ".elrc"))
                    RemoveSidecar(stale, replaceForeignSidecar, ref replaced, ref kept);
            }

            var ttmlPath = Path.ChangeExtension(path, ".ttml");
            if (!string.IsNullOrWhiteSpace(ttml))
                WriteSidecar(ttmlPath, ttml, replaceForeignSidecar, ref sidecarWritten, ref replaced, ref kept);
            else
                foreach (var stale in SidecarsOnDisk(path, ".ttml"))
                    RemoveSidecar(stale, replaceForeignSidecar, ref replaced, ref kept);
            // LRCGET's .lyricsfile out-ranks every other sidecar: a Studio save stayed invisible under one.
            foreach (var stale in SidecarsOnDisk(path, ".lyricsfile"))
                RemoveSidecar(stale, replaceForeignSidecar, ref replaced, ref kept);
        }

        if (embedInTags && !string.IsNullOrWhiteSpace(path) && track.SourceType == SourceType.Local)
        {
            if (_tagWriter is not null)
                _tagWriter.Enqueue(path, "lyrics", () => _metadata.WriteTrackMetadata(track));
            else
                Task.Run(() => { try { _metadata.WriteTrackMetadata(track); } catch { } });
        }
        return new LyricsSaveOutcome(true, sidecarWritten, replaced, kept);
    }

    /// <summary>
    /// The lyrics files beside <paramref name="audioPath"/> that a <see cref="SaveDetailed"/> of
    /// <paramref name="synced"/> (no TTML) would replace or remove, quoted names only, in the
    /// lyrics page's read order — for the prompt before a save. A file that already holds exactly
    /// what the save writes is left out: re-saving lyrics already on disk changes nothing.
    /// </summary>
    internal static List<string> FilesChangedBySave(string audioPath, string synced)
    {
        var changed = new List<string>();
        var isWordLevel = LyricsFormatDetector.Detect(null, synced) == LyricsFormat.Elrc;
        Check(".lyricsfile", null);
        Check(".ttml", null);
        Check(".elrc", isWordLevel ? synced : null);
        Check(".lrc", isWordLevel ? LineLevelProjection(synced) : synced);
        return changed;

        void Check(string ext, string? written)
        {
            foreach (var path in SidecarsOnDisk(audioPath, ext))
            {
                try
                {
                    if (written is not null && Comparable(File.ReadAllText(path)) == Comparable(written)) continue;
                }
                catch { /* unreadable: say it will be replaced */ }
                changed.Add($"“{Path.GetFileName(path)}”");
            }
        }

        static string Comparable(string text) =>
            text.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd();
    }

    /// <summary>
    /// The files beside <paramref name="audioPath"/> named like it with extension
    /// <paramref name="ext"/> in any letter case, as they are on disk. The lyrics page also reads
    /// ".TTML", ".Elrc" and the like; on a case-sensitive file system (Linux) a save that only
    /// looked for the lower-case name left an upper-case one standing to hide it, and on Windows
    /// the prompt named "Song.ttml" for a file called "Song.TTML".
    /// </summary>
    internal static List<string> SidecarsOnDisk(string audioPath, string ext)
    {
        var found = new List<string>();
        try
        {
            var dir = Path.GetDirectoryName(audioPath);
            var stem = Path.GetFileNameWithoutExtension(audioPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem) || !Directory.Exists(dir)) return found;
            foreach (var file in Directory.EnumerateFiles(dir, stem + ".*"))
                if (string.Equals(Path.GetFileNameWithoutExtension(file), stem, StringComparison.Ordinal)
                    && string.Equals(Path.GetExtension(file), ext, StringComparison.OrdinalIgnoreCase))
                    found.Add(file);
        }
        catch { /* folder unreadable: nothing found */ }
        return found;
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
            foreach (var ext in new[] { ".ttml", ".elrc", ".lrc" })
            {
                var sidecar = Path.ChangeExtension(path, ext);
                if (!_registry.Contains(sidecar)) continue;
                if (!File.Exists(sidecar) || TrashFile(sidecar))
                    _registry.Remove(sidecar);
                else
                    DebugLogger.Error(DebugLogger.Category.Error, "Lyrics.SidecarTrashFailed", sidecar);
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

    /// <summary>Writes one sidecar unless a foreign file sits there and we were not asked to replace it (trash first when we were).</summary>
    private void WriteSidecar(string sidecarPath, string content, bool replaceForeign, ref bool written, ref bool replaced, ref bool kept)
    {
        var foreign = File.Exists(sidecarPath) && !_registry.Contains(sidecarPath);
        if (foreign)
        {
            if (!replaceForeign) return;
            // The user's own file is only overwritten once it is safely in the trash. If the
            // trash refuses (e.g. macOS Finder automation denied or timed out), keep it.
            if (!TrashFile(sidecarPath) && File.Exists(sidecarPath))
            {
                DebugLogger.Warn(DebugLogger.Category.Lyrics, "Lyrics.SidecarTrashFailed", sidecarPath);
                kept = true;
                return;
            }
            replaced = true;
        }
        File.WriteAllText(sidecarPath, Normalize(content), new UTF8Encoding(false));
        _registry.Add(sidecarPath);
        written = true;
    }

    /// <summary>Trashes our own sidecar at the path, or a foreign one when asked; leaves a foreign one alone otherwise.</summary>
    private void RemoveSidecar(string sidecarPath, bool replaceForeign, ref bool replaced, ref bool kept)
    {
        if (!File.Exists(sidecarPath)) return;
        var ours = _registry.Contains(sidecarPath);
        if (!ours && !replaceForeign) return;
        if (TrashFile(sidecarPath) || !File.Exists(sidecarPath))
        {
            _registry.Remove(sidecarPath);
            if (!ours) replaced = true;
        }
        else
        {
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "Lyrics.SidecarTrashFailed", sidecarPath);
            if (!ours) kept = true;
        }
    }

    /// <summary>Enhanced LRC → plain LRC: inline word tags stripped, line stamps and header tags kept.</summary>
    internal static string LineLevelProjection(string elrc)
    {
        var lines = elrc.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var stripped = EnhancedLrcParser.StripWordTags(lines[i]);
            while (stripped.Contains("  ", StringComparison.Ordinal)) stripped = stripped.Replace("  ", " ", StringComparison.Ordinal);
            lines[i] = stripped.TrimEnd();
        }
        return string.Join('\n', lines);
    }

    private static string Normalize(string lyrics) =>
        lyrics.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd()
              .Replace("\n", Environment.NewLine, StringComparison.Ordinal);
}
