using TagLib;
using TagLib.Id3v2;
using TagLib.Mpeg4;
using TagLib.Ogg;
using TagFile = TagLib.File;

namespace Noctis.Services;

/// <summary>
/// Read/write helpers for advanced metadata fields (sort tags, people, identifiers,
/// content descriptors, technical info) and raw tag enumeration for the Advanced Details tab.
/// </summary>
internal static class AdvancedTagIO
{
    // ── Known fields that are already handled by the Details/Options/File tabs ──
    // These are excluded from the "custom tags" enumeration.
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // Standard TagLib# properties
        "TITLE", "ARTIST", "ALBUMARTIST", "ALBUM", "GENRE", "COMPOSER",
        "TRACKNUMBER", "TRACK", "TOTALTRACKS", "TRACKTOTAL",
        "DISCNUMBER", "DISC", "TOTALDISCS", "DISCTOTAL",
        "BPM", "TBPM", "DATE", "YEAR", "COMMENT", "LYRICS",
        "UNSYNCEDLYRICS", "GROUPING", "COPYRIGHT",
        // Sort fields (handled by this tab, but known)
        "TITLESORT", "ARTISTSORT", "ALBUMSORT", "ALBUMARTISTSORT", "COMPOSERSORT",
        // People fields (handled by this tab, but known)
        "CONDUCTOR", "LYRICIST", "PUBLISHER", "ORGANIZATION", "LABEL",
        "ENCODEDBY", "ENCODED-BY",
        // Identification (handled by this tab, but known)
        "ISRC", "CATALOGNUMBER", "BARCODE", "UPC",
        // Content descriptors (handled by this tab, but known)
        "ITUNESADVISORY", "LANGUAGE", "MOOD", "DESCRIPTION", "SUBTITLE",
        "RELEASEDATE", "RELEASETIME",
        // Technical (handled by this tab, but known)
        "ENCODER", "REPLAYGAIN_TRACK_GAIN", "REPLAYGAIN_TRACK_PEAK",
        "REPLAYGAIN_ALBUM_GAIN", "REPLAYGAIN_ALBUM_PEAK",
        // ExtendedTagIO custom keys
        "WORK", "MOVEMENTNAME", "MOVEMENT", "MOVEMENTTOTAL",
        "NOCTIS_SHOWCOMPOSER", "NOCTIS_USEWORKMOVEMENT",
    };

    // ── ID3v2 frame IDs that map to standard TagLib# properties (skip in enumeration) ──
    private static readonly HashSet<string> KnownId3FrameIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "TIT2", "TPE1", "TPE2", "TALB", "TCON", "TCOM", "TRCK", "TPOS",
        "TBPM", "TDRC", "TDRL", "TYER", "COMM", "USLT", "GRP1", "TIT1",
        "TCOP", "APIC", "TXXX", // TXXX handled separately
        // Sort frames
        "TSOT", "TSOP", "TSOA", "TSO2", "TSOC",
        // People
        "TPE3", "TEXT", "TPUB", "TENC", "TSSE",
        // ID
        "TSRC",
        // Content
        "TLAN", "TIT3",
    };

    /// <summary>
    /// Represents a single advanced metadata field with its current value.
    /// </summary>
    public record AdvancedFields
    {
        // ── Sorting ──
        public string TitleSort { get; set; } = string.Empty;
        public string ArtistSort { get; set; } = string.Empty;
        public string AlbumSort { get; set; } = string.Empty;
        public string AlbumArtistSort { get; set; } = string.Empty;
        public string ComposerSort { get; set; } = string.Empty;

        // ── People & Credits ──
        public string Conductor { get; set; } = string.Empty;
        public string Lyricist { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public string EncodedBy { get; set; } = string.Empty;

        // ── Identification ──
        public string Isrc { get; set; } = string.Empty;
        public string CatalogNumber { get; set; } = string.Empty;
        public string Barcode { get; set; } = string.Empty;

        // ── Content Descriptors ──
        /// <summary>0 = None, 1 = Explicit, 2 = Clean</summary>
        public int ItunesAdvisory { get; set; }
        public string Language { get; set; } = string.Empty;
        public string Mood { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ReleaseDate { get; set; } = string.Empty;

        // ── Technical (read-only) ──
        public string Encoder { get; set; } = string.Empty;
        public string ReplayGainTrackGain { get; set; } = string.Empty;
        public string ReplayGainTrackPeak { get; set; } = string.Empty;
        public string ReplayGainAlbumGain { get; set; } = string.Empty;
        public string ReplayGainAlbumPeak { get; set; } = string.Empty;

        // ── Custom tags (key-value pairs not covered above) ──
        public List<KeyValuePair<string, string>> CustomTags { get; set; } = new();
    }

    // ══════════════════════════════════════════════════════════════
    //  READ
    // ══════════════════════════════════════════════════════════════

    public static AdvancedFields ReadAll(string filePath)
    {
        using var file = TagFile.Create(filePath);
        var tag = file.Tag;
        var fields = new AdvancedFields();

        // ── Sort fields (TagLib# direct properties) ──
        fields.TitleSort = tag.TitleSort ?? string.Empty;
        fields.ArtistSort = string.Join("; ", tag.PerformersSort ?? Array.Empty<string>());
        fields.AlbumSort = tag.AlbumSort ?? string.Empty;
        fields.AlbumArtistSort = string.Join("; ", tag.AlbumArtistsSort ?? Array.Empty<string>());
        fields.ComposerSort = string.Join("; ", tag.ComposersSort ?? Array.Empty<string>());

        // ── People ──
        fields.Conductor = tag.Conductor ?? string.Empty;
        fields.Lyricist = ReadField(file, "TEXT", "LYRICIST", null) ?? string.Empty;
        fields.Publisher = ReadField(file, "TPUB", "ORGANIZATION", "©pub") ?? ReadField(file, "TPUB", "PUBLISHER", "©pub") ?? string.Empty;
        fields.EncodedBy = ReadField(file, "TENC", "ENCODEDBY", "©too") ?? ReadField(file, "TENC", "ENCODED-BY", null) ?? string.Empty;

        // ── Identification ──
        fields.Isrc = ReadField(file, "TSRC", "ISRC", null) ?? string.Empty;
        fields.CatalogNumber = ReadCustomField(file, "CATALOGNUMBER");
        fields.Barcode = ReadCustomField(file, "BARCODE");
        if (string.IsNullOrEmpty(fields.Barcode))
            fields.Barcode = ReadCustomField(file, "UPC");

        // ── Content ──
        fields.ItunesAdvisory = ReadAdvisoryValue(file);
        fields.Language = ReadField(file, "TLAN", "LANGUAGE", null) ?? string.Empty;
        fields.Mood = ReadCustomField(file, "MOOD");
        fields.Description = ReadField(file, "TIT3", "DESCRIPTION", null) ?? ReadField(file, null, "SUBTITLE", null) ?? string.Empty;
        fields.ReleaseDate = ReadCustomField(file, "RELEASETIME");
        if (string.IsNullOrEmpty(fields.ReleaseDate))
            fields.ReleaseDate = ReadCustomField(file, "RELEASEDATE");

        // ── Technical ──
        fields.Encoder = ReadField(file, "TSSE", "ENCODER", "©too") ?? string.Empty;
        fields.ReplayGainTrackGain = ReadCustomField(file, "REPLAYGAIN_TRACK_GAIN");
        fields.ReplayGainTrackPeak = ReadCustomField(file, "REPLAYGAIN_TRACK_PEAK");
        fields.ReplayGainAlbumGain = ReadCustomField(file, "REPLAYGAIN_ALBUM_GAIN");
        fields.ReplayGainAlbumPeak = ReadCustomField(file, "REPLAYGAIN_ALBUM_PEAK");

        // ── Custom tags ──
        fields.CustomTags = EnumerateCustomTags(file);

        return fields;
    }

    // ══════════════════════════════════════════════════════════════
    //  WRITE
    // ══════════════════════════════════════════════════════════════

    public static bool WriteAll(string filePath, AdvancedFields fields, AdvancedFields original)
    {
        try
        {
            using var file = TagFile.Create(filePath);
            var tag = file.Tag;

            // ── Sort fields ──
            tag.TitleSort = NullIfEmpty(fields.TitleSort);
            tag.PerformersSort = SplitSemicolon(fields.ArtistSort);
            tag.AlbumSort = NullIfEmpty(fields.AlbumSort);
            tag.AlbumArtistsSort = SplitSemicolon(fields.AlbumArtistSort);
            tag.ComposersSort = SplitSemicolon(fields.ComposerSort);

            // ── People ──
            tag.Conductor = NullIfEmpty(fields.Conductor);
            WriteField(file, "TEXT", "LYRICIST", null, fields.Lyricist);
            WritePublisher(file, fields.Publisher);
            WriteField(file, "TENC", "ENCODEDBY", "©too", fields.EncodedBy);

            // ── Identification ──
            WriteField(file, "TSRC", "ISRC", null, fields.Isrc);
            WriteCustomField(file, "CATALOGNUMBER", fields.CatalogNumber);
            WriteCustomField(file, "BARCODE", fields.Barcode);

            // ── Content ──
            WriteAdvisoryValue(file, fields.ItunesAdvisory);
            WriteField(file, "TLAN", "LANGUAGE", null, fields.Language);
            WriteCustomField(file, "MOOD", fields.Mood);
            WriteField(file, "TIT3", "DESCRIPTION", null, fields.Description);

            // ── Custom tags: remove deleted, update changed, add new ──
            var origDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in original.CustomTags) origDict[kv.Key] = kv.Value;

            var newDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in fields.CustomTags) newDict[kv.Key] = kv.Value;

            // Remove tags that were deleted
            foreach (var key in origDict.Keys)
            {
                if (!newDict.ContainsKey(key))
                    WriteCustomField(file, key, string.Empty);
            }

            // Add/update tags
            foreach (var kv in fields.CustomTags)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key))
                    WriteCustomField(file, kv.Key, kv.Value);
            }

            file.Save();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  FIELD READ HELPERS
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads a tag field across formats using the standard frame ID for ID3v2,
    /// Xiph field name, and Apple atom code.
    /// </summary>
    private static string? ReadField(TagFile file, string? id3FrameId, string? xiphKey, string? appleAtom)
    {
        // ID3v2
        if (id3FrameId != null && file.GetTag(TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
        {
            var frame = id3.GetFrames<TextInformationFrame>()
                .FirstOrDefault(f => f.FrameId == ByteVector.FromString(id3FrameId, StringType.Latin1));
            if (frame != null)
            {
                var val = frame.Text?.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
            }
        }

        // Xiph
        if (xiphKey != null && file.GetTag(TagTypes.Xiph, false) is XiphComment xiph)
        {
            var vals = xiph.GetField(xiphKey);
            var val = vals?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
        }

        // Apple
        if (appleAtom != null && file.GetTag(TagTypes.Apple, false) is AppleTag apple)
        {
            var bv = appleAtom.StartsWith("\u00A9")
                ? new ByteVector(new byte[] { 0xA9 }.Concat(System.Text.Encoding.ASCII.GetBytes(appleAtom[1..])).ToArray())
                : ByteVector.FromString(appleAtom, StringType.Latin1);
            var vals = apple.GetText(bv);
            var val = vals?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
        }

        return null;
    }

    /// <summary>Reads a TXXX / Xiph / APE / Apple dash-box custom field by key.</summary>
    private static string ReadCustomField(TagFile file, string key)
    {
        // TXXX (ID3v2) — match description case-insensitively, since writers
        // (e.g. mp3tag) may store descriptions like "iTunesAdvisory" while our
        // known keys are upper-case.
        if (file.GetTag(TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
        {
            foreach (var frame in id3.GetFrames<UserTextInformationFrame>())
            {
                if (!string.Equals(frame.Description, key, StringComparison.OrdinalIgnoreCase))
                    continue;
                var val = frame.Text?.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
            }
        }

        // Xiph
        if (file.GetTag(TagTypes.Xiph, false) is XiphComment xiph)
        {
            var vals = xiph.GetField(key);
            var val = vals?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
        }

        // APE
        if (file.GetTag(TagTypes.Ape, false) is TagLib.Ape.Tag ape)
        {
            var item = ape.GetItem(key);
            var text = item?.ToString();
            if (!string.IsNullOrWhiteSpace(text)) return text!.Trim();
        }

        // Apple dash-box
        if (file.GetTag(TagTypes.Apple, false) is AppleTag apple)
        {
            var val = apple.GetDashBox("com.apple.iTunes", key);
            if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
        }

        return string.Empty;
    }

    private static int ReadAdvisoryValue(TagFile file)
    {
        // For MP4/M4A, the canonical advisory location is the binary `rtng` atom
        // (with `rate` as an alias). Check these FIRST so reload reflects whatever
        // the writer last persisted there — the dash-box ITUNESADVISORY may exist
        // as a stale remnant from older saves.
        if (file.GetTag(TagTypes.Apple, false) is AppleTag apple)
        {
            foreach (var atom in new[] { "rtng", "rate" })
            {
                foreach (var box in apple.DataBoxes(ByteVector.FromString(atom, StringType.Latin1)))
                {
                    var data = box.Data;
                    if (data != null && data.Count > 0)
                    {
                        int n = data[0];
                        if (n == 1 || n == 4) return 1;
                        if (n == 2) return 2;
                        if (n == 0) return 0;
                    }
                    if (TryParseAdvisory(box.Text, out var textVal)) return textVal;
                }
            }

            var dashAdvisory = apple.GetDashBox("com.apple.iTunes", "ITUNESADVISORY");
            if (TryParseAdvisory(dashAdvisory, out var dashParsed)) return dashParsed;

            var extc = apple.GetDashBox("com.apple.iTunes", "iTunEXTC");
            if (!string.IsNullOrEmpty(extc))
            {
                if (extc.Contains("Explicit", StringComparison.OrdinalIgnoreCase)) return 1;
                if (extc.Contains("Clean", StringComparison.OrdinalIgnoreCase)) return 2;
            }

            return 0;
        }

        // Non-MP4 formats: TXXX / Xiph / APE custom field "ITUNESADVISORY".
        var raw = ReadCustomField(file, "ITUNESADVISORY");
        if (TryParseAdvisory(raw, out var parsed)) return parsed;

        return 0;
    }

    private static bool TryParseAdvisory(string? raw, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim().Trim('\0');
        if (int.TryParse(s, out var n))
        {
            // iTunes uses 0=None, 1=Explicit, 2=Clean; historical files use 4=Explicit.
            if (n == 1 || n == 4) { value = 1; return true; }
            if (n == 2) { value = 2; return true; }
            if (n == 0) { value = 0; return true; }
        }
        if (s.Contains("Explicit", StringComparison.OrdinalIgnoreCase)) { value = 1; return true; }
        if (s.Contains("Clean", StringComparison.OrdinalIgnoreCase)) { value = 2; return true; }
        return false;
    }

    // ══════════════════════════════════════════════════════════════
    //  FIELD WRITE HELPERS
    // ══════════════════════════════════════════════════════════════

    private static void WriteField(TagFile file, string? id3FrameId, string? xiphKey, string? appleAtom, string? value)
    {
        var clean = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        if (id3FrameId != null && file.GetTag(TagTypes.Id3v2, clean != null) is TagLib.Id3v2.Tag id3)
        {
            var frameId = ByteVector.FromString(id3FrameId, StringType.Latin1);
            var existing = id3.GetFrames<TextInformationFrame>()
                .FirstOrDefault(f => f.FrameId == frameId);
            if (clean == null)
            {
                if (existing != null) id3.RemoveFrame(existing);
            }
            else
            {
                if (existing == null)
                {
                    existing = new TextInformationFrame(frameId);
                    id3.AddFrame(existing);
                }
                existing.Text = new[] { clean };
            }
        }

        if (xiphKey != null && file.GetTag(TagTypes.Xiph, clean != null) is XiphComment xiph)
        {
            if (clean == null) xiph.RemoveField(xiphKey);
            else xiph.SetField(xiphKey, new[] { clean });
        }

        if (appleAtom != null && file.GetTag(TagTypes.Apple, clean != null) is AppleTag apple)
        {
            var bv = appleAtom.StartsWith("\u00A9")
                ? new ByteVector(new byte[] { 0xA9 }.Concat(System.Text.Encoding.ASCII.GetBytes(appleAtom[1..])).ToArray())
                : ByteVector.FromString(appleAtom, StringType.Latin1);
            apple.SetText(bv, clean);
        }
    }

    private static void WritePublisher(TagFile file, string? value)
    {
        WriteField(file, "TPUB", "ORGANIZATION", "©pub", value);
        // Also write to PUBLISHER xiph field for compatibility
        var clean = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (file.GetTag(TagTypes.Xiph, clean != null) is XiphComment xiph)
        {
            if (clean == null) xiph.RemoveField("PUBLISHER");
            else xiph.SetField("PUBLISHER", new[] { clean });
        }
    }

    private static void WriteCustomField(TagFile file, string key, string? value)
    {
        var clean = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        if (file.GetTag(TagTypes.Id3v2, clean != null) is TagLib.Id3v2.Tag id3)
        {
            if (clean == null)
            {
                var frame = UserTextInformationFrame.Get(id3, key, false);
                if (frame != null) id3.RemoveFrame(frame);
            }
            else
            {
                var frame = UserTextInformationFrame.Get(id3, key, true);
                frame.Text = new[] { clean };
            }
        }

        if (file.GetTag(TagTypes.Xiph, clean != null) is XiphComment xiph)
        {
            if (clean == null) xiph.RemoveField(key);
            else xiph.SetField(key, new[] { clean });
        }

        if (file.GetTag(TagTypes.Ape, clean != null) is TagLib.Ape.Tag ape)
        {
            if (clean == null) ape.RemoveItem(key);
            else ape.SetValue(key, clean);
        }

        if (file.GetTag(TagTypes.Apple, clean != null) is AppleTag apple)
        {
            if (clean == null) apple.SetDashBox("com.apple.iTunes", key, (string?)null);
            else apple.SetDashBox("com.apple.iTunes", key, clean);
        }
    }

    private static void WriteAdvisoryValue(TagFile file, int value)
    {
        var text = value > 0 ? value.ToString() : null;
        WriteCustomField(file, "ITUNESADVISORY", text);

        // MP4/M4A: advisory lives in the binary `rtng` atom (canonical iTunes
        // location). Mirror to `rtng` so the read path and other tools agree.
        // Also clear the `rate` alias and the `iTunEXTC` dash-box so a stale
        // value there can't override the new one on next read.
        if (file.GetTag(TagTypes.Apple, value > 0) is AppleTag apple)
        {
            var rtng = ByteVector.FromString("rtng", StringType.Latin1);
            var rate = ByteVector.FromString("rate", StringType.Latin1);
            apple.SetDashBox("com.apple.iTunes", "iTunEXTC", null);
            if (value <= 0)
            {
                apple.ClearData(rtng);
                apple.ClearData(rate);
            }
            else
            {
                // iTunes stores rtng as a single-byte AppleDataBox with flag = 21
                // (ContainsBeData). 1 = Explicit, 2 = Clean.
                var payload = new ByteVector((byte)value);
                apple.SetData(rtng, payload, 21);
                apple.ClearData(rate);
            }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  CUSTOM TAG ENUMERATION
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Enumerates all non-standard tags from the file that are not already covered
    /// by other tabs or by the known advanced fields above.
    /// </summary>
    private static List<KeyValuePair<string, string>> EnumerateCustomTags(TagFile file)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ID3v2 TXXX frames
        if (file.GetTag(TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
        {
            foreach (var frame in id3.GetFrames<UserTextInformationFrame>())
            {
                var key = frame.Description;
                if (string.IsNullOrWhiteSpace(key) || KnownKeys.Contains(key)) continue;
                var val = frame.Text?.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(val))
                    result.TryAdd(key, val.Trim());
            }
        }

        // Xiph comments — field_list is private, access via reflection
        if (file.GetTag(TagTypes.Xiph, false) is XiphComment xiph)
        {
            try
            {
                var fieldListInfo = typeof(XiphComment).GetField("field_list",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (fieldListInfo?.GetValue(xiph) is System.Collections.IDictionary dict)
                {
                    foreach (var key in dict.Keys.Cast<string>())
                    {
                        if (string.IsNullOrWhiteSpace(key) || KnownKeys.Contains(key)) continue;
                        var vals = xiph.GetField(key);
                        var val = vals?.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(val))
                            result.TryAdd(key, val.Trim());
                    }
                }
            }
            catch { /* Reflection may fail on newer TagLib# versions — non-fatal */ }
        }

        // APE tags
        if (file.GetTag(TagTypes.Ape, false) is TagLib.Ape.Tag ape)
        {
            // APE tag doesn't expose field names easily, skip enumeration
        }

        // Apple dash-boxes: no easy way to enumerate all freeform boxes in TagLib#,
        // but common ones are covered by explicit reads above.

        return result.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)).ToList();
    }

    // ══════════════════════════════════════════════════════════════
    //  UTILITY
    // ══════════════════════════════════════════════════════════════

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string[] SplitSemicolon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
        return value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Apple atom byte vector for ©pub.</summary>
    private static readonly ByteVector ApplePubAtom = new byte[] { 0xa9, (byte)'p', (byte)'u', (byte)'b' };
}
