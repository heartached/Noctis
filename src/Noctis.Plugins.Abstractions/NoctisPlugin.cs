using Avalonia.Controls;

namespace Noctis.Plugins;

/// <summary>
/// Entry point of a Noctis plugin. Build a class library against
/// <c>Noctis.Plugins.Abstractions</c>, implement this interface once, and drop the output
/// folder into <c>&lt;Noctis data&gt;/plugins/&lt;YourPlugin&gt;/</c>. Noctis loads every DLL in
/// that folder in an isolated load context, creates one instance per implementation, and
/// calls <see cref="Initialize"/> on the UI thread. Exceptions are caught and shown in
/// Settings → Plugins; a plugin can never take the app down.
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

/// <summary>What the host offers a plugin. Everything here is safe to call from the UI thread.</summary>
public interface IPluginHost
{
    /// <summary>The Noctis version the plugin is running in.</summary>
    string AppVersion { get; }

    /// <summary>A folder private to this plugin (created on demand) for its own files/settings.</summary>
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
