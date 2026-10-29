using System.Globalization;

namespace Noctis.Plugins.ABLoop.Looping;

/// <summary>A-B Loop's view of the real Noctis host (plugin API 1.4, "playback.control").</summary>
public sealed class PluginLoopHost : ILoopHost, IDisposable
{
    private readonly IPluginHost _host;

    public PluginLoopHost(IPluginHost host)
    {
        _host = host;
        host.NowPlaying.TrackChanged += OnTrackChanged;
        host.Settings.Changed += OnSettings;
    }

    private void OnTrackChanged(object? sender, EventArgs e) => TrackChanged?.Invoke(this, EventArgs.Empty);
    private void OnSettings(object? sender, string key) => SettingsChanged?.Invoke(this, EventArgs.Empty);

    public TimeSpan Position => _host.NowPlaying.Position;
    public bool IsPlaying => _host.NowPlaying.IsPlaying;
    public TimeSpan Duration => _host.NowPlaying.Track?.Duration ?? TimeSpan.Zero;
    public void Seek(TimeSpan position) => _host.Playback.Seek(position);
    public void Play() => _host.Playback.Play();
    public void Pause() => _host.Playback.Pause();

    public string? TrackPath => _host.NowPlaying.Track is { FilePath.Length: > 0 } t ? t.FilePath : null;
    public string TrackTitle => _host.NowPlaying.Track?.Title ?? "";
    public string TrackArtist => _host.NowPlaying.Track?.Artist ?? "";
    public event EventHandler? TrackChanged;

    public TimeSpan GapSetting => ParseSeconds(_host.Settings.GetString("gap"), max: 2);
    public bool KeepLoops => _host.Settings.GetBool("keepLoops", true);
    public TimeSpan NudgeSetting => ParseSeconds(_host.Settings.GetString("nudge"), max: 1) is { } n && n > TimeSpan.Zero ? n : TimeSpan.FromMilliseconds(100);
    public event EventHandler? SettingsChanged;

    public string? DataDirectory => _host.DataDirectory;
    public void Log(string message) => _host.Log(message);

    /// <summary>"0.5 s" → 0.5 s, "None" → 0, clamped to 0..<paramref name="max"/>.</summary>
    public static TimeSpan ParseSeconds(string? text, double max)
    {
        if (string.IsNullOrWhiteSpace(text)) return TimeSpan.Zero;
        var number = new string(text.Trim().TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && double.IsFinite(s)
            ? TimeSpan.FromSeconds(Math.Clamp(s, 0, max))
            : TimeSpan.Zero;
    }

    public void Dispose()
    {
        _host.NowPlaying.TrackChanged -= OnTrackChanged;
        _host.Settings.Changed -= OnSettings;
        TrackChanged = null;
        SettingsChanged = null;
    }
}
