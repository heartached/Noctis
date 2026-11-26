using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Noctis.Services.AudioAnalysis;

/// <summary>
/// Cheap "fake 24-bit" check: decodes a slice of the file to raw s32 through ffmpeg (no
/// resampling, no filters) and ORs the bits below the 16-bit word. A 24-bit file made by
/// padding 16-bit audio has those bits zero in every sample.
/// </summary>
public static class BitDepthProbe
{
    /// <summary>How much of the file is read (from ~30 % in, so intros/fade-ins don't decide it).</summary>
    public static readonly TimeSpan SliceLength = TimeSpan.FromSeconds(30);
    /// <summary>Fewer non-zero samples than this and the slice proves nothing.</summary>
    public const int MinNonZeroSamples = 4096;

    /// <summary>True = the low bits are always zero (16-bit audio in a wider container),
    /// false = real extra bits, null = can't tell (too quiet, decode failed, not &gt; 16 bits).</summary>
    public static async Task<bool?> IsPadded16BitAsync(
        string ffmpegPath, string filePath, int bitsPerSample, TimeSpan duration, CancellationToken ct)
    {
        if (bitsPerSample <= 16 || bitsPerSample > 32) return null;

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var args = new List<string> { "-nostats", "-hide_banner", "-loglevel", "error" };
        if (duration > SliceLength * 2)
        {
            args.Add("-ss");
            args.Add((duration.TotalSeconds * 0.3).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }
        args.AddRange(new[]
        {
            "-i", filePath, "-map", "0:a:0",
            "-t", SliceLength.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-f", "s32le", "-",
        });
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        if (p == null) return null;
        using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });
        var stderrTask = p.StandardError.ReadToEndAsync();

        var acc = new LowBitsAccumulator(bitsPerSample);
        var buf = new byte[1 << 16];
        int carry = 0;
        var stdout = p.StandardOutput.BaseStream;
        while (true)
        {
            int n = await stdout.ReadAsync(buf.AsMemory(carry, buf.Length - carry), ct);
            if (n == 0) break;
            int avail = carry + n;
            int whole = avail / 4;
            acc.Add(MemoryMarshal.Cast<byte, int>(buf.AsSpan(0, whole * 4)));
            carry = avail - whole * 4;
            if (carry > 0) Buffer.BlockCopy(buf, whole * 4, buf, 0, carry);
        }
        await stderrTask;
        await p.WaitForExitAsync(ct);
        ct.ThrowIfCancellationRequested();
        return acc.Result;
    }

    /// <summary>Pure part, unit-testable: ORs the bits a <paramref name="bits"/>-bit sample
    /// carries below its top 16, over left-aligned s32 samples.</summary>
    public sealed class LowBitsAccumulator
    {
        private readonly int _mask;
        private int _or;
        private long _nonZero;

        public LowBitsAccumulator(int bits)
        {
            bits = Math.Clamp(bits, 17, 32);
            // s32 left-aligned: the 16-bit word is the top 16 bits; the extra (bits-16)
            // bits sit just under it, anything lower is always zero from the decoder.
            _mask = (int)((((1L << (bits - 16)) - 1) << (32 - bits)) & 0xFFFF);
        }

        public void Add(ReadOnlySpan<int> samples)
        {
            foreach (var s in samples)
            {
                if (s == 0) continue;
                _nonZero++;
                _or |= s & _mask;
            }
        }

        public bool? Result => _nonZero < MinNonZeroSamples ? null : _or == 0;
    }
}
