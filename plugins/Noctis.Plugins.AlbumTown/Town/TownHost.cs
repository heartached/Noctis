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
    /// <summary>The "Motion" setting (<see cref="Town.Motion"/>): frames per second.</summary>
    string Motion => Town.Motion.Balanced;
    event EventHandler? SettingsChanged;

    /// <summary>Albums the user called to the plaza (kept between sessions).</summary>
    IReadOnlyList<string> LoadCalled();
    void SaveCalled(IReadOnlyList<string> keys);

    /// <summary>The plaza's creature size (zoom, 1 = automatic size), kept between sessions.</summary>
    double LoadZoom();
    void SaveZoom(double zoom);

    /// <summary>The user's playlists (API 1.4; called off the UI thread). Empty when the app has none to give.</summary>
    IReadOnlyList<PlaylistInfo> LoadPlaylists() => Array.Empty<PlaylistInfo>();

    /// <summary>Replaces the queue with these tracks and plays (a playlist, a song).</summary>
    void PlayTracks(IReadOnlyList<string> trackIds) { }

    /// <summary>The Apartments building as last saved, or null for none yet.</summary>
    ApartmentLayout? LoadApartments() => null;
    void SaveApartments(ApartmentLayout layout) { }

    /// <summary>The "Menu bar" setting: "Top (horizontal)" or "Side (vertical)".</summary>
    string MenuBar => "Top (horizontal)";

    /// <summary>Lyric lines Noctis has for a song (plugin API 1.7; called off the UI thread). Empty when none or unsupported.</summary>
    IReadOnlyList<string> LoadLyricLines(string trackId) => Array.Empty<string>();
}

/// <summary>The town's view of the real Noctis host (plugin API 1.4).</summary>
public sealed class PluginTownHost : ITownHost, IDisposable
{
    private readonly IPluginHost _host;
    private readonly string _calledFile;
    private readonly string _viewFile;
    private readonly string _apartmentsFile;

    public PluginTownHost(IPluginHost host)
    {
        _host = host;
        _calledFile = Path.Combine(host.DataDirectory, "called.json");
        _viewFile = Path.Combine(host.DataDirectory, "view.json");
        _apartmentsFile = Path.Combine(host.DataDirectory, "apartments.json");
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
    public void PlayTracks(IReadOnlyList<string> trackIds) => _host.Playback.PlayTracks(trackIds);

    public IReadOnlyList<PlaylistInfo> LoadPlaylists()
    {
        try { return _host.Library.GetPlaylists(); }
        catch (NotSupportedException)
        {
            // A Noctis before plugin API 1.4: albums only.
            return Array.Empty<PlaylistInfo>();
        }
    }

    public ApartmentLayout? LoadApartments()
    {
        try
        {
            return File.Exists(_apartmentsFile) ? ApartmentLayout.FromJson(File.ReadAllText(_apartmentsFile)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.Log("could not read the apartments: " + ex.Message);
            return null;
        }
    }

    public void SaveApartments(ApartmentLayout layout)
    {
        try
        {
            Directory.CreateDirectory(_host.DataDirectory);
            // Written beside, then swapped in: a crash mid-write never loses the building.
            var temp = _apartmentsFile + ".tmp";
            File.WriteAllText(temp, layout.ToJson());
            File.Move(temp, _apartmentsFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.Log("could not save the apartments: " + ex.Message);
        }
    }
    public bool ShowAlbum(TownAlbum album) => album.AlbumId.Length > 0 && _host.ShowAlbum(album.AlbumId);

    public int CrowdSize => (int)Math.Round(_host.Settings.GetNumber("crowdSize", PlazaCast.DefaultCrowd));
    public bool ShowNames => _host.Settings.GetBool("showNames");
    public bool Chatty => _host.Settings.GetBool("chatty", true);
    public string Motion => _host.Settings.GetString("motion") ?? Town.Motion.Balanced;
    public string MenuBar => _host.Settings.GetString("menuBar") ?? "Top (horizontal)";

    private volatile bool _noLyrics;

    public IReadOnlyList<string> LoadLyricLines(string trackId)
    {
        if (_noLyrics) return Array.Empty<string>();
        try { return _host.Library.GetLyricLines(trackId); }
        catch (NotSupportedException)
        {
            // A Noctis before plugin API 1.7: the residents just do not quote.
            _noLyrics = true;
            return Array.Empty<string>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
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

    public double LoadZoom()
    {
        try
        {
            if (!File.Exists(_viewFile)) return 1;
            var view = JsonSerializer.Deserialize<ViewState>(File.ReadAllText(_viewFile));
            return view is { Zoom: > 0 and var z } && double.IsFinite(z) ? z : 1;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return 1;
        }
    }

    public void SaveZoom(double zoom)
    {
        try
        {
            Directory.CreateDirectory(_host.DataDirectory);
            File.WriteAllText(_viewFile, JsonSerializer.Serialize(new ViewState { Zoom = Math.Round(zoom, 2) }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.Log("could not save the creature size: " + ex.Message);
        }
    }

    private sealed class ViewState
    {
        public double Zoom { get; set; } = 1;
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
