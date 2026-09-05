using Whisper.net;

namespace Noctis.Services.LyricsStudio;

public sealed record Transcript(IReadOnlyList<RecognizedWord> Words, string Language);

/// <summary>
/// Runs a Whisper ggml model over 16 kHz mono PCM and returns word-level timings. Tokens are
/// merged back into words (Whisper emits sub-word pieces; a leading space starts a new word).
/// One model load per call — Lyrics Studio processes a queue, so the caller keeps the
/// factory alive across tracks through <see cref="Session"/>.
/// </summary>
public sealed class WhisperTranscriber
{
    /// <summary>A loaded model; reuse it for every track in a run.</summary>
    public sealed class Session : IDisposable
    {
        private readonly WhisperFactory _factory;
        public string ModelPath { get; }

        public Session(string modelPath)
        {
            ModelPath = modelPath;
            _factory = WhisperFactory.FromPath(modelPath);
        }

        public async Task<Transcript> TranscribeAsync(float[] pcm16k, string? language, string? prompt, IProgress<double>? progress, CancellationToken ct)
        {
            var builder = _factory.CreateBuilder()
                .WithTokenTimestamps()
                .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 1, 8))
                .WithProgressHandler(p => progress?.Report(Math.Clamp(p / 100.0, 0, 1)));

            if (string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase))
                builder.WithLanguageDetection();
            else
                builder.WithLanguage(language.Trim().ToLowerInvariant());

            // Known lyrics as the decoding prompt bias the vocabulary toward the actual words
            // (names, slang, invented spellings) — the single biggest accuracy lever for alignment.
            if (!string.IsNullOrWhiteSpace(prompt))
                builder.WithPrompt(TrimPrompt(prompt));

            var words = new List<RecognizedWord>();
            var detected = language ?? "auto";
            using var processor = builder.Build();
            await foreach (var segment in processor.ProcessAsync(pcm16k, ct).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(segment.Language)) detected = segment.Language;
                words.AddRange(WordsFromSegment(segment));
            }
            return new Transcript(words, detected);
        }

        public void Dispose() => _factory.Dispose();
    }

    /// <summary>Whisper's prompt window is ~224 tokens; keep the first ~600 characters.</summary>
    internal static string TrimPrompt(string prompt)
    {
        var flat = string.Join(' ', prompt.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length <= 600 ? flat : flat[..600];
    }

    /// <summary>A token as Whisper.net reports it: text, start/end in centiseconds, probability.</summary>
    public readonly record struct TokenView(string Text, long StartCs, long EndCs, float Probability);

    internal static IEnumerable<RecognizedWord> WordsFromSegment(SegmentData segment)
    {
        var tokens = (segment.Tokens ?? Array.Empty<WhisperToken>())
            .Select(t => new TokenView(t.Text ?? string.Empty, t.Start, t.End, t.Probability));
        return WordsFromTokens(tokens, segment.Start, segment.End, segment.Text);
    }

    /// <summary>
    /// Merges tokens into words. Token timestamps that fall outside the segment (a known
    /// weakness of token-level DTW on music) are replaced by an even spread over the segment.
    /// </summary>
    internal static List<RecognizedWord> WordsFromTokens(IEnumerable<TokenView> tokens, TimeSpan segStart, TimeSpan segEnd, string? segmentText)
    {
        var words = new List<(string Text, long Start, long End, float Prob, int Count)>();
        foreach (var t in tokens)
        {
            var text = t.Text;
            if (text.Length == 0) continue;
            if (text.StartsWith("[_", StringComparison.Ordinal) || text.StartsWith("<|", StringComparison.Ordinal)) continue;
            var startsWord = text[0] == ' ' || words.Count == 0;
            var body = text.Trim();
            if (body.Length == 0) continue;
            if (startsWord || IsPunctuationOnly(body) == false && words.Count == 0)
            {
                words.Add((body, t.StartCs, t.EndCs, t.Probability, 1));
            }
            else
            {
                // Continuation piece (or punctuation): glue to the previous word.
                var last = words[^1];
                words[^1] = (last.Text + body, last.Start, Math.Max(last.End, t.EndCs), last.Prob + t.Probability, last.Count + 1);
            }
        }

        if (words.Count == 0)
        {
            // No token detail: spread the segment text evenly.
            var pieces = (segmentText ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Spread(pieces, segStart, segEnd, 0.5f);
        }

        var result = new List<RecognizedWord>(words.Count);
        var tolerance = TimeSpan.FromSeconds(1.5);
        var sane = true;
        foreach (var w in words)
        {
            var s = TimeSpan.FromMilliseconds(w.Start * 10);
            var e = TimeSpan.FromMilliseconds(w.End * 10);
            if (e < s) e = s;
            if (s < segStart - tolerance || e > segEnd + tolerance || w.Start < 0) sane = false;
            result.Add(new RecognizedWord(w.Text, s, e, w.Prob / Math.Max(1, w.Count)));
        }
        if (!sane)
            return Spread(words.Select(w => w.Text).ToArray(), segStart, segEnd, (float)words.Average(w => w.Prob / Math.Max(1, w.Count)));
        return result;
    }

    private static List<RecognizedWord> Spread(string[] pieces, TimeSpan start, TimeSpan end, float probability)
    {
        var list = new List<RecognizedWord>(pieces.Length);
        if (pieces.Length == 0) return list;
        if (end <= start) end = start + TimeSpan.FromMilliseconds(300 * pieces.Length);
        var slice = (end - start) / pieces.Length;
        for (var i = 0; i < pieces.Length; i++)
            list.Add(new RecognizedWord(pieces[i], start + slice * i, start + slice * (i + 1), probability));
        return list;
    }

    private static bool IsPunctuationOnly(string s) => s.All(c => char.IsPunctuation(c) || char.IsSymbol(c));
}
