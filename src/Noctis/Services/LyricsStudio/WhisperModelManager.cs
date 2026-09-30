using System.Security.Cryptography;
using Whisper.net;
using Whisper.net.Ggml;

namespace Noctis.Services.LyricsStudio;

/// <summary>One model is offered (owner, 09-29): Lullaby, a Whisper Medium tuned on sung lyrics. The
/// size names stay so an old saved preference still parses; every size resolves to
/// <see cref="WhisperModelManager.Lullaby"/>.</summary>
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
/// and intact, and on-demand, resumable download from the Noctis GitHub release. The model is
/// big, so nothing is fetched until the user asks. One download runs at a time per file; a
/// second request joins it.
/// </summary>
public sealed class WhisperModelManager
{
    /// <summary>
    /// The one model: Lullaby, OpenAI Whisper Medium fine-tuned on sung lyrics (09-29/30), stored
    /// q8_0. On 47 held-out songs from artists it never trained on, lines more than 1 s off went
    /// 407 → 202 and WER 0.344 → 0.307 against stock Medium, at the same speed and about half the
    /// size (D:\NoctisLyricsLab reports). Same architecture as Medium, so Medium's DTW heads.
    /// The file is attached once to the fixed "lullaby-v1" release so every app version uses one URL.
    /// </summary>
    public static readonly WhisperModelInfo Lullaby = new(
        WhisperModelSize.Medium,
        "Lullaby",
        "lullaby.bin",
        "https://github.com/heartached/Noctis/releases/download/lullaby-v1/lullaby.bin",
        823_369_779L,
        "996c39be3658908ad9cbc3b71a84804afff403ad421260d334915be7c69ca86f",
        WhisperAlignmentHeadsPreset.Medium);

    /// <summary>
    /// Stock Whisper files earlier versions downloaded into the same folder. Removed once Lullaby is
    /// installed and checked, so an upgrade does not leave a dead 1.5 GB Medium behind. Never
    /// before: a failed or cancelled Lullaby download deletes nothing.
    /// </summary>
    internal static readonly string[] RetiredFileNames = { "ggml-tiny.bin", "ggml-base.bin", "ggml-small.bin", "ggml-medium.bin" };

    /// <summary>Every saved size (Tiny, Base, Small, anything) is the one model.</summary>
    public static WhisperModelSize Normalize(WhisperModelSize size) => WhisperModelSize.Medium;

    // No overall timeout: a 1.5 GB model on a slow link takes over an hour. Stalls are caught
    // per read by ResumableDownload instead.
    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    private readonly string _directory;
    private readonly HttpClient _http;
    private readonly ResumableDownload.Options? _downloadOptions;
    private readonly WhisperModelInfo _model;

    private readonly object _downloadGate = new();
    private Task? _download;
    private CancellationTokenSource? _downloadCts;
    private readonly List<IProgress<ModelDownloadProgress>> _listeners = new();
    private ModelDownloadProgress? _lastReport;

    public WhisperModelManager(string dataRoot) : this(dataRoot, null, null) { }

    /// <summary>Test seam: a fake HTTP handler, short retry delays, and a small stand-in model.</summary>
    internal WhisperModelManager(string dataRoot, HttpClient? http, ResumableDownload.Options? downloadOptions, WhisperModelInfo? model = null)
    {
        _directory = Path.Combine(dataRoot, "models", "whisper");
        _http = http ?? SharedHttp.Value;
        _downloadOptions = downloadOptions;
        _model = model ?? Lullaby;
    }

    public string Directory => _directory;

    /// <summary>The model this manager installs and loads.</summary>
    public WhisperModelInfo Model => _model;

    public static WhisperModelInfo Info(WhisperModelSize size) => Lullaby;

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
        RaiseStateChanged();
        var ok = State == WhisperModelState.Ready;
        if (ok)
        {
            DebugLogger.Info(DebugLogger.Category.Lyrics, "Whisper.ModelVerified", _model.FileName);
            RemoveRetiredModels();
        }
        else DebugLogger.Warn(DebugLogger.Category.Lyrics, "Whisper.ModelDamaged", $"{_model.FileName}: sha256 {hash}");
        return ok;
    }

    /// <summary>
    /// Deletes <see cref="RetiredFileNames"/> (with their ".part" and ".sha256") from the model
    /// folder, only while this model is <see cref="WhisperModelState.Ready"/>. Exact names in the
    /// app's own folder only; a file in use or read-only is skipped and logged.
    /// </summary>
    internal void RemoveRetiredModels()
    {
        if (State != WhisperModelState.Ready) return;
        foreach (var name in RetiredFileNames)
        {
            if (string.Equals(name, _model.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var path in new[] { name, name + ".part", name + ".sha256" }.Select(n => Path.Combine(_directory, n)))
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var bytes = new FileInfo(path).Length;
                    File.Delete(path);
                    DebugLogger.Info(DebugLogger.Category.Lyrics, "Whisper.RetiredModelRemoved", $"{Path.GetFileName(path)} ({bytes} bytes)");
                }
                catch (Exception ex)
                {
                    DebugLogger.Warn(DebugLogger.Category.Lyrics, "Whisper.RetiredModelKept", $"{Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// A download started or ended (any outcome), or a check finished. Raised on a worker thread;
    /// a Studio on screen refreshes its model banner from it.
    /// </summary>
    public event EventHandler? StateChanged;

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.Lyrics, "Whisper.StateChangedHandlerFailed", ex.Message); }
    }

    /// <summary>True while a download (or its checksum pass) runs.</summary>
    public bool IsDownloading
    {
        get { lock (_downloadGate) return _download is { IsCompleted: false }; }
    }

    /// <summary>The running download's latest report, or null when none runs.</summary>
    public ModelDownloadProgress? CurrentDownload
    {
        get { lock (_downloadGate) return _download is { IsCompleted: false } ? _lastReport : null; }
    }

    /// <summary>
    /// Downloads the model to a ".part" file, checks its SHA-256 and moves it into place. A
    /// dropped or stalled connection is retried and resumed (<see cref="ResumableDownload"/>), and
    /// the ".part" survives a failure or a cancel so the next attempt continues where this one
    /// stopped (09-25 Discord: Medium "fails halfway" on a slow link). A download already running
    /// is joined rather than started twice: two downloads fought over the same ".part" and the
    /// second failed with "file in use" (reopening the Studio mid-download offered Download again).
    /// A file that fails the checksum is discarded and <see cref="WhisperModelIntegrityException"/> thrown.
    /// </summary>
    public Task DownloadAsync(WhisperModelSize size, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        Task download;
        var started = false;
        lock (_downloadGate)
        {
            if (progress is not null) _listeners.Add(progress);
            if (_download is not { IsCompleted: false })
            {
                _downloadCts?.Dispose();
                _downloadCts = new CancellationTokenSource();
                _lastReport = null;
                var token = _downloadCts.Token;
                _download = Task.Run(() => RunDownloadAsync(token));
                // After completion, so a listener already reads IsDownloading as false.
                _download.ContinueWith(_ => RaiseStateChanged(), TaskScheduler.Default);
                started = true;
            }
            download = _download;
        }
        if (started) RaiseStateChanged();
        if (ct.CanBeCanceled)
        {
            var registration = ct.Register(CancelDownload);
            _ = download.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        }
        if (progress is not null)
            _ = download.ContinueWith(_ => { lock (_downloadGate) _listeners.Remove(progress); }, TaskScheduler.Default);
        return download;
    }

    /// <summary>Stops the running download; its ".part" is kept for a later resume.</summary>
    public void CancelDownload()
    {
        lock (_downloadGate)
        {
            try { _downloadCts?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private void Report(ModelDownloadProgress report)
    {
        IProgress<ModelDownloadProgress>[] listeners;
        lock (_downloadGate)
        {
            _lastReport = report;
            listeners = _listeners.ToArray();
        }
        foreach (var l in listeners) l.Report(report);
    }

    private async Task RunDownloadAsync(CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(_directory);
        var target = ModelPath;
        var temp = PartPath;
        long done = PartialBytes, total = _model.Bytes;
        Report(new ModelDownloadProgress(ModelDownloadPhase.Connecting, done, total));
        var options = (_downloadOptions ?? ResumableDownload.Options.Default) with
        {
            OnRetry = (attempt, delay, error) => Report(new ModelDownloadProgress(ModelDownloadPhase.Retrying, done, total,
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
                Report(new ModelDownloadProgress(ModelDownloadPhase.Downloading, done, total));
            },
            "Whisper." + _model.FileName,
            ct,
            options).ConfigureAwait(false);

        Report(new ModelDownloadProgress(ModelDownloadPhase.Verifying, done, total));
        var length = new FileInfo(temp).Length;
        var hash = length != _model.Bytes || _model.Sha256 is null
            ? SizeOnly
            : ComputeSha256(temp, new InlineProgress<double>(f => Report(new ModelDownloadProgress(ModelDownloadPhase.Verifying, done, total, f))), ct);
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
        Report(new ModelDownloadProgress(ModelDownloadPhase.Done, length, length, 1));
        DebugLogger.Info(DebugLogger.Category.Lyrics, "Whisper.ModelInstalled", $"{_model.FileName} ({length} bytes, checksum ok)");
        RemoveRetiredModels();
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
