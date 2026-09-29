using Noctis.Plugins;

namespace Noctis.SamplePlugin.TrackTools;

/// <summary>
/// Reference plugin for API 1.1. Everything it registers is returned as an
/// <see cref="IDisposable"/>; <see cref="Shutdown"/> disposes them (Noctis would also drop
/// them on disable, but tidy plugins clean up after themselves).
/// </summary>
public sealed class TrackToolsPlugin : INoctisPlugin
{
    private readonly List<IDisposable> _registrations = new();
    private IPluginHost? _host;

    public PluginInfo Info { get; } = new(
        Id: "dev.noctis.samples.tracktools",
        Name: "Track Tools",
        Version: "1.0.0",
        Author: "Noctis",
        Description: "Reference for plugin API 1.1.");

    public void Initialize(IPluginHost host)
    {
        _host = host;

        // "menu.commands" + "library.read" + "notifications"
        _registrations.Add(host.RegisterTrackCommand(
            "More by this artist",
            // A 24×24 SVG path (a person), drawn with the menu's foreground colour.
            "M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8zm0 2c-4 0-8 2-8 5v1h16v-1c0-3-4-5-8-5z",
            MoreByArtist));

        // "lyrics.provider": joins the search after LRCLIB and NetEase.
        _registrations.Add(host.RegisterLyricsProvider(new StubLyricsProvider(host)));

        // Default "playback.read": fires on the Last.fm rule (half the track or 4 minutes).
        _registrations.Add(host.OnTrackScrobbled((track, startedAt) =>
            host.Log($"{host.Settings.GetString("greeting") ?? "Listened to"} {track.Artist} – {track.Title} (started {startedAt:t})")));

        host.Settings.Changed += OnSettingChanged;
        host.Log($"initialized on Noctis {host.AppVersion}, data in {host.DataDirectory}");
    }

    public void Shutdown()
    {
        if (_host is not null) _host.Settings.Changed -= OnSettingChanged;
        foreach (var r in _registrations) r.Dispose();
        _registrations.Clear();
        _host = null;
    }

    private void MoreByArtist(TrackInfo track)
    {
        if (_host is null) return;
        var limit = (int)_host.Settings.GetNumber("searchLimit", 25);
        var others = _host.Library.Search(track.Artist, limit)
            .Where(t => t.Id != track.Id && string.Equals(t.Artist, track.Artist, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var albums = others.Select(t => t.Album).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        _host.Notify(others.Count == 0
            ? $"Nothing else by {track.Artist} in your library."
            : $"{others.Count} more by {track.Artist} across {albums} album{(albums == 1 ? "" : "s")}.");
    }

    private void OnSettingChanged(object? sender, string key) => _host?.Log($"setting '{key}' changed");

    /// <summary>
    /// A provider stub: answers only when the "stubLyrics" setting is on, with placeholder
    /// text. A real provider would call a web API here (and declare "network").
    /// </summary>
    private sealed class StubLyricsProvider : ILyricsProvider
    {
        private readonly IPluginHost _host;
        public StubLyricsProvider(IPluginHost host) => _host = host;

        public string Name => "Track Tools (stub)";

        public async Task<PluginLyrics?> FindAsync(LyricsQuery query, CancellationToken ct)
        {
            if (!_host.Settings.GetBool("stubLyrics")) return null;
            await Task.Delay(50, ct); // stands in for a network round trip; honour ct
            return new PluginLyrics(
                Synced: null,
                Plain: $"Placeholder lyrics for \"{query.Title}\"\nby {query.Artist}\n\n(from the Track Tools sample plugin)");
        }
    }
}
