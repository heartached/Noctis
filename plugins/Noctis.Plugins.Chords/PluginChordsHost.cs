using Avalonia.Threading;

namespace Noctis.Plugins.Chords;

/// <summary>The Chords plugin's view of the real Noctis host (plugin API 1.6: "audio.decode", "playback.control").</summary>
public sealed class PluginChordsHost : IChordsHost, IDisposable
{
    private readonly IPluginHost _host;

    public PluginChordsHost(IPluginHost host)
    {
        _host = host;
        host.NowPlaying.TrackChanged += OnTrackChanged;
        host.Settings.Changed += OnSettings;
    }

    private void OnTrackChanged(object? sender, EventArgs e) => TrackChanged?.Invoke(this, EventArgs.Empty);
    private void OnSettings(object? sender, string key) => SettingsChanged?.Invoke(this, EventArgs.Empty);

    public string? TrackPath => _host.NowPlaying.Track is { FilePath.Length: > 0 } t ? t.FilePath : null;
    public string TrackTitle => _host.NowPlaying.Track?.Title ?? "";
    public string TrackArtist => _host.NowPlaying.Track?.Artist ?? "";
    public TimeSpan Position => _host.NowPlaying.Position;
    public bool IsPlaying => _host.NowPlaying.IsPlaying;
    public event EventHandler? TrackChanged;

    public bool Sevenths => _host.Settings.GetBool("sevenths", false);
    public bool AutoFind => _host.Settings.GetBool("autoFind", true);
    public event EventHandler? SettingsChanged;

    public bool CanDecode
    {
        get
        {
            try { return _host.AudioDecoder.IsAvailable; }
            catch (Exception ex) when (ex is NotSupportedException or PluginPermissionException) { return false; }
        }
    }

    public Task<DecodedAudio> DecodeAsync(string path, int sampleRate, TimeSpan maxDuration, CancellationToken ct)
        => _host.AudioDecoder.DecodeMonoAsync(path, sampleRate, maxDuration, ct);

    public void Seek(TimeSpan position) => _host.Playback.Seek(position);
    public string? DataDirectory => _host.DataDirectory;
    public void Log(string message) => _host.Log(message);
    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public void Dispose()
    {
        _host.NowPlaying.TrackChanged -= OnTrackChanged;
        _host.Settings.Changed -= OnSettings;
        TrackChanged = null;
        SettingsChanged = null;
    }
}
