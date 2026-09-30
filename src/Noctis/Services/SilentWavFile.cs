using System.Text;

namespace Noctis.Services;

/// <summary>
/// Generates small all-zero 16-bit PCM WAVs: test fixtures, and (seconds: 0) the
/// unknown-length header <see cref="EndlessSilenceInput"/> streams for the keep-alive.
/// Pure (no LibVLC, no app state) so it is unit-testable.
/// </summary>
internal static class SilentWavFile
{
    public const int HeaderBytes = 44;

    /// <summary>
    /// Writes a silent 16-bit PCM WAV of the given duration/format to <paramref name="output"/>.
    /// 0 seconds writes just the header with a data size of 0, which WAV demuxers read as
    /// "length unknown, play until the stream ends".
    /// </summary>
    public static void Write(Stream output, int seconds, int sampleRate, int channels)
    {
        const short bitsPerSample = 16;
        var blockAlign = channels * (bitsPerSample / 8);
        var byteRate = sampleRate * blockAlign;
        var dataSize = seconds * byteRate;

        using var w = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataSize);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);                  // PCM fmt chunk size
        w.Write((short)1);            // audio format = PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write(bitsPerSample);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataSize);
        w.Write(new byte[dataSize]);  // silence
    }
}
