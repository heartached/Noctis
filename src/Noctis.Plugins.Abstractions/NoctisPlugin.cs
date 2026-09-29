using Avalonia.Controls;

namespace Noctis.Plugins;

/// <summary>
/// Entry point of a Noctis plugin. Build a class library against
/// <c>Noctis.Plugins.Abstractions</c>, implement this interface once, put a <c>plugin.json</c>
/// manifest next to the DLL, zip the folder and install it from Settings → Plugins (or drop the
/// folder into <c>&lt;Noctis data&gt;/plugins/&lt;id&gt;/</c>). Noctis loads the manifest's entry
/// DLL in an isolated load context, creates one instance, and calls <see cref="Initialize"/> on
/// the UI thread. Exceptions from any callback are caught, the plugin is marked Failed and
/// stopped; it cannot take the app down. See docs/PLUGINS.md.
/// </summary>
public interface INoctisPlugin
{
    /// <summary>Who and what this is; shown in Settings → Plugins.</summary>
    PluginInfo Info { get; }

    /// <summary>Called once after load, on the UI thread. Register extensions on <paramref name="host"/> here.</summary>
    void Initialize(IPluginHost host);

    /// <summary>Called on disable/unload/app exit. Release timers, files and subscriptions.</summary>
    void Shutdown();
}

/// <summary>Plugin identity. <paramref name="Id"/> must be stable across versions (reverse-DNS style, e.g. "dev.example.pulsering").</summary>
public sealed record PluginInfo(string Id, string Name, string Version, string Author, string Description);

/// <summary>
/// What the host offers a plugin. Everything here is safe to call from the UI thread.
/// Members added in API 1.1 have default bodies that throw <see cref="NotSupportedException"/>,
/// so code compiled against 1.0 (and any 1.0 implementation of this interface) keeps working;
/// Noctis itself implements all of them. Hooks marked with a permission throw
/// <see cref="PluginPermissionException"/> unless plugin.json declares it.
/// </summary>
public interface IPluginHost
{
    /// <summary>The Noctis version the plugin is running in ("1.5.3").</summary>
    string AppVersion { get; }

    /// <summary>
    /// A folder private to this plugin for its own files. Since API 1.1 it lives outside the
    /// plugin folder (<c>&lt;data&gt;/plugin-data/&lt;id&gt;/</c>), so updating or replacing the
    /// plugin keeps it. Create it before writing (<see cref="Directory.CreateDirectory(string)"/>).
    /// </summary>
    string DataDirectory { get; }

    /// <summary>What is playing right now, with change notifications.</summary>
    INowPlaying NowPlaying { get; }

    /// <summary>Live beat pulse of the audio being heard (0..1), for visuals that move with the music.</summary>
    IBeatSource Beat { get; }

    /// <summary>Live spectrum of the audio being heard, for visualizer-style plugins.</summary>
    ISpectrumSource Spectrum { get; }

    /// <summary>Writes a line to the Noctis debug log, prefixed with the plugin name.</summary>
    void Log(string message);

    /// <summary>Adds a visual layer drawn behind the lyrics on the lyrics page. Call from <see cref="INoctisPlugin.Initialize"/>.</summary>
    void RegisterVisualLayer(IVisualLayerProvider provider);

    // ── API 1.1 ──

    /// <summary>API 1.1. The folder the plugin was loaded from (read-only; replaced on update).
    /// Use it for files you ship next to the DLL: plugin assemblies are loaded from memory, so
    /// <c>Assembly.Location</c> is empty.</summary>
    string PluginDirectory => throw Missing();

    /// <summary>API 1.1, "playback.control": play, pause, skip and seek.</summary>
    IPlaybackControl Playback => throw Missing();

    /// <summary>API 1.1, "library.read": read-only search over the library. Results are copies.</summary>
    ILibraryReader Library => throw Missing();

    /// <summary>API 1.1: the values of the settings this plugin declared in plugin.json.
    /// Noctis draws the controls in Settings → Plugins; the plugin only reads them.</summary>
    IPluginSettings Settings => throw Missing();

    /// <summary>API 1.1, "notifications": a short message in the app's notice pill.</summary>
    void Notify(string message) => throw Missing();

    /// <summary>API 1.1, "lyrics.provider": joins the online lyrics search after the built-in
    /// providers. Dispose the result to withdraw it (Noctis also does on disable).</summary>
    IDisposable RegisterLyricsProvider(ILyricsProvider provider) => throw Missing();

    /// <summary>API 1.1, "menu.commands": adds an entry to the track context menu.</summary>
    /// <param name="label">Menu text.</param>
    /// <param name="icon">Optional SVG path data (24×24 box), or null.</param>
    /// <param name="handler">Runs on the UI thread with a copy of the clicked track.</param>
    IDisposable RegisterTrackCommand(string label, string? icon, Action<TrackInfo> handler) => throw Missing();

    /// <summary>API 1.1, "menu.commands": async variant of
    /// <see cref="RegisterTrackCommand(string, string?, Action{TrackInfo})"/>.</summary>
    IDisposable RegisterTrackCommand(string label, string? icon, Func<TrackInfo, Task> handler) => throw Missing();

    /// <summary>API 1.1, default "playback.read": called when a track counts as listened
    /// (played past half its length or four minutes: the Last.fm rule), with the time it started.
    /// Fires whether or not a scrobbling service is connected.</summary>
    IDisposable OnTrackScrobbled(Action<TrackInfo, DateTimeOffset> handler) => throw Missing();

    private static NotSupportedException Missing()
        => new("This Noctis build does not implement plugin API 1.1. Set minAppVersion in plugin.json.");
}

/// <summary>Current track and transport state. Events are raised on the UI thread.</summary>
public interface INowPlaying
{
    /// <summary>The current track, or null when nothing is loaded.</summary>
    NowPlayingTrack? Track { get; }

    bool IsPlaying { get; }

    /// <summary>Playback position; polled, not a stream of events.</summary>
    TimeSpan Position { get; }

    /// <summary>A new track started (or playback was cleared: <see cref="Track"/> is null).</summary>
    event EventHandler? TrackChanged;

    /// <summary>Play/pause flipped.</summary>
    event EventHandler? IsPlayingChanged;
}

/// <summary>A read-only snapshot of a track for plugins.</summary>
public sealed record NowPlayingTrack(
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    string FilePath,
    string? ArtworkPath,
    int Bpm);

/// <summary>Beat pulse of the audio at the speaker. False when no live audio is flowing (paused, or an engine without a tap).</summary>
public interface IBeatSource
{
    bool TryRead(out double pulse);
}

/// <summary>Log-spaced spectrum (0..1 per band) of the audio at the speaker. Fills <paramref name="bands"/> with as many bands as it has room for.</summary>
public interface ISpectrumSource
{
    bool TryRead(Span<float> bands);
}

/// <summary>
/// A visual layer for the lyrics page. <see cref="CreateLayer"/> is called on the UI thread
/// when the page mounts and the control is disposed with the page; keep per-frame work
/// cheap (transform/opacity writes), never touch layout per frame.
/// </summary>
public interface IVisualLayerProvider
{
    /// <summary>Shown in Settings → Plugins under the owning plugin.</summary>
    string Name { get; }

    Control CreateLayer();
}
