using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// ID3v2 SYLT (synchronised lyrics) → synced <see cref="LyricLine"/>s. Read on demand from the
/// audio stream rather than at scan time, so desktop scans persist exactly what they did.
/// Only millisecond stamps are supported: MPEG-frame stamps need the stream's frame rate.
/// </summary>
public static class SyltLyrics
{
    /// <summary>Lines from the first SYLT frame of type Lyrics with millisecond stamps, or null.</summary>
    public static List<LyricLine>? ToLines(TagLib.Id3v2.Tag? tag)
    {
        if (tag == null) return null;
        foreach (var frame in tag.GetFrames<TagLib.Id3v2.SynchronisedLyricsFrame>())
        {
            if (frame.Type != TagLib.Id3v2.SynchedTextType.Lyrics) continue;
            if (frame.Format != TagLib.Id3v2.TimestampFormat.AbsoluteMilliseconds) continue;

            var lines = new List<LyricLine>();
            foreach (var entry in frame.Text ?? [])
            {
                if (lines.Count >= LrcParser.MaxLines) break;
                var text = entry.Text?.Trim();
                if (string.IsNullOrEmpty(text) || entry.Time < 0) continue;
                lines.Add(new LyricLine { Timestamp = TimeSpan.FromMilliseconds(entry.Time), Text = LrcParser.SoftWrap(text) });
            }
            if (lines.Count == 0) continue;

            // Stable, so entries sharing a stamp keep their frame order.
            return lines.OrderBy(l => l.Timestamp).ToList();
        }
        return null;
    }

    /// <summary>Opens <paramref name="audio"/> with TagLib (type chosen from
    /// <paramref name="fileName"/>'s extension) and reads its SYLT. Takes ownership of the
    /// stream. Null on any failure: a missing tag is the common case, not an error.</summary>
    public static List<LyricLine>? Read(Stream audio, string fileName)
    {
        try
        {
            using var abstraction = new MetadataService.StreamFileAbstraction(fileName, audio);
            using var file = TagLib.File.Create(abstraction, null, TagLib.ReadStyle.Average);
            return ToLines(file.GetTag(TagLib.TagTypes.Id3v2) as TagLib.Id3v2.Tag);
        }
        catch (Exception)
        {
            audio.Dispose();
            return null;
        }
    }
}
