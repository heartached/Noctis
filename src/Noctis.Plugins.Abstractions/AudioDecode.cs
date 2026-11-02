namespace Noctis.Plugins;

/// <summary>
/// API 1.6, "audio.decode": decodes a song file to mono PCM so a plugin can analyse its sound
/// offline (chords, tempo, loudness), with the decoder Noctis already ships (ffmpeg) instead of
/// one bundled per plugin. Nothing is played, written or sent anywhere.
/// </summary>
public interface IAudioDecoder
{
    /// <summary>False when Noctis has no decoder to offer (no ffmpeg found: Settings → Advanced →
    /// Helper programs). <see cref="DecodeMonoAsync"/> then throws.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Decodes the first audio stream of <paramref name="filePath"/> from its start, downmixed to
    /// one channel and resampled to <paramref name="sampleRate"/> (clamped to 8000–48000 Hz), as
    /// floats in -1..1. Stops after <paramref name="maxDuration"/> (at most 20 minutes; longer
    /// asks get 20 minutes). Runs off the UI thread; callable from any thread. Cancel the token
    /// to stop it at once.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidOperationException">No decoder, the file is not audio Noctis can
    /// decode, or the decode took too long.</exception>
    Task<DecodedAudio> DecodeMonoAsync(string filePath, int sampleRate, TimeSpan maxDuration, CancellationToken ct = default);
}

/// <summary>API 1.6: mono PCM from <see cref="IAudioDecoder.DecodeMonoAsync"/>.</summary>
/// <param name="Samples">The samples, -1..1, one channel.</param>
/// <param name="SampleRate">Samples per second.</param>
public sealed record DecodedAudio(float[] Samples, int SampleRate)
{
    /// <summary>How long the decoded audio lasts.</summary>
    public TimeSpan Duration => SampleRate > 0 ? TimeSpan.FromSeconds(Samples.Length / (double)SampleRate) : TimeSpan.Zero;
}
