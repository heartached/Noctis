using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.Loader;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.ViewModels;

namespace Noctis.Services.Plugins;

/// <summary>State of one discovered plugin, shown in Settings → Plugins.</summary>
public sealed partial class LoadedPlugin : ObservableObject
{
    public LoadedPlugin(string directory, string assemblyPath)
    {
        Directory = directory;
        AssemblyPath = assemblyPath;
        Name = System.IO.Path.GetFileName(directory);
    }

    /// <summary>Folder under plugins/ this came from — also the identity for the disabled list.</summary>
    public string Directory { get; }
    public string AssemblyPath { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _description = "";
    /// <summary>"Running", "Disabled" or "Failed".</summary>
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _isEnabled;
    /// <summary>Names of the visual layers this plugin registered.</summary>
    [ObservableProperty] private string _extensions = "";

    public string FolderName => System.IO.Path.GetFileName(Directory);
    public bool HasError => Error.Length > 0;

    internal PluginLoadContext? Context;
    internal INoctisPlugin? Instance;
    internal readonly List<IVisualLayerProvider> VisualLayers = new();

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    /// <summary>Set by the host: a flip of <see cref="IsEnabled"/> from the UI starts/stops the plugin.</summary>
    internal Action<LoadedPlugin, bool>? EnabledChangedByUser;

    partial void OnIsEnabledChanged(bool value) => EnabledChangedByUser?.Invoke(this, value);
}

/// <summary>Isolated, unloadable load context: the plugin's own dependencies come from its
/// folder; anything the host already has (the SDK, Avalonia, the BCL) resolves to the host's
/// copy so types match across the boundary.</summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string mainAssemblyPath) : base(name: System.IO.Path.GetFileName(mainAssemblyPath), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Shared surface: the host's assemblies win so INoctisPlugin/Control are the same types.
        if (Default.Assemblies.Any(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase)))
            return null;
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}

/// <summary>
/// Discovers, loads and supervises plugins. Layout: <c>&lt;data&gt;/plugins/&lt;Name&gt;/*.dll</c>, one
/// folder per plugin, every DLL in the folder scanned for <see cref="INoctisPlugin"/>. Each
/// plugin gets its own collectible <see cref="AssemblyLoadContext"/>, a private data folder,
/// and a host adapter over the player. Every call into a plugin is guarded: a throwing plugin
/// is marked Failed with the message, never propagated. Disabled plugins are remembered by
/// folder name in <see cref="AppSettings.DisabledPlugins"/>.
/// </summary>
public sealed class PluginHost
{
    private readonly PlayerViewModel? _player;
    private readonly Func<AppSettings> _settings;
    private readonly Action _saveSettings;
    private readonly string _appVersion;

    /// <param name="player">The live player; null in tests (plugins then see no track and no events).</param>
    public PluginHost(PlayerViewModel? player, string dataDirectory, Func<AppSettings> settings, Action saveSettings, string appVersion)
    {
        _player = player;
        _settings = settings;
        _saveSettings = saveSettings;
        _appVersion = appVersion;
        PluginsDirectory = Path.Combine(dataDirectory, "plugins");
    }

    /// <summary>Where users drop plugin folders.</summary>
    public string PluginsDirectory { get; }

    /// <summary>Every discovered plugin, loaded or not, in folder-name order.</summary>
    public ObservableCollection<LoadedPlugin> Plugins { get; } = new();

    /// <summary>Visual layers of every running plugin, in load order.</summary>
    public IReadOnlyList<IVisualLayerProvider> VisualLayers
        => Plugins.Where(p => p.Instance is not null).SelectMany(p => p.VisualLayers).ToList();

    /// <summary>Raised on the UI thread when the set of visual layers changes (load/unload/reload).</summary>
    public event EventHandler? VisualLayersChanged;

    /// <summary>Scans the plugins folder and loads everything not disabled. Safe to call again: unloads first.</summary>
    public void LoadAll()
    {
        UnloadAll();
        try { Directory.CreateDirectory(PluginsDirectory); }
        catch (Exception ex) { DebugLogger.Error(DebugLogger.Category.State, "Plugins", $"create folder: {ex.Message}"); return; }

        var disabled = new HashSet<string>(_settings().DisabledPlugins ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(PluginsDirectory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var main = PickMainAssembly(dir);
            if (main is null) continue;
            var plugin = new LoadedPlugin(dir, main) { IsEnabled = !disabled.Contains(Path.GetFileName(dir)) };
            plugin.EnabledChangedByUser = OnEnabledToggled;
            Plugins.Add(plugin);
            if (plugin.IsEnabled) Start(plugin);
            else plugin.Status = "Disabled";
        }
        VisualLayersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Loads one plugin folder directly (tests and future "install from zip").</summary>
    public LoadedPlugin LoadFrom(string directory)
    {
        var main = PickMainAssembly(directory) ?? throw new FileNotFoundException("No plugin DLL in " + directory);
        var plugin = new LoadedPlugin(directory, main) { IsEnabled = true };
        plugin.EnabledChangedByUser = OnEnabledToggled;
        Plugins.Add(plugin);
        Start(plugin);
        VisualLayersChanged?.Invoke(this, EventArgs.Empty);
        return plugin;
    }

    /// <summary>Enables or disables a plugin, persists the choice, and starts/stops it live.</summary>
    public void SetEnabled(LoadedPlugin plugin, bool enabled)
    {
        var settings = _settings();
        settings.DisabledPlugins ??= new List<string>();
        var key = plugin.FolderName;
        settings.DisabledPlugins.RemoveAll(d => string.Equals(d, key, StringComparison.OrdinalIgnoreCase));
        if (!enabled) settings.DisabledPlugins.Add(key);
        _saveSettings();

        // Start/stop BEFORE writing IsEnabled: the flag's change handler compares it with the
        // running state, so writing it first would re-enter SetEnabled once more.
        if (enabled && plugin.Instance is null) Start(plugin);
        else if (!enabled && plugin.Instance is not null) { Stop(plugin); plugin.Status = "Disabled"; }
        plugin.IsEnabled = enabled;
        VisualLayersChanged?.Invoke(this, EventArgs.Empty);
    }

    // The Settings toggle binds IsEnabled two-way; SetEnabled also writes IsEnabled, so only
    // act when the flag disagrees with the actual running state (re-entrancy guard).
    private void OnEnabledToggled(LoadedPlugin plugin, bool enabled)
    {
        var running = plugin.Instance is not null || plugin.Status == "Failed";
        if (enabled != running) SetEnabled(plugin, enabled);
    }

    /// <summary>Shuts every plugin down and forgets them (app exit, or before a rescan).</summary>
    public void UnloadAll()
    {
        foreach (var p in Plugins) Stop(p);
        Plugins.Clear();
    }

    private static string? PickMainAssembly(string dir)
    {
        // Prefer a DLL named like the folder; else the first DLL that isn't the SDK.
        var dlls = Directory.GetFiles(dir, "*.dll");
        var folder = Path.GetFileName(dir);
        return dlls.FirstOrDefault(d => Path.GetFileNameWithoutExtension(d).Equals(folder, StringComparison.OrdinalIgnoreCase))
            ?? dlls.FirstOrDefault(d => !Path.GetFileName(d).StartsWith("Noctis.Plugins.Abstractions", StringComparison.OrdinalIgnoreCase)
                                     && !Path.GetFileName(d).StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));
    }

    private void Start(LoadedPlugin plugin)
    {
        plugin.Error = "";
        try
        {
            plugin.Context = new PluginLoadContext(plugin.AssemblyPath);
            var assembly = plugin.Context.LoadFromAssemblyPath(plugin.AssemblyPath);
            // One plugin per folder. With several implementations in one DLL the folder name
            // picks the type (so a test can install the same DLL twice); else the first by name.
            var candidates = assembly.GetTypes()
                .Where(t => typeof(INoctisPlugin).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null)
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToList();
            var type = candidates.FirstOrDefault(t => t.Name.Equals(plugin.FolderName, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault()
                ?? throw new InvalidOperationException("No public INoctisPlugin with a parameterless constructor.");
            var instance = (INoctisPlugin)Activator.CreateInstance(type)!;

            var info = instance.Info ?? throw new InvalidOperationException("Plugin.Info returned null.");
            plugin.Name = string.IsNullOrWhiteSpace(info.Name) ? plugin.FolderName : info.Name;
            plugin.Version = info.Version ?? "";
            plugin.Author = info.Author ?? "";
            plugin.Description = info.Description ?? "";

            var adapter = new HostAdapter(this, plugin, _player, Path.Combine(plugin.Directory, "data"), _appVersion);
            instance.Initialize(adapter);
            plugin.Instance = instance;
            plugin.Extensions = string.Join(", ", plugin.VisualLayers.Select(v => v.Name));
            plugin.Status = "Running";
            DebugLogger.Info(DebugLogger.Category.State, "Plugins", $"loaded {plugin.Name} {plugin.Version} from {plugin.FolderName}");
        }
        catch (Exception ex)
        {
            var message = ex is ReflectionTypeLoadException rtl
                ? string.Join("; ", rtl.LoaderExceptions.Select(e => e?.Message).Where(m => m is not null))
                : ex.Message;
            plugin.Instance = null;
            plugin.VisualLayers.Clear();
            plugin.Status = "Failed";
            plugin.Error = message;
            DebugLogger.Error(DebugLogger.Category.State, "Plugins", $"{plugin.FolderName} failed: {message}");
            TryUnloadContext(plugin);
        }
    }

    private void Stop(LoadedPlugin plugin)
    {
        if (plugin.Instance is not null)
        {
            try { plugin.Instance.Shutdown(); }
            catch (Exception ex) { DebugLogger.Error(DebugLogger.Category.State, "Plugins", $"{plugin.FolderName} shutdown: {ex.Message}"); }
        }
        plugin.Instance = null;
        plugin.VisualLayers.Clear();
        plugin.Extensions = "";
        TryUnloadContext(plugin);
    }

    private static void TryUnloadContext(LoadedPlugin plugin)
    {
        try { plugin.Context?.Unload(); } catch { /* best effort */ }
        plugin.Context = null;
    }

    /// <summary>What a plugin sees as its host: thin adapters over the player and the audio taps.</summary>
    private sealed class HostAdapter : IPluginHost, INowPlaying, IBeatSource, ISpectrumSource
    {
        private readonly PluginHost _owner;
        private readonly LoadedPlugin _plugin;
        private readonly PlayerViewModel? _player;

        public HostAdapter(PluginHost owner, LoadedPlugin plugin, PlayerViewModel? player, string dataDirectory, string appVersion)
        {
            _owner = owner;
            _plugin = plugin;
            _player = player;
            DataDirectory = dataDirectory;
            AppVersion = appVersion;
            if (_player is not null) _player.PropertyChanged += OnPlayerPropertyChanged;
        }

        public string AppVersion { get; }
        public string DataDirectory { get; }
        public INowPlaying NowPlaying => this;
        public IBeatSource Beat => this;
        public ISpectrumSource Spectrum => this;

        public void Log(string message)
            => DebugLogger.Info(DebugLogger.Category.State, "Plugin:" + _plugin.Name, message ?? "");

        public void RegisterVisualLayer(IVisualLayerProvider provider)
        {
            if (provider is null) return;
            _plugin.VisualLayers.Add(provider);
            _plugin.Extensions = string.Join(", ", _plugin.VisualLayers.Select(v => v.Name));
            if (_plugin.Instance is not null) _owner.VisualLayersChanged?.Invoke(_owner, EventArgs.Empty);
        }

        // INowPlaying
        public NowPlayingTrack? Track => _player?.CurrentTrack is { } t
            ? new NowPlayingTrack(t.Title, t.Artist, t.Album, t.Duration, t.FilePath, _player.CurrentArtPath, Convert.ToInt32((object?)t.Bpm ?? 0))
            : null;
        public bool IsPlaying => _player?.IsPlaying ?? false;
        public TimeSpan Position => _player?.Position ?? TimeSpan.Zero;
        public event EventHandler? TrackChanged;
        public event EventHandler? IsPlayingChanged;

        private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_plugin.Instance is null) return;
            try
            {
                if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack)) TrackChanged?.Invoke(this, EventArgs.Empty);
                else if (e.PropertyName is nameof(PlayerViewModel.IsPlaying) or "State") IsPlayingChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                DebugLogger.Error(DebugLogger.Category.State, "Plugins", $"{_plugin.Name} event handler: {ex.Message}");
            }
        }

        // IBeatSource / ISpectrumSource
        public bool TryRead(out double pulse) => BeatMeter.Shared.TryRead(BeatMeter.Shared.NowMs, out pulse);
        public bool TryRead(Span<float> bands) => SpectrumMeter.Shared.TryRead(SpectrumMeter.Shared.NowMs, bands);
    }
}
