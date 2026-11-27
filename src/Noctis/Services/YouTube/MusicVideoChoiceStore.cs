using System.Text.Json;
using System.Text.Json.Serialization;
using Noctis.Helpers;

namespace Noctis.Services.YouTube;

/// <summary>What a song's music video is: a YouTube video, or deliberately none.</summary>
public enum MusicVideoChoiceKind { Video, NoVideo }

/// <summary>Who decided: the user (wins, never searched over) or the automatic match.</summary>
public enum MusicVideoChoiceSource { User, Auto }

/// <summary>
/// One song's music video choice. Kept in a shape that could be shared later (Discord ask:
/// "users could suggest the right link and upvote it"; Noctis has no server for that, 11-25):
/// besides the local track id it carries the song's artist/title <see cref="MatchKey"/>, its
/// length and the plain YouTube video id, nothing machine-specific.
/// </summary>
public sealed class MusicVideoChoice
{
    /// <summary>"track:{id:N}", this library's song.</summary>
    public string TrackKey { get; set; } = string.Empty;
    /// <summary>Portable song key (<see cref="MusicVideoMatcher.MatchKey"/>).</summary>
    public string MatchKey { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MusicVideoChoiceKind Kind { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MusicVideoChoiceSource Source { get; set; }

    /// <summary>The YouTube video id (Kind = Video).</summary>
    public string? VideoId { get; set; }
    /// <summary>The video's title when chosen, for the "Wrong video?" dialog.</summary>
    public string? VideoTitle { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// The per-song music video choices, in <c>music_video_choices.json</c> under the data folder.
/// Small (one entry per song played with the feature on), read once, written atomically on
/// each change. Thread-safe: the automatic search writes from a worker.
/// </summary>
public sealed class MusicVideoChoiceStore
{
    private const int FileVersion = 1;
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, MusicVideoChoice>? _choices;

    public MusicVideoChoiceStore(string path) => _path = path;

    public static string KeyFor(Guid trackId) => "track:" + trackId.ToString("N");

    public MusicVideoChoice? Get(Guid trackId)
    {
        lock (_lock)
        {
            return Load().TryGetValue(KeyFor(trackId), out var c) ? Clone(c) : null;
        }
    }

    public void Set(MusicVideoChoice choice)
    {
        lock (_lock)
        {
            Load()[choice.TrackKey] = Clone(choice);
            Save();
        }
    }

    public bool Remove(Guid trackId)
    {
        lock (_lock)
        {
            if (!Load().Remove(KeyFor(trackId))) return false;
            Save();
            return true;
        }
    }

    /// <summary>Settings › Reset Everything: forgets every choice, in memory and on disk.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _choices = new Dictionary<string, MusicVideoChoice>(StringComparer.OrdinalIgnoreCase);
            try { if (File.Exists(_path)) File.Delete(_path); }
            catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.State, "MusicVideo.ChoicesClearFailed", ex.Message); }
        }
    }

    /// <summary>Every stored choice (tests, a future export).</summary>
    public IReadOnlyList<MusicVideoChoice> All()
    {
        lock (_lock) return Load().Values.Select(Clone).ToList();
    }

    private sealed class FileShape
    {
        public int Version { get; set; } = FileVersion;
        public List<MusicVideoChoice> Choices { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private Dictionary<string, MusicVideoChoice> Load()
    {
        if (_choices != null) return _choices;
        _choices = new Dictionary<string, MusicVideoChoice>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_path))
            {
                var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(_path), Json);
                foreach (var c in shape?.Choices ?? new List<MusicVideoChoice>())
                    if (!string.IsNullOrEmpty(c.TrackKey)) _choices[c.TrackKey] = c;
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.State, "MusicVideo.ChoicesReadFailed", ex.Message);
        }
        return _choices;
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var shape = new FileShape { Choices = _choices!.Values.OrderBy(c => c.TrackKey, StringComparer.Ordinal).ToList() };
            AtomicFileWriter.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(shape, Json));
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.State, "MusicVideo.ChoicesWriteFailed", ex.Message);
        }
    }

    private static MusicVideoChoice Clone(MusicVideoChoice c) => new()
    {
        TrackKey = c.TrackKey,
        MatchKey = c.MatchKey,
        Artist = c.Artist,
        Title = c.Title,
        DurationSeconds = c.DurationSeconds,
        Kind = c.Kind,
        Source = c.Source,
        VideoId = c.VideoId,
        VideoTitle = c.VideoTitle,
        UpdatedUtc = c.UpdatedUtc,
    };
}
