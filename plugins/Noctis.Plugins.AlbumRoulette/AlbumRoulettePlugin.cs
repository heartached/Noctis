using Avalonia;
using Avalonia.Controls;
using Noctis.Plugins.AlbumRoulette.Roulette;
using Noctis.Plugins.AlbumRoulette.Views;

namespace Noctis.Plugins.AlbumRoulette;

/// <summary>
/// Album Roulette (official Noctis plugin): Settings → Plugins → Album Roulette → "Open Album
/// Roulette" opens the reel in a compact window. "Spin from this artist" in a track's menu opens
/// it with that artist's albums and spins. With Album of the Day on, one album a day arrives as
/// a notice at the hour the user picked.
/// </summary>
public sealed class AlbumRoulettePlugin : INoctisPlugin, IPluginViewProvider
{
    public const string Id = "dev.noctis.plugins.albumroulette";
    public const string Version = "1.0.1";
    public const string About = "Spin a reel of your albums and let it pick what to play.";
    public const string SpinFromArtistLabel = "Spin from this artist";
    // A small wheel (24x24 box) for the menu entry.
    private const string WheelIcon = "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zm1 2.07A8 8 0 0 1 19.93 11H13zM11 4.07V11H4.07A8 8 0 0 1 11 4.07zM4.07 13H11v6.93A8 8 0 0 1 4.07 13zM13 19.93V13h6.93A8 8 0 0 1 13 19.93z";

    private IPluginHost? _host;
    private PluginRouletteHost? _rouletteHost;
    private DailyPicker? _daily;
    private IDisposable? _view;
    private IDisposable? _command;
    private RouletteView? _open;
    private string? _pendingArtist;

    public PluginInfo Info { get; } = new(Id, "Album Roulette", Version, "Noctis", About);

    public string Name => "Album Roulette";

    /// <summary>API 1.5: a compact, tall window (the host's default is made for pages).</summary>
    public Size? PreferredWindowSize => new Size(RouletteView.WindowWidth, RouletteView.WindowHeight);

    public Size? MinWindowSize => new Size(RouletteView.MinWindowWidth, RouletteView.MinWindowHeight);

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _rouletteHost = new PluginRouletteHost(host);
        _view = host.RegisterView(this);
        _command = host.RegisterTrackCommand(SpinFromArtistLabel, WheelIcon, SpinFromArtist);
        _daily = new DailyPicker(_rouletteHost);
        _daily.Start();
    }

    public Control CreateView()
    {
        var host = _rouletteHost ?? throw new InvalidOperationException("Album Roulette is not running.");
        var view = new RouletteView(host);
        _open = view;
        if (_pendingArtist is { } artist)
        {
            _pendingArtist = null;
            view.SpinFromArtist(artist);
        }
        // The window closed: drop the covers and the subscriptions this view held.
        view.DetachedFromVisualTree += (_, _) =>
        {
            view.Release();
            if (ReferenceEquals(_open, view)) _open = null;
        };
        return view;
    }

    /// <summary>The track menu entry: that artist's albums on the reel, and a spin.</summary>
    public void SpinFromArtist(TrackInfo track)
    {
        var artist = string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist;
        if (string.IsNullOrWhiteSpace(artist) || _host is null) return;
        if (_open is { } view)
        {
            view.SpinFromArtist(artist);
            _host.OpenView(this); // brings the open window forward
            return;
        }
        _pendingArtist = artist;
        if (!_host.OpenView(this)) _pendingArtist = null;
    }

    public void Shutdown()
    {
        _command?.Dispose();
        _command = null;
        _view?.Dispose();
        _view = null;
        _daily?.Dispose();
        _daily = null;
        _rouletteHost?.Dispose();
        _rouletteHost = null;
        _open = null;
        _host = null;
    }
}
