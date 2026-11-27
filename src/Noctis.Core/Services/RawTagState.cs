namespace Noctis.Services;

/// <summary>
/// Every entry of a file's tag blocks as raw values (<see cref="RawTagSnapshot"/>), so a write
/// can record what it replaced and Undo Grouping can put exactly that back: ID3v2 frames as
/// rendered bytes (keyed by frame id, TXXX by description), the ID3v1 block, Vorbis comment
/// fields, MP4 atoms. Other blocks (APE, ASF, RIFF INFO) are only noted
/// (<see cref="RawTagSnapshot.OtherBlocks"/>).
/// </summary>
internal static class RawTagState
{
    private const TagLib.TagTypes Kept =
        TagLib.TagTypes.Id3v2 | TagLib.TagTypes.Id3v1 | TagLib.TagTypes.Xiph | TagLib.TagTypes.Apple
        | TagLib.TagTypes.FlacMetadata;

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
        s.OtherBlocks = (file.TagTypes & ~Kept) != TagLib.TagTypes.None;
        return s;
    }

    /// <summary>What <paramref name="before"/> held for each entry that differs in
    /// <paramref name="after"/> (empty: there was none).</summary>
    public static RawTagSnapshot Replaced(RawTagSnapshot before, RawTagSnapshot after)
    {
        var s = new RawTagSnapshot { Id3v2Version = before.Id3v2Version, OtherBlocks = after.OtherBlocks };
        s.Id3v2 = Changed(before.Id3v2, after.Id3v2, (a, b) => a.SequenceEqual(b), () => new List<string>());
        s.Xiph = Changed(before.Xiph, after.Xiph, (a, b) => a.SequenceEqual(b), Array.Empty<string>);
        s.Apple = Changed(before.Apple, after.Apple, (a, b) => a.SequenceEqual(b), () => new List<string>());
        s.Id3v1 = before.Id3v1 == after.Id3v1 ? null : before.Id3v1;
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
