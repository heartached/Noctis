using System.Globalization;
using TagFile = TagLib.File;

namespace Noctis.Services;

/// <summary>ReplayGain values read from a file's tags. Null = the tag is absent or unparseable.</summary>
public readonly record struct ReplayGainValues(double? TrackGainDb, double? TrackPeak, double? AlbumGainDb)
{
    public static readonly ReplayGainValues None = default;
}

/// <summary>
/// Reads REPLAYGAIN_TRACK_GAIN / _TRACK_PEAK / _ALBUM_GAIN from an open TagLib file. One reader
/// for playback (VlcAudioPlayer.ReadReplayGainTags), the library scan and the ReplayGain
/// column's backfill (GitHub #151), so the Songs list shows the gain the player applies.
/// Tag order: ID3v2 TXXX, Xiph, MP4 freeform (com.apple.iTunes), APEv2, ASF; the first tag
/// that holds a value wins per field.
/// </summary>
public static class ReplayGainTags
{
    public static ReplayGainValues Read(TagFile file)
    {
        double? track = null, peak = null, album = null;

        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
        {
            track ??= ReadTxxx(id3, "REPLAYGAIN_TRACK_GAIN");
            peak ??= ReadTxxx(id3, "REPLAYGAIN_TRACK_PEAK");
            album ??= ReadTxxx(id3, "REPLAYGAIN_ALBUM_GAIN");
        }
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
        {
            track ??= ParseDb(xiph.GetField("REPLAYGAIN_TRACK_GAIN").FirstOrDefault());
            peak ??= ParseDb(xiph.GetField("REPLAYGAIN_TRACK_PEAK").FirstOrDefault());
            album ??= ParseDb(xiph.GetField("REPLAYGAIN_ALBUM_GAIN").FirstOrDefault());
        }
        // MP4 / M4A / ALAC / AAC: RG lives in iTunes freeform atoms.
        if (file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple)
        {
            track ??= ParseDb(apple.GetDashBox("com.apple.iTunes", "REPLAYGAIN_TRACK_GAIN"));
            peak ??= ParseDb(apple.GetDashBox("com.apple.iTunes", "REPLAYGAIN_TRACK_PEAK"));
            album ??= ParseDb(apple.GetDashBox("com.apple.iTunes", "REPLAYGAIN_ALBUM_GAIN"));
        }
        // WavPack / Monkey's Audio, and MP3s tagged by mp3gain/foobar2000: APEv2
        // (item keys are case-insensitive).
        if (file.GetTag(TagLib.TagTypes.Ape, false) is TagLib.Ape.Tag ape)
        {
            track ??= ParseDb(ape.GetItem("REPLAYGAIN_TRACK_GAIN")?.ToString());
            peak ??= ParseDb(ape.GetItem("REPLAYGAIN_TRACK_PEAK")?.ToString());
            album ??= ParseDb(ape.GetItem("REPLAYGAIN_ALBUM_GAIN")?.ToString());
        }
        // WMA: only an ASF tag, which is where the in-app scanner writes. Descriptor
        // names match case-sensitively, and foobar2000 writes them lower-case.
        if (file.GetTag(TagLib.TagTypes.Asf, false) is TagLib.Asf.Tag asf)
        {
            track ??= ParseDb(asf.GetDescriptorString("REPLAYGAIN_TRACK_GAIN", "replaygain_track_gain"));
            peak ??= ParseDb(asf.GetDescriptorString("REPLAYGAIN_TRACK_PEAK", "replaygain_track_peak"));
            album ??= ParseDb(asf.GetDescriptorString("REPLAYGAIN_ALBUM_GAIN", "replaygain_album_gain"));
        }
        return new ReplayGainValues(track, peak, album);
    }

    /// <summary>
    /// The ReplayGain tags of a file on disk, read light: the FLAC comment block alone,
    /// otherwise TagLib with no audio properties and lazy pictures. Null when the file can't be
    /// read now (try again later); <see cref="ReplayGainValues.None"/> when it has no ReplayGain
    /// tags. The column's backfill (MetadataService.ReadReplayGain) and the ReplayGain dialog's
    /// "already scanned" check (GitHub #161) both use it, so they agree on what has a value.
    /// </summary>
    public static ReplayGainValues? ReadFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || filePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return null;
        var ext = Path.GetExtension(filePath);
        // DSDIFF has no TagLib reader; ReadDsdiffTrack reads no ReplayGain either.
        if (ext.Equals(".dff", StringComparison.OrdinalIgnoreCase))
            return ReplayGainValues.None;
        if (ext.Equals(".flac", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (TryReadFlac(stream, out var flac))
                    return flac;
            }
            catch (Exception)
            {
                // Locked or vanished: let the TagLib path decide.
            }
        }
        try
        {
            using var file = TagLib.File.Create(filePath, TagLib.ReadStyle.None | TagLib.ReadStyle.PictureLazy);
            return Read(file);
        }
        catch (TagLib.CorruptFileException)
        {
            return ReplayGainValues.None; // never readable: don't retry every launch
        }
        catch (TagLib.UnsupportedFormatException)
        {
            return ReplayGainValues.None;
        }
        catch (Exception)
        {
            return null; // locked, vanished, I/O error: the next pass tries again
        }
    }

    /// <summary>
    /// The same three fields from a plain FLAC's comment block alone (<see cref="ExtendedTagIO"/>'s
    /// block walker, which skips the cover bytes TagLib# always loads). False means "ask TagLib":
    /// no 'fLaC' at offset 0, a malformed block, or a trailing APEv2/ID3v2 tag.
    /// </summary>
    internal static bool TryReadFlac(Stream stream, out ReplayGainValues values)
    {
        values = ReplayGainValues.None;
        if (!ExtendedTagIO.TryReadFlacComments(stream, out var fields)) return false;
        double? Field(string key) => fields.TryGetValue(key, out var v) ? ParseDb(v[0]) : null;
        values = new ReplayGainValues(Field("REPLAYGAIN_TRACK_GAIN"), Field("REPLAYGAIN_TRACK_PEAK"), Field("REPLAYGAIN_ALBUM_GAIN"));
        return true;
    }

    private static double? ReadTxxx(TagLib.Id3v2.Tag id3, string desc)
    {
        var frame = id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>()
            .FirstOrDefault(f => string.Equals(f.Description, desc, StringComparison.OrdinalIgnoreCase));
        return ParseDb(frame?.Text.FirstOrDefault());
    }

    /// <summary>"-7.84 dB" → -7.84; a peak ("0.988123") parses the same way. Null when blank, not a
    /// number, or not finite: a "nan dB" tag made the player's gain NaN instead of no gain.</summary>
    public static double? ParseDb(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var token = s.Trim();
        // RG values are stored like "-7.84 dB". Strip the unit.
        var spaceIdx = token.IndexOf(' ');
        if (spaceIdx > 0) token = token.Substring(0, spaceIdx);
        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
            ? d
            : null;
    }
}
