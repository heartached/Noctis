namespace Noctis.Models;

/// <summary>
/// One stage of the audio signal path shown by the quality badge near the
/// now-playing info (source → ReplayGain → EQ → crossfade → output).
/// </summary>
public sealed record SignalPathStage(string Stage, string Detail, bool IsActive);
