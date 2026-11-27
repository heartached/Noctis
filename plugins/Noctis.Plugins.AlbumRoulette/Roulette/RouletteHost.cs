using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Noctis.Plugins.AlbumRoulette.Roulette;

/// <summary>What Album Roulette needs from Noctis. <see cref="PluginRouletteHost"/> is the real one;
/// tests and the preview use a fake.</summary>
public interface IRouletteHost
{
    /// <summary>Every library track (called off the UI thread).</summary>
    IReadOnlyList<TrackInfo> LoadTracks();

    /// <summary>The albums of the user's media servers (Navidrome, Jellyfin, Ampache, …; GitHub
    /// #268), called off the UI thread. Empty on a Noctis without plugin API 1.9.</summary>
    IReadOnlyList<RemoteAlbum> LoadServerAlbums() => Array.Empty<RemoteAlbum>();

    /// <summary>Path of a server album's cover, fetched from its server when needed; null when none.</summary>
    Task<string?> ServerAlbumArtworkAsync(string serverAlbumId) => Task.FromResult<string?>(null);

    /// <summary>Plays a server album (shuffled or in order).</summary>
    void PlayServerAlbum(string serverAlbumId, bool shuffle) { }

    /// <summary>Play Next / Add to Queue for a server album.</summary>
    void QueueServerAlbum(string serverAlbumId, bool playNext) { }

    /// <summary>The lyric lines Noctis already has for a track, offline (called off the UI
    /// thread); empty when it has none or this Noctis cannot say.</summary>
    IReadOnlyList<string> LyricLines(string trackId);

    /// <summary>Replaces the queue with these tracks and plays.</summary>
    void PlayTracks(IReadOnlyList<string> trackIds);

    /// <summary>Play Next (<paramref name="playNext"/> true) or Add to Queue (false).</summary>
    void QueueTracks(IReadOnlyList<string> trackIds, bool playNext);

    /// <summary>Opens the album's page in the main window.</summary>
    bool ShowAlbum(string albumId);

    /// <summary>A short message in the app's notice pill.</summary>
    void Notify(string message);

    void Log(string message);

    bool AlbumOfTheDayEnabled { get; }
    int DailyHour { get; }
    bool SkipCompilations { get; }
    bool ReducedMotion { get; }
    event EventHandler? SettingsChanged;

    DailyState? LoadDaily();
    void SaveDaily(DailyState state);
}

/// <summary>The real host (plugin API 1.4; 1.7 when Noctis has it).</summary>
public sealed class PluginRouletteHost : IRouletteHost, IDisposable
{
    private readonly IPluginHost _host;
    private readonly string _dailyFile;

    public PluginRouletteHost(IPluginHost host)
    {
        _host = host;
        _dailyFile = Path.Combine(host.DataDirectory, "album-of-the-day.json");
        host.Settings.Changed += OnSettings;
    }

    private void OnSettings(object? sender, string key) => SettingsChanged?.Invoke(this, EventArgs.Empty);

    public IReadOnlyList<TrackInfo> LoadTracks() => _host.Library.GetAll();

    private volatile bool _noServerAlbums;

    /// <summary>API 1.9 (GitHub #268). On an older Noctis neither the calls nor ServerAlbumInfo
    /// exist: every use sits in its own non-inlined method, so the MissingMethodException or
    /// TypeLoadException is thrown at its call, inside the try here, and the reel keeps to the
    /// library.</summary>
    public IReadOnlyList<RemoteAlbum> LoadServerAlbums()
    {
        if (_noServerAlbums) return Array.Empty<RemoteAlbum>();
        try
        {
            return ReadServerAlbums();
        }
        catch (Exception ex) when (ex is NotSupportedException or MissingMethodException or TypeLoadException)
        {
            _noServerAlbums = true;
            return Array.Empty<RemoteAlbum>();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private IReadOnlyList<RemoteAlbum> ReadServerAlbums()
    {
        var albums = _host.Library.GetServerAlbumsAsync().GetAwaiter().GetResult();
        var result = new RemoteAlbum[albums.Count];
        for (var i = 0; i < albums.Count; i++)
        {
            var a = albums[i];
            var (playCount, lastPlayed) = PlayInfo(a);
            result[i] = new RemoteAlbum(a.Id, a.Title, a.Artist, a.Year, a.Genre, a.ServerName, a.ArtworkPath, playCount, lastPlayed);
        }
        return result;
    }

    private volatile bool _noServerPlays;

    /// <summary>What the album's server says about its plays. A Noctis whose API 1.9 has no play
    /// counts yet leaves them unknown (the MissingMethodException is thrown at the call into
    /// <see cref="ReadPlayInfo"/>, inside the try here), so those albums stay off the play filters.</summary>
    private (int? PlayCount, DateTimeOffset? LastPlayed) PlayInfo(ServerAlbumInfo album)
    {
        if (_noServerPlays) return (null, null);
        try
        {
            return ReadPlayInfo(album);
        }
        catch (Exception ex) when (ex is MissingMethodException or MissingMemberException)
        {
            _noServerPlays = true;
            return (null, null);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (int? PlayCount, DateTimeOffset? LastPlayed) ReadPlayInfo(ServerAlbumInfo album) => (album.PlayCount, album.LastPlayed);

    public async Task<string?> ServerAlbumArtworkAsync(string serverAlbumId)
    {
        if (_noServerAlbums) return null;
        try
        {
            return await ReadServerArtwork(serverAlbumId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NotSupportedException or MissingMethodException or TypeLoadException)
        {
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Task<string?> ReadServerArtwork(string serverAlbumId) => _host.Library.GetServerAlbumArtworkAsync(serverAlbumId);

    public void PlayServerAlbum(string serverAlbumId, bool shuffle) => RunServerCall(() => CallPlayServerAlbum(serverAlbumId, shuffle));

    public void QueueServerAlbum(string serverAlbumId, bool playNext) => RunServerCall(() => CallQueueServerAlbum(serverAlbumId, playNext));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Task<bool> CallPlayServerAlbum(string id, bool shuffle) => _host.Playback.PlayServerAlbumAsync(id, shuffle);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Task<bool> CallQueueServerAlbum(string id, bool playNext) => _host.Playback.QueueServerAlbumAsync(id, playNext);

    /// <summary>The songs come from the server: off the UI thread, and a failure says so in the pill.</summary>
    private void RunServerCall(Func<Task<bool>> call) => _ = Task.Run(async () =>
    {
        try
        {
            if (!await call().ConfigureAwait(false)) _host.Notify("Couldn't load this album from its server.");
        }
        catch (Exception ex)
        {
            _host.Log("server album: " + ex.Message);
        }
    });

    private volatile bool _noLyricLines;

    /// <summary>API 1.7 (Noctis 1.6.9+, this plugin's minAppVersion). On a host without it the
    /// call fails in <see cref="ReadLyricLines"/> (kept apart so a MissingMethodException is
    /// thrown at its call, inside the try here) and the Language chip goes by titles only.</summary>
    public IReadOnlyList<string> LyricLines(string trackId)
    {
        if (_noLyricLines) return Array.Empty<string>();
        try
        {
            return ReadLyricLines(trackId);
        }
        catch (Exception ex) when (ex is NotSupportedException or MissingMethodException)
        {
            _noLyricLines = true;
            return Array.Empty<string>();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private IReadOnlyList<string> ReadLyricLines(string trackId) => _host.Library.GetLyricLines(trackId);
    public void PlayTracks(IReadOnlyList<string> trackIds) => _host.Playback.PlayTracks(trackIds);
    public bool ShowAlbum(string albumId) => albumId.Length > 0 && _host.ShowAlbum(albumId);

    /// <summary>API 1.7 queues the way the app's own menus do. On an older Noctis the songs go
    /// into Up Next through API 1.5 (which switches Noctis's Shuffle off).</summary>
    public void QueueTracks(IReadOnlyList<string> trackIds, bool playNext)
    {
        var playback = _host.Playback;
        try
        {
            playback.QueueTracks(trackIds, playNext);
            return;
        }
        catch (NotSupportedException) { }
        var up = playback.GetQueue().UpNext;
        playback.SetUpNext(playNext ? trackIds.Concat(up).ToArray() : up.Concat(trackIds).ToArray());
    }
    public void Notify(string message) => _host.Notify(message);
    public void Log(string message) => _host.Log(message);

    public bool AlbumOfTheDayEnabled => _host.Settings.GetBool("albumOfTheDay");
    public int DailyHour => (int)Math.Clamp(Math.Round(_host.Settings.GetNumber("dailyHour", 9)), 0, 23);
    public bool SkipCompilations => _host.Settings.GetBool("skipCompilations", true);
    public bool ReducedMotion => string.Equals(_host.Settings.GetString("animation"), "Reduced", StringComparison.OrdinalIgnoreCase);
    public event EventHandler? SettingsChanged;

    public DailyState? LoadDaily()
    {
        try
        {
            return File.Exists(_dailyFile) ? JsonSerializer.Deserialize<DailyState>(File.ReadAllText(_dailyFile)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void SaveDaily(DailyState state)
    {
        try
        {
            Directory.CreateDirectory(_host.DataDirectory);
            var temp = _dailyFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state));
            File.Move(temp, _dailyFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.Log("could not save the Album of the Day: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _host.Settings.Changed -= OnSettings;
        SettingsChanged = null;
    }
}
