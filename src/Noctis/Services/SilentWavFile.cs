using System.Text;

namespace Noctis.Services;

/// <summary>
/// Generates a small all-zero 16-bit PCM WAV used by <see cref="VlcSilenceKeepAlive"/>
/// as the looped silent source. Pure (no LibVLC, no app state) so it is unit-testable.
/// </summary>
internal static class SilentWavFile
{
    public const int HeaderBytes = 44;

    private const string FileName = "silence.wav";
    private const int DefaultSeconds = 1;
    private const int DefaultSampleRate = 48000;
    private const int DefaultChannels = 2;

    /// <summary>Writes a silent 16-bit PCM WAV of the given duration/format to <paramref name="output"/>.</summary>
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

    /// <summary>
    /// Ensures <paramref name="dir"/>/silence.wav exists with the expected size and
    /// returns its full path. Reuses an existing correct file; rewrites a missing or
    /// truncated one. Writes via a temp file + move so a crash never leaves a partial.
    /// </summary>
    public static string EnsureCached(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileName);
        var expected = HeaderBytes + DefaultSeconds * DefaultSampleRate * DefaultChannels * 2;

        if (File.Exists(path) && new FileInfo(path).Length == expected)
            return path;

        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            Write(fs, DefaultSeconds, DefaultSampleRate, DefaultChannels);
        File.Move(tmp, path, overwrite: true);
        return path;
    }
}
