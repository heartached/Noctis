using Avalonia;
using Avalonia.Controls;
using Noctis.Plugins.AlbumRoulette.Roulette;
using Noctis.Plugins.AlbumRoulette.Views;

namespace Noctis.Plugins.AlbumRoulette;

/// <summary>
/// Album Roulette (official Noctis plugin): Settings → Plugins → Album Roulette → "Open Album
/// Roulette" opens the reel in a compact window, which spins albums or single songs. "Spin from
/// this artist" in a track's menu opens it with that artist's albums and spins. On Noctis with
/// plugin API 1.7, an artist page's "…" menu has Album Roulette and Song Roulette (that artist
/// only), and the Songs page's top bar a Song Roulette button (the whole library). With Album of
/// the Day on, one album a day arrives as a notice at the hour the user picked.
/// </summary>
public sealed class AlbumRoulettePlugin : INoctisPlugin, IPluginViewProvider
{
    public const string Id = "dev.noctis.plugins.albumroulette";
    public const string Version = "1.0.1";
    public const string About = "Spin a reel of your albums and let it pick what to play.";
    public const string SpinFromArtistLabel = "Spin from this artist";
    public const string AlbumRouletteLabel = "Album Roulette";
    public const string SongRouletteLabel = "Song Roulette";
    // A small wheel (24x24 box) for the menu entry.
    private const string WheelIcon = "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zm1 2.07A8 8 0 0 1 19.93 11H13zM11 4.07V11H4.07A8 8 0 0 1 11 4.07zM4.07 13H11v6.93A8 8 0 0 1 4.07 13zM13 19.93V13h6.93A8 8 0 0 1 13 19.93z";
    // The artist menu lists both roulettes side by side: one icon each (owner 11-08, the shared
    // wheel read as two identical "⊕" rows). Album: a segmented wheel. Song: a note in a ring.
    private const string AlbumRouletteIcon = "M12 2a10 10 0 1 0 0 20a10 10 0 1 0 0-20zm0 1.2a8.8 8.8 0 1 1 0 17.6a8.8 8.8 0 1 1 0-17.6zM12 12L12 4.5A7.5 7.5 0 0 1 18.5 8.25zM12 12L18.5 15.75A7.5 7.5 0 0 1 12 19.5zM12 12L5.5 15.75A7.5 7.5 0 0 1 5.5 8.25z";
    private const string SongRouletteIcon = "M12 2a10 10 0 1 0 0 20a10 10 0 1 0 0-20zm0 2a8 8 0 1 1 0 16a8 8 0 1 1 0-16zM10.7 13.1a2.2 2.2 0 1 0 0 4.4a2.2 2.2 0 1 0 0-4.4zM12.9 7.2h1.4v8.1h-1.4zM14.3 7.2H17v2.4h-2.7z";

    private IPluginHost? _host;
    private PluginRouletteHost? _rouletteHost;
    private DailyPicker? _daily;
    private IDisposable? _view;
    private IDisposable? _command;
    private readonly List<IDisposable> _libraryCommands = new();
    private RouletteView? _open;
    private (RouletteMode? Mode, string? Artist)? _pending;

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
        try
        {
            _libraryCommands.Add(host.RegisterLibraryCommand(AlbumRouletteLabel, AlbumRouletteIcon, LibraryCommandPlaces.ArtistPage,
                c => Open(RouletteMode.Albums, c.Artist)));
            _libraryCommands.Add(host.RegisterLibraryCommand(SongRouletteLabel, SongRouletteIcon,
                LibraryCommandPlaces.ArtistPage | LibraryCommandPlaces.SongsPage, c => Open(RouletteMode.Songs, c.Artist)));
        }
        catch (NotSupportedException)
        {
            // Noctis before plugin API 1.7: the window and the track menu entry still work.
        }
        _daily = new DailyPicker(_rouletteHost);
        _daily.Start();
    }

    public Control CreateView()
    {
        var host = _rouletteHost ?? throw new InvalidOperationException("Album Roulette is not running.");
        var view = new RouletteView(host);
        _open = view;
        if (_pending is { } pending)
        {
            _pending = null;
            view.SpinFor(pending.Mode, pending.Artist);
        }
        // The window closed: drop the covers and the subscriptions this view held.
        view.DetachedFromVisualTree += (_, _) =>
        {
            view.Release();
            if (ReferenceEquals(_open, view)) _open = null;
        };
        return view;
    }

    /// <summary>The track menu entry: that artist's albums on the reel (or songs, if the window
    /// is in Song Roulette), and a spin.</summary>
    public void SpinFromArtist(TrackInfo track)
    {
        var artist = string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist;
        if (string.IsNullOrWhiteSpace(artist)) return;
        Open(null, artist);
    }

    /// <summary>Opens the window (or brings it forward) in <paramref name="mode"/> (null: as it is),
    /// scoped to <paramref name="artist"/> (null: the whole library), and spins.</summary>
    public void Open(RouletteMode? mode, string? artist)
    {
        if (_host is null) return;
        if (_open is { } view)
        {
            view.SpinFor(mode, artist);
            _host.OpenView(this); // brings the open window forward
            return;
        }
        _pending = (mode, artist);
        if (!_host.OpenView(this)) _pending = null;
    }

    public void Shutdown()
    {
        _command?.Dispose();
        _command = null;
        foreach (var c in _libraryCommands) c.Dispose();
        _libraryCommands.Clear();
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
