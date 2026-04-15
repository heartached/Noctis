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
