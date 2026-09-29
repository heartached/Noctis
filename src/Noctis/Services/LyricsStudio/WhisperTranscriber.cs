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
    /// <summary>One Whisper input window: 30 s of 16 kHz audio.</summary>
    private const int WindowSamples = 30 * PcmDecoder16k.SampleRate;

    /// <summary>A loaded model; reuse it for every track in a run.</summary>
    public sealed class Session : IDisposable
    {
        private readonly WhisperFactory _factory;
        private readonly bool _dtw;
        private readonly long _dtwShiftCs;
        public string ModelPath { get; }

        /// <param name="alignmentHeads">
        /// The model's cross-attention alignment heads. Anything but None turns on DTW token
        /// timestamps: word starts measured against the audio instead of whisper.cpp's
        /// heuristic token times (benchmark 09-23, 8 songs with hand-made word timings:
        /// median word-start error 0.29 → 0.12 s on Base, 0.25 → 0.12 s on Medium).
        /// </param>
        public Session(string modelPath, WhisperAlignmentHeadsPreset alignmentHeads = WhisperAlignmentHeadsPreset.None)
        {
            ModelPath = modelPath;
            _dtw = alignmentHeads != WhisperAlignmentHeadsPreset.None;
            _dtwShiftCs = DtwShiftCs(alignmentHeads);
            _factory = _dtw
                ? WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseDtwTimeStamps = true, HeadsPreset = alignmentHeads })
                : WhisperFactory.FromPath(modelPath);
        }

        public async Task<Transcript> TranscribeAsync(float[] pcm16k, string? language, string? prompt, IProgress<double>? progress, CancellationToken ct)
        {
            // Window bookkeeping for the DTW path (see below); the progress handler maps a
            // call's own 0–100 onto the whole song.
            var seek = 0;
            var span = pcm16k.Length;
            var windows = 0;
            var reportedPercent = 0;
            var builder = _factory.CreateBuilder()
                .WithTokenTimestamps()
                // No text context between 30 s windows. Conditioned on its own output, the
                // decoder locks into a repetition loop on songs with a repeated hook
                // (MAMACITA, Base: one line "heard" ~40 times over a minute, alignment off by
                // 30 s+; Medium: 18 words for the whole song). Measured on three songs, context
                // off gave the best line starts on every one (median 0.26–0.54 s vs 0.74–33 s).
                // Note whisper.cpp also skips the initial prompt when this is 0.
                .WithMaxLastTextTokens(0)
                .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 1, 8))
                .WithProgressHandler(p =>
                {
                    reportedPercent = p;
                    progress?.Report(Math.Clamp((seek + p / 100.0 * span) / Math.Max(1, pcm16k.Length), 0, 1));
                });

            var detectLanguage = string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase);
            if (detectLanguage)
                builder.WithLanguageDetection();
            else
                builder.WithLanguage(language!.Trim().ToLowerInvariant());

            // Known lyrics as the decoding prompt bias the vocabulary toward the actual words
            // (names, slang, invented spellings). Inert while the text context above is 0;
            // kept so raising it (or WithCarryInitialPrompt) brings the prompt back.
            if (!string.IsNullOrWhiteSpace(prompt))
                builder.WithPrompt(TrimPrompt(prompt));

            // whisper.cpp (up to its current master) only hands DTW-timed segments to the
            // callback while a window's segment count exceeds half the running total — after
            // the first 30 s nearly everything is silently dropped. So with DTW each call gets
            // one window: stop before the second encoder pass, then resume where whisper.cpp's
            // own loop would (NextWindowOffset). No text context crosses windows anyway (above);
            // the text can still differ a little from one call, as each call scales its log-mel
            // to its own 30 s. Checked on 10 songs: windows tile the song end to end, no word
            // is repeated or reordered at a boundary. Passing the whole song with WithOffset/
            // WithDuration instead (identical scaling) measured no better and 55% slower.
            if (_dtw)
                builder.WithEncoderBeginHandler(_ => ++windows == 1);

            var words = new List<RecognizedWord>();
            var detected = language ?? "auto";
            // Async disposal: Whisper.net refuses a synchronous Dispose while a decode is in
            // flight ("Cannot dispose while processing, please use DisposeAsync instead"),
            // which is exactly the state Stop leaves the processor in. The old `using` threw
            // that on the row, and the session then freed the native factory under a live
            // processor and took the app down (user report 09-19). DisposeAsync waits for
            // the cancelled decode to let go first.
            await using var processor = builder.Build();
            if (!_dtw)
            {
                await foreach (var segment in processor.ProcessAsync(pcm16k, ct).ConfigureAwait(false))
                {
                    if (!string.IsNullOrWhiteSpace(segment.Language)) detected = segment.Language;
                    words.AddRange(WordsFromSegment(segment));
                }
                return new Transcript(words, detected);
            }

            // Like whisper.cpp: stop when less than a second is left.
            while (seek + PcmDecoder16k.SampleRate < pcm16k.Length)
            {
                span = Math.Min(WindowSamples, pcm16k.Length - seek);
                windows = 0;
                reportedPercent = 0;
                var offset = TimeSpan.FromSeconds(seek / (double)PcmDecoder16k.SampleRate);
                long? previousEndCs = null;
                SegmentData? last = null;
                await foreach (var segment in processor.ProcessAsync(new ReadOnlyMemory<float>(pcm16k, seek, span), ct).ConfigureAwait(false))
                {
                    if (!string.IsNullOrWhiteSpace(segment.Language)) detected = segment.Language;
                    words.AddRange(WordsFromDtwSegment(segment, offset, _dtwShiftCs, ref previousEndCs));
                    last = segment;
                }
                // One language per song, as a single whisper.cpp run detects it once.
                if (detectLanguage && last is not null && !string.IsNullOrWhiteSpace(detected) && detected != "auto")
                {
                    processor.ChangeLanguage(detected);
                    detectLanguage = false;
                }
                seek += NextWindowOffset(last?.End, reportedPercent, span);
            }
            return new Transcript(words, detected);
        }

        public void Dispose() => _factory.Dispose();
    }

    /// <summary>
    /// Where whisper.cpp's own loop would start the next window, in samples from the start of
    /// the one just decoded: the end of its last segment — unless the progress whisper.cpp
    /// reported as it began the next window (whole percent of <paramref name="span"/>) lies
    /// past that end: its "single timestamp ending" rule skipped the rest of the chunk, or
    /// the last timestamp came after the last text. Always moves at least a second.
    /// </summary>
    internal static int NextWindowOffset(TimeSpan? lastSegmentEnd, int reportedPercent, int span)
    {
        const int second = PcmDecoder16k.SampleRate;
        var reported = (int)Math.Clamp((long)reportedPercent * span / 100, 0, span);
        var next = reported;
        if (lastSegmentEnd is { } end)
        {
            var endSamples = (int)Math.Clamp(Math.Round(end.TotalSeconds * second), 0, span);
            if (reported <= endSamples + second / 10) next = endSamples;
        }
        return next >= second ? next : span;
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

    /// <summary>A token with both clocks, in centiseconds: whisper.cpp's heuristic t0/t1 and its DTW time (−1 when not computed).</summary>
    public readonly record struct DtwTokenView(string Text, long T0Cs, long T1Cs, long DtwCs, float Probability);

    /// <summary>Longest a word's first piece may last, measured back from its DTW end; anything before that is the pause ahead of the word.</summary>
    internal const long MaxWordLeadCs = 30;

    /// <summary>
    /// Correction added to DTW times per model. Medium's alignment heads leave a token about
    /// 0.1 s after the hand-made timings (benchmark 09-23: word starts a median +0.15 s late
    /// uncorrected, +0.05 s with −0.10 s, line starts −0.04 s); Base's are on time.
    /// </summary>
    internal static long DtwShiftCs(WhisperAlignmentHeadsPreset heads) =>
        heads is WhisperAlignmentHeadsPreset.Medium or WhisperAlignmentHeadsPreset.MediumEn ? -10 : 0;

    /// <summary>Words of a segment from a DTW run, shifted from its window onto the song's clock.</summary>
    internal static IEnumerable<RecognizedWord> WordsFromDtwSegment(SegmentData segment, TimeSpan windowStart, long dtwShiftCs, ref long? previousEndCs)
    {
        var shift = (long)Math.Round(windowStart.TotalMilliseconds / 10);
        var tokens = (segment.Tokens ?? Array.Empty<WhisperToken>())
            .Select(t => new DtwTokenView(t.Text ?? string.Empty, t.Start + shift, t.End + shift,
                t.DtwTimestamp < 0 ? -1 : Math.Max(0, t.DtwTimestamp + shift + dtwShiftCs), t.Probability));
        return WordsFromTokens(DtwTokenSpans(tokens, ref previousEndCs), segment.Start + windowStart, segment.End + windowStart, segment.Text);
    }

    /// <summary>
    /// Token spans from DTW times. whisper.cpp stamps t_dtw where the alignment path moves on
    /// to the next token — the token's END (read as a start, every word came out a median
    /// 0.26 s late on the benchmark). So a token starts where the previous text token of the
    /// same window ended, except that a word's first piece starts at most
    /// <see cref="MaxWordLeadCs"/> before its end: after a pause the path leaves the previous
    /// word early, and without the limit words after rests started early (lines −0.18 s).
    /// The window's first token has no predecessor and starts at its t0, same limit. Tokens
    /// without a DTW time keep t0/t1. <paramref name="previousEndCs"/> carries the running
    /// end across the segments of one window; reset it per window.
    /// </summary>
    internal static List<TokenView> DtwTokenSpans(IEnumerable<DtwTokenView> tokens, ref long? previousEndCs)
    {
        var spans = new List<TokenView>();
        foreach (var t in tokens)
        {
            if (t.Text.Length == 0 || IsSpecialToken(t.Text) || t.DtwCs < 0)
            {
                spans.Add(new TokenView(t.Text, t.T0Cs, t.T1Cs, t.Probability));
                continue;
            }
            var start = previousEndCs ?? t.T0Cs;
            if (previousEndCs is null || t.Text[0] == ' ')
                start = Math.Max(start, t.DtwCs - MaxWordLeadCs);
            spans.Add(new TokenView(t.Text, Math.Min(start, t.DtwCs), t.DtwCs, t.Probability));
            previousEndCs = t.DtwCs;
        }
        return spans;
    }

    private static bool IsSpecialToken(string text) =>
        text.StartsWith("[_", StringComparison.Ordinal) || text.StartsWith("<|", StringComparison.Ordinal);

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
            if (IsSpecialToken(text)) continue;
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
