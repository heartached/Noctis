using Whisper.net;
using Whisper.net.Ggml;

namespace Noctis.Services.LyricsStudio;

/// <summary>Tiny and Small are no longer offered (2026-09-07: two choices are enough);
/// the names stay so an old saved preference still parses and maps onto the pair.</summary>
public enum WhisperModelSize { Tiny, Base, Small, Medium }

public sealed record WhisperModelInfo(WhisperModelSize Size, string DisplayName, string FileName, long ApproxBytes, string Description)
{
    public string SizeText => ApproxBytes >= 1L << 30
        ? $"{ApproxBytes / (double)(1L << 30):0.#} GB"
        : $"{ApproxBytes / (double)(1L << 20):0} MB";
}

/// <summary>
/// Whisper (ggml) speech models for Lyrics Studio: where they live, which are installed,
/// and on-demand, resumable download from Whisper.net's Hugging Face mirror. Models are big,
/// so nothing is fetched until the user asks.
/// </summary>
public sealed class WhisperModelManager
{
    public static readonly IReadOnlyList<WhisperModelInfo> Catalog = new[]
    {
        new WhisperModelInfo(WhisperModelSize.Base, "Base", "ggml-base.bin", 147_951_465L, "Quick. Times lyrics you already have."),
        new WhisperModelInfo(WhisperModelSize.Medium, "Medium", "ggml-medium.bin", 1_533_774_781L, "Most accurate. Slow without a fast CPU."),
    };

    /// <summary>Folds the retired sizes onto the offered pair: Tiny → Base, Small → Medium.</summary>
    public static WhisperModelSize Normalize(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Tiny => WhisperModelSize.Base,
        WhisperModelSize.Small => WhisperModelSize.Medium,
        _ => size,
    };

    // No overall timeout: a 1.5 GB model on a slow link takes over an hour. Stalls are caught
    // per read by ResumableDownload instead.
    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    private readonly string _directory;
    private readonly HttpClient _http;
    private readonly ResumableDownload.Options? _downloadOptions;

    public WhisperModelManager(string dataRoot) : this(dataRoot, null, null) { }

    /// <summary>Test seam: a fake HTTP handler and short retry delays.</summary>
    internal WhisperModelManager(string dataRoot, HttpClient? http, ResumableDownload.Options? downloadOptions)
    {
        _directory = Path.Combine(dataRoot, "models", "whisper");
        _http = http ?? SharedHttp.Value;
        _downloadOptions = downloadOptions;
    }

    public string Directory => _directory;

    public static WhisperModelInfo Info(WhisperModelSize size) => Catalog.First(m => m.Size == Normalize(size));

    public static WhisperModelSize Parse(string? name) =>
        Enum.TryParse<WhisperModelSize>(name, ignoreCase: true, out var size) ? Normalize(size) : WhisperModelSize.Base;

    public string PathFor(WhisperModelSize size) => Path.Combine(_directory, Info(size).FileName);

    public bool IsInstalled(WhisperModelSize size)
    {
        try
        {
            var info = new FileInfo(PathFor(size));
            // A partial download is not a model: require at least 90% of the published size.
            return info.Exists && info.Length >= Info(size).ApproxBytes * 9 / 10;
        }
        catch { return false; }
    }

    public IReadOnlyList<WhisperModelSize> Installed() => Catalog.Where(m => IsInstalled(m.Size)).Select(m => m.Size).ToList();

    /// <summary>
    /// Downloads the model to a ".part" file and moves it into place; progress is 0–1. A dropped or
    /// stalled connection is retried and resumed (<see cref="ResumableDownload"/>), and the ".part"
    /// survives a failure so the next attempt continues where this one stopped (09-25 Discord: Medium
    /// "fails halfway" on a slow link, and each retry used to start again from zero).
    /// </summary>
    public async Task DownloadAsync(WhisperModelSize size, IProgress<double>? progress, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(_directory);
        var target = PathFor(size);
        var temp = target + ".part";
        var info = Info(size);
        await ResumableDownload.DownloadAsync(
            _http,
            () => new HttpRequestMessage(HttpMethod.Get, ModelUrl(size)),
            temp,
            resumeExisting: true,
            (done, total) => progress?.Report(Math.Min(0.999, done / (double)(total > 0 ? total : info.ApproxBytes))),
            "Whisper." + info.FileName,
            ct,
            _downloadOptions).ConfigureAwait(false);
        File.Move(temp, target, overwrite: true);
        progress?.Report(1);
        DebugLogger.Info(DebugLogger.Category.Lyrics, "Whisper.ModelInstalled", $"{info.FileName} ({new FileInfo(target).Length} bytes)");
    }

    /// <summary>
    /// The URL Whisper.net 1.9.1's WhisperGgmlDownloader builds for an unquantized model
    /// ("{repo}/v4/classic/{name}.bin"). Fetched directly because that downloader cannot resume.
    /// </summary>
    internal static string ModelUrl(WhisperModelSize size) =>
        "https://huggingface.co/sandrohanea/whisper.net/resolve/v4/classic/" + Info(size).FileName;

    public void Delete(WhisperModelSize size)
    {
        try { File.Delete(PathFor(size)); } catch { }
    }

    /// <summary>Cross-attention heads for DTW word timing, matching the file <see cref="PathFor"/> loads.</summary>
    public static WhisperAlignmentHeadsPreset AlignmentHeads(WhisperModelSize size) => Normalize(size) switch
    {
        WhisperModelSize.Medium => WhisperAlignmentHeadsPreset.Medium,
        _ => WhisperAlignmentHeadsPreset.Base,
    };
}
