using Avalonia.Controls;
using Noctis.Plugins.AlbumTown.Town;
using Noctis.Plugins.AlbumTown.Views;

namespace Noctis.Plugins.AlbumTown;

/// <summary>
/// Album Town: your albums as little square creatures. Settings → Plugins → Album Town →
/// "Open Album Town" opens the town in a window of its own. By Jafezy JD (Noctis Discord).
/// </summary>
public sealed class AlbumTownPlugin : INoctisPlugin, IPluginViewProvider
{
    public const string Id = "dev.noctis.plugins.albumtown";
    public const string Author = "Jafezy JD";
    public const string Version = "1.1.0";
    public const string About = "Your albums become little cover creatures that wander a plaza, chat about what they have in common and live in apartments that grow with every album. By Jafezy JD.";

    private IPluginHost? _host;
    private IDisposable? _view;
    private readonly List<PluginTownHost> _townHosts = new();

    public PluginInfo Info { get; } = new(Id, "Album Town", Version, Author, About);

    public string Name => "Album Town";

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _view = host.RegisterView(this);
    }

    public Control CreateView()
    {
        var host = _host ?? throw new InvalidOperationException("Album Town is not running.");
        var town = new PluginTownHost(host);
        _townHosts.Add(town);
        var view = new AlbumTownView(town);
        // The window closed: drop the host subscriptions this view held.
        view.DetachedFromVisualTree += (_, _) =>
        {
            town.Dispose();
            _townHosts.Remove(town);
            view.ReleaseCovers();
        };
        return view;
    }

    public void Shutdown()
    {
        _view?.Dispose();
        _view = null;
        foreach (var t in _townHosts) t.Dispose();
        _townHosts.Clear();
        _host = null;
    }
}
