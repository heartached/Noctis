using System.Buffers.Binary;

namespace Noctis.Services.Dsd;

/// <summary>
/// PCM → DSF, streaming in constant memory. Takes interleaved 32-bit float PCM at
/// <see cref="PcmRate"/> (352.8 kHz — ffmpeg resamples the source to it, so the last ×8–×32
/// step is a plain linear interpolation whose images sit above 330 kHz, where the DSD
/// noise floor is already far higher) and writes DSD64 / DSD128 / DSD256. Each channel
/// runs its own <see cref="DsdModulator"/> on its own thread.
/// </summary>
internal static class DsdEncoder
{
    public const int PcmRate = 352800;
    public const int Dsd64 = 2822400;

    /// <summary>"DSD64" / "DSD128" / "DSD256" → bit rate; null for anything else.</summary>
    public static int? RateFor(string? name) => name?.Trim().ToUpperInvariant() switch
    {
        "DSD64" => Dsd64,
        "DSD128" => Dsd64 * 2,
        "DSD256" => Dsd64 * 4,
        _ => null,
    };

    /// <summary>
    /// Encodes until <paramref name="pcm"/> ends. <paramref name="onFrames"/> gets the PCM
    /// frames consumed so far after each chunk. Returns the modulator resets (0 normally).
    /// </summary>
    public static long Encode(Stream pcm, int channels, int dsdRate, Stream output,
        Action<long>? onFrames = null, CancellationToken ct = default)
    {
        if (dsdRate % PcmRate != 0 || (dsdRate / PcmRate) % 8 != 0)
            throw new ArgumentOutOfRangeException(nameof(dsdRate));
        var up = dsdRate / PcmRate;
        // Eight DSF blocks of every channel per chunk.
        var frames = DsfWriter.BlockSize * 8 * 8 / up;
        var frameBytes = channels * sizeof(float);
        var raw = new byte[frames * frameBytes];
        var planar = new float[channels][];
        var bits = new byte[channels][];
        var mods = new DsdModulator[channels];
        for (var c = 0; c < channels; c++)
        {
            planar[c] = new float[frames];
            bits[c] = new byte[frames * up / 8];
            mods[c] = new DsdModulator(dsdRate);
        }
        var writer = new DsfWriter(output, dsdRate, channels);
        long done = 0;
        var carry = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Fill the chunk (pipes hand out short reads); a partial frame waits for the next one.
            var got = carry + pcm.ReadAtLeast(raw.AsSpan(carry), raw.Length - carry, throwOnEndOfStream: false);
            var n = got / frameBytes;
            if (n == 0) break;
            for (var i = 0; i < n; i++)
                for (var c = 0; c < channels; c++)
                    planar[c][i] = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan((i * channels + c) * sizeof(float)));
            carry = got - n * frameBytes;
            if (carry > 0) raw.AsSpan(n * frameBytes, carry).CopyTo(raw);

            var count = n;
            Parallel.For(0, channels, c => mods[c].Process(planar[c].AsSpan(0, count), up, bits[c]));
            writer.Write(bits, count * up / 8);
            done += n;
            onFrames?.Invoke(done);
            if (got < raw.Length) break;
        }
        writer.Finish();
        long resets = 0;
        foreach (var m in mods) resets += m.Resets;
        return resets;
    }
}
