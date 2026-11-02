using Avalonia;
using Avalonia.Controls;
using Noctis.Plugins.Chords.Views;

namespace Noctis.Plugins.Chords;

/// <summary>
/// Chords (official Noctis plugin): Settings → Plugins → Chords → "Open Chords", or "Show chords" in
/// a track's menu, opens a window that shows the playing song's chords in time with it: the chord
/// now (large), the ones coming up, a lane of the chords around the playhead and the key. The
/// chords are estimated from the song's own audio on this computer (no chord website is asked),
/// and the window says so.
/// </summary>
public sealed class ChordsPlugin : INoctisPlugin, IPluginViewProvider
{
    public const string Id = "dev.noctis.plugins.chords";
    public const string Version = "1.0.0";
    public const string About = "See the chords of the playing song, worked out from its audio.";
    public const string ShowChordsLabel = "Show chords";
    // Piano keys (Material Icons "piano", 24x24 box).
    private const string ChordIcon = "M19 3H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-5.5 11.5h.25V19h-3.5v-4.5h.25c.41 0 .75-.34.75-.75V5h1.5v8.75c0 .41.34.75.75.75zM5 5h2v8.75c0 .41.34.75.75.75H8V19H5V5zm14 14h-3v-4.5h.25c.41 0 .75-.34.75-.75V5h2v14z";

    private IPluginHost? _host;
    private PluginChordsHost? _chordsHost;
    private ChordsSession? _session;
    private IDisposable? _view;
    private IDisposable? _command;

    public PluginInfo Info { get; } = new(Id, "Chords", Version, "Noctis", About);

    public string Name => "Chords";

    public Size? PreferredWindowSize => new Size(ChordsView.WindowWidth, ChordsView.WindowHeight);

    public Size? MinWindowSize => new Size(ChordsView.MinWindowWidth, ChordsView.MinWindowHeight);

    /// <summary>The running session (tests).</summary>
    public ChordsSession? Session => _session;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _chordsHost = new PluginChordsHost(host);
        _session = new ChordsSession(_chordsHost);
        _view = host.RegisterView(this);
        _command = host.RegisterTrackCommand(ShowChordsLabel, ChordIcon, ShowChords);
    }

    public Control CreateView()
    {
        var session = _session ?? throw new InvalidOperationException("Chords is not running.");
        var view = new ChordsView(session);
        view.DetachedFromVisualTree += (_, _) => view.Release();
        return view;
    }

    /// <summary>The track menu entry: plays the song if another one is on, then opens the window.</summary>
    public void ShowChords(TrackInfo track)
    {
        if (_host is null) return;
        var now = _host.NowPlaying.Track?.FilePath;
        if (!string.Equals(now, track.FilePath, StringComparison.OrdinalIgnoreCase) && track.Id.Length > 0)
            _host.Playback.PlayTracks(new[] { track.Id });
        _host.OpenView(this);
    }

    public void Shutdown()
    {
        _command?.Dispose();
        _command = null;
        _view?.Dispose();
        _view = null;
        _session?.Dispose();
        _session = null;
        _chordsHost?.Dispose();
        _chordsHost = null;
        _host = null;
    }
}
