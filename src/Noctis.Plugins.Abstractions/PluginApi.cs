namespace Noctis.Plugins;

/// <summary>
/// Version of this plugin kit. A plugin states the version it was built against in
/// plugin.json ("apiVersion": "1.6"). Noctis refuses a plugin whose major version differs
/// from <see cref="Major"/>; minor versions only ever add members.
/// </summary>
public static class PluginApi
{
    public const int Major = 1;
    public const int Minor = 6;

    /// <summary>"1.6": the value to put in plugin.json's apiVersion.</summary>
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
    /// <summary><see cref="IPluginHost.RegisterTrackCommand(string, string?, Action{TrackInfo})"/>; since API 1.7
    /// also <see cref="IPluginHost.RegisterLibraryCommand"/>.</summary>
    public const string MenuCommands = "menu.commands";
    /// <summary><see cref="IPluginHost.Notify"/>.</summary>
    public const string Notifications = "notifications";
    /// <summary>API 1.2: <see cref="IPluginHost.TrackAnalysis"/> (BPM and key of library tracks).</summary>
    public const string LibraryWriteAnalysis = "library.write.analysis";
    /// <summary>API 1.5: <see cref="IPluginHost.LibraryImport"/> (add audio files to the library).</summary>
    public const string LibraryImport = "library.import";
    /// <summary>API 1.6: <see cref="IPluginHost.AudioDecoder"/> (decode song files to PCM for analysis).</summary>
    public const string AudioDecode = "audio.decode";

    /// <summary>Every permission this kit version knows.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        PlaybackRead, PlaybackControl, LibraryRead, Network, LyricsProvider, MenuCommands, Notifications, LibraryWriteAnalysis, LibraryImport, AudioDecode,
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

    /// <summary>API 1.3. The album this track belongs to, as Noctis groups it (the id
    /// <see cref="IPluginHost.ShowAlbum"/> takes); empty = unknown.</summary>
    public string AlbumId { get; init; } = "";

    /// <summary>API 1.3. Path of the cover image Noctis shows for this track (its album's cover,
    /// or its own when it differs), or null when it has none. Read-only: never write to it.</summary>
    public string? ArtworkPath { get; init; }

    /// <summary>API 1.3. Release type of the track's album: "Album", "Single", "EP",
    /// "Compilation", "Live", "Remix", "Soundtrack" or "Other".</summary>
    public string ReleaseType { get; init; } = "Album";

    /// <summary>API 1.3. When the track was added to the library.</summary>
    public DateTimeOffset? DateAdded { get; init; }

    /// <summary>API 1.3. When the track was last played, or null for never.</summary>
    public DateTimeOffset? LastPlayed { get; init; }

    /// <summary>API 1.5. Marked "not liked".</summary>
    public bool IsDisliked { get; init; }

    /// <summary>API 1.5. Marked explicit.</summary>
    public bool IsExplicit { get; init; }

    /// <summary>API 1.5. "Skip when shuffling" is on: Noctis's own shuffle leaves it out.</summary>
    public bool SkipWhenShuffling { get; init; }

    /// <summary>API 1.5. Snoozed until this time (Noctis's shuffle leaves it out until then), or null.</summary>
    public DateTimeOffset? SnoozedUntil { get; init; }
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

    /// <summary>API 1.3. Replaces the queue with these library tracks (<see cref="TrackInfo.Id"/>,
    /// in this order) and plays from <paramref name="startIndex"/>. Unknown ids are skipped.</summary>
    void PlayTracks(IReadOnlyList<string> trackIds, int startIndex = 0)
        => throw new NotSupportedException("This Noctis build does not implement plugin API 1.3. Set minAppVersion in plugin.json.");

    /// <summary>API 1.5. What is playing and what is up next (track ids, in order). Exact on the
    /// UI thread; from another thread a best-effort copy.</summary>
    PlaybackQueue GetQueue()
        => throw new NotSupportedException("This Noctis build does not implement plugin API 1.5. Set minAppVersion in plugin.json.");

    /// <summary>API 1.5. Replaces the songs up next with these library tracks (<see cref="TrackInfo.Id"/>,
    /// in this order) without touching the song that is playing. Unknown ids are skipped, and
    /// explicit songs are left out while Settings hides them. Noctis's own Shuffle is switched off:
    /// the order is now the caller's (switching it on again shuffles what is up next, as always).</summary>
    void SetUpNext(IReadOnlyList<string> trackIds)
        => throw new NotSupportedException("This Noctis build does not implement plugin API 1.5. Set minAppVersion in plugin.json.");

    /// <summary>API 1.5. The user put songs in the queue themselves (Play Next or Add to Queue).
    /// Raised on the UI thread, after the queue changed.</summary>
    event EventHandler<TracksQueuedEventArgs>? TracksQueued
    {
        add => throw new NotSupportedException("This Noctis build does not implement plugin API 1.5. Set minAppVersion in plugin.json.");
        remove { }
    }

    /// <summary>API 1.7. Play Next (<paramref name="playNext"/> true: to the front of Up Next, in
    /// this order) or Add to Queue (false: to the end), exactly as the app's own menu entries do:
    /// Shuffle stays as it is, the queue notice shows, and <see cref="TracksQueued"/> is raised.
    /// Unknown ids are skipped.</summary>
    void QueueTracks(IReadOnlyList<string> trackIds, bool playNext)
        => throw new NotSupportedException("This Noctis build does not implement plugin API 1.7. Set minAppVersion in plugin.json.");
}

/// <summary>API 1.5. A copy of the play queue (<see cref="IPlaybackControl.GetQueue"/>).</summary>
/// <param name="CurrentTrackId">The current track's <see cref="TrackInfo.Id"/>, or null.</param>
/// <param name="UpNext">The tracks up next, in order.</param>
public sealed record PlaybackQueue(string? CurrentTrackId, IReadOnlyList<string> UpNext);

/// <summary>API 1.5. Songs the user queued themselves (<see cref="IPlaybackControl.TracksQueued"/>).</summary>
public sealed class TracksQueuedEventArgs : EventArgs
{
    /// <summary>Raised by Noctis; plugins only read it.</summary>
    public TracksQueuedEventArgs(IReadOnlyList<string> trackIds, bool playNext)
    {
        TrackIds = trackIds;
        PlayNext = playNext;
    }

    /// <summary>The queued tracks, in the order they now stand in the queue.</summary>
    public IReadOnlyList<string> TrackIds { get; }

    /// <summary>True for Play Next (they went to the front), false for Add to Queue (the end).</summary>
    public bool PlayNext { get; }
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

    /// <summary>API 1.4. The user's playlists, in sidebar order (copies). Smart playlists come
    /// with the tracks their rules match right now. Play one with
    /// <see cref="IPlaybackControl.PlayTracks"/> and its <see cref="PlaylistInfo.TrackIds"/>.</summary>
    IReadOnlyList<PlaylistInfo> GetPlaylists()
        => throw new NotSupportedException("This Noctis build does not implement plugin API 1.4. Set minAppVersion in plugin.json.");

    /// <summary>API 1.7. The lyric lines Noctis already has for a library track (<see cref="TrackInfo.Id"/>),
    /// in order, without timestamps: from a .ttml/.elrc/.lrc file beside the song (what Lyrics
    /// Studio saves), the lyrics Noctis stored for it, or its plain lyrics tag. Never goes online;
    /// reads a few small files, so call it off the UI thread. Empty when the song has none (or
    /// the id is unknown).</summary>
    IReadOnlyList<string> GetLyricLines(string trackId)
        => throw new NotSupportedException("This Noctis build does not implement plugin API 1.7. Set minAppVersion in plugin.json.");
}

/// <summary>
/// API 1.4. A plain copy of one of the user's playlists (<see cref="ILibraryReader.GetPlaylists"/>).
/// </summary>
/// <param name="Id">Stable id of the playlist.</param>
/// <param name="Name">The name the user gave it.</param>
/// <param name="CoverPath">The playlist's own cover image, or null when it has none (Noctis then
/// shows a mosaic of its songs' covers). Read it, never write it.</param>
/// <param name="TrackIds">Its library tracks (<see cref="TrackInfo.Id"/>), in playlist order.
/// Ids the library no longer has are left out.</param>
public sealed record PlaylistInfo(string Id, string Name, string? CoverPath, IReadOnlyList<string> TrackIds)
{
    /// <summary>The playlist's colour in Noctis ("#RRGGBB"), the tile colour it shows without a cover.</summary>
    public string Color { get; init; } = "";

    /// <summary>True for a smart (rule-based) playlist.</summary>
    public bool IsSmart { get; init; }
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

/// <summary>API 1.7. Where a library command shows (<see cref="IPluginHost.RegisterLibraryCommand"/>).
/// Combine them: <c>ArtistPage | SongsPage</c>.</summary>
[Flags]
public enum LibraryCommandPlaces
{
    /// <summary>Nowhere (not accepted by <see cref="IPluginHost.RegisterLibraryCommand"/>).</summary>
    None = 0,
    /// <summary>The "…" options menu on an artist's page.</summary>
    ArtistPage = 1,
    /// <summary>A button in the Songs page's top bar (next to Shuffle), with the command's icon.</summary>
    SongsPage = 2,
}

/// <summary>API 1.7. What a library command was clicked on.</summary>
/// <param name="Place">The one place it was clicked in (a single flag).</param>
/// <param name="Artist">The artist's name on <see cref="LibraryCommandPlaces.ArtistPage"/>; null elsewhere.</param>
public sealed record LibraryCommandContext(LibraryCommandPlaces Place, string? Artist);
