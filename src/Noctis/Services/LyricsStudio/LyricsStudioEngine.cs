using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services.LyricsStudio;

public enum LyricsStudioSource
{
    /// <summary>The track's own plain (or previously synced) lyrics were re-timed.</summary>
    ExistingLyrics,
    /// <summary>Plain lyrics came from LRCLIB and were timed against the audio.</summary>
    Lrclib,
    /// <summary>No lyrics anywhere: the speech model's transcript is the lyrics.</summary>
    Transcription,
}

public sealed record LyricsStudioOptions(
    WhisperModelSize Model,
    string Language,
    bool AllowOnlineLyrics = true,
    bool ForceTranscription = false);

public sealed record LyricsStudioProgress(string Stage, double Fraction);

public sealed record LyricsStudioResult(
    Track Track,
    IReadOnlyList<AlignedLine> Lines,
    LyricsStudioSource Source,
    double Confidence,
    string Language,
    int HeardWords);

public interface ILyricsStudioEngine
{
    bool HasFfmpeg { get; }
    WhisperModelManager Models { get; }

    /// <summary>Loads the model once; dispose after the run.</summary>
    IDisposable OpenSession(WhisperModelSize model);

    Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct);
}

/// <summary>
/// One track through Lyrics Studio: pick the text (own lyrics → LRCLIB → none), listen with
/// Whisper (lyrics as the prompt when known), then align words to lines or group the
/// transcript into lines. Nothing is written — the caller reviews and saves.
/// </summary>
public sealed class LyricsStudioEngine : ILyricsStudioEngine
{
    private readonly IAudioConverterService _ffmpeg;
    private readonly ILrcLibService _lrcLib;
    private WhisperTranscriber.Session? _session;
    private readonly object _sessionGate = new();

    public WhisperModelManager Models { get; }

    public LyricsStudioEngine(IAudioConverterService ffmpeg, ILrcLibService lrcLib, IPersistenceService persistence)
    {
        _ffmpeg = ffmpeg;
        _lrcLib = lrcLib;
        Models = new WhisperModelManager(persistence.DataDirectory);
    }

    public bool HasFfmpeg => _ffmpeg.GetFfmpegPath() != null;

    public IDisposable OpenSession(WhisperModelSize model)
    {
        var path = Models.PathFor(model);
        if (!Models.IsInstalled(model))
            throw new InvalidOperationException($"The {WhisperModelManager.Info(model).DisplayName} model is not installed.");
        lock (_sessionGate)
        {
            _session?.Dispose();
            _session = new WhisperTranscriber.Session(path);
            return new SessionHandle(this);
        }
    }

    private sealed class SessionHandle : IDisposable
    {
        private readonly LyricsStudioEngine _owner;
        public SessionHandle(LyricsStudioEngine owner) => _owner = owner;
        public void Dispose()
        {
            lock (_owner._sessionGate)
            {
                _owner._session?.Dispose();
                _owner._session = null;
            }
        }
    }

    public async Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct)
    {
        var ffmpeg = _ffmpeg.GetFfmpegPath() ?? throw new InvalidOperationException("ffmpeg is required to decode the song. Set its path under Settings → Audio → Audio tools.");
        if (string.IsNullOrWhiteSpace(track.FilePath) || !File.Exists(track.FilePath))
            throw new FileNotFoundException("The audio file is missing.", track.FilePath);

        WhisperTranscriber.Session session;
        lock (_sessionGate)
            session = _session ?? throw new InvalidOperationException("No model session — call OpenSession first.");

        // 1. Source text.
        progress?.Report(new LyricsStudioProgress("Finding lyrics", 0.02));
        var (lines, source) = options.ForceTranscription
            ? (null, LyricsStudioSource.Transcription)
            : await ResolveSourceLinesAsync(track, options.AllowOnlineLyrics, ct).ConfigureAwait(false);

        // 2. Decode.
        progress?.Report(new LyricsStudioProgress("Decoding", 0.08));
        var pcm = await PcmDecoder16k.DecodeAsync(ffmpeg, track.FilePath, ct).ConfigureAwait(false);
        if (pcm.Length < PcmDecoder16k.SampleRate)
            throw new InvalidOperationException("The song is too short to analyse.");

        // 3. Listen.
        var listenProgress = new Progress<double>(f => progress?.Report(new LyricsStudioProgress("Listening", 0.1 + 0.8 * f)));
        var prompt = lines is { Count: > 0 } ? string.Join('\n', lines) : null;
        var transcript = await session.TranscribeAsync(pcm, options.Language, prompt, listenProgress, ct).ConfigureAwait(false);

        // 4. Align or group.
        progress?.Report(new LyricsStudioProgress("Aligning", 0.95));
        var duration = track.Duration > TimeSpan.Zero ? track.Duration : TimeSpan.FromSeconds(pcm.Length / (double)PcmDecoder16k.SampleRate);
        IReadOnlyList<AlignedLine> aligned;
        if (lines is { Count: > 0 })
            aligned = LyricsAligner.Align(lines, transcript.Words, duration);
        else
        {
            aligned = TranscriptLines.Group(transcript.Words);
            source = LyricsStudioSource.Transcription;
        }

        var confidence = aligned.Count == 0 ? 0 : aligned.Average(l => l.Confidence);
        progress?.Report(new LyricsStudioProgress("Ready", 1));
        DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsStudio.Processed",
            $"{track.Title}: source={source}, lines={aligned.Count}, heard={transcript.Words.Count}, confidence={confidence:0.00}, lang={transcript.Language}");
        return new LyricsStudioResult(track, aligned, source, confidence, transcript.Language, transcript.Words.Count);
    }

    private async Task<(List<string>? Lines, LyricsStudioSource Source)> ResolveSourceLinesAsync(Track track, bool allowOnline, CancellationToken ct)
    {
        var own = FirstText(track.Lyrics, track.SyncedLyrics);
        if (own is not null) return (own, LyricsStudioSource.ExistingLyrics);

        if (allowOnline)
        {
            try
            {
                var result = await _lrcLib.GetLyricsAsync(track.Artist ?? string.Empty, track.Title ?? string.Empty, track.Duration.TotalSeconds, ct).ConfigureAwait(false);
                var online = FirstText(result?.PlainLyrics, result?.SyncedLyrics);
                if (online is not null) return (online, LyricsStudioSource.Lrclib);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsStudio.LrclibFailed", ex.Message);
            }
        }
        return (null, LyricsStudioSource.Transcription);
    }

    /// <summary>Plain lines from the first non-empty text; timestamps are stripped so synced text can be re-timed.</summary>
    internal static List<string>? FirstText(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            var plain = LyricsTextHelper.ContainsTimestamps(c) ? LyricsTextHelper.StripTimestamps(c) : c;
            var lines = plain.Split('\n')
                .Select(l => l.Trim('\r', ' ', '\t'))
                .Where(l => l.Length > 0 && !IsMetadataTag(l))
                .ToList();
            if (lines.Count > 0) return lines;
        }
        return null;
    }

    /// <summary>LRC header tags like [ar:], [ti:], [length:] are not lyrics.</summary>
    private static bool IsMetadataTag(string line) =>
        line.Length > 3 && line[0] == '[' && line.EndsWith(']') && line.IndexOf(':') is var i && i > 1 && line[1..i].All(char.IsLetter);
}
