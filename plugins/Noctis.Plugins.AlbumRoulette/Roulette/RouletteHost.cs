using System.Text.Json;

namespace Noctis.Plugins.AlbumRoulette.Roulette;

/// <summary>What Album Roulette needs from Noctis. <see cref="PluginRouletteHost"/> is the real one;
/// tests and the preview use a fake.</summary>
public interface IRouletteHost
{
    /// <summary>Every library track (called off the UI thread).</summary>
    IReadOnlyList<TrackInfo> LoadTracks();

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
