using Whisper.net.Ggml;

namespace Noctis.Services.LyricsStudio;

public enum WhisperModelSize { Tiny, Base, Small, Medium }

public sealed record WhisperModelInfo(WhisperModelSize Size, string DisplayName, string FileName, long ApproxBytes, string Description)
{
    public string SizeText => ApproxBytes >= 1L << 30
        ? $"{ApproxBytes / (double)(1L << 30):0.#} GB"
        : $"{ApproxBytes / (double)(1L << 20):0} MB";
}

/// <summary>
/// Whisper (ggml) speech models for Lyrics Studio: where they live, which are installed,
/// and on-demand download from the official whisper.cpp mirror through Whisper.net's
/// downloader. Models are big, so nothing is fetched until the user asks.
/// </summary>
public sealed class WhisperModelManager
{
    public static readonly IReadOnlyList<WhisperModelInfo> Catalog = new[]
    {
        new WhisperModelInfo(WhisperModelSize.Tiny, "Tiny", "ggml-tiny.bin", 77_691_713L, "Fastest. Rough timing, misses words in dense mixes."),
        new WhisperModelInfo(WhisperModelSize.Base, "Base", "ggml-base.bin", 147_951_465L, "Good balance for syncing lyrics you already have."),
        new WhisperModelInfo(WhisperModelSize.Small, "Small", "ggml-small.bin", 487_601_967L, "Accurate transcription; a few minutes per song on a laptop."),
        new WhisperModelInfo(WhisperModelSize.Medium, "Medium", "ggml-medium.bin", 1_533_774_781L, "Most accurate. Slow without a fast CPU."),
    };

    private readonly string _directory;

    public WhisperModelManager(string dataRoot)
    {
        _directory = Path.Combine(dataRoot, "models", "whisper");
    }

    public string Directory => _directory;

    public static WhisperModelInfo Info(WhisperModelSize size) => Catalog.First(m => m.Size == size);

    public static WhisperModelSize Parse(string? name) =>
        Enum.TryParse<WhisperModelSize>(name, ignoreCase: true, out var size) ? size : WhisperModelSize.Base;

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

    /// <summary>Downloads the model to a temp file and moves it into place; progress is 0–1 against the published size.</summary>
    public async Task DownloadAsync(WhisperModelSize size, IProgress<double>? progress, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(_directory);
        var target = PathFor(size);
        var temp = target + ".part";
        var info = Info(size);
        try
        {
            await using var source = await WhisperGgmlDownloader.Default
                .GetGgmlModelAsync(ToGgml(size), QuantizationType.NoQuantization, ct).ConfigureAwait(false);
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    total += read;
                    progress?.Report(Math.Min(0.999, total / (double)info.ApproxBytes));
                }
            }
            File.Move(temp, target, overwrite: true);
            progress?.Report(1);
            DebugLogger.Info(DebugLogger.Category.Lyrics, "Whisper.ModelInstalled", $"{info.FileName} ({new FileInfo(target).Length} bytes)");
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    public void Delete(WhisperModelSize size)
    {
        try { File.Delete(PathFor(size)); } catch { }
    }

    private static GgmlType ToGgml(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Tiny => GgmlType.Tiny,
        WhisperModelSize.Small => GgmlType.Small,
        WhisperModelSize.Medium => GgmlType.Medium,
        _ => GgmlType.Base,
    };
}
