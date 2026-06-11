namespace Noctis.Services.AudioAnalysis;

/// <summary>Persisted BPM/key bookkeeping row, keyed by file path. Signature = size + mtime.</summary>
public sealed record TrackAnalysisRecord(
    string FilePath,
    long FileSize,
    string LastModifiedUtc,
    int Bpm,
    double BpmConfidence,
    string MusicalKey,
    double KeyConfidence,
    string AnalyzedUtc);

public interface IAudioAnalysisStore
{
    Task<TrackAnalysisRecord?> GetAsync(string filePath, CancellationToken ct);
    Task UpsertAsync(TrackAnalysisRecord record, CancellationToken ct);
}
