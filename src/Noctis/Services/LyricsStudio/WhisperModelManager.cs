using System.Security.Cryptography;
using Whisper.net;
using Whisper.net.Ggml;

namespace Noctis.Services.LyricsStudio;

/// <summary>Only Medium is offered (owner, 09-29: one model, the strongest). The other names stay
/// so an old saved preference still parses; every size resolves to <see cref="WhisperModelManager.Medium"/>.</summary>
public enum WhisperModelSize { Tiny, Base, Small, Medium }

/// <summary>
/// The speech model file: where it comes from, its exact length and SHA-256 (null = checked by
/// length only), and the cross-attention heads its DTW word timing needs. Swapping in another
/// build of the model means changing this one entry.
/// </summary>
public sealed record WhisperModelInfo(
    WhisperModelSize Size,
    string DisplayName,
    string FileName,
    string Url,
    long Bytes,
    string? Sha256,
    WhisperAlignmentHeadsPreset AlignmentHeads)
{
    public string SizeText => ByteText.Format(Bytes);
}

/// <summary>What is on disk for the model.</summary>
public enum WhisperModelState
{
    /// <summary>Nothing downloaded.</summary>
    Missing,
    /// <summary>A stopped download: the ".part" holds the first bytes and the next download continues it.</summary>
    Partial,
    /// <summary>Whole by length, checksum not checked yet (installed before checks existed); usable, checked before the first run.</summary>
    Unverified,
    /// <summary>Checked and ready to load.</summary>
    Ready,
    /// <summary>Wrong length or a failed checksum: not loaded, download it again.</summary>
    Damaged,
}

public enum ModelDownloadPhase { Connecting, Downloading, Retrying, Verifying, Done }

/// <summary>
/// One model download report. <see cref="BytesDone"/> counts bytes on disk (a resumed download
/// starts above zero); <see cref="VerifyFraction"/> is the checksum pass; <see cref="Attempt"/>,
/// <see cref="RetryIn"/> and <see cref="Error"/> describe the retry being waited out.
/// </summary>
public sealed record ModelDownloadProgress(
    ModelDownloadPhase Phase,
    long BytesDone,
    long BytesTotal,
    double VerifyFraction = 0,
    int Attempt = 0,
    TimeSpan RetryIn = default,
    string? Error = null);

/// <summary>The downloaded file is not the published model (checksum or length mismatch); it was discarded.</summary>
public sealed class WhisperModelIntegrityException : Exception
{
    public WhisperModelIntegrityException(string message) : base(message) { }
}

/// <summary>
/// The Whisper (ggml) speech model for Lyrics Studio: where it lives, whether it is installed
/// and intact, and on-demand, resumable download from Whisper.net's Hugging Face mirror. The
/// model is big, so nothing is fetched until the user asks. One download runs at a time per
/// file; a second request joins it.
/// </summary>
public sealed class WhisperModelManager
{
    /// <summary>
    /// The one model. Length and SHA-256 are the Hugging Face LFS values for classic/ggml-medium.bin
    /// (sandrohanea/whisper.net v4, identical to ggerganov/whisper.cpp's ggml-medium.bin; checked 09-29).
    /// </summary>
    public static readonly WhisperModelInfo Medium = new(
        WhisperModelSize.Medium,
        "Medium",
        "ggml-medium.bin",
        // The URL Whisper.net 1.9.1's WhisperGgmlDownloader builds for an unquantized model
        // ("{repo}/v4/classic/{name}.bin"). Fetched directly because that downloader cannot resume.
        "https://huggingface.co/sandrohanea/whisper.net/resolve/v4/classic/ggml-medium.bin",
        1_533_763_059L,
        "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208",
        WhisperAlignmentHeadsPreset.Medium);

    /// <summary>Every saved size (Tiny, Base, Small, anything) is the one model.</summary>
    public static WhisperModelSize Normalize(WhisperModelSize size) => WhisperModelSize.Medium;

    // No overall timeout: a 1.5 GB model on a slow link takes over an hour. Stalls are caught
    // per read by ResumableDownload instead.
    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    private readonly string _directory;
    private readonly HttpClient _http;
    private readonly ResumableDownload.Options? _downloadOptions;
    private readonly WhisperModelInfo _model;

    public WhisperModelManager(string dataRoot) : this(dataRoot, null, null) { }

    /// <summary>Test seam: a fake HTTP handler, short retry delays, and a small stand-in model.</summary>
    internal WhisperModelManager(string dataRoot, HttpClient? http, ResumableDownload.Options? downloadOptions, WhisperModelInfo? model = null)
    {
        _directory = Path.Combine(dataRoot, "models", "whisper");
        _http = http ?? SharedHttp.Value;
        _downloadOptions = downloadOptions;
        _model = model ?? Medium;
    }

    public string Directory => _directory;

    /// <summary>The model this manager installs and loads.</summary>
    public WhisperModelInfo Model => _model;

    public static WhisperModelInfo Info(WhisperModelSize size) => Medium;

    /// <summary>Any saved name, known or not, resolves to the one model.</summary>
    public static WhisperModelSize Parse(string? name) => WhisperModelSize.Medium;

    public string ModelPath => Path.Combine(_directory, _model.FileName);
    public string PathFor(WhisperModelSize size) => ModelPath;
    private string PartPath => ModelPath + ".part";
    /// <summary>Checksum record written after a check: "sha256 length lastWriteUtcTicks" of the file it checked.</summary>
    private string MarkerPath => ModelPath + ".sha256";
    /// <summary>Marker hash for a model entry with no published checksum (checked by length only).</summary>
    private const string SizeOnly = "size-only";

    public bool IsInstalled(WhisperModelSize size) => IsInstalled();

    /// <summary>Usable: checked, or whole by length and not yet checked (checked before its first run).</summary>
    public bool IsInstalled() => State is WhisperModelState.Ready or WhisperModelState.Unverified;

    public WhisperModelState State
    {
        get
        {
            try
            {
                var file = new FileInfo(ModelPath);
                if (file.Exists)
                {
                    if (file.Length != _model.Bytes) return WhisperModelState.Damaged;
                    return ReadMarker(file) switch
                    {
                        null => WhisperModelState.Unverified,
                        var hash when _model.Sha256 is null || hash.Equals(_model.Sha256, StringComparison.OrdinalIgnoreCase) => WhisperModelState.Ready,
                        _ => WhisperModelState.Damaged,
                    };
                }
                return PartialBytes > 0 ? WhisperModelState.Partial : WhisperModelState.Missing;
            }
            catch { return WhisperModelState.Missing; }
        }
    }

    /// <summary>Bytes of a stopped download waiting to be resumed (0 when none).</summary>
    public long PartialBytes
    {
        get
        {
            try { var part = new FileInfo(PartPath); return part.Exists ? part.Length : 0; }
            catch { return 0; }
        }
    }

    /// <summary>
    /// Checks a file installed before checksums existed (<see cref="WhisperModelState.Unverified"/>)
    /// and records the result, so the ~1.5 GB read happens once. True when the model is fine.
    /// </summary>
    public async Task<bool> VerifyAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var state = State;
        if (state == WhisperModelState.Ready) return true;
        if (state != WhisperModelState.Unverified) return false;
        var path = ModelPath;
        var hash = _model.Sha256 is null ? SizeOnly : await Task.Run(() => ComputeSha256(path, progress, ct), ct).ConfigureAwait(false);
        WriteMarker(path, hash);
        var ok = State == WhisperModelState.Ready;
        if (ok) DebugLogger.Info(DebugLogger.Category.Lyrics, "Whisper.ModelVerified", _model.FileName);
        else DebugLogger.Warn(DebugLogger.Category.Lyrics, "Whisper.ModelDamaged", $"{_model.FileName}: sha256 {hash}");
        return ok;
    }

    /// <summary>
    /// Downloads the model to a ".part" file, checks its SHA-256 and moves it into place. A
    /// dropped or stalled connection is retried and resumed (<see cref="ResumableDownload"/>), and
    /// the ".part" survives a failure so the next attempt continues where this one stopped (09-25
    /// Discord: Medium "fails halfway" on a slow link). A file that fails the checksum is discarded
    /// and <see cref="WhisperModelIntegrityException"/> thrown.
    /// </summary>
    public Task DownloadAsync(WhisperModelSize size, IProgress<ModelDownloadProgress>? progress, CancellationToken ct) =>
        RunDownloadAsync(progress, ct);

    private async Task RunDownloadAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(_directory);
        var target = ModelPath;
        var temp = PartPath;
        long done = PartialBytes, total = _model.Bytes;
        progress?.Report(new ModelDownloadProgress(ModelDownloadPhase.Connecting, done, total));
        var options = (_downloadOptions ?? ResumableDownload.Options.Default) with
        {
            OnRetry = (attempt, delay, error) => progress?.Report(new ModelDownloadProgress(ModelDownloadPhase.Retrying, done, total,
                Attempt: attempt, RetryIn: delay, Error: error?.Message)),
        };
        await ResumableDownload.DownloadAsync(
            _http,
            () => new HttpRequestMessage(HttpMethod.Get, _model.Url),
            temp,
            resumeExisting: true,
            (bytes, length) =>
            {
                done = bytes;
                if (length > 0) total = length;
                progress?.Report(new ModelDownloadProgress(ModelDownloadPhase.Downloading, done, total));
            },
            "Whisper." + _model.FileName,
            ct,
            options).ConfigureAwait(false);

        progress?.Report(new ModelDownloadProgress(ModelDownloadPhase.Verifying, done, total));
        var length = new FileInfo(temp).Length;
        var hash = length != _model.Bytes || _model.Sha256 is null
            ? SizeOnly
            : ComputeSha256(temp, new InlineProgress<double>(f => progress?.Report(new ModelDownloadProgress(ModelDownloadPhase.Verifying, done, total, f))), ct);
        if (length != _model.Bytes || (_model.Sha256 is not null && !hash.Equals(_model.Sha256, StringComparison.OrdinalIgnoreCase)))
        {
            // Resuming onto these bytes would keep the damage: start the next try from zero.
            try { File.Delete(temp); } catch { }
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "Whisper.ModelChecksumFailed", $"{_model.FileName}: {length} bytes, sha256 {hash}");
            throw new WhisperModelIntegrityException(length != _model.Bytes
                ? $"The download is {length:N0} bytes, not the {_model.Bytes:N0} expected."
                : "The download did not match the published checksum.");
        }

        File.Move(temp, target, overwrite: true);
        WriteMarker(target, hash);
        progress?.Report(new ModelDownloadProgress(ModelDownloadPhase.Done, length, length, 1));
        DebugLogger.Info(DebugLogger.Category.Lyrics, "Whisper.ModelInstalled", $"{_model.FileName} ({length} bytes, checksum ok)");
    }

    /// <summary>SHA-256 of a file as lowercase hex, read in 1 MB blocks; progress 0–1.</summary>
    internal static string ComputeSha256(string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var buffer = new byte[1 << 20];
        var length = Math.Max(1, stream.Length);
        long read = 0;
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, n);
            read += n;
            progress?.Report(read / (double)length);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private void WriteMarker(string path, string hash)
    {
        try
        {
            var file = new FileInfo(path);
            File.WriteAllText(MarkerPath, $"{hash} {file.Length} {file.LastWriteTimeUtc.Ticks}");
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "Whisper.MarkerWriteFailed", ex.Message);
        }
    }

    /// <summary>The recorded hash when the marker describes this exact file (same length and write time), else null.</summary>
    private string? ReadMarker(FileInfo file)
    {
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            var parts = File.ReadAllText(MarkerPath).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 3
                && long.TryParse(parts[1], out var length) && length == file.Length
                && long.TryParse(parts[2], out var ticks) && ticks == file.LastWriteTimeUtc.Ticks
                ? parts[0]
                : null;
        }
        catch { return null; }
    }

    internal static string ModelUrl(WhisperModelSize size) => Info(size).Url;

    /// <summary>Cross-attention heads for DTW word timing, matching the file <see cref="PathFor"/> loads.</summary>
    public static WhisperAlignmentHeadsPreset AlignmentHeads(WhisperModelSize size) => Info(size).AlignmentHeads;
}

/// <summary>An <see cref="IProgress{T}"/> that calls back on the reporting thread, in order
/// (<see cref="Progress{T}"/> posts each report to the thread pool when there is no UI context,
/// so reports can land out of order).</summary>
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>Byte counts for people: "612 MB", "1.43 GB" (binary units, as Windows shows them).</summary>
public static class ByteText
{
    public static string Format(long bytes, bool precise = false)
    {
        var c = System.Globalization.CultureInfo.CurrentCulture;
        if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString(precise ? "0.00" : "0.#", c) + " GB";
        if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0", c) + " MB";
        if (bytes >= 1L << 10) return (bytes / (double)(1L << 10)).ToString("0", c) + " KB";
        return bytes.ToString(c) + " B";
    }
}
