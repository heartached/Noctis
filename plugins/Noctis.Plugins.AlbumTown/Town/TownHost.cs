using System.Text.Json;

namespace Noctis.Plugins.AlbumTown.Town;

/// <summary>What the town needs from Noctis. <see cref="PluginTownHost"/> is the real one; tests
/// and the preview use a fake.</summary>
public interface ITownHost
{
    /// <summary>Every library track (called off the UI thread).</summary>
    IReadOnlyList<TrackInfo> LoadTracks();

    NowPlayingTrack? NowPlaying { get; }
    bool IsPlaying { get; }
    /// <summary>Track or play/pause changed (UI thread).</summary>
    event EventHandler? NowPlayingChanged;
    bool TryReadBeat(out double pulse);

    void Play(TownAlbum album);
    bool ShowAlbum(TownAlbum album);

    int CrowdSize { get; }
    bool ShowNames { get; }
    bool Chatty { get; }
    event EventHandler? SettingsChanged;

    /// <summary>Albums the user called to the plaza (kept between sessions).</summary>
    IReadOnlyList<string> LoadCalled();
    void SaveCalled(IReadOnlyList<string> keys);
}

/// <summary>The town's view of the real Noctis host (plugin API 1.3).</summary>
public sealed class PluginTownHost : ITownHost, IDisposable
{
    private readonly IPluginHost _host;
    private readonly string _calledFile;

    public PluginTownHost(IPluginHost host)
    {
        _host = host;
        _calledFile = Path.Combine(host.DataDirectory, "called.json");
        host.NowPlaying.TrackChanged += OnNowPlaying;
        host.NowPlaying.IsPlayingChanged += OnNowPlaying;
        host.Settings.Changed += OnSettings;
    }

    private void OnNowPlaying(object? sender, EventArgs e) => NowPlayingChanged?.Invoke(this, EventArgs.Empty);
    private void OnSettings(object? sender, string key) => SettingsChanged?.Invoke(this, EventArgs.Empty);

    public IReadOnlyList<TrackInfo> LoadTracks() => _host.Library.GetAll();
    public NowPlayingTrack? NowPlaying => _host.NowPlaying.Track;
    public bool IsPlaying => _host.NowPlaying.IsPlaying;
    public event EventHandler? NowPlayingChanged;
    public bool TryReadBeat(out double pulse) => _host.Beat.TryRead(out pulse);

    public void Play(TownAlbum album) => _host.Playback.PlayTracks(album.TrackIds);
    public bool ShowAlbum(TownAlbum album) => album.AlbumId.Length > 0 && _host.ShowAlbum(album.AlbumId);

    public int CrowdSize => (int)Math.Round(_host.Settings.GetNumber("crowdSize", PlazaCast.DefaultCrowd));
    public bool ShowNames => _host.Settings.GetBool("showNames");
    public bool Chatty => _host.Settings.GetBool("chatty", true);
    public event EventHandler? SettingsChanged;

    public IReadOnlyList<string> LoadCalled()
    {
        try
        {
            return File.Exists(_calledFile)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_calledFile)) ?? new List<string>()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    public void SaveCalled(IReadOnlyList<string> keys)
    {
        try
        {
            Directory.CreateDirectory(_host.DataDirectory);
            File.WriteAllText(_calledFile, JsonSerializer.Serialize(keys));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.Log("could not save called albums: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _host.NowPlaying.TrackChanged -= OnNowPlaying;
        _host.NowPlaying.IsPlayingChanged -= OnNowPlaying;
        _host.Settings.Changed -= OnSettings;
        NowPlayingChanged = null;
        SettingsChanged = null;
    }
}
