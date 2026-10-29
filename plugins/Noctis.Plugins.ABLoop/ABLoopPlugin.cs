using Avalonia;
using Avalonia.Controls;
using Noctis.Plugins.ABLoop.Looping;
using Noctis.Plugins.ABLoop.Views;

namespace Noctis.Plugins.ABLoop;

/// <summary>
/// A-B Loop (official Noctis plugin): Settings → Plugins → A-B Loop → "Open A-B Loop", or
/// "Loop a part" in a track's menu, opens a slim window to set A and B in the playing song and
/// loop that part. The loop keeps going with the window closed, and clears when the song changes.
/// </summary>
public sealed class ABLoopPlugin : INoctisPlugin, IPluginViewProvider
{
    public const string Id = "dev.noctis.plugins.abloop";
    public const string Version = "1.0.0";
    public const string About = "Loop any part of a song to practise it, and save your loops for later.";
    public const string LoopAPartLabel = "Loop a part";
    // A looping arrow (24x24 box) for the menu entry.
    private const string LoopIcon = "M12 4V1L8 5l4 4V6c3.31 0 6 2.69 6 6 0 1.01-.25 1.97-.7 2.8l1.46 1.46A7.93 7.93 0 0 0 20 12c0-4.42-3.58-8-8-8zm0 14c-3.31 0-6-2.69-6-6 0-1.01.25-1.97.7-2.8L5.24 7.74A7.93 7.93 0 0 0 4 12c0 4.42 3.58 8 8 8v3l4-4-4-4v3z";

    private IPluginHost? _host;
    private PluginLoopHost? _loopHost;
    private LoopSession? _session;
    private IDisposable? _view;
    private IDisposable? _command;

    public PluginInfo Info { get; } = new(Id, "A-B Loop", Version, "Noctis", About);

    public string Name => "A-B Loop";

    /// <summary>API 1.5: a slim window (the host's default is made for pages).</summary>
    public Size? PreferredWindowSize => new Size(ABLoopView.WindowWidth, ABLoopView.WindowHeight);

    public Size? MinWindowSize => new Size(ABLoopView.MinWindowWidth, ABLoopView.MinWindowHeight);

    /// <summary>The running loop (tests).</summary>
    public LoopSession? Session => _session;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _loopHost = new PluginLoopHost(host);
        _session = new LoopSession(_loopHost);
        _view = host.RegisterView(this);
        _command = host.RegisterTrackCommand(LoopAPartLabel, LoopIcon, LoopAPart);
    }

    public Control CreateView()
    {
        var session = _session ?? throw new InvalidOperationException("A-B Loop is not running.");
        var view = new ABLoopView(session);
        view.DetachedFromVisualTree += (_, _) => view.Release();
        return view;
    }

    /// <summary>The track menu entry: plays the song if another one is on, then opens the window.</summary>
    public void LoopAPart(TrackInfo track)
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
        _loopHost?.Dispose();
        _loopHost = null;
        _host = null;
    }
}
