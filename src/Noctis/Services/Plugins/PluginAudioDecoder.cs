using System.Diagnostics;
using System.Globalization;
using Noctis.Plugins;

namespace Noctis.Services.Plugins;

/// <summary>
/// "audio.decode" (API 1.6): a plugin decodes a song to mono float PCM with the ffmpeg Noctis
/// already uses for its own BPM/key analysis (<see cref="AudioAnalysis.AudioAnalysisService"/>),
/// out of process. Bounded in every direction: rate 8–48 kHz, at most 20 minutes of audio, two
/// decodes at a time across all plugins, a 3-minute watchdog per decode, and a stopped plugin's
/// calls fail.
/// </summary>
internal sealed class PluginAudioDecoder : IAudioDecoder
{
    internal const int MinRate = 8000;
    internal const int MaxRate = 48000;
    internal static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(20);
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    // Shared by every plugin: two ffmpeg processes at most, so plugins cannot flood the machine.
    private static readonly SemaphoreSlim Gate = new(2, 2);

    private readonly Func<string?> _ffmpegPath;
    private readonly Func<bool> _isActive;
    private readonly string _pluginId;

    public PluginAudioDecoder(Func<string?> ffmpegPath, Func<bool> isActive, string pluginId)
    {
        _ffmpegPath = ffmpegPath;
        _isActive = isActive;
        _pluginId = pluginId;
    }

    public bool IsAvailable
    {
        get
        {
            try { return _ffmpegPath() is { Length: > 0 }; }
            catch { return false; }
        }
    }

    public async Task<DecodedAudio> DecodeMonoAsync(string filePath, int sampleRate, TimeSpan maxDuration, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
            throw new ArgumentException("An absolute file path is needed.", nameof(filePath));
        if (!File.Exists(filePath)) throw new FileNotFoundException("The song file does not exist.", filePath);
        if (!_isActive()) throw new InvalidOperationException("The plugin is not running.");
        string? ffmpeg;
        try { ffmpeg = _ffmpegPath(); }
        catch { ffmpeg = null; }
        if (string.IsNullOrEmpty(ffmpeg))
            throw new InvalidOperationException("Noctis has no decoder: set ffmpeg under Settings → Advanced → Helper programs.");

        var (rate, seconds) = Clamp(sampleRate, maxDuration);
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
            watchdog.CancelAfter(Timeout);
            try
            {
                var samples = await Task.Run(() => DecodeAsync(ffmpeg, filePath, rate, seconds, watchdog.Token), watchdog.Token).ConfigureAwait(false);
                DebugLogger.Info(DebugLogger.Category.State, "Plugins", $"{_pluginId} decoded {samples.Length / (double)rate:0.0} s at {rate} Hz");
                return new DecodedAudio(samples, rate);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException("Decoding the song took too long.");
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The rate and length actually used: rate 8000–48000 Hz, 0.1 s to 20 minutes.</summary>
    internal static (int Rate, double Seconds) Clamp(int sampleRate, TimeSpan maxDuration)
    {
        var rate = Math.Clamp(sampleRate, MinRate, MaxRate);
        var seconds = maxDuration <= TimeSpan.Zero || maxDuration > MaxDuration ? MaxDuration.TotalSeconds : Math.Max(0.1, maxDuration.TotalSeconds);
        return (rate, seconds);
    }

    /// <summary>ffmpeg's arguments: first audio stream, mono, the rate, float32 LE to stdout, for <paramref name="seconds"/>.</summary>
    internal static IReadOnlyList<string> Arguments(string filePath, int rate, double seconds) => new[]
    {
        "-nostats", "-hide_banner", "-loglevel", "error", "-nostdin",
        "-t", seconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", filePath,
        "-map", "0:a:0", "-ac", "1", "-ar", rate.ToString(CultureInfo.InvariantCulture),
        "-f", "f32le", "-",
    };

    private static async Task<float[]> DecodeAsync(string ffmpeg, string filePath, int rate, double seconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in Arguments(filePath, rate, seconds)) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
        using var kill = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);

        var maxSamples = (long)Math.Ceiling(rate * seconds) + rate; // ffmpeg can run a little past -t
        var samples = new float[Math.Min(maxSamples, rate * 60L * 5)];
        var count = 0;
        var bytes = new byte[1 << 16];
        var carry = 0;
        var stdout = p.StandardOutput.BaseStream;
        while (true)
        {
            var n = await stdout.ReadAsync(bytes.AsMemory(carry, bytes.Length - carry), ct).ConfigureAwait(false);
            if (n == 0) break;
            var total = carry + n;
            var whole = total / 4;
            if (count + whole > samples.Length && samples.Length < maxSamples)
                Array.Resize(ref samples, (int)Math.Min(maxSamples, Math.Max(samples.Length * 2L, count + whole)));
            var take = Math.Min(whole, samples.Length - count);
            Buffer.BlockCopy(bytes, 0, samples, count * 4, take * 4);
            count += take;
            carry = total - whole * 4;
            if (carry > 0) Buffer.BlockCopy(bytes, whole * 4, bytes, 0, carry);
            if (count >= samples.Length && samples.Length >= maxSamples) break;
        }
        await stdout.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        var errors = await stderr.ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            var reason = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            throw new InvalidOperationException("Noctis could not decode this file" + (string.IsNullOrEmpty(reason) ? "." : ": " + reason));
        }
        if (count == 0) throw new InvalidOperationException("The file has no audio Noctis can decode.");
        if (count < samples.Length) Array.Resize(ref samples, count);
        // Sanitize: a NaN/Inf sample would poison every analysis downstream.
        for (var i = 0; i < samples.Length; i++)
            if (!float.IsFinite(samples[i])) samples[i] = 0;
        return samples;
    }
}
