namespace Noctis.Plugins;

/// <summary>
/// Version of this plugin kit. A plugin states the version it was built against in
/// plugin.json ("apiVersion": "1.2"). Noctis refuses a plugin whose major version differs
/// from <see cref="Major"/>; minor versions only ever add members.
/// </summary>
public static class PluginApi
{
    public const int Major = 1;
    public const int Minor = 2;

    /// <summary>"1.2": the value to put in plugin.json's apiVersion.</summary>
    public static string Version => $"{Major}.{Minor}";
}

/// <summary>Permission names a plugin declares in plugin.json's "permissions" array.</summary>
public static class PluginPermissions
{
    /// <summary>Now playing, play/pause events and the scrobble hook. Always granted.</summary>
    public const string PlaybackRead = "playback.read";
    /// <summary><see cref="IPluginHost.Playback"/>.</summary>
    public const string PlaybackControl = "playback.control";
    /// <summary><see cref="IPluginHost.Library"/>.</summary>
    public const string LibraryRead = "library.read";
    /// <summary>Declares that the plugin talks to the internet. Disclosure only: Noctis cannot stop a .NET plugin from opening sockets.</summary>
    public const string Network = "network";
    /// <summary><see cref="IPluginHost.RegisterLyricsProvider"/>.</summary>
    public const string LyricsProvider = "lyrics.provider";
    /// <summary><see cref="IPluginHost.RegisterTrackCommand(string, string?, Action{TrackInfo})"/>.</summary>
    public const string MenuCommands = "menu.commands";
    /// <summary><see cref="IPluginHost.Notify"/>.</summary>
    public const string Notifications = "notifications";
    /// <summary>API 1.2: <see cref="IPluginHost.TrackAnalysis"/> (BPM and key of library tracks).</summary>
    public const string LibraryWriteAnalysis = "library.write.analysis";

    /// <summary>Every permission this kit version knows.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        PlaybackRead, PlaybackControl, LibraryRead, Network, LyricsProvider, MenuCommands, Notifications, LibraryWriteAnalysis,
    };
}

/// <summary>Thrown when a plugin calls a hook whose permission its plugin.json does not declare.</summary>
public sealed class PluginPermissionException : InvalidOperationException
{
    public PluginPermissionException(string permission)
        : base($"The plugin did not declare the \"{permission}\" permission in plugin.json.")
        => Permission = permission;

    public string Permission { get; }
}

/// <summary>A read-only copy of a library track. Changing it changes nothing in Noctis.</summary>
public sealed record TrackInfo(
    string Id,
    string Title,
    string Artist,
    string Album,
    string AlbumArtist,
    TimeSpan Duration,
    int Year,
    string Genre,
    int TrackNumber,
    string FilePath,
    bool IsFavorite,
    int PlayCount,
    int Rating)
{
    /// <summary>API 1.2. Beats per minute, 0 = unknown.</summary>
    public int Bpm { get; init; }

    /// <summary>API 1.2. Musical key as Noctis stores it ("A minor", "8A", whatever the tags
    /// said); empty = unknown.</summary>
    public string MusicalKey { get; init; } = "";
}

/// <summary>Transport control ("playback.control"). Calls from any thread are marshalled to the UI thread.</summary>
public interface IPlaybackControl
{
    void PlayPause();
    void Play();
    void Pause();
    void Next();
    void Previous();
    /// <summary>Seeks within the current track (clamped to its length).</summary>
    void Seek(TimeSpan position);
}

/// <summary>Read-only library access ("library.read"). Returns copies, never live objects.</summary>
public interface ILibraryReader
{
    int TrackCount { get; }

    /// <summary>Tracks whose title, artist or album contain every word of <paramref name="query"/>
    /// (case- and accent-insensitive). An empty query returns the first <paramref name="limit"/> tracks.
    /// <paramref name="limit"/> is capped at 500.</summary>
    IReadOnlyList<TrackInfo> Search(string query, int limit = 50);

    /// <summary>API 1.2. Every library track, in library order (copies). For whole-library work
    /// such as matching by <see cref="TrackInfo.FilePath"/>; call it off the UI thread for big libraries.</summary>
    IReadOnlyList<TrackInfo> GetAll()
        => throw new NotSupportedException("This Noctis build does not implement plugin API 1.2. Set minAppVersion in plugin.json.");
}

/// <summary>One track's tempo and key for <see cref="ITrackAnalysisWriter.SetTrackAnalysisAsync"/>.
/// Null leaves that value alone.</summary>
/// <param name="TrackId"><see cref="TrackInfo.Id"/>.</param>
/// <param name="Bpm">Whole beats per minute, 1 to 999.</param>
/// <param name="MusicalKey">Up to 32 characters, e.g. "A minor".</param>
public sealed record TrackAnalysisUpdate(string TrackId, int? Bpm = null, string? MusicalKey = null);

/// <summary>
/// API 1.2, "library.write.analysis": fills in BPM and musical key of library tracks, the two
/// values Noctis's own tempo/key analysis produces. Only Noctis's library changes: file tags
/// are never written, and no other field can be set.
/// </summary>
public interface ITrackAnalysisWriter
{
    /// <summary>
    /// Applies <paramref name="updates"/> on the UI thread, then saves the library once. Without
    /// <paramref name="overwrite"/> only empty values are filled (BPM 0, no key). Unknown track ids
    /// and out-of-range values (BPM outside 1–999, a blank key or one over 32 characters) are
    /// skipped. Callable from any thread. Returns how many tracks changed.
    /// </summary>
    Task<int> SetTrackAnalysisAsync(IReadOnlyList<TrackAnalysisUpdate> updates, bool overwrite = false);
}

/// <summary>What Noctis is looking lyrics up for.</summary>
public sealed record LyricsQuery(string Artist, string Title, string Album, TimeSpan Duration);

/// <summary>A lyrics answer. <paramref name="Synced"/> is LRC text (ELRC word timing allowed);
/// <paramref name="Plain"/> is unsynced text. Set <paramref name="Instrumental"/> for a definitive "no vocals".</summary>
public sealed record PluginLyrics(string? Synced, string? Plain, bool Instrumental = false);

/// <summary>
/// A lyrics source ("lyrics.provider"). <see cref="FindAsync"/> runs on a worker thread, in
/// parallel with the built-in providers; the built-ins win ties, a synced answer beats an
/// unsynced one. Noctis cancels the token and moves on after a few seconds (8 s today).
/// Return null for "not found"; throw for "could not ask" (network down).
/// </summary>
public interface ILyricsProvider
{
    /// <summary>Shown as the lyrics source ("Try &lt;Name&gt;").</summary>
    string Name { get; }

    Task<PluginLyrics?> FindAsync(LyricsQuery query, CancellationToken ct);
}

/// <summary>Values of the settings declared in plugin.json. The user edits them in Settings → Plugins.</summary>
public interface IPluginSettings
{
    /// <summary>The value as text (choice settings return the chosen value), or the declared default.</summary>
    string? GetString(string key);
    bool GetBool(string key, bool fallback = false);
    double GetNumber(string key, double fallback = 0);

    /// <summary>Raised on the UI thread with the key the user changed.</summary>
    event EventHandler<string>? Changed;
}
