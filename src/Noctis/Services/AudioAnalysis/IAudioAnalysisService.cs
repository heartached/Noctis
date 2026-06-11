namespace Noctis.Services.AudioAnalysis;

public interface IAudioAnalysisService
{
    /// <summary>True when a usable ffmpeg binary is available for decoding.</summary>
    bool IsAvailable { get; }

    /// <summary>Decodes the file to mono PCM and runs BPM + key detection.</summary>
    Task<AudioAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct);
}
