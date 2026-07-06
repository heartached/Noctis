using Noctis.Models;
using TagLib;
using TagLib.Id3v2;
using TagLib.Mpeg4;
using TagLib.Ogg;
using TagFile = TagLib.File;

namespace Noctis.Services;

/// <summary>
/// Read/write helpers for tag fields that TagLib# does not expose on the base <see cref="Tag"/> class:
/// IsCompilation, Apple Work/Movement, and the "show work & movement" / "show composer" display flags.
///
/// Storage strategy per container:
///   - MP4 (Apple):    native atoms — cpil, ©wrk, ©mvn, ©mvi, ©mvc, shwm
///   - ID3v2 (MP3):    TCMP frame for compilation; TXXX custom frames for work/movement/show-flags
///   - Xiph (FLAC/Ogg): native IsCompilation; SetField for custom string fields
/// </summary>
internal static class ExtendedTagIO
{
    // ── Custom field keys (shared across non-MP4 containers) ──
    private const string WorkKey = "WORK";
    private const string MovementNameKey = "MOVEMENTNAME";
    private const string MovementNumberKey = "MOVEMENT";
    private const string MovementTotalKey = "MOVEMENTTOTAL";
    private const string ShowComposerKey = "NOCTIS_SHOWCOMPOSER";
    private const string UseWorkMovementKey = "NOCTIS_USEWORKMOVEMENT";

    // Release type tags. RELEASETYPE is the primary key used by mp3tag/foobar2000.
    // MUSICBRAINZ_ALBUM_TYPE is what MusicBrainz Picard writes (often multi-valued,
    // e.g. "album; soundtrack" — we pick the most specific token).
    private const string ReleaseTypeKey = "RELEASETYPE";
    private const string MusicBrainzAlbumTypeKey = "MUSICBRAINZ_ALBUM_TYPE";
    private const string NoctisReleaseTypeOverrideKey = "NOCTIS_RELEASETYPE";

    // Apple MP4 atom names (4 chars including the © sign).
    private static readonly ByteVector AppleWorkAtom = new byte[] { 0xa9, (byte)'w', (byte)'r', (byte)'k' };
    private static readonly ByteVector AppleMovementNameAtom = new byte[] { 0xa9, (byte)'m', (byte)'v', (byte)'n' };
    private static readonly ByteVector AppleMovementIndexAtom = new byte[] { 0xa9, (byte)'m', (byte)'v', (byte)'i' };
    private static readonly ByteVector AppleMovementCountAtom = new byte[] { 0xa9, (byte)'m', (byte)'v', (byte)'c' };
    private static readonly ByteVector AppleShowWorkAtom = "shwm";

    // ── IsCompilation ──

    public static bool ReadIsCompilation(TagFile file)
    {
        if (file.GetTag(TagTypes.Apple, false) is AppleTag apple)
            return apple.IsCompilation;
        if (file.GetTag(TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
            return id3.IsCompilation;
        if (file.GetTag(TagTypes.Xiph, false) is XiphComment xiph)
            return xiph.IsCompilation;
        return false;
    }

    public static void WriteIsCompilation(TagFile file, bool value)
    {
        if (file.GetTag(TagTypes.Apple, true) is AppleTag apple)
            apple.IsCompilation = value;
        if (file.GetTag(TagTypes.Id3v2, true) is TagLib.Id3v2.Tag id3)
            id3.IsCompilation = value;
        if (file.GetTag(TagTypes.Xiph, true) is XiphComment xiph)
            xiph.IsCompilation = value;
    }

    // ── Work name ──

    public static string ReadWorkName(TagFile file)
    {
        if (file.GetTag(TagTypes.Apple, false) is AppleTag apple)
        {
            var values = apple.GetText(AppleWorkAtom);
            if (values is { Length: > 0 } && !string.IsNullOrEmpty(values[0]))
                return values[0];
        }
        return ReadCustomString(file, WorkKey);
    }

    public static void WriteWorkName(TagFile file, string? value)
    {
        var clean = string.IsNullOrWhiteSpace(value) ? null : value;
        if (file.GetTag(TagTypes.Apple, clean != null) is AppleTag apple)
            apple.SetText(AppleWorkAtom, clean);
        WriteCustomString(file, WorkKey, clean);
    }

    // ── Movement name ──

    public static string ReadMovementName(TagFile file)
    {
        if (file.GetTag(TagTypes.Apple, false) is AppleTag apple)
        {
            var values = apple.GetText(AppleMovementNameAtom);
            if (values is { Length: > 0 } && !string.IsNullOrEmpty(values[0]))
                return values[0];
        }
        return ReadCustomString(file, MovementNameKey);
    }

    public static void WriteMovementName(TagFile file, string? value)
    {
        var clean = string.IsNullOrWhiteSpace(value) ? null : value;
        if (file.GetTag(TagTypes.Apple, clean != null) is AppleTag apple)
            apple.SetText(AppleMovementNameAtom, clean);
        WriteCustomString(file, MovementNameKey, clean);
    }

    // ── Movement number / total ──
    // Apple stores these as single-byte integers in a custom box; we'll just store as text,
    // which round-trips fine inside the same app and is ignored safely by other players.

    public static int ReadMovementNumber(TagFile file)
    {
        if (file.GetTag(TagTypes.Apple, false) is AppleTag apple)
        {
            var values = apple.GetText(AppleMovementIndexAtom);
            if (values is { Length: > 0 } && int.TryParse(values[0], out var mv))
                return mv;
        }
        return int.TryParse(ReadCustomString(file, MovementNumberKey), out var v) ? v : 0;
    }

    public static void WriteMovementNumber(TagFile file, int value)
    {
        var text = value > 0 ? value.ToString() : null;
        if (file.GetTag(TagTypes.Apple, text != null) is AppleTag apple)
            apple.SetText(AppleMovementIndexAtom, text);
        WriteCustomString(file, MovementNumberKey, text);
    }

    public static int ReadMovementCount(TagFile file)
    {
        if (file.GetTag(TagTypes.Apple, false) is AppleTag apple)
        {
            var values = apple.GetText(AppleMovementCountAtom);
            if (values is { Length: > 0 } && int.TryParse(values[0], out var mv))
                return mv;
        }
        return int.TryParse(ReadCustomString(file, MovementTotalKey), out var v) ? v : 0;
    }

    public static void WriteMovementCount(TagFile file, int value)
    {
        var text = value > 0 ? value.ToString() : null;
        if (file.GetTag(TagTypes.Apple, text != null) is AppleTag apple)
            apple.SetText(AppleMovementCountAtom, text);
        WriteCustomString(file, MovementTotalKey, text);
    }

    // ── Use Work & Movement (Apple shwm == 1) ──

    public static bool ReadUseWorkAndMovement(TagFile file)
    {
        if (file.GetTag(TagTypes.Apple, false) is AppleTag apple)
        {
            var values = apple.GetText(AppleShowWorkAtom);
            if (values is { Length: > 0 } && int.TryParse(values[0], out var v) && v == 1)
                return true;
        }
        return ReadCustomBool(file, UseWorkMovementKey);
    }

    public static void WriteUseWorkAndMovement(TagFile file, bool value)
    {
        // Apple's shwm is a single enum: 0 off, 1 work/movement, 2 composer.
        // We only touch the atom when this flag is the "active" display mode to avoid clobbering ShowComposer.
        if (file.GetTag(TagTypes.Apple, value) is AppleTag apple)
        {
            if (value)
                apple.SetText(AppleShowWorkAtom, "1");
            else
            {
                // Only clear if the atom currently says "1" — otherwise the composer flag owns it.
                var current = apple.GetText(AppleShowWorkAtom);
                if (current is { Length: > 0 } && current[0] == "1")
                    apple.SetText(AppleShowWorkAtom, (string?)null);
            }
        }
        WriteCustomBool(file, UseWorkMovementKey, value);
    }

    // ── Show composer in all views (Apple shwm == 2) ──

    public static bool ReadShowComposer(TagFile file)
    {
        if (file.GetTag(TagTypes.Apple, false) is AppleTag apple)
        {
            var values = apple.GetText(AppleShowWorkAtom);
            if (values is { Length: > 0 } && int.TryParse(values[0], out var v) && v == 2)
                return true;
        }
        return ReadCustomBool(file, ShowComposerKey);
    }

    public static void WriteShowComposer(TagFile file, bool value)
    {
        if (file.GetTag(TagTypes.Apple, value) is AppleTag apple)
        {
            if (value)
                apple.SetText(AppleShowWorkAtom, "2");
            else
            {
                var current = apple.GetText(AppleShowWorkAtom);
                if (current is { Length: > 0 } && current[0] == "2")
                    apple.SetText(AppleShowWorkAtom, (string?)null);
            }
        }
        WriteCustomBool(file, ShowComposerKey, value);
    }

    // ── Release type (Album / Single / EP / Compilation / Live / Remix / Soundtrack / Other) ──

    /// <summary>
    /// Reads the release type from tags using priority:
    /// 1. NOCTIS_RELEASETYPE (user override)
    /// 2. RELEASETYPE (mp3tag/foobar2000 convention)
    /// 3. MUSICBRAINZ_ALBUM_TYPE (Picard, may be multi-valued)
    /// Returns null if none of the tags are set, so callers can fall back to
    /// album-name heuristics or track-count heuristics.
    /// </summary>
    public static ReleaseType? ReadReleaseType(TagFile file, out bool isUserOverride)
    {
        isUserOverride = false;

        var overrideValue = ReadCustomString(file, NoctisReleaseTypeOverrideKey);
        if (!string.IsNullOrWhiteSpace(overrideValue) && TryParseReleaseType(overrideValue, out var overrideParsed))
        {
            isUserOverride = true;
            return overrideParsed;
        }

        var primary = ReadCustomString(file, ReleaseTypeKey);
        if (!string.IsNullOrWhiteSpace(primary) && TryParseReleaseTypeList(primary, out var parsedPrimary))
            return parsedPrimary;

        var mb = ReadCustomString(file, MusicBrainzAlbumTypeKey);
        if (!string.IsNullOrWhiteSpace(mb) && TryParseReleaseTypeList(mb, out var parsedMb))
            return parsedMb;

        return null;
    }

    public static void WriteReleaseTypeOverride(TagFile file, ReleaseType? value)
    {
        WriteCustomString(file, NoctisReleaseTypeOverrideKey, value?.ToString());
    }

    /// <summary>Parses a tag value like "Album", "ep", "Soundtrack" into <see cref="ReleaseType"/>.</summary>
    private static bool TryParseReleaseType(string raw, out ReleaseType type)
    {
        type = ReleaseType.Album;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var token = raw.Trim();
        switch (token.ToLowerInvariant())
        {
            case "album": type = ReleaseType.Album; return true;
            case "single": type = ReleaseType.Single; return true;
            case "ep": type = ReleaseType.EP; return true;
            case "compilation": type = ReleaseType.Compilation; return true;
            case "live": type = ReleaseType.Live; return true;
            case "remix": type = ReleaseType.Remix; return true;
            case "soundtrack": type = ReleaseType.Soundtrack; return true;
            case "other": type = ReleaseType.Other; return true;
        }
        return false;
    }

    /// <summary>
    /// Parses a list of release type tokens (Picard writes multi-valued like
    /// "album; soundtrack") and picks the most specific one. Ordering puts
    /// secondary types (Soundtrack/Live/Remix/Compilation/EP/Single) ahead of
    /// the generic "Album" so a track tagged "album; soundtrack" classifies
    /// as Soundtrack.
    /// </summary>
    private static bool TryParseReleaseTypeList(string raw, out ReleaseType type)
    {
        type = ReleaseType.Album;
        var tokens = raw.Split(new[] { ';', ',', '/' }, System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        ReleaseType? best = null;
        foreach (var token in tokens)
        {
            if (!TryParseReleaseType(token, out var parsed)) continue;
            if (best == null || Specificity(parsed) > Specificity(best.Value))
                best = parsed;
        }
        if (best == null) return false;
        type = best.Value;
        return true;
    }

    private static int Specificity(ReleaseType t) => t switch
    {
        ReleaseType.Album => 0,
        ReleaseType.Other => 1,
        ReleaseType.EP => 2,
        ReleaseType.Single => 3,
        ReleaseType.Compilation => 4,
        ReleaseType.Live => 5,
        ReleaseType.Remix => 6,
        ReleaseType.Soundtrack => 7,
        _ => 0,
    };

    // ── Rating (ID3v2 POPM / Vorbis+APE RATING / MP4 ----:com.apple.iTunes:RATING) ──
    // POPM stores 0-255 (Windows convention: 1/64/128/196/255 for 1-5 stars); the text
    // containers store 0-100 (MediaMonkey/MusicBee convention, stars × 20). Reads only
    // accept the 0-100 scale: Apple Music downloads carry the iTunes advisory flag
    // (1=explicit, 2=clean, 4=legacy explicit) in a "rating" text tag, so small values
    // would otherwise show up as phantom 1/2/4-star ratings on freshly imported files.

    private const string RatingKey = "RATING";
    private const string DislikedKey = "NOCTIS_DISLIKED";
    private const string PopmOwner = "Noctis";
    private const string AppleItunesMean = "com.apple.iTunes";

    public static int ReadRating(TagFile file)
    {
        if (file.GetTag(TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
        {
            foreach (var frame in id3.GetFrames<PopularimeterFrame>())
            {
                if (frame.Rating > 0)
                    return PopmToStars(frame.Rating);
            }
        }

        if (file.GetTag(TagTypes.Apple, false) is AppleTag apple)
        {
            var text = apple.GetDashBox(AppleItunesMean, RatingKey);
            if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var appleValue))
                return NumericToStars(appleValue);
        }

        var raw = ReadCustomString(file, RatingKey);
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            return NumericToStars(value);

        return 0;
    }

    public static void WriteRating(TagFile file, int stars)
    {
        stars = System.Math.Clamp(stars, 0, 5);

        if (file.GetTag(TagTypes.Id3v2, stars > 0) is TagLib.Id3v2.Tag id3)
        {
            if (stars == 0)
            {
                foreach (var frame in id3.GetFrames<PopularimeterFrame>().ToArray())
                    id3.RemoveFrame(frame);
            }
            else
            {
                // Update every existing POPM frame so other players' views stay in sync.
                var frames = id3.GetFrames<PopularimeterFrame>().ToArray();
                if (frames.Length == 0)
                    frames = new[] { PopularimeterFrame.Get(id3, PopmOwner, true) };
                foreach (var frame in frames)
                    frame.Rating = StarsToPopm(stars);
            }
        }

        var text = stars > 0
            ? (stars * 20).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;

        if (file.GetTag(TagTypes.Apple, stars > 0) is AppleTag apple)
            apple.SetDashBox(AppleItunesMean, RatingKey, text ?? string.Empty);

        if (file.GetTag(TagTypes.Xiph, stars > 0) is XiphComment xiph)
        {
            if (text == null) xiph.RemoveField(RatingKey);
            else xiph.SetField(RatingKey, new[] { text });
        }

        if (file.GetTag(TagTypes.Ape, stars > 0) is TagLib.Ape.Tag ape)
        {
            if (text == null) ape.RemoveItem(RatingKey);
            else ape.SetValue(RatingKey, text);
        }
    }

    public static bool ReadIsDisliked(TagFile file) => ReadCustomBool(file, DislikedKey);

    public static void WriteIsDisliked(TagFile file, bool value) => WriteCustomBool(file, DislikedKey, value);

    private static int PopmToStars(byte popm) => popm switch
    {
        0 => 0,
        < 32 => 1,
        < 96 => 2,
        < 160 => 3,
        < 224 => 4,
        _ => 5
    };

    private static byte StarsToPopm(int stars) => stars switch
    {
        <= 0 => 0,
        1 => 1,
        2 => 64,
        3 => 128,
        4 => 196,
        _ => 255
    };

    private static int NumericToStars(double value)
    {
        // Below the 0-100 scale's half-star floor (10 = ½★) the value is ambiguous with
        // the iTunes advisory codes (1/2/4), so treat it as unrated rather than stars.
        if (value < 10) return 0;
        return System.Math.Clamp((int)System.Math.Round(value / 20.0), 1, 5);
    }

    // ── Shared custom-field helpers (ID3v2 TXXX / Xiph SetField / APE item) ──

    private static string ReadCustomString(TagFile file, string key)
    {
        if (file.GetTag(TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
        {
            var frame = UserTextInformationFrame.Get(id3, key, false);
            if (frame != null)
            {
                var txt = frame.Text;
                if (txt is { Length: > 0 } && !string.IsNullOrEmpty(txt[0]))
                    return txt[0];
            }
        }
        if (file.GetTag(TagTypes.Xiph, false) is XiphComment xiph)
        {
            var values = xiph.GetField(key);
            if (values is { Length: > 0 } && !string.IsNullOrEmpty(values[0]))
                return values[0];
        }
        if (file.GetTag(TagTypes.Ape, false) is TagLib.Ape.Tag ape)
        {
            var item = ape.GetItem(key);
            var text = item?.ToString();
            if (!string.IsNullOrEmpty(text))
                return text!;
        }
        return string.Empty;
    }

    private static void WriteCustomString(TagFile file, string key, string? value)
    {
        var clean = string.IsNullOrWhiteSpace(value) ? null : value;

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
    }

    private static bool ReadCustomBool(TagFile file, string key)
    {
        var raw = ReadCustomString(file, key);
        return raw == "1" || string.Equals(raw, "true", System.StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteCustomBool(TagFile file, string key, bool value)
    {
        WriteCustomString(file, key, value ? "1" : null);
    }
}
