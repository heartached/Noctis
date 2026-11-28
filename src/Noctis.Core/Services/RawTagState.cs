namespace Noctis.Services;

/// <summary>
/// Every entry of a file's tag blocks as raw values (<see cref="RawTagSnapshot"/>), so a write
/// can record what it replaced and Undo Grouping can put exactly that back: ID3v2 frames as
/// rendered bytes (keyed by frame id, TXXX by description), the ID3v1 block, Vorbis comment
/// fields, MP4 atoms, APEv2 items, ASF descriptors, RIFF INFO / MovieID fields and the DivX
/// block: every tag block TagLib writes for the formats the library takes. Any other block is
/// only noted (<see cref="RawTagSnapshot.OtherBlocks"/>).
/// </summary>
internal static class RawTagState
{
    private const TagLib.TagTypes Kept =
        TagLib.TagTypes.Id3v2 | TagLib.TagTypes.Id3v1 | TagLib.TagTypes.Xiph | TagLib.TagTypes.Apple
        | TagLib.TagTypes.FlacMetadata | TagLib.TagTypes.Ape | TagLib.TagTypes.Asf | TagLib.TagTypes.RiffInfo
        | TagLib.TagTypes.MovieId | TagLib.TagTypes.DivX;

    /// <summary>The file's tag entries now.</summary>
    public static RawTagSnapshot Capture(TagLib.File file)
    {
        var s = new RawTagSnapshot();
        if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
        {
            s.Id3v2Version = id3.Version;
            s.Id3v2 = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var frame in id3)
            {
                var key = Id3Key(frame);
                if (!s.Id3v2.TryGetValue(key, out var list)) s.Id3v2[key] = list = new List<string>();
                list.Add(Convert.ToBase64String(frame.Render(id3.Version).Data));
            }
        }
        s.Id3v1 = file.GetTag(TagLib.TagTypes.Id3v1) is TagLib.Id3v1.Tag v1
            ? Convert.ToBase64String(v1.Render().Data)
            : string.Empty;
        if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
        {
            s.Xiph = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var name in xiph)
                s.Xiph[name] = xiph.GetField(name) ?? Array.Empty<string>();
        }
        if (file.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
        {
            s.Apple = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var box in apple)
            {
                var type = box.BoxType;
                if (DashName(box) is { } dash)
                    s.Apple[DashKey(dash.Mean, dash.Name)] = (apple.GetDashBoxes(dash.Mean, dash.Name) ?? Array.Empty<string>()).ToList();
                else
                    s.Apple[TypeKey(type)] = apple.DataBoxes(type)
                        .Select(d => d.Flags.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Convert.ToBase64String(d.Data.Data))
                        .ToList();
            }
        }
        if (file.GetTag(TagLib.TagTypes.Ape) is TagLib.Ape.Tag ape)
        {
            s.Ape = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in ape)
                if (ape.GetItem(key) is { } item)
                    s.Ape[key] = Convert.ToBase64String(item.Render().Data);
        }
        if (file.GetTag(TagLib.TagTypes.Asf) is TagLib.Asf.Tag asf)
            s.Asf = CaptureAsf(asf);
        if (file.GetTag(TagLib.TagTypes.RiffInfo) is TagLib.Riff.ListTag info)
            CaptureList(s, "INFO", info);
        if (file.GetTag(TagLib.TagTypes.MovieId) is TagLib.Riff.ListTag movie)
            CaptureList(s, "MID ", movie);
        s.DivX = file.GetTag(TagLib.TagTypes.DivX) is TagLib.Riff.DivXTag divx
            ? Convert.ToBase64String(divx.Render().Data)
            : string.Empty;
        s.OtherBlocks = (file.TagTypes & ~Kept) != TagLib.TagTypes.None;
        return s;
    }

    // ── ASF (WMA): content description, extended content descriptors, metadata library ──

    private static readonly string[] AsfDescriptionFields = { "Title", "Author", "Copyright", "Description", "Rating" };

    private static Dictionary<string, List<string>> CaptureAsf(TagLib.Asf.Tag asf)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (asf.ContentDescriptionObject is { } cd)
            foreach (var field in AsfDescriptionFields)
            {
                var value = AsfDescription(cd, field);
                d["c:" + field] = string.IsNullOrEmpty(value) ? new List<string>() : new List<string> { "U:" + B64(value) };
            }
        if (asf.ExtendedContentDescriptionObject is { } ext)
            foreach (var x in ext)
                Add(d, "x:" + x.Name, AsfValue(x.Type, x.ToString, x.ToByteVector, x.ToBool, x.ToWord, x.ToDWord, x.ToQWord, null));
        if (asf.MetadataLibraryObject is { } lib)
            foreach (var r in lib)
                Add(d, $"m:{r.LanguageListIndex}:{r.StreamNumber}:{r.Name}",
                    AsfValue(r.Type, r.ToString, r.ToByteVector, r.ToBool, r.ToWord, r.ToDWord, r.ToQWord, r.ToGuid));
        return d;
    }

    private static string? AsfDescription(TagLib.Asf.ContentDescriptionObject cd, string field) => field switch
    {
        "Title" => cd.Title,
        "Author" => cd.Author,
        "Copyright" => cd.Copyright,
        "Description" => cd.Description,
        _ => cd.Rating,
    };

    private static void SetAsfDescription(TagLib.Asf.ContentDescriptionObject cd, string field, string? value)
    {
        switch (field)
        {
            case "Title": cd.Title = value; break;
            case "Author": cd.Author = value; break;
            case "Copyright": cd.Copyright = value; break;
            case "Description": cd.Description = value; break;
            default: cd.Rating = value; break;
        }
    }

    private static string AsfValue(TagLib.Asf.DataType type, Func<string> text, Func<TagLib.ByteVector> bytes,
        Func<bool> flag, Func<ushort> word, Func<uint> dword, Func<ulong> qword, Func<Guid>? guid)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return type switch
        {
            TagLib.Asf.DataType.Unicode => "U:" + B64(text()),
            TagLib.Asf.DataType.Bool => "b:" + (flag() ? "1" : "0"),
            TagLib.Asf.DataType.Word => "W:" + word().ToString(inv),
            TagLib.Asf.DataType.DWord => "D:" + dword().ToString(inv),
            TagLib.Asf.DataType.QWord => "Q:" + qword().ToString(inv),
            TagLib.Asf.DataType.Guid when guid != null => "G:" + guid().ToString(),
            _ => "B:" + Convert.ToBase64String(bytes().Data),
        };
    }

    private static TagLib.Asf.ContentDescriptor AsfDescriptor(string name, string value)
    {
        var data = value[2..];
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return value[0] switch
        {
            'U' => new TagLib.Asf.ContentDescriptor(name, Unb64(data)),
            'b' => new TagLib.Asf.ContentDescriptor(name, data == "1"),
            'W' => new TagLib.Asf.ContentDescriptor(name, ushort.Parse(data, inv)),
            'D' => new TagLib.Asf.ContentDescriptor(name, uint.Parse(data, inv)),
            'Q' => new TagLib.Asf.ContentDescriptor(name, ulong.Parse(data, inv)),
            _ => new TagLib.Asf.ContentDescriptor(name, new TagLib.ByteVector(Convert.FromBase64String(data))),
        };
    }

    private static TagLib.Asf.DescriptionRecord AsfRecord(ushort language, ushort stream, string name, string value)
    {
        var data = value[2..];
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return value[0] switch
        {
            'U' => new TagLib.Asf.DescriptionRecord(language, stream, name, Unb64(data)),
            'b' => new TagLib.Asf.DescriptionRecord(language, stream, name, data == "1"),
            'W' => new TagLib.Asf.DescriptionRecord(language, stream, name, ushort.Parse(data, inv)),
            'D' => new TagLib.Asf.DescriptionRecord(language, stream, name, uint.Parse(data, inv)),
            'Q' => new TagLib.Asf.DescriptionRecord(language, stream, name, ulong.Parse(data, inv)),
            'G' => new TagLib.Asf.DescriptionRecord(language, stream, name, Guid.Parse(data)),
            _ => new TagLib.Asf.DescriptionRecord(language, stream, name, new TagLib.ByteVector(Convert.FromBase64String(data))),
        };
    }

    private static void RestoreAsf(TagLib.Asf.Tag asf, Dictionary<string, List<string>> entries)
    {
        foreach (var (key, values) in entries)
        {
            if (key.StartsWith("c:", StringComparison.Ordinal))
            {
                if (asf.ContentDescriptionObject is { } cd)
                    SetAsfDescription(cd, key[2..], values.Count == 0 ? null : Unb64(values[0][2..]));
            }
            else if (key.StartsWith("x:", StringComparison.Ordinal))
            {
                var name = key[2..];
                if (asf.ExtendedContentDescriptionObject is not { } ext) continue;
                ext.RemoveDescriptors(name);
                foreach (var v in values) ext.AddDescriptor(AsfDescriptor(name, v));
            }
            else if (key.StartsWith("m:", StringComparison.Ordinal) && asf.MetadataLibraryObject is { } lib)
            {
                var parts = key.Split(':', 4);
                var language = ushort.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                var stream = ushort.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                lib.RemoveRecords(language, stream, parts[3]);
                foreach (var v in values) lib.AddRecord(AsfRecord(language, stream, parts[3], v));
            }
        }
    }

    // ── RIFF lists (WAV): INFO and MovieID fields, read from the rendered list ──

    private static void CaptureList(RawTagSnapshot s, string list, TagLib.Riff.ListTag tag)
    {
        s.Riff ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var data = tag.Render().Data;
        for (var at = 0; at + 8 <= data.Length;)
        {
            var id = System.Text.Encoding.ASCII.GetString(data, at, 4);
            var size = (int)BitConverter.ToUInt32(data, at + 4);
            if (size < 0 || at + 8 + size > data.Length) break;
            Add(s.Riff, list + ":" + id, Convert.ToBase64String(data, at + 8, size));
            at += 8 + size + (size & 1);
        }
    }

    private static void RestoreLists(TagLib.File file, Dictionary<string, List<string>> fields)
    {
        foreach (var (key, values) in fields)
        {
            var type = key.StartsWith("MID :", StringComparison.Ordinal) ? TagLib.TagTypes.MovieId : TagLib.TagTypes.RiffInfo;
            if (file.GetTag(type, values.Count > 0) is not TagLib.Riff.ListTag tag) continue;
            var id = TagLib.ByteVector.FromString(key[5..], TagLib.StringType.Latin1);
            if (values.Count == 0) tag.RemoveValue(id);
            else tag.SetValue(id, values.Select(v => new TagLib.ByteVector(Convert.FromBase64String(v))).ToArray());
        }
    }

    private static void Add(Dictionary<string, List<string>> d, string key, string value)
    {
        if (!d.TryGetValue(key, out var list)) d[key] = list = new List<string>();
        list.Add(value);
    }

    private static string B64(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
    private static string Unb64(string data) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data));

    /// <summary>What <paramref name="before"/> held for each entry that differs in
    /// <paramref name="after"/> (empty: there was none).</summary>
    public static RawTagSnapshot Replaced(RawTagSnapshot before, RawTagSnapshot after)
    {
        var s = new RawTagSnapshot { Id3v2Version = before.Id3v2Version, OtherBlocks = after.OtherBlocks };
        s.Id3v2 = Changed(before.Id3v2, after.Id3v2, (a, b) => a.SequenceEqual(b), () => new List<string>());
        s.Xiph = Changed(before.Xiph, after.Xiph, (a, b) => a.SequenceEqual(b), Array.Empty<string>);
        s.Apple = Changed(before.Apple, after.Apple, (a, b) => a.SequenceEqual(b), () => new List<string>());
        s.Ape = Changed(before.Ape, after.Ape, (a, b) => a == b, () => string.Empty);
        s.Asf = Changed(before.Asf, after.Asf, (a, b) => a.SequenceEqual(b), () => new List<string>());
        s.Riff = Changed(before.Riff, after.Riff, (a, b) => a.SequenceEqual(b), () => new List<string>());
        s.Id3v1 = before.Id3v1 == after.Id3v1 ? null : before.Id3v1;
        s.DivX = before.DivX == after.DivX ? null : before.DivX;
        return s;
    }

    private static Dictionary<string, T>? Changed<T>(Dictionary<string, T>? before, Dictionary<string, T>? after,
        Func<T, T, bool> same, Func<T> none)
    {
        if (before == null && after == null) return null;
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var key in (before?.Keys ?? Enumerable.Empty<string>()).Union(after?.Keys ?? Enumerable.Empty<string>()))
        {
            var hadBefore = before != null && before.TryGetValue(key, out _);
            var hasAfter = after != null && after.TryGetValue(key, out _);
            if (hadBefore && hasAfter && same(before![key], after![key])) continue;
            result[key] = hadBefore ? before![key] : none();
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>Puts <paramref name="original"/>'s entries back into <paramref name="file"/>:
    /// each listed entry as it was (removed when it had none), in its old place when it is still
    /// there. Entries not listed stay as they are.</summary>
    public static void Restore(TagLib.File file, RawTagSnapshot original)
    {
        if (original.Id3v2 is { Count: > 0 } frames)
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true);
            foreach (var (key, values) in frames)
            {
                var existing = id3.Where(f => Id3Key(f) == key).ToList();
                var wanted = values.Select(v => ParseFrame(v, original.Id3v2Version)).OfType<TagLib.Id3v2.Frame>().ToList();
                for (var i = 0; i < Math.Max(existing.Count, wanted.Count); i++)
                {
                    if (i < existing.Count && i < wanted.Count) id3.ReplaceFrame(existing[i], wanted[i]);
                    else if (i < existing.Count) id3.RemoveFrame(existing[i]);
                    else id3.AddFrame(wanted[i]);
                }
            }
        }

        if (original.Id3v1 != null)
        {
            if (original.Id3v1.Length == 0)
                file.RemoveTags(TagLib.TagTypes.Id3v1);
            else if (file.GetTag(TagLib.TagTypes.Id3v1, true) is TagLib.Id3v1.Tag v1)
                new TagLib.Id3v1.Tag(new TagLib.ByteVector(Convert.FromBase64String(original.Id3v1))).CopyTo(v1, true);
        }

        if (original.Xiph is { Count: > 0 } fields && file.GetTag(TagLib.TagTypes.Xiph, true) is TagLib.Ogg.XiphComment xiph)
            foreach (var (name, values) in fields)
            {
                if (values.Length == 0) xiph.RemoveField(name);
                else xiph.SetField(name, values);
            }

        if (original.Apple is { Count: > 0 } atoms && file.GetTag(TagLib.TagTypes.Apple, true) is TagLib.Mpeg4.AppleTag apple)
            foreach (var (key, values) in atoms)
            {
                if (key.StartsWith("----:", StringComparison.Ordinal))
                {
                    var parts = key.Split(':', 3);
                    if (values.Count == 0) apple.SetDashBox(parts[1], parts[2], string.Empty);
                    else apple.SetDashBoxes(parts[1], parts[2], values.ToArray());
                    continue;
                }
                var type = new TagLib.ByteVector(Convert.FromBase64String(key));
                if (values.Count == 0)
                {
                    apple.ClearData(type);
                    continue;
                }
                apple.SetData(type, values.Select(v =>
                {
                    var cut = v.IndexOf(':');
                    var flags = uint.Parse(v[..cut], System.Globalization.CultureInfo.InvariantCulture);
                    return new TagLib.Mpeg4.AppleDataBox(new TagLib.ByteVector(Convert.FromBase64String(v[(cut + 1)..])), flags);
                }).ToArray());
            }

        RestoreOthers(file, original);
    }

    /// <summary>The blocks <see cref="Restore"/> covers besides ID3, Vorbis and MP4.</summary>
    private static void RestoreOthers(TagLib.File file, RawTagSnapshot original)
    {
        if (original.Ape is { Count: > 0 } items && file.GetTag(TagLib.TagTypes.Ape, true) is TagLib.Ape.Tag ape)
            foreach (var (key, value) in items)
            {
                if (value.Length == 0) ape.RemoveItem(key);
                else ape.SetItem(new TagLib.Ape.Item(new TagLib.ByteVector(Convert.FromBase64String(value)), 0));
            }
        if (original.Asf is { Count: > 0 } asfEntries && file.GetTag(TagLib.TagTypes.Asf, true) is TagLib.Asf.Tag asf)
            RestoreAsf(asf, asfEntries);
        if (original.Riff is { Count: > 0 } fields)
            RestoreLists(file, fields);
        if (original.DivX != null)
        {
            if (original.DivX.Length == 0)
                file.RemoveTags(TagLib.TagTypes.DivX);
            else if (file.GetTag(TagLib.TagTypes.DivX, true) is TagLib.Riff.DivXTag divx)
                CopyDivX(new TagLib.Riff.DivXTag(new TagLib.ByteVector(Convert.FromBase64String(original.DivX))), divx);
        }
    }

    /// <summary>
    /// The DivX block as it was, field for field. TagLib#'s DivXTag offers no way to load raw data
    /// into the file's block and its CopyTo goes through the generic properties, which turns the
    /// "255" (no genre) the block held into blanks; its fields are copied as they are instead.
    /// </summary>
    private static void CopyDivX(TagLib.Riff.DivXTag from, TagLib.Riff.DivXTag to)
    {
        var fields = typeof(TagLib.Riff.DivXTag).GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        if (fields.Length == 0)
        {
            from.CopyTo(to, true);
            return;
        }
        foreach (var field in fields)
            if (!field.IsInitOnly) field.SetValue(to, field.GetValue(from));
    }

    private static TagLib.Id3v2.Frame? ParseFrame(string base64, byte version)
    {
        try
        {
            var offset = 0;
            return TagLib.Id3v2.FrameFactory.CreateFrame(new TagLib.ByteVector(Convert.FromBase64String(base64)), null,
                ref offset, version, true);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Metadata", $"Undo grouping: an ID3v2 frame could not be read back: {ex.Message}");
            return null;
        }
    }

    private static string Id3Key(TagLib.Id3v2.Frame frame)
    {
        var id = frame.FrameId.ToString(TagLib.StringType.Latin1);
        return frame is TagLib.Id3v2.UserTextInformationFrame u ? id + ":" + u.Description : id;
    }

    // MP4 atom types carry non-ASCII bytes (©alb): keyed by their bytes, freeform ones by name.
    private static string TypeKey(TagLib.ByteVector type) => Convert.ToBase64String(type.Data);

    private static string DashKey(string mean, string name) => "----:" + mean + ":" + name;

    private static (string Mean, string Name)? DashName(TagLib.Mpeg4.Box box)
    {
        if (box.BoxType != (TagLib.ByteVector)"----" || box.Children == null) return null;
        string? mean = null, name = null;
        foreach (var child in box.Children)
            if (child is TagLib.Mpeg4.AppleAdditionalInfoBox info)
            {
                if (child.BoxType == (TagLib.ByteVector)"mean") mean = info.Text;
                else if (child.BoxType == (TagLib.ByteVector)"name") name = info.Text;
            }
        return mean != null && name != null ? (mean, name) : null;
    }
}
