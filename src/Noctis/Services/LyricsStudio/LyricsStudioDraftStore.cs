using System.Text.Json;
using System.Text.Json.Serialization;
using Noctis.Models;

namespace Noctis.Services.LyricsStudio;

/// <summary>
/// A finished-but-unsaved Lyrics Studio result for one track: everything needed to put the
/// review back on screen after the app was closed, without running the speech model again.
/// </summary>
public sealed record LyricsStudioDraft(
    string? FilePath,
    DateTime SavedUtc,
    LyricsStudioSource Source,
    double Confidence,
    string Language,
    int HeardWords,
    IReadOnlyList<AlignedLine> Lines)
{
    public static LyricsStudioDraft From(LyricsStudioResult result, IReadOnlyList<AlignedLine>? lines = null) =>
        new(result.Track.FilePath, DateTime.UtcNow, result.Source, result.Confidence, result.Language, result.HeardWords, lines ?? result.Lines);

    public LyricsStudioResult ToResult(Track track) =>
        new(track, Lines, Source, Confidence, Language, HeardWords);
}

/// <summary>
/// Keeps Lyrics Studio reviews across restarts: one JSON file per track id under the app's
/// data directory, written the moment a song finishes (and again with the user's edits when
/// they move on or close), deleted when the song is saved or skipped. A missing or unreadable
/// file simply means "nothing to restore" — the model can always run again.
/// </summary>
public sealed class LyricsStudioDraftStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dir;

    public LyricsStudioDraftStore(string directory) => _dir = directory;

    private string PathFor(Guid trackId) => Path.Combine(_dir, trackId.ToString("N") + ".json");

    public bool TryLoad(Guid trackId, out LyricsStudioDraft draft)
    {
        draft = null!;
        try
        {
            var path = PathFor(trackId);
            if (!File.Exists(path)) return false;
            var loaded = JsonSerializer.Deserialize<LyricsStudioDraft>(File.ReadAllText(path), JsonOptions);
            if (loaded is null || loaded.Lines is null || loaded.Lines.Count == 0) return false;
            draft = loaded;
            return true;
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsStudio.DraftLoadFailed", ex.Message);
            return false;
        }
    }

    public void Save(Guid trackId, LyricsStudioDraft draft)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var path = PathFor(trackId);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(draft, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsStudio.DraftSaveFailed", ex.Message);
        }
    }

    public void Delete(Guid trackId)
    {
        try
        {
            var path = PathFor(trackId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsStudio.DraftDeleteFailed", ex.Message);
        }
    }
}
