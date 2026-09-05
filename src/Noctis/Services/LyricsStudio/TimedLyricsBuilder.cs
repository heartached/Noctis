using System.Text;

namespace Noctis.Services.LyricsStudio;

/// <summary>
/// Serialises aligned lines to the two formats the lyrics page reads: plain LRC
/// (<c>[mm:ss.xx]text</c>) and enhanced LRC with inline word tags
/// (<c>[mm:ss.xx]&lt;mm:ss.xx&gt;word …&lt;mm:ss.xx&gt;</c>, the syntax
/// <see cref="EnhancedLrcParser"/> accepts, trailing tag = end of the last word).
/// </summary>
public static class TimedLyricsBuilder
{
    public static string FormatTimestamp(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}";
    }

    public static string BuildLrc(IEnumerable<AlignedLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in Ordered(lines))
            sb.Append('[').Append(FormatTimestamp(line.Start)).Append(']').Append(line.Text).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    public static string BuildElrc(IEnumerable<AlignedLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in Ordered(lines))
        {
            sb.Append('[').Append(FormatTimestamp(line.Start)).Append(']');
            if (line.Words.Count == 0)
            {
                sb.Append(line.Text).Append('\n');
                continue;
            }
            for (var i = 0; i < line.Words.Count; i++)
            {
                var w = line.Words[i];
                sb.Append('<').Append(FormatTimestamp(w.Start)).Append('>').Append(w.Text.Trim());
                if (i + 1 < line.Words.Count) sb.Append(' ');
            }
            sb.Append('<').Append(FormatTimestamp(line.Words[^1].End)).Append('>').Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    public static string BuildPlain(IEnumerable<AlignedLine> lines) =>
        string.Join('\n', Ordered(lines).Select(l => l.Text));

    private static IEnumerable<AlignedLine> Ordered(IEnumerable<AlignedLine> lines) =>
        lines.Where(l => l is not null && !string.IsNullOrWhiteSpace(l.Text)).OrderBy(l => l.Start);
}
