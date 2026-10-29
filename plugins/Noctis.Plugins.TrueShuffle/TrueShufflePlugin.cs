using Avalonia;
using Avalonia.Controls;
using Noctis.Plugins.TrueShuffle.Shuffle;
using Noctis.Plugins.TrueShuffle.Views;

namespace Noctis.Plugins.TrueShuffle;

/// <summary>
/// True Shuffle, by Alfamin (MIT; https://github.com/Alfamin/TrueShuffle). Settings → Plugins →
/// True Shuffle → "Open True Shuffle" (or a song's menu → True Shuffle) opens its window. It uses
/// only the user's own library and listening on this computer: no network, no catalogue.
/// </summary>
public sealed class TrueShufflePlugin : INoctisPlugin, IPluginViewProvider
{
    public const string Id = "dev.noctis.plugins.trueshuffle";
    public const string Author = "Alfamin";
    public const string Version = "1.0.0";
    public const string About = "A fairer shuffle with modes for no repeats, rarely played songs and more.";

    private IPluginHost? _host;
    private ShuffleController? _controller;
    private IDisposable? _view, _command;

    public PluginInfo Info { get; } = new(Id, "True Shuffle", Version, Author, About);

    public string Name => "True Shuffle";

    /// <summary>API 1.5: the view is one short column (560 wide at most), not a page.</summary>
    public Size? PreferredWindowSize => new Size(600, 440);

    /// <summary>Tests: the running controller.</summary>
    public ShuffleController? Controller => _controller;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _controller = new ShuffleController(host);
        _view = host.RegisterView(this);
        _command = host.RegisterTrackCommand("True Shuffle", null, (TrackInfo _) => { host.OpenView(this); });
    }

    public Control CreateView()
    {
        var host = _host ?? throw new InvalidOperationException("True Shuffle is not running.");
        return new TrueShuffleView(_controller!, host);
    }

    public void Shutdown()
    {
        _command?.Dispose();
        _view?.Dispose();
        _controller?.Dispose();
        _command = _view = null;
        _controller = null;
        _host = null;
    }
}
