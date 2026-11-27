using System.Text.Json;

namespace Noctis.Services;

/// <summary>One GitHub release's notes: its tag, when it was published and the markdown body.</summary>
public sealed record ReleaseNotesDocument(string Tag, DateTimeOffset? PublishedAt, string Body);

/// <summary>Where Settings › About › What's new gets a release's notes (UpdateService in the app,
/// a fake in tests). Null = no release with that tag; a throw = offline / GitHub failed.</summary>
public interface IReleaseNotesSource
{
    Task<ReleaseNotesDocument?> FetchAsync(string tag, CancellationToken ct = default);
}

/// <summary>What <see cref="WhatsNewCache"/> keeps on disk.</summary>
public sealed class WhatsNewCacheEntry
{
    public string Tag { get; set; } = "";
    public DateTimeOffset? PublishedAt { get; set; }
    public string Body { get; set; } = "";
    public DateTimeOffset FetchedUtc { get; set; }
}

/// <summary>
/// <c>&lt;data&gt;/whats_new_cache.json</c>: the last fetched release notes (one version), so What's
/// new opens at once next time and works offline. Same file discipline as AutoUpdateStore: temp +
/// move, a corrupt file reads as absent. Cleared by Reset Everything (a cache).
/// </summary>
public sealed class WhatsNewCache
{
    public const string FileName = "whats_new_cache.json";

    /// <summary>A cached copy younger than this is not fetched again.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(1);

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public WhatsNewCache(string dataDirectory) => FilePath = Path.Combine(dataDirectory, FileName);

    public string FilePath { get; }

    /// <summary>The cached notes for <paramref name="tag"/>, or null (none, another version, corrupt).</summary>
    public WhatsNewCacheEntry? Load(string tag)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var entry = JsonSerializer.Deserialize<WhatsNewCacheEntry>(File.ReadAllText(FilePath));
            return entry is not null && string.Equals(entry.Tag, tag, StringComparison.OrdinalIgnoreCase) ? entry : null;
        }
        catch
        {
            return null; // corrupt → treated as absent; the next save repairs it
        }
    }

    public void Save(WhatsNewCacheEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entry, WriteOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            DebugLog.Write("WhatsNew", $"Could not save {FileName}: {ex.Message}");
        }
    }
}
