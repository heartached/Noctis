namespace Noctis.Services.AudioAnalysis;

/// <summary>Outcome of analysing one audio file. BPM 0 / empty key = undetected.</summary>
public sealed record AudioAnalysisResult(
    int Bpm,
    double BpmConfidence,
    string MusicalKey,
    double KeyConfidence,
    bool Failed = false,
    string Error = "")
{
    public static AudioAnalysisResult Fail(string error) => new(0, 0, "", 0, true, error);
}
