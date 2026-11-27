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
/// <param name="TagsQueued">The lyrics are on their way into the audio file's tags ("Save lyrics to" includes the file, GitHub #135).
/// The write itself waits until the song is not playing, so its outcome is not known here.</param>
public sealed record LyricsSaveOutcome(bool Wrote, bool SidecarWritten, bool ReplacedForeignSidecar, bool KeptForeignSidecar, bool TagsQueued = false)
{
    public static readonly LyricsSaveOutcome Nothing = new(false, false, false, false);

    /// <summary>The lyrics reached a file or are queued for the audio file's tags.</summary>
    public bool Saved => SidecarWritten || TagsQueued;
}

public sealed class LyricsWriter
{
    private readonly IMetadataService _metadata;
    private readonly IDeferredTagWriter? _tagWriter;
    private readonly AppWrittenSidecarRegistry _registry;
    private readonly string _cacheDir;

    /// <summary>Test hook: moving a sidecar to the trash (defaults to the recycle bin helper).</summary>
    internal Func<string, bool> TrashFile { get; set; } = RecycleBin.TryMoveToTrash;

    /// <summary>Where saves go when the caller does not say: Settings ▸ Lyrics ▸ Save lyrics to (GitHub #135).</summary>
    public Func<LyricsSaveTarget> SaveTarget { get; set; } = LyricsSaveSettings.CurrentTarget;

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
    /// <param name="target">Where the lyrics go; null = <see cref="SaveTarget"/>. "Inside the audio file" writes the
    /// tags (synced text with its word timings, plus SYLT for ID3v2) and no .lrc/.elrc; the files the lyrics page
    /// reads before the tags are removed like stale ones, or they would hide the save. An explicit TTML is still
    /// written: it is a file the user asked for. <paramref name="embedInTags"/> (Lyrics Studio / bulk "also
    /// embed") keeps its old meaning for lyrics files only: plain text in the tags.</param>
    /// <param name="files">Word-timed lyrics: which lyrics files (Lyrics Studio's choice). ELRC only removes an old
    /// .lrc; LRC only saves the line timings alone (word timings dropped, in the tags too) and removes an old .elrc,
    /// which the lyrics page reads first.</param>
    public LyricsSaveOutcome SaveDetailed(Track track, string? plain, string? synced, bool embedInTags, bool replaceForeignSidecar, string? ttml = null,
        LyricsSaveTarget? target = null, LyricsStudioFiles files = LyricsStudioFiles.ElrcAndLrc)
    {
        if (files == LyricsStudioFiles.LrcOnly && !string.IsNullOrWhiteSpace(synced)
            && LyricsFormatDetector.Detect(null, synced) == LyricsFormat.Elrc)
            synced = LineLevelProjection(synced);
        var hasSynced = !string.IsNullOrWhiteSpace(synced);
        var hasPlain = !string.IsNullOrWhiteSpace(plain);
        if (!hasSynced && !hasPlain) return LyricsSaveOutcome.Nothing;
        if (!hasPlain && hasSynced) plain = LyricsTextHelper.StripTimestamps(synced);
        var mode = target ?? SafeTarget();

        track.Lyrics = plain ?? string.Empty;
        track.SyncedLyrics = synced ?? string.Empty;

        var sidecarWritten = false;
        var replaced = false;
        var kept = false;
        var path = track.FilePath;
        var isLocal = !string.IsNullOrWhiteSpace(path) && track.SourceType == SourceType.Local;
        if (hasSynced && isLocal && !mode.WritesSidecars())
        {
            // Inside the audio file only: every lyrics file the page reads before the tags would hide them.
            if (!string.IsNullOrWhiteSpace(ttml))
                WriteSidecar(Path.ChangeExtension(path, ".ttml"), ttml, replaceForeignSidecar, ref sidecarWritten, ref replaced, ref kept);
            foreach (var ext in new[] { ".lyricsfile", ".ttml", ".elrc", ".lrc" })
            {
                if (ext == ".ttml" && !string.IsNullOrWhiteSpace(ttml)) continue;
                foreach (var stale in SidecarsOnDisk(path, ext))
                    RemoveSidecar(stale, replaceForeignSidecar, ref replaced, ref kept);
            }
        }
        else if (hasSynced && isLocal)
        {
            var lrcPath = Path.ChangeExtension(path, ".lrc");
            var elrcPath = Path.ChangeExtension(path, ".elrc");
            var isWordLevel = LyricsFormatDetector.Detect(null, synced) == LyricsFormat.Elrc;
            if (isWordLevel)
            {
                // Word timings live in .elrc; the .lrc keeps a line-level projection so
                // players that only know LRC still work. ELRC is additive, never a replacement.
                WriteSidecar(elrcPath, synced!, replaceForeignSidecar, ref sidecarWritten, ref replaced, ref kept);
                if (files != LyricsStudioFiles.ElrcOnly)
                    WriteSidecar(lrcPath, LineLevelProjection(synced!), replaceForeignSidecar, ref sidecarWritten, ref replaced, ref kept);
                else
                    foreach (var stale in SidecarsOnDisk(path, ".lrc"))
                        RemoveSidecar(stale, replaceForeignSidecar, ref replaced, ref kept);
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

        var tagsQueued = false;
        if (isLocal && mode.WritesTags())
        {
            var (tagPlain, tagSynced) = (plain, synced);
            QueueTagWrite(path, () => _metadata.WriteLyrics(path, tagPlain, tagSynced));
            tagsQueued = true;
        }
        else if (embedInTags && isLocal)
        {
            QueueTagWrite(path, () => _metadata.WriteTrackMetadata(track));
        }
        return new LyricsSaveOutcome(true, sidecarWritten, replaced, kept, tagsQueued);
    }

    private LyricsSaveTarget SafeTarget()
    {
        try { return SaveTarget(); }
        catch { return LyricsSaveTarget.Sidecar; }
    }

    /// <summary>Tag writes wait (deferred writer) until the file is not the one playing; one per file, the latest wins.</summary>
    private void QueueTagWrite(string path, Func<bool> write)
    {
        if (_tagWriter is not null)
            _tagWriter.Enqueue(path, "lyrics", () => write());
        else
            Task.Run(() => { try { write(); } catch { } });
    }

    /// <summary>
    /// The lyrics files beside <paramref name="audioPath"/> that a <see cref="SaveDetailed"/> of
    /// <paramref name="synced"/> (and <paramref name="ttml"/>, when given) would replace or remove,
    /// quoted names only, in the lyrics page's read order — for the prompt before a save. A file
    /// that already holds exactly what the save writes is left out: re-saving lyrics already on
    /// disk changes nothing.
    /// </summary>
    internal static List<string> FilesChangedBySave(string audioPath, string synced, string? ttml = null,
        LyricsSaveTarget target = LyricsSaveTarget.Sidecar)
    {
        var changed = new List<string>();
        var isWordLevel = LyricsFormatDetector.Detect(null, synced) == LyricsFormat.Elrc;
        var files = target.WritesSidecars();
        Check(".lyricsfile", null);
        Check(".ttml", string.IsNullOrWhiteSpace(ttml) ? null : ttml);
        // Inside the audio file only: the .elrc/.lrc go, whatever they hold.
        Check(".elrc", files && isWordLevel ? synced : null);
        Check(".lrc", !files ? null : isWordLevel ? LineLevelProjection(synced) : synced);
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
    }

    /// <summary>Text as a sidecar comparison sees it: no BOM, LF line ends, no trailing blank space.</summary>
    private static string Comparable(string text) =>
        text.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd();

    /// <summary>The file at <paramref name="path"/> already holds <paramref name="content"/> (as <see cref="Comparable"/> sees it).</summary>
    private static bool HoldsAlready(string path, string content)
    {
        try { return Comparable(File.ReadAllText(path)) == Comparable(content); }
        catch { return false; }
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
            // Every lyrics field: clearing only the main one left SYLT / UNSYNCEDLYRICS to bring them back.
            QueueTagWrite(path, () => _metadata.WriteLyrics(path, null, null));
    }

    /// <summary>Writes one sidecar unless a foreign file sits there and we were not asked to replace it (trash first when we were).</summary>
    private void WriteSidecar(string sidecarPath, string content, bool replaceForeign, ref bool written, ref bool replaced, ref bool kept)
    {
        var foreign = File.Exists(sidecarPath) && !_registry.Contains(sidecarPath);
        // The user's own file already says exactly this: leave it be. The prompt before a save
        // leaves such a file out (FilesChangedBySave), so trashing it here went unannounced —
        // Save ▸ TTML of the song's own .lrc sent that same .lrc to the Recycle Bin.
        if (foreign && HoldsAlready(sidecarPath, content))
        {
            written = true;
            return;
        }
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
        WriteAllTextAtomic(sidecarPath, Normalize(content));
        _registry.Add(sidecarPath);
        written = true;
    }

    /// <summary>
    /// Writes a temp file beside <paramref name="path"/> and swaps it in, so a crash, a full disk or a
    /// cut power mid-write never leaves a half-written lyrics file (written in place, it did). An
    /// existing file is replaced under its own on-disk name ("Song.LRC" stays "Song.LRC", as the
    /// in-place write kept it). Where the swap is refused (some network shares), it writes in place.
    /// </summary>
    internal static void WriteAllTextAtomic(string path, string text)
    {
        var encoding = new UTF8Encoding(false);
        var tmp = path + ".noctis-tmp";
        try
        {
            File.WriteAllText(tmp, text, encoding);
            if (OnDiskName(path) is { } existing) File.Replace(tmp, existing, null, ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            File.WriteAllText(path, text, encoding);
        }
    }

    /// <summary>The existing file at <paramref name="path"/> as the file system spells it, or null when there is none.</summary>
    private static string? OnDiskName(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)
                && Directory.EnumerateFiles(dir, Path.GetFileName(path)).FirstOrDefault() is { } spelled
                && string.Equals(spelled, path, StringComparison.OrdinalIgnoreCase))
                return spelled;
        }
        catch { /* unlistable: the path as given */ }
        return path;
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
