using LibVLCSharp.Shared;
using Noctis.Controls;

namespace Noctis.Services.AudioAnalysis;

/// <summary>
/// Skip Silence without ffmpeg: Linux builds don't bundle it (Windows and macOS do), and the
/// Steam Deck user who asked for the feature (Discord, Spark 11-23) may not have it. libVLC
/// ships with every build, so it decodes one end of the song into a temporary WAV file
/// (stream output, not played: nothing reaches a sound device) that is folded into peaks
/// like ffmpeg's output. Never touches the song file.
/// </summary>
internal static class VlcEdgeDecoder
{
    private const int SampleRate = 22050;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    /// <summary>Per-window peaks of the first, or the last, <paramref name="edgeSeconds"/>
    /// seconds (the last ones run to the real end of the stream); null when libVLC fails or
    /// writes nothing.</summary>
    public static async Task<List<float>?> DecodePeaksAsync(string source, TimeSpan duration, bool fromEnd, int edgeSeconds, CancellationToken ct)
    {
        var wav = Path.Combine(Path.GetTempPath(), "noctis-silence-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            // Forward slashes and quotes: the stream-output chain parses backslashes itself.
            var dst = wav.Replace('\\', '/');
            var options = new List<string>
            {
                $":sout=#transcode{{vcodec=none,scodec=none,acodec=s16l,channels=2,samplerate={SampleRate}}}:std{{access=file,mux=wav,dst=\"{dst}\"}}",
                ":no-sout-video",
                ":no-sout-spu",
            };
            if (fromEnd)
            {
                // A second early: a seek may land a little late, and the end is measured from
                // the last sample written anyway.
                var start = Math.Max(0, duration.TotalSeconds - edgeSeconds - 1);
                options.Add(string.Format(inv, ":start-time={0:0.###}", start));
            }
            else
            {
                options.Add(string.Format(inv, ":stop-time={0}", edgeSeconds));
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var media = new Media(SharedLibVlc.Instance, source, FromType.FromPath, options.ToArray()))
            using (var player = new MediaPlayer(media))
            {
                player.EndReached += (_, _) => done.TrySetResult();
                player.EncounteredError += (_, _) => done.TrySetResult();
                if (!player.Play()) return null;
                try
                {
                    await done.Task.WaitAsync(Timeout, ct).ConfigureAwait(false);
                }
                finally
                {
                    // Off libVLC's event thread here: Stop from inside its callback deadlocks.
                    player.Stop();
                }
            }

            return ReadWavPeaks(wav);
        }
        finally
        {
            try { File.Delete(wav); } catch { /* temp file */ }
        }
    }

    /// <summary>Peaks of a 16-bit PCM WAV file, read to its end whatever its header claims (a
    /// header written before the end was known holds a placeholder size).</summary>
    internal static List<float>? ReadWavPeaks(string wav)
    {
        if (!File.Exists(wav)) return null;
        var bytes = File.ReadAllBytes(wav);
        if (bytes.Length < 44 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F') return null;

        int channels = 0, rate = 0, bits = 0, dataStart = -1;
        var pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            var size = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "fmt " && pos + 24 <= bytes.Length)
            {
                channels = BitConverter.ToInt16(bytes, pos + 10);
                rate = BitConverter.ToInt32(bytes, pos + 12);
                bits = BitConverter.ToInt16(bytes, pos + 22);
            }
            else if (id == "data")
            {
                dataStart = pos + 8;
                break;
            }
            if (size < 0 || pos + 8 + (long)size > bytes.Length) break;
            pos += 8 + size + (size & 1);
        }
        if (dataStart < 0 || channels <= 0 || rate <= 0 || bits != 16) return null;

        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(
            bytes.AsSpan(dataStart, (bytes.Length - dataStart) / 2 * 2));
        var acc = new SilenceScanService.PeakAccumulator((int)Math.Round(rate * SilenceEdges.WindowSeconds) * channels);
        var floats = new float[Math.Min(samples.Length, 1 << 16)];
        for (var i = 0; i < samples.Length; i += floats.Length)
        {
            var n = Math.Min(floats.Length, samples.Length - i);
            for (var j = 0; j < n; j++) floats[j] = samples[i + j] / 32768f;
            acc.Add(floats.AsSpan(0, n));
        }
        var peaks = acc.Finish();
        return peaks.Count == 0 ? null : peaks;
    }
}
