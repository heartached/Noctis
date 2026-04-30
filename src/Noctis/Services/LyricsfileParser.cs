using Noctis.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Noctis.Services;

/// <summary>
/// Parses Lyricsfile v1.0 YAML (LRCGET v2.0+) into <see cref="LyricLine"/> objects,
/// preserving optional per-word karaoke timing.
///
/// Spec: https://github.com/tranxuanthang/lrcget/blob/main/LYRICSFILE_CONCEPT.md
/// </summary>
public static class LyricsfileParser
{
    // YamlDotNet is thread-safe for Deserializer instances once built.
    private static readonly IDeserializer _yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Cheap discriminator — Lyricsfiles start with a top-level 'version:' key.</summary>
    public static bool LooksLikeLyricsfile(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var span = content.AsSpan().TrimStart();
        return span.StartsWith("version:") || span.StartsWith("version :");
    }

    /// <summary>
    /// Parses YAML Lyricsfile content. Returns (lines, plainText) where plainText is the
    /// unsynchronized block for the Unsync tab. Returns nulls when the content is malformed.
    /// </summary>
    public static (List<LyricLine>? Lines, string? Plain) Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return (null, null);

        LyricsfileDto? dto;
        try
        {
            dto = _yaml.Deserialize<LyricsfileDto>(content);
        }
        catch
        {
            return (null, null);
        }

        if (dto == null) return (null, null);

        var offset = TimeSpan.FromMilliseconds(dto.Metadata?.OffsetMs ?? 0);
        var lines = new List<LyricLine>();

        if (dto.Lines != null)
        {
            foreach (var raw in dto.Lines)
            {
                if (raw == null) continue;

                var text = raw.Text ?? string.Empty;
                var start = TimeSpan.FromMilliseconds(raw.StartMs) + offset;
                if (start < TimeSpan.Zero) start = TimeSpan.Zero;

                TimeSpan? end = null;
                if (raw.EndMs.HasValue)
                {
                    var e = TimeSpan.FromMilliseconds(raw.EndMs.Value) + offset;
                    if (e >= start) end = e;
                }

                var line = new LyricLine
                {
                    Timestamp = start,
                    EndTimestamp = end,
                    Text = text,
                };

                if (raw.Words != null && raw.Words.Count > 0)
                {
                    var words = new List<WordTiming>(raw.Words.Count);
                    foreach (var w in raw.Words)
                    {
                        if (w == null || string.IsNullOrEmpty(w.Text)) continue;
                        var ws = TimeSpan.FromMilliseconds(w.StartMs) + offset;
                        if (ws < TimeSpan.Zero) ws = TimeSpan.Zero;
                        TimeSpan? we = null;
                        if (w.EndMs.HasValue)
                        {
                            var candidate = TimeSpan.FromMilliseconds(w.EndMs.Value) + offset;
                            if (candidate >= ws) we = candidate;
                        }
                        words.Add(new WordTiming { Text = w.Text, Start = ws, End = we });
                    }

                    // Backfill missing End times from the next word's Start, and the final word
                    // from the line end (falls back to last word's Start + 300ms as a minimum).
                    for (int i = 0; i < words.Count; i++)
                    {
                        if (words[i].End.HasValue) continue;
                        TimeSpan fallback;
                        if (i + 1 < words.Count)
                            fallback = words[i + 1].Start;
                        else if (end.HasValue)
                            fallback = end.Value;
                        else
                            fallback = words[i].Start + TimeSpan.FromMilliseconds(300);

                        words[i] = new WordTiming
                        {
                            Text = words[i].Text,
                            Start = words[i].Start,
                            End = fallback,
                        };
                    }

                    line.Words = words;
                }

                lines.Add(line);
            }
        }

        // Lines are already emitted in source order; sort defensively.
        lines.Sort((a, b) =>
            Nullable.Compare(a.Timestamp, b.Timestamp));

        return (lines, dto.Plain);
    }

    // ── YAML DTOs (local to the parser — external shape isn't used elsewhere) ──

    private sealed class LyricsfileDto
    {
        public string? Version { get; set; }
        public MetadataDto? Metadata { get; set; }
        public List<LineDto>? Lines { get; set; }
        public string? Plain { get; set; }
    }

    private sealed class MetadataDto
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public int? DurationMs { get; set; }
        public int OffsetMs { get; set; }
        public string? Language { get; set; }
        public bool Instrumental { get; set; }
    }

    private sealed class LineDto
    {
        public string? Text { get; set; }
        public int StartMs { get; set; }
        public int? EndMs { get; set; }
        public List<WordDto>? Words { get; set; }
    }

    private sealed class WordDto
    {
        public string? Text { get; set; }
        public int StartMs { get; set; }
        public int? EndMs { get; set; }
    }
}
