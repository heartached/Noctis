using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.ViewModels;

namespace Noctis.Services.Plugins;

/// <summary>Status values of <see cref="LoadedPlugin.Status"/> (also the resx suffix of their labels).</summary>
public static class PluginStatus
{
    public const string Running = "Running";
    public const string Disabled = "Disabled";
    public const string Failed = "Failed";
    /// <summary>Community plugins are off: nothing third-party runs.</summary>
    public const string Restricted = "Restricted";
    /// <summary>Wrong API major, too-old app, or another platform.</summary>
    public const string Incompatible = "Incompatible";
    /// <summary>plugin.json is broken, the entry DLL is missing, or the id is taken.</summary>
    public const string Invalid = "Invalid";
    /// <summary>Was approved before, but an update asks for more permissions.</summary>
    public const string NeedsApproval = "NeedsApproval";
    /// <summary>A content pack that is switched on: its themes, presets and languages are offered.</summary>
    public const string Active = "Active";
}

/// <summary>Localized names of plugin permissions: short chip labels and the approval dialog's sentences.</summary>
public static class PluginPermissionText
{
    private static string Suffix(string permission) => permission switch
    {
        PluginPermissions.PlaybackControl => "PlaybackControl",
        PluginPermissions.LibraryRead => "LibraryRead",
        PluginPermissions.Network => "Network",
        PluginPermissions.LyricsProvider => "LyricsProvider",
        PluginPermissions.MenuCommands => "MenuCommands",
        PluginPermissions.Notifications => "Notifications",
        _ => "",
    };

    /// <summary>"Playback control".</summary>
    public static string Short(string permission)
        => Suffix(permission) is { Length: > 0 } s ? Loc.T("Plugins.PermShort." + s) : permission;

    /// <summary>"Control playback: play, pause, skip and seek".</summary>
    public static string Describe(string permission)
        => Suffix(permission) is { Length: > 0 } s ? Loc.T("Plugins.Perm." + s) : permission;
}

/// <summary>State of one discovered plugin, shown in Settings → Plugins.</summary>
public sealed partial class LoadedPlugin : ObservableObject
{
    public LoadedPlugin(string directory, string? assemblyPath, PluginManifest? manifest = null, string? manifestError = null)
    {
        Directory = directory;
        AssemblyPath = assemblyPath;
        Manifest = manifest;
        ManifestError = manifestError;
        Id = manifest?.Id ?? System.IO.Path.GetFileName(directory);
        Name = manifest?.Name ?? System.IO.Path.GetFileName(directory);
        Version = manifest?.Version ?? "";
        Author = manifest?.Author ?? "";
        Description = manifest?.Description ?? "";
        Permissions = manifest?.Permissions ?? Array.Empty<string>();
    }

    /// <summary>Folder under plugins/ this came from.</summary>
    public string Directory { get; }
    /// <summary>The DLL to load; null when there is nothing loadable (broken manifest, content pack).</summary>
    public string? AssemblyPath { get; }
    /// <summary>plugin.json, or null for a legacy plugin (or when it failed to parse).</summary>
    public PluginManifest? Manifest { get; }
    public string? ManifestError { get; }
    /// <summary>Identity for the enabled list, approvals, settings and the data folder:
    /// the manifest id, or the folder name for a legacy plugin.</summary>
    public string Id { get; }
    /// <summary>Loaded without a plugin.json (built for API 1.0).</summary>
    public bool IsLegacy => Manifest is null && ManifestError is null;
    /// <summary>A data-only pack ("type": "content"): never loads code, needs no approval.</summary>
    public bool IsContentPack => Manifest?.IsContent == true;
    public bool IsCodePlugin => !IsContentPack;
    /// <summary>The pack's parsed files; null for code plugins and for packs that failed to load.</summary>
    public ContentPackData? Content { get; init; }
    /// <summary>Declared permissions (without the implicit "playback.read").</summary>
    public IReadOnlyList<string> Permissions { get; }
    public bool HasPermissions => Permissions.Count > 0;
    /// <summary>Localized chip labels for <see cref="Permissions"/>.</summary>
    public IReadOnlyList<string> PermissionChips => Permissions.Select(PluginPermissionText.Short).ToList();
    public string? Homepage => Manifest?.Homepage;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _description = "";
    /// <summary>One of <see cref="PluginStatus"/>.</summary>
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _isEnabled;
    /// <summary>False when the switch has no effect (restricted mode, incompatible, invalid).</summary>
    [ObservableProperty] private bool _canToggle = true;
    /// <summary>What the running plugin added: visual layers, lyrics providers, menu commands.</summary>
    [ObservableProperty] private string _extensions = "";

    public string FolderName => System.IO.Path.GetFileName(Directory);
    public bool HasError => Error.Length > 0;
    /// <summary>Localized <see cref="Status"/>.</summary>
    public string StatusText => Status.Length == 0 ? "" : Loc.T("Plugins.Status." + Status);
    public bool HasAuthor => Author.Length > 0;
    public bool HasDescription => Description.Length > 0;
    public bool HasExtensions => Extensions.Length > 0;
    /// <summary>Manifest warnings, shown quietly under the card.</summary>
    public string Warnings => string.Join(" ", (Manifest?.Warnings ?? Array.Empty<string>()).Concat(Content?.Warnings ?? Array.Empty<string>()));
    public bool HasWarnings => Warnings.Length > 0;

    /// <summary>Controls for the settings declared in plugin.json.</summary>
    public ObservableCollection<PluginSettingItem> SettingItems { get; } = new();
    public bool HasSettings => SettingItems.Count > 0;

    /// <summary>Where <see cref="IPluginHost.DataDirectory"/> points: &lt;data&gt;/plugin-data/&lt;id&gt;.</summary>
    public string DataDirectory { get; internal set; } = "";

    public bool HasPermission(string permission)
        => permission == PluginPermissions.PlaybackRead || Permissions.Contains(permission);

    internal PluginLoadContext? Context;
    internal INoctisPlugin? Instance;
    internal PluginHost.HostAdapter? Adapter;
    internal readonly List<IVisualLayerProvider> VisualLayers = new();
    /// <summary>Tests: creates the instance in-process instead of from <see cref="AssemblyPath"/>.</summary>
    internal Func<INoctisPlugin>? InProcessFactory;
    internal bool PendingApproval;
    internal DateTime LastNotice = DateTime.MinValue;

    public bool IsRunning => Instance is not null;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusText));
    partial void OnAuthorChanged(string value) => OnPropertyChanged(nameof(HasAuthor));
    partial void OnDescriptionChanged(string value) => OnPropertyChanged(nameof(HasDescription));
    partial void OnExtensionsChanged(string value) => OnPropertyChanged(nameof(HasExtensions));

    /// <summary>Set by the host: a flip of <see cref="IsEnabled"/> from the UI starts/stops the plugin.</summary>
    internal Action<LoadedPlugin, bool>? EnabledChangedByUser;

    partial void OnIsEnabledChanged(bool value) => EnabledChangedByUser?.Invoke(this, value);
}

/// <summary>One declared setting as an editable row. Writes go back to the host, which persists them.</summary>
public sealed partial class PluginSettingItem : ObservableObject
{
    private readonly Action<PluginSettingItem> _changed;
    private bool _loading;

    public PluginSettingItem(PluginSettingDefinition definition, string value, Action<PluginSettingItem> changed)
    {
        Definition = definition;
        _changed = changed;
        _loading = true;
        Value = value;
        _loading = false;
    }

    public PluginSettingDefinition Definition { get; }
    public string Key => Definition.Key;
    public string Label => Definition.Label;
    public string? Description => Definition.Description;
    public bool HasDescription => !string.IsNullOrEmpty(Definition.Description);
    public bool IsBool => Definition.Type == PluginSettingType.Bool;
    public bool IsString => Definition.Type == PluginSettingType.String;
    public bool IsNumber => Definition.Type == PluginSettingType.Number;
    public bool IsChoice => Definition.Type == PluginSettingType.Choice;
    public IReadOnlyList<string> Choices => Definition.Choices;
    public decimal Minimum => Definition.Min is { } m ? (decimal)Math.Max(m, -1e9) : -1_000_000_000m;
    public decimal Maximum => Definition.Max is { } m ? (decimal)Math.Min(m, 1e9) : 1_000_000_000m;

    /// <summary>The stored value as invariant text (what <see cref="IPluginSettings"/> reads).</summary>
    public string Value { get; private set; } = "";

    public bool BoolValue
    {
        get => string.Equals(Value, "true", StringComparison.OrdinalIgnoreCase);
        set => Set(value ? "true" : "false");
    }

    public string TextValue
    {
        get => Value;
        set => Set(value ?? "");
    }

    public decimal? NumberValue
    {
        get => double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Math.Abs(d) < 7.9e27 ? (decimal)d : null;
        set
        {
            if (value is null) return;
            var d = (double)value.Value;
            if (Definition.Min is { } min) d = Math.Max(min, d);
            if (Definition.Max is { } max) d = Math.Min(max, d);
            Set(d.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    public string? SelectedChoice
    {
        get => Choices.Contains(Value) ? Value : Definition.Default;
        set { if (value is not null && Choices.Contains(value)) Set(value); }
    }

    private void Set(string value)
    {
        if (value == Value) return;
        Value = value;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(TextValue));
        OnPropertyChanged(nameof(NumberValue));
        OnPropertyChanged(nameof(SelectedChoice));
        if (!_loading) _changed(this);
    }
}

/// <summary>Isolated, unloadable load context: the plugin's own dependencies come from its
/// folder; anything the host already has (the SDK, Avalonia, the BCL) resolves to the host's
/// copy so types match across the boundary. Assemblies are read into memory rather than
/// mapped from disk, so the plugin folder is never locked and can be updated or removed
/// while Noctis runs.</summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _mainPath;

    public PluginLoadContext(string mainAssemblyPath) : base(name: System.IO.Path.GetFileName(mainAssemblyPath), isCollectible: true)
    {
        _mainPath = mainAssemblyPath;
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    public Assembly LoadMain() => LoadFromBytes(_mainPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Shared surface: the host's assemblies win so INoctisPlugin/Control are the same types.
        // The kit is always shared, whatever version the plugin was compiled against (minor
        // versions only add, so a 1.0 plugin binds to the host's 1.1).
        if (string.Equals(assemblyName.Name, "Noctis.Plugins.Abstractions", StringComparison.OrdinalIgnoreCase)
            || Default.Assemblies.Any(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase)))
            return null;
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromBytes(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    private Assembly LoadFromBytes(string path)
    {
        using var dll = new MemoryStream(File.ReadAllBytes(path));
        var pdb = System.IO.Path.ChangeExtension(path, ".pdb");
        if (!File.Exists(pdb)) return LoadFromStream(dll);
        using var symbols = new MemoryStream(File.ReadAllBytes(pdb));
        return LoadFromStream(dll, symbols);
    }
}

/// <summary>A plugin's lyrics provider as the lyrics search sees it: bounded in time, errors contained.</summary>
public sealed class PluginLyricsSource
{
    private readonly PluginHost _owner;
    internal readonly LoadedPlugin Plugin;
    private readonly ILyricsProvider _provider;

    internal PluginLyricsSource(PluginHost owner, LoadedPlugin plugin, ILyricsProvider provider, string name)
    {
        _owner = owner;
        Plugin = plugin;
        _provider = provider;
        Name = name;
    }

    /// <summary>Source label ("Try &lt;Name&gt;").</summary>
    public string Name { get; }
    public string PluginName => Plugin.Name;

    /// <summary>
    /// Asks the plugin on a worker thread. Null = not found (or the plugin stopped meanwhile);
    /// throws <see cref="LyricsProviderException"/> when the plugin threw or ran past
    /// <see cref="PluginHost.LyricsTimeout"/>, so the search counts it as "could not ask".
    /// </summary>
    public async Task<LrcLibResult?> FindAsync(string artist, string title, string album, TimeSpan duration, CancellationToken ct = default)
    {
        if (!Plugin.IsRunning) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = _owner.LyricsTimeout;
        cts.CancelAfter(timeout);
        var query = new LyricsQuery(artist ?? "", title ?? "", album ?? "", duration);
        // Task.Run: a provider that blocks before its first await must not stall the caller (the UI thread).
        var task = Task.Run(() => _provider.FindAsync(query, cts.Token) ?? Task.FromResult<PluginLyrics?>(null), CancellationToken.None);
        PluginLyrics? answer;
        try
        {
            answer = await task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            cts.Cancel();
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            DebugLog.WriteOnce("Plugins", $"lyrics-timeout:{Plugin.Id}", $"{Plugin.Name}: lyrics provider '{Name}' took longer than {timeout.TotalSeconds:0} s");
            throw new LyricsProviderException(Name, new TimeoutException($"no answer within {timeout.TotalSeconds:0} s"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DebugLog.WriteOnce("Plugins", $"lyrics-error:{Plugin.Id}:{ex.GetType().Name}", $"{Plugin.Name}: lyrics provider '{Name}' threw {ex.GetType().Name}: {ex.Message}");
            throw new LyricsProviderException(Name, ex);
        }
        if (answer is null || !Plugin.IsRunning) return null;
        return new LrcLibResult
        {
            TrackName = title,
            ArtistName = artist,
            AlbumName = album,
            Duration = duration.TotalSeconds,
            SyncedLyrics = string.IsNullOrWhiteSpace(answer.Synced) ? null : answer.Synced,
            PlainLyrics = string.IsNullOrWhiteSpace(answer.Plain) ? null : answer.Plain,
            Instrumental = answer.Instrumental,
        };
    }
}

/// <summary>A plugin's entry in the track context menu.</summary>
public sealed class PluginTrackCommand
{
    private readonly PluginHost _owner;
    internal readonly LoadedPlugin Plugin;
    internal readonly Func<TrackInfo, Task?> Handler;

    internal PluginTrackCommand(PluginHost owner, LoadedPlugin plugin, string label, string? icon, Func<TrackInfo, Task?> handler)
    {
        _owner = owner;
        Plugin = plugin;
        Label = label;
        Icon = icon;
        Handler = handler;
    }

    public string Label { get; }
    /// <summary>SVG path data, or null.</summary>
    public string? Icon { get; }
    public string PluginName => Plugin.Name;

    /// <summary>Runs the handler with a copy of <paramref name="track"/>; failures mark the plugin Failed.</summary>
    public void Execute(Track track) => _owner.RunTrackCommand(this, track);
}

/// <summary>A message a plugin asked to show.</summary>
public sealed record PluginNotice(string PluginName, string Message);

/// <summary>Result of an install from a .zip.</summary>
public enum PluginInstallOutcome { Installed, Updated, AlreadyInstalled, NotNewer, Failed }

public sealed record PluginInstallResult(PluginInstallOutcome Outcome, string Message, LoadedPlugin? Plugin = null);

/// <summary>
/// Discovers, loads and supervises plugins. Layout: <c>&lt;data&gt;/plugins/&lt;id&gt;/</c> with a
/// plugin.json naming the entry DLL (a folder without one loads as a legacy 1.0 plugin: the
/// DLL named like the folder, else the first DLL). Each plugin gets its own collectible
/// <see cref="AssemblyLoadContext"/>, a data folder at <c>&lt;data&gt;/plugin-data/&lt;id&gt;/</c>,
/// and a host adapter over the player. Every call into a plugin is guarded: a throwing or
/// UI-blocking callback marks the plugin Failed and stops it, never propagating.
/// Enabled state, approvals and setting values are kept by id in <see cref="AppSettings"/>.
/// </summary>
public sealed class PluginHost
{
    private readonly PlayerViewModel? _player;
    private readonly ILibraryService? _library;
    private readonly Func<AppSettings> _settings;
    private readonly Action _saveSettings;
    private readonly string _appVersion;

    /// <param name="player">The live player; null in tests (plugins then see no track and no events).</param>
    /// <param name="library">For "library.read"; null → an empty library.</param>
    public PluginHost(PlayerViewModel? player, string dataDirectory, Func<AppSettings> settings, Action saveSettings, string appVersion,
        ILibraryService? library = null)
    {
        _player = player;
        _library = library;
        _settings = settings;
        _saveSettings = saveSettings;
        _appVersion = appVersion;
        PluginsDirectory = Path.Combine(dataDirectory, "plugins");
        PluginDataRoot = Path.Combine(dataDirectory, "plugin-data");
    }

    /// <summary>Where plugin folders live.</summary>
    public string PluginsDirectory { get; }

    /// <summary>Per-plugin data folders, outside the plugin folders so updates keep them.</summary>
    public string PluginDataRoot { get; }

    /// <summary>Every discovered plugin, loaded or not, in folder-name order.</summary>
    public ObservableCollection<LoadedPlugin> Plugins { get; } = new();

    /// <summary>Themes, lyrics presets and languages of the switched-on content packs.</summary>
    public ContentCatalog Content { get; } = new();

    /// <summary>False = restricted mode: plugins are listed but none is loaded.</summary>
    public bool CommunityPluginsEnabled => _settings().CommunityPluginsEnabled == true;

    /// <summary>A synchronous plugin callback running longer than this froze the UI; the plugin is stopped.</summary>
    public TimeSpan CallbackBudget { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>How long the lyrics search waits for a plugin provider.</summary>
    public TimeSpan LyricsTimeout { get; set; } = TimeSpan.FromSeconds(8);
    /// <summary>How long an async menu command may run before it is logged as stuck.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Asked before a plugin is enabled for the first time (or after an update adds
    /// permissions). True = the user approved. Null = approve silently (tests).
    /// </summary>
    public Func<LoadedPlugin, Task<bool>>? ConfirmEnable { get; set; }

    /// <summary>Visual layers of every running plugin, in load order.</summary>
    public IReadOnlyList<IVisualLayerProvider> VisualLayers
        => Plugins.Where(p => p.Instance is not null).SelectMany(p => p.VisualLayers).ToList();

    /// <summary>Lyrics providers of every running plugin, in load order.</summary>
    public IReadOnlyList<PluginLyricsSource> LyricsProviders
        => Plugins.Where(p => p.Adapter is not null && p.Instance is not null).SelectMany(p => p.Adapter!.LyricsSources).ToList();

    /// <summary>Track-menu commands of every running plugin, in load order.</summary>
    public IReadOnlyList<PluginTrackCommand> TrackCommands
        => Plugins.Where(p => p.Adapter is not null && p.Instance is not null).SelectMany(p => p.Adapter!.TrackCommands).ToList();

    /// <summary>True when some running plugin listens for scrobbles (so the player computes them even without Last.fm).</summary>
    public bool HasScrobbleListeners => Plugins.Any(p => p.Instance is not null && p.Adapter?.HasScrobbleHandlers == true);

    /// <summary>Raised on the UI thread when the set of visual layers changes (load/unload/reload).</summary>
    public event EventHandler? VisualLayersChanged;

    /// <summary>Raised when the community-plugins switch changes.</summary>
    public event EventHandler? CommunityPluginsChanged;

    /// <summary>A plugin asked to show a message ("notifications").</summary>
    public event EventHandler<PluginNotice>? NotificationRequested;

    // ── Discovery and lifecycle ──

    /// <summary>Scans the plugins folder and loads everything enabled. Safe to call again: unloads first.</summary>
    public void LoadAll()
    {
        UnloadAll();
        try { Directory.CreateDirectory(PluginsDirectory); }
        catch (Exception ex) { DebugLogger.Error(DebugLogger.Category.State, "Plugins", $"create folder: {ex.Message}"); return; }

        var found = new List<LoadedPlugin>();
        foreach (var dir in Directory.EnumerateDirectories(PluginsDirectory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(dir).StartsWith('.')) continue; // .staging-* from an interrupted install
            var plugin = Discover(dir);
            if (plugin is not null) found.Add(plugin);
        }

        var dirty = MigrateSettings(found);
        if (dirty) _saveSettings();

        foreach (var plugin in found)
        {
            Plugins.Add(plugin);
            Activate(plugin);
        }
        RebuildContent();
        VisualLayersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reads just the enabled content packs into <see cref="Content"/>, before <see cref="LoadAll"/>
    /// runs (that one waits for the first frame). Settings calls it while loading so a pack theme
    /// or language chosen last session applies at startup instead of flashing the default first.
    /// No-op once plugins are loaded. Content packs are plain JSON, so this is cheap.
    /// </summary>
    public void PreloadContent()
    {
        if (Plugins.Count > 0 || !Directory.Exists(PluginsDirectory)) return;
        var packs = new List<ContentPackData>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(PluginsDirectory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (Path.GetFileName(dir).StartsWith('.')) continue;
                try
                {
                    if (PluginManifest.TryLoad(dir) is not { IsContent: true } manifest || !seen.Add(manifest.Id)) continue;
                    if (IsDisabledId(manifest.Id) || manifest.CheckCompatibility(_appVersion) is not null) continue;
                    packs.Add(ContentPackLoader.Load(dir, manifest));
                }
                catch (Exception ex) when (ex is PluginManifestException or IOException or UnauthorizedAccessException)
                {
                    // LoadAll reports it on the pack's card.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLogger.Error(DebugLogger.Category.State, "Plugins", $"preload content: {ex.Message}");
        }
        Content.Update(packs);
    }

    /// <summary>Publishes the switched-on packs' content (a no-op when nothing changed).</summary>
    private void RebuildContent()
        => Content.Update(Plugins.Where(p => p.IsContentPack && p.Status == PluginStatus.Active && p.Content is not null)
                                 .Select(p => p.Content!).ToList());

    /// <summary>Loads one plugin folder (tests; installs). Honors restricted mode, approvals and the disabled list.</summary>
    public LoadedPlugin LoadFrom(string directory)
    {
        var plugin = Discover(directory) ?? throw new FileNotFoundException("No plugin in " + directory);
        if (MigrateSettings(new[] { plugin })) _saveSettings();
        InsertSorted(plugin);
        Activate(plugin);
        RebuildContent();
        VisualLayersChanged?.Invoke(this, EventArgs.Empty);
        return plugin;
    }

    /// <summary>Tests: registers a plugin whose instance comes from <paramref name="factory"/> instead of a DLL.</summary>
    internal LoadedPlugin AddInProcess(string directory, PluginManifest? manifest, Func<INoctisPlugin> factory)
    {
        var plugin = new LoadedPlugin(directory, assemblyPath: null, manifest) { InProcessFactory = factory };
        Prepare(plugin);
        if (MigrateSettings(new[] { plugin })) _saveSettings();
        Plugins.Add(plugin);
        Activate(plugin);
        VisualLayersChanged?.Invoke(this, EventArgs.Empty);
        return plugin;
    }

    /// <summary>Turns restricted mode off (loads enabled plugins) or on (stops every plugin).</summary>
    public void SetCommunityPluginsEnabled(bool enabled)
    {
        var settings = _settings();
        if (settings.CommunityPluginsEnabled == enabled) return;
        settings.CommunityPluginsEnabled = enabled;
        _saveSettings();
        LoadAll();
        CommunityPluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Enables or disables a plugin, persists the choice, and starts/stops it live.
    /// Enabling also records approval of the permissions it declares.</summary>
    public void SetEnabled(LoadedPlugin plugin, bool enabled)
    {
        var settings = _settings();
        settings.DisabledPlugins ??= new List<string>();
        settings.DisabledPlugins.RemoveAll(d => SameId(d, plugin.Id) || SameId(d, plugin.FolderName));
        if (!enabled) settings.DisabledPlugins.Add(plugin.Id);

        if (plugin.IsContentPack)
        {
            // Data only: nothing to approve, start or stop. Switching it publishes or withdraws
            // its content; Settings falls back from a theme or language that went away.
            _saveSettings();
            var on = enabled && plugin.CanToggle;
            if (plugin.CanToggle) plugin.Status = on ? PluginStatus.Active : PluginStatus.Disabled;
            SetIsEnabledQuietly(plugin, on);
            RebuildContent();
            return;
        }

        if (enabled) Grant(settings, plugin);
        _saveSettings();

        // Start/stop BEFORE writing IsEnabled: the flag's change handler compares it with the
        // running state, so writing it first would re-enter SetEnabled once more.
        if (enabled && plugin.Instance is null)
        {
            if (CanRun(plugin)) Start(plugin);
            else if (!CommunityPluginsEnabled) plugin.Status = PluginStatus.Restricted;
        }
        else if (!enabled && (plugin.Instance is not null || plugin.Status == PluginStatus.Failed))
        {
            Stop(plugin);
            plugin.Status = PluginStatus.Disabled;
            plugin.Error = "";
        }
        else if (!enabled && plugin.Status is PluginStatus.NeedsApproval) plugin.Status = PluginStatus.Disabled;
        SetIsEnabledQuietly(plugin, enabled);
        VisualLayersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shuts every plugin down and forgets them (app exit, or before a rescan).</summary>
    public void UnloadAll()
    {
        foreach (var p in Plugins) Stop(p);
        Plugins.Clear();
    }

    /// <summary>True when the permissions the plugin declares were approved before.</summary>
    public bool IsApproved(LoadedPlugin plugin)
    {
        var grants = _settings().PluginPermissionGrants;
        if (grants is null) return false;
        var granted = grants.FirstOrDefault(kv => SameId(kv.Key, plugin.Id)).Value;
        return granted is not null && plugin.Permissions.All(p => granted.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    private LoadedPlugin? Discover(string dir)
    {
        PluginManifest? manifest = null;
        string? error = null;
        try { manifest = PluginManifest.TryLoad(dir); }
        catch (PluginManifestException ex) { error = ex.Message; }

        string? assembly;
        if (error is not null) assembly = null;
        else if (manifest is null)
        {
            assembly = PickMainAssembly(dir);
            if (assembly is null) return null; // not a plugin folder
        }
        else assembly = manifest.IsDotnet ? ResolveEntry(dir, manifest.Entry) : null;

        // A content pack is read (JSON only) and validated here; a DLL in its folder is a
        // reason to refuse it, never something to load.
        ContentPackData? content = null;
        if (error is null && manifest is { IsContent: true })
        {
            try { content = ContentPackLoader.Load(dir, manifest); }
            catch (PluginManifestException ex) { error = ex.Message; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = "The pack could not be read: " + ex.Message; }
        }

        var plugin = new LoadedPlugin(dir, assembly, manifest, error) { Content = content };
        Prepare(plugin);
        return plugin;
    }

    /// <summary>Data folder, its migration, and the settings rows.</summary>
    private void Prepare(LoadedPlugin plugin)
    {
        plugin.DataDirectory = Path.Combine(PluginDataRoot, SafeFolderName(plugin.Id));
        // 1.0 kept data in <plugin>/data; move it out so updates keep it. Only for plugins built
        // against 1.0: a 1.1 plugin may ship its own "data" folder of assets.
        var builtFor10 = plugin.IsLegacy
            || (plugin.Manifest is { } m && PluginManifest.TryParseApiVersion(m.ApiVersion, out var maj, out var min) && maj == 1 && min == 0);
        if (builtFor10 && plugin.ManifestError is null)
            MigrateDataFolder(Path.Combine(plugin.Directory, "data"), plugin.DataDirectory);

        if (plugin.Manifest is { Settings.Count: > 0 } manifest)
        {
            var stored = StoredValues(plugin.Id, create: false);
            foreach (var def in manifest.Settings)
            {
                var value = stored is not null && stored.TryGetValue(def.Key, out var v) ? v : def.Default;
                plugin.SettingItems.Add(new PluginSettingItem(def, value, item => OnSettingChanged(plugin, item)));
            }
        }
    }

    /// <summary>
    /// One-time and idempotent settings upkeep: decides the community-plugins switch for
    /// installs that predate it, and rewrites disabled entries keyed by folder name to ids.
    /// </summary>
    private bool MigrateSettings(IReadOnlyCollection<LoadedPlugin> found)
    {
        var settings = _settings();
        settings.DisabledPlugins ??= new List<string>();
        settings.PluginPermissionGrants ??= new Dictionary<string, List<string>>();
        settings.PluginSettingValues ??= new Dictionary<string, Dictionary<string, string>>();
        var dirty = false;

        foreach (var p in found.Where(p => !SameId(p.FolderName, p.Id)))
        {
            var idx = settings.DisabledPlugins.FindIndex(d => SameId(d, p.FolderName));
            if (idx < 0) continue;
            if (settings.DisabledPlugins.Any(d => SameId(d, p.Id))) settings.DisabledPlugins.RemoveAt(idx);
            else settings.DisabledPlugins[idx] = p.Id;
            dirty = true;
        }

        if (settings.CommunityPluginsEnabled is null)
        {
            // Plugins already installed ran before this switch existed: keep them running and
            // treat what they declare today as approved. Everyone else starts restricted.
            var hadPlugins = found.Any(p => p.AssemblyPath is not null || p.InProcessFactory is not null);
            settings.CommunityPluginsEnabled = hadPlugins;
            if (hadPlugins)
                foreach (var p in found.Where(p => !IsDisabled(p)))
                    Grant(settings, p);
            dirty = true;
            DebugLogger.Info(DebugLogger.Category.State, "Plugins", $"community plugins default {(hadPlugins ? "ON (plugins were installed)" : "OFF")}");
        }
        return dirty;
    }

    /// <summary>Applies the state a freshly discovered plugin should be in, starting it when allowed.</summary>
    private void Activate(LoadedPlugin plugin)
    {
        var duplicate = Plugins.FirstOrDefault(p => !ReferenceEquals(p, plugin) && SameId(p.Id, plugin.Id));
        // Content packs need no approval: they are on unless the user switched them off.
        var wanted = !IsDisabled(plugin) && (plugin.IsContentPack || IsApproved(plugin));
        plugin.IsEnabled = wanted && duplicate is null;
        plugin.EnabledChangedByUser = OnEnabledToggled;
        if (plugin.Content is { } content) plugin.Extensions = content.Summary;

        string? blocker = null;
        if (plugin.ManifestError is not null) { plugin.Status = PluginStatus.Invalid; blocker = plugin.ManifestError; }
        else if (duplicate is not null) { plugin.Status = PluginStatus.Invalid; blocker = $"Another plugin already uses the id \"{plugin.Id}\" ({duplicate.FolderName})."; }
        else if (plugin.Manifest?.CheckCompatibility(_appVersion) is { } why) { plugin.Status = PluginStatus.Incompatible; blocker = why; }
        else if (plugin.IsContentPack)
        {
            // Restricted mode is about code; a content pack toggles freely and is never started.
            plugin.CanToggle = true;
            plugin.Status = plugin.IsEnabled ? PluginStatus.Active : PluginStatus.Disabled;
            return;
        }
        else if (plugin.AssemblyPath is null && plugin.InProcessFactory is null)
        {
            plugin.Status = PluginStatus.Invalid;
            blocker = $"\"{plugin.Manifest?.Entry}\" is not in the plugin folder.";
        }

        if (blocker is not null)
        {
            plugin.Error = blocker;
            plugin.CanToggle = false;
            SetIsEnabledQuietly(plugin, false);
            return;
        }

        plugin.CanToggle = CommunityPluginsEnabled;
        if (!CommunityPluginsEnabled) plugin.Status = PluginStatus.Restricted;
        else if (plugin.IsEnabled) Start(plugin);
        else if (!IsDisabled(plugin) && HasGrantEntry(plugin)) plugin.Status = PluginStatus.NeedsApproval;
        else plugin.Status = PluginStatus.Disabled;
    }

    private bool CanRun(LoadedPlugin plugin)
        => CommunityPluginsEnabled && plugin.CanToggle && (plugin.AssemblyPath is not null || plugin.InProcessFactory is not null);

    // The Settings toggle binds IsEnabled two-way; SetEnabled also writes IsEnabled, so only
    // act when the flag disagrees with the actual running state (re-entrancy guard).
    private void OnEnabledToggled(LoadedPlugin plugin, bool enabled)
    {
        if (!plugin.CanToggle)
        {
            // Restricted/incompatible: the switch is disabled in the UI; undo a programmatic flip.
            if (enabled) Dispatcher.UIThread.Post(() => SetIsEnabledQuietly(plugin, false));
            return;
        }
        if (plugin.IsContentPack)
        {
            if (enabled != (plugin.Status == PluginStatus.Active)) SetEnabled(plugin, enabled);
            return;
        }
        var running = plugin.Instance is not null || plugin.Status == PluginStatus.Failed;
        if (enabled == running) return;
        if (enabled && !IsApproved(plugin) && ConfirmEnable is { } confirm)
        {
            if (plugin.PendingApproval) return;
            plugin.PendingApproval = true;
            _ = ConfirmThenEnableAsync(plugin, confirm);
            return;
        }
        SetEnabled(plugin, enabled);
    }

    private async Task ConfirmThenEnableAsync(LoadedPlugin plugin, Func<LoadedPlugin, Task<bool>> confirm)
    {
        bool approved;
        try { approved = await confirm(plugin); }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.State, "Plugins", $"approval dialog: {ex.Message}");
            approved = false;
        }
        finally { plugin.PendingApproval = false; }

        if (approved && Plugins.Contains(plugin)) SetEnabled(plugin, true);
        else SetIsEnabledQuietly(plugin, false);
    }

    private static void SetIsEnabledQuietly(LoadedPlugin plugin, bool value)
    {
        var handler = plugin.EnabledChangedByUser;
        plugin.EnabledChangedByUser = null;
        try { plugin.IsEnabled = value; }
        finally { plugin.EnabledChangedByUser = handler; }
    }

    private static string? PickMainAssembly(string dir)
    {
        // Legacy (no plugin.json): prefer a DLL named like the folder; else the first DLL that isn't the SDK.
        var dlls = Directory.GetFiles(dir, "*.dll");
        var folder = Path.GetFileName(dir);
        return dlls.FirstOrDefault(d => Path.GetFileNameWithoutExtension(d).Equals(folder, StringComparison.OrdinalIgnoreCase))
            ?? dlls.FirstOrDefault(d => !Path.GetFileName(d).StartsWith("Noctis.Plugins.Abstractions", StringComparison.OrdinalIgnoreCase)
                                     && !Path.GetFileName(d).StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResolveEntry(string dir, string entry)
    {
        var path = Path.Combine(dir, entry);
        return File.Exists(path) ? path : null;
    }

    private void Start(LoadedPlugin plugin)
    {
        plugin.Error = "";
        HostAdapter? adapter = null;
        try
        {
            INoctisPlugin instance;
            if (plugin.InProcessFactory is { } factory) instance = factory();
            else
            {
                plugin.Context = new PluginLoadContext(plugin.AssemblyPath!);
                var assembly = plugin.Context.LoadMain();
                instance = (INoctisPlugin)Activator.CreateInstance(PickPluginType(assembly, plugin))!;
            }

            var info = instance.Info ?? throw new InvalidOperationException("Plugin.Info returned null.");
            if (plugin.Manifest is null)
            {
                // Legacy: the code is the only description there is.
                plugin.Name = string.IsNullOrWhiteSpace(info.Name) ? plugin.FolderName : info.Name;
                plugin.Version = info.Version ?? "";
                plugin.Author = info.Author ?? "";
                plugin.Description = info.Description ?? "";
            }
            else if (!string.Equals(info.Id, plugin.Id, StringComparison.OrdinalIgnoreCase))
                DebugLogger.Warn(DebugLogger.Category.State, "Plugins", $"{plugin.Id}: Info.Id \"{info.Id}\" differs from plugin.json; plugin.json wins");

            try { Directory.CreateDirectory(plugin.DataDirectory); } catch { /* the plugin reports its own IO errors */ }
            adapter = new HostAdapter(this, plugin, _player, _library, _appVersion);
            plugin.Adapter = adapter;
            var sw = Stopwatch.StartNew();
            instance.Initialize(adapter);
            if (sw.Elapsed > CallbackBudget * 3)
                DebugLogger.Warn(DebugLogger.Category.State, "Plugins", $"{plugin.Name} Initialize took {sw.ElapsedMilliseconds} ms on the UI thread");
            plugin.Instance = instance;
            UpdateExtensions(plugin);
            plugin.Status = PluginStatus.Running;
            DebugLogger.Info(DebugLogger.Category.State, "Plugins", $"loaded {plugin.Name} {plugin.Version} ({plugin.Id}) from {plugin.FolderName}");
        }
        catch (Exception ex)
        {
            var message = ex is ReflectionTypeLoadException rtl
                ? string.Join("; ", rtl.LoaderExceptions.Select(e => e?.Message).Where(m => m is not null))
                : ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;
            adapter?.Dispose();
            plugin.Adapter = null;
            plugin.Instance = null;
            plugin.VisualLayers.Clear();
            plugin.Extensions = "";
            plugin.Status = PluginStatus.Failed;
            plugin.Error = message;
            DebugLogger.Error(DebugLogger.Category.State, "Plugins", $"{plugin.Id} failed: {message}");
            TryUnloadContext(plugin);
        }
    }

    private static Type PickPluginType(Assembly assembly, LoadedPlugin plugin)
    {
        // One plugin per folder. plugin.json's entryType picks explicitly; otherwise a type named
        // like the folder (so a test can install the same DLL twice), else the first by name.
        var candidates = assembly.GetTypes()
            .Where(t => typeof(INoctisPlugin).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
        if (plugin.Manifest?.EntryType is { } wanted)
            return candidates.FirstOrDefault(t => t.FullName == wanted)
                ?? throw new InvalidOperationException($"entryType \"{wanted}\" is not a public INoctisPlugin with a parameterless constructor in {plugin.Manifest.Entry}.");
        return candidates.FirstOrDefault(t => t.Name.Equals(plugin.FolderName, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault()
            ?? throw new InvalidOperationException("No public INoctisPlugin with a parameterless constructor.");
    }

    private void Stop(LoadedPlugin plugin)
    {
        if (plugin.Instance is not null)
        {
            try { plugin.Instance.Shutdown(); }
            catch (Exception ex) { DebugLogger.Error(DebugLogger.Category.State, "Plugins", $"{plugin.Id} shutdown: {ex.Message}"); }
        }
        // After Shutdown: whatever the plugin forgot to unhook, the adapter drops (player
        // subscription, event handlers, providers, commands, scrobble handlers).
        plugin.Adapter?.Dispose();
        plugin.Adapter = null;
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

    /// <summary>Stops a running plugin that misbehaved and shows why.</summary>
    internal void Fault(LoadedPlugin plugin, string message)
    {
        if (plugin.Instance is null) return;
        DebugLogger.Error(DebugLogger.Category.State, "Plugins", $"{plugin.Id} stopped: {message}");
        Stop(plugin);
        plugin.Status = PluginStatus.Failed;
        plugin.Error = message;
        VisualLayersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Runs a synchronous plugin callback: exceptions and callbacks that held the UI thread
    /// past <see cref="CallbackBudget"/> stop the plugin. False when the call failed.
    /// </summary>
    internal bool Guard(LoadedPlugin plugin, string what, Action call)
    {
        if (plugin.Instance is null) return false;
        var sw = Stopwatch.StartNew();
        try { call(); }
        catch (Exception ex)
        {
            Fault(plugin, $"{what}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        if (sw.Elapsed > CallbackBudget)
        {
            Fault(plugin, $"{what} blocked Noctis for {sw.ElapsedMilliseconds} ms.");
            return false;
        }
        return true;
    }

    private void UpdateExtensions(LoadedPlugin plugin)
    {
        var parts = new List<string>();
        parts.AddRange(plugin.VisualLayers.Select(v => v.Name));
        if (plugin.Adapter is { } a)
        {
            parts.AddRange(a.LyricsSources.Select(l => "Lyrics: " + l.Name));
            parts.AddRange(a.TrackCommands.Select(c => "Menu: " + c.Label));
        }
        plugin.Extensions = string.Join(" · ", parts);
    }

    // ── Hooks the app calls ──

    /// <summary>Tells running plugins a track counted as listened.</summary>
    public void RaiseTrackScrobbled(Track track, DateTime startedAtUtc)
    {
        if (!HasScrobbleListeners) return;
        var info = ToTrackInfo(track);
        var at = new DateTimeOffset(DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc));
        foreach (var plugin in Plugins.ToList())
            plugin.Adapter?.RaiseScrobbled(info, at);
    }

    internal void RunTrackCommand(PluginTrackCommand command, Track track)
    {
        var info = ToTrackInfo(track);
        Task? pending = null;
        if (!Guard(command.Plugin, $"menu command '{command.Label}'", () => pending = command.Handler(info)) || pending is null) return;
        _ = ObserveCommandAsync(command, pending);
    }

    private async Task ObserveCommandAsync(PluginTrackCommand command, Task pending)
    {
        try { await pending.WaitAsync(CommandTimeout); }
        catch (TimeoutException)
        {
            DebugLogger.Warn(DebugLogger.Category.State, "Plugins", $"{command.Plugin.Id}: menu command '{command.Label}' still running after {CommandTimeout.TotalSeconds:0} s");
        }
        catch (Exception ex)
        {
            void Report() => Fault(command.Plugin, $"menu command '{command.Label}': {ex.GetType().Name}: {ex.Message}");
            if (Dispatcher.UIThread.CheckAccess()) Report();
            else Dispatcher.UIThread.Post(Report);
        }
    }

    internal void RaiseNotice(LoadedPlugin plugin, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        // One message per second per plugin: a chatty plugin cannot flood the notice pill.
        var now = DateTime.UtcNow;
        if (now - plugin.LastNotice < TimeSpan.FromSeconds(1)) return;
        plugin.LastNotice = now;
        var text = message.Length > 200 ? message[..200] + "…" : message;
        var notice = new PluginNotice(plugin.Name, text);
        void Raise() => NotificationRequested?.Invoke(this, notice);
        if (Dispatcher.UIThread.CheckAccess()) Raise();
        else Dispatcher.UIThread.Post(Raise);
    }

    /// <summary>A plain copy of a library track for plugins.</summary>
    public static TrackInfo ToTrackInfo(Track t) => new(
        t.Id.ToString("D"), t.Title ?? "", t.Artist ?? "", t.Album ?? "", t.AlbumArtist ?? "",
        t.Duration, t.Year, t.Genre ?? "", t.TrackNumber, t.FilePath ?? "", t.IsFavorite, t.PlayCount, t.Rating);

    // ── Settings declared in plugin.json ──

    private Dictionary<string, string>? StoredValues(string id, bool create)
    {
        var all = _settings().PluginSettingValues ??= new Dictionary<string, Dictionary<string, string>>();
        var key = all.Keys.FirstOrDefault(k => SameId(k, id));
        if (key is not null) return all[key];
        if (!create) return null;
        var fresh = new Dictionary<string, string>();
        all[id] = fresh;
        return fresh;
    }

    private void OnSettingChanged(LoadedPlugin plugin, PluginSettingItem item)
    {
        StoredValues(plugin.Id, create: true)![item.Key] = item.Value;
        _saveSettings();
        plugin.Adapter?.RaiseSettingChanged(item.Key);
    }

    // ── Install / remove ──

    /// <summary>Reads a package without installing it; the plugin with the same id, if any.</summary>
    /// <exception cref="PluginInstallException">Not a usable package.</exception>
    public (PluginPackage Package, LoadedPlugin? Existing) InspectPackage(string zipPath)
    {
        var package = PluginInstaller.Inspect(zipPath);
        return (package, FindById(package.Manifest.Id));
    }

    public LoadedPlugin? FindById(string id) => Plugins.FirstOrDefault(p => SameId(p.Id, id));

    /// <summary>
    /// Installs a plugin .zip into <c>plugins/&lt;id&gt;/</c>. With the id already installed it
    /// refuses, unless <paramref name="allowUpdate"/> and the package's version is newer: then the
    /// old folder is replaced (data and settings are kept, living outside it). The new plugin
    /// starts disabled until the user approves its permissions, unless an update asks for no
    /// new ones and it was enabled.
    /// </summary>
    public PluginInstallResult InstallPackage(string zipPath, bool allowUpdate)
    {
        // Settle the community-plugins default from what was installed BEFORE this package,
        // so installing into a fresh setup does not count as "plugins were already there".
        if (_settings().CommunityPluginsEnabled is null && MigrateSettings(Plugins.ToList())) _saveSettings();

        PluginPackage package;
        LoadedPlugin? existing;
        try { (package, existing) = InspectPackage(zipPath); }
        catch (PluginInstallException ex) { return new PluginInstallResult(PluginInstallOutcome.Failed, ex.Message); }

        var manifest = package.Manifest;
        var target = Path.Combine(PluginsDirectory, SafeFolderName(manifest.Id));
        if (existing is not null)
        {
            if (PluginVersion.Compare(manifest.Version, existing.Version) <= 0)
                return new PluginInstallResult(
                    PluginVersion.Compare(manifest.Version, existing.Version) == 0 ? PluginInstallOutcome.AlreadyInstalled : PluginInstallOutcome.NotNewer,
                    $"{existing.Name} {existing.Version} is installed; the file has {manifest.Version}.", existing);
            if (!allowUpdate)
                return new PluginInstallResult(PluginInstallOutcome.AlreadyInstalled, $"{existing.Name} {existing.Version} is already installed.", existing);
            target = existing.Directory;
        }
        else if (Directory.Exists(target))
            return new PluginInstallResult(PluginInstallOutcome.Failed, $"A folder named \"{Path.GetFileName(target)}\" already exists in the plugins folder.");

        Directory.CreateDirectory(PluginsDirectory);
        var staging = Path.Combine(PluginsDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
        try { PluginInstaller.Extract(package, staging); }
        catch (Exception ex) when (ex is PluginInstallException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new PluginInstallResult(PluginInstallOutcome.Failed, ex.Message);
        }

        if (existing is not null)
        {
            Stop(existing);
            Plugins.Remove(existing);
            if (!PluginInstaller.TryDeleteDirectory(existing.Directory))
            {
                PluginInstaller.TryDeleteDirectory(staging);
                LoadFrom(existing.Directory); // put the old one back
                return new PluginInstallResult(PluginInstallOutcome.Failed, $"Could not replace {existing.Directory}. Close Noctis and delete it by hand, then install again.");
            }
        }

        try { Directory.Move(staging, target); }
        catch (Exception ex)
        {
            PluginInstaller.TryDeleteDirectory(staging);
            return new PluginInstallResult(PluginInstallOutcome.Failed, "Could not move the plugin into place: " + ex.Message);
        }

        var plugin = LoadFrom(target);
        DebugLogger.Info(DebugLogger.Category.State, "Plugins", $"{(existing is null ? "installed" : "updated")} {manifest.Id} {manifest.Version}");
        return existing is null
            ? new PluginInstallResult(PluginInstallOutcome.Installed, $"Installed {plugin.Name} {plugin.Version}.", plugin)
            : new PluginInstallResult(PluginInstallOutcome.Updated, $"Updated {plugin.Name} to {plugin.Version}.", plugin);
    }

    /// <summary>
    /// Stops the plugin and deletes its folder. Its data folder and setting values stay unless
    /// <paramref name="deleteData"/>; its approval is always dropped (a reinstall asks again).
    /// </summary>
    public bool Remove(LoadedPlugin plugin, bool deleteData)
    {
        Stop(plugin);
        Plugins.Remove(plugin);
        var settings = _settings();
        settings.DisabledPlugins?.RemoveAll(d => SameId(d, plugin.Id) || SameId(d, plugin.FolderName));
        foreach (var key in (settings.PluginPermissionGrants ?? new()).Keys.Where(k => SameId(k, plugin.Id)).ToList())
            settings.PluginPermissionGrants!.Remove(key);
        if (deleteData)
        {
            foreach (var key in (settings.PluginSettingValues ?? new()).Keys.Where(k => SameId(k, plugin.Id)).ToList())
                settings.PluginSettingValues!.Remove(key);
            PluginInstaller.TryDeleteDirectory(plugin.DataDirectory);
        }
        _saveSettings();
        var removed = PluginInstaller.TryDeleteDirectory(plugin.Directory);
        RebuildContent();
        VisualLayersChanged?.Invoke(this, EventArgs.Empty);
        DebugLogger.Info(DebugLogger.Category.State, "Plugins", $"removed {plugin.Id} (data {(deleteData ? "deleted" : "kept")}, folder {(removed ? "deleted" : "left behind")})");
        return removed;
    }

    private void InsertSorted(LoadedPlugin plugin)
    {
        var index = 0;
        while (index < Plugins.Count && string.Compare(Plugins[index].FolderName, plugin.FolderName, StringComparison.OrdinalIgnoreCase) < 0) index++;
        Plugins.Insert(index, plugin);
    }

    // ── Settings helpers ──

    private bool IsDisabled(LoadedPlugin plugin) => IsDisabledId(plugin.Id);

    private bool IsDisabledId(string id)
        => (_settings().DisabledPlugins ?? new List<string>()).Any(d => SameId(d, id));

    private bool HasGrantEntry(LoadedPlugin plugin)
        => (_settings().PluginPermissionGrants ?? new()).Keys.Any(k => SameId(k, plugin.Id));

    private static void Grant(AppSettings settings, LoadedPlugin plugin)
    {
        settings.PluginPermissionGrants ??= new Dictionary<string, List<string>>();
        foreach (var key in settings.PluginPermissionGrants.Keys.Where(k => SameId(k, plugin.Id)).ToList())
            settings.PluginPermissionGrants.Remove(key);
        settings.PluginPermissionGrants[plugin.Id] = plugin.Permissions.ToList();
    }

    private static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string SafeFolderName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(id.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
        return clean.Length == 0 ? "_" : clean;
    }

    /// <summary>Moves a 1.0-era data folder to its new home when that is empty; never merges.</summary>
    internal static void MigrateDataFolder(string oldDir, string newDir)
    {
        try
        {
            if (!Directory.Exists(oldDir)) return;
            if (Directory.Exists(newDir))
            {
                if (Directory.EnumerateFileSystemEntries(newDir).Any())
                {
                    DebugLogger.Warn(DebugLogger.Category.State, "Plugins", $"data in both {oldDir} and {newDir}; kept both, using the new one");
                    return;
                }
                Directory.Delete(newDir);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(newDir)!);
            try { Directory.Move(oldDir, newDir); }
            catch (IOException)
            {
                // Different volume: copy, then remove the old tree.
                CopyDirectory(oldDir, newDir);
                Directory.Delete(oldDir, recursive: true);
            }
            DebugLogger.Info(DebugLogger.Category.State, "Plugins", $"moved plugin data {oldDir} -> {newDir}");
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.State, "Plugins", $"data migration {oldDir}: {ex.Message}");
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    /// <summary>What a plugin sees as its host: thin adapters over the player and the audio taps.
    /// Disposed when the plugin stops; after that every hook is inert.</summary>
    internal sealed class HostAdapter : IPluginHost, INowPlaying, IBeatSource, ISpectrumSource, IPlaybackControl, ILibraryReader, IPluginSettings, IDisposable
    {
        private readonly PluginHost _owner;
        private readonly LoadedPlugin _plugin;
        private readonly PlayerViewModel? _player;
        private readonly ILibraryService? _library;
        private readonly object _gate = new();
        private readonly List<PluginLyricsSource> _lyrics = new();
        private readonly List<PluginTrackCommand> _commands = new();
        private readonly List<Action<TrackInfo, DateTimeOffset>> _scrobbled = new();
        private bool _disposed;

        public HostAdapter(PluginHost owner, LoadedPlugin plugin, PlayerViewModel? player, ILibraryService? library, string appVersion)
        {
            _owner = owner;
            _plugin = plugin;
            _player = player;
            _library = library;
            AppVersion = appVersion;
            if (_player is not null) _player.PropertyChanged += OnPlayerPropertyChanged;
        }

        /// <summary>Tests: whether the player subscription is still live.</summary>
        internal bool IsSubscribedToPlayer { get; private set; } = true;
        internal bool IsDisposed => _disposed;

        internal IReadOnlyList<PluginLyricsSource> LyricsSources { get { lock (_gate) return _lyrics.ToList(); } }
        internal IReadOnlyList<PluginTrackCommand> TrackCommands { get { lock (_gate) return _commands.ToList(); } }
        internal bool HasScrobbleHandlers { get { lock (_gate) return _scrobbled.Count > 0; } }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_player is not null) _player.PropertyChanged -= OnPlayerPropertyChanged;
            IsSubscribedToPlayer = false;
            TrackChanged = null;
            IsPlayingChanged = null;
            Changed = null;
            lock (_gate)
            {
                _lyrics.Clear();
                _commands.Clear();
                _scrobbled.Clear();
            }
        }

        public string AppVersion { get; }
        public string DataDirectory => _plugin.DataDirectory;
        public string PluginDirectory => _plugin.Directory;
        public INowPlaying NowPlaying => this;
        public IBeatSource Beat => this;
        public ISpectrumSource Spectrum => this;

        public IPlaybackControl Playback { get { Require(PluginPermissions.PlaybackControl); return this; } }
        public ILibraryReader Library { get { Require(PluginPermissions.LibraryRead); return this; } }
        public IPluginSettings Settings => this;

        public void Log(string message)
            => DebugLogger.Info(DebugLogger.Category.State, "Plugin:" + _plugin.Name, message ?? "");

        public void RegisterVisualLayer(IVisualLayerProvider provider)
        {
            if (provider is null || _disposed) return;
            _plugin.VisualLayers.Add(provider);
            _owner.UpdateExtensions(_plugin);
            if (_plugin.Instance is not null) _owner.VisualLayersChanged?.Invoke(_owner, EventArgs.Empty);
        }

        public void Notify(string message)
        {
            Require(PluginPermissions.Notifications);
            if (_disposed) return;
            _owner.RaiseNotice(_plugin, message);
        }

        public IDisposable RegisterLyricsProvider(ILyricsProvider provider)
        {
            Require(PluginPermissions.LyricsProvider);
            ArgumentNullException.ThrowIfNull(provider);
            if (_disposed) return Registration.None;
            string name;
            try { name = string.IsNullOrWhiteSpace(provider.Name) ? _plugin.Name : provider.Name.Trim(); }
            catch { name = _plugin.Name; }
            var source = new PluginLyricsSource(_owner, _plugin, provider, name);
            lock (_gate) _lyrics.Add(source);
            _owner.UpdateExtensions(_plugin);
            return new Registration(() => { lock (_gate) _lyrics.Remove(source); _owner.UpdateExtensions(_plugin); });
        }

        public IDisposable RegisterTrackCommand(string label, string? icon, Action<TrackInfo> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            return AddCommand(label, icon, t => { handler(t); return null; });
        }

        public IDisposable RegisterTrackCommand(string label, string? icon, Func<TrackInfo, Task> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            return AddCommand(label, icon, t => handler(t));
        }

        private IDisposable AddCommand(string label, string? icon, Func<TrackInfo, Task?> handler)
        {
            Require(PluginPermissions.MenuCommands);
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A menu command needs a label.", nameof(label));
            if (_disposed) return Registration.None;
            var command = new PluginTrackCommand(_owner, _plugin, label.Trim(), icon, handler);
            lock (_gate) _commands.Add(command);
            _owner.UpdateExtensions(_plugin);
            return new Registration(() => { lock (_gate) _commands.Remove(command); _owner.UpdateExtensions(_plugin); });
        }

        public IDisposable OnTrackScrobbled(Action<TrackInfo, DateTimeOffset> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (_disposed) return Registration.None;
            lock (_gate) _scrobbled.Add(handler);
            return new Registration(() => { lock (_gate) _scrobbled.Remove(handler); });
        }

        internal void RaiseScrobbled(TrackInfo info, DateTimeOffset startedAt)
        {
            if (_disposed) return;
            Action<TrackInfo, DateTimeOffset>[] handlers;
            lock (_gate) handlers = _scrobbled.ToArray();
            foreach (var h in handlers)
                if (!_owner.Guard(_plugin, "OnTrackScrobbled", () => h(info, startedAt))) return;
        }

        private void Require(string permission)
        {
            if (!_plugin.HasPermission(permission)) throw new PluginPermissionException(permission);
        }

        // INowPlaying
        public NowPlayingTrack? Track => !_disposed && _player?.CurrentTrack is { } t
            ? new NowPlayingTrack(t.Title, t.Artist, t.Album, t.Duration, t.FilePath, _player.CurrentArtPath, Convert.ToInt32((object?)t.Bpm ?? 0))
            : null;
        public bool IsPlaying => !_disposed && (_player?.IsPlaying ?? false);
        public TimeSpan Position => _disposed ? TimeSpan.Zero : _player?.Position ?? TimeSpan.Zero;
        public event EventHandler? TrackChanged;
        public event EventHandler? IsPlayingChanged;

        private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_disposed || _plugin.Instance is null) return;
            if (e.PropertyName == nameof(PlayerViewModel.CurrentTrack))
            {
                if (TrackChanged is { } handler) _owner.Guard(_plugin, "TrackChanged", () => handler(this, EventArgs.Empty));
            }
            else if (e.PropertyName is nameof(PlayerViewModel.IsPlaying) or "State")
            {
                if (IsPlayingChanged is { } handler) _owner.Guard(_plugin, "IsPlayingChanged", () => handler(this, EventArgs.Empty));
            }
        }

        // IBeatSource / ISpectrumSource
        public bool TryRead(out double pulse)
        {
            pulse = 0;
            return !_disposed && BeatMeter.Shared.TryRead(BeatMeter.Shared.NowMs, out pulse);
        }

        public bool TryRead(Span<float> bands) => !_disposed && SpectrumMeter.Shared.TryRead(SpectrumMeter.Shared.NowMs, bands);

        // IPlaybackControl (marshalled to the UI thread; a stopped plugin's calls do nothing)
        public void PlayPause() => OnUi(p => p.PlayPauseCommand.Execute(null));
        public void Play() => OnUi(p => { if (!p.IsPlaying) p.PlayPauseCommand.Execute(null); });
        public void Pause() => OnUi(p => { if (p.IsPlaying) p.PlayPauseCommand.Execute(null); });
        public void Next() => OnUi(p => p.NextCommand.Execute(null));
        public void Previous() => OnUi(p => p.PreviousCommand.Execute(null));
        public void Seek(TimeSpan position) => OnUi(p => p.SeekTo(position));

        private void OnUi(Action<PlayerViewModel> action)
        {
            if (_disposed || _player is not { } player) return;
            void Run()
            {
                if (_disposed) return;
                try { action(player); }
                catch (Exception ex) { DebugLogger.Error(DebugLogger.Category.State, "Plugins", $"{_plugin.Id} playback call: {ex.Message}"); }
            }
            if (Dispatcher.UIThread.CheckAccess()) Run();
            else Dispatcher.UIThread.Post(Run);
        }

        // ILibraryReader
        public int TrackCount => _disposed ? 0 : _library?.Tracks.Count ?? 0;

        public IReadOnlyList<TrackInfo> Search(string query, int limit = 50)
        {
            if (_disposed || _library is null) return Array.Empty<TrackInfo>();
            limit = Math.Clamp(limit, 0, 500);
            if (limit == 0) return Array.Empty<TrackInfo>();
            Track[] snapshot;
            try { snapshot = _library.Tracks.ToArray(); }
            catch (InvalidOperationException) { snapshot = _library.Tracks.ToArray(); } // mutated mid-copy by a scan: once more
            // Normalize per word: the key drops spaces and punctuation, so "the beatles" → ["the", "beatles"].
            var words = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(Noctis.Helpers.SearchText.Normalize)
                .Where(w => w.Length > 0)
                .ToArray();
            var results = new List<TrackInfo>(Math.Min(limit, 64));
            foreach (var t in snapshot)
            {
                if (words.Length > 0 && !words.All(w => t.SearchTitleKey.Contains(w, StringComparison.Ordinal)
                                                      || t.SearchArtistKey.Contains(w, StringComparison.Ordinal)
                                                      || t.SearchAlbumKey.Contains(w, StringComparison.Ordinal)))
                    continue;
                results.Add(ToTrackInfo(t));
                if (results.Count >= limit) break;
            }
            return results;
        }

        // IPluginSettings
        public string? GetString(string key)
        {
            var item = _plugin.SettingItems.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
            return item?.Value;
        }

        public bool GetBool(string key, bool fallback = false)
            => GetString(key) is { } v && bool.TryParse(v, out var b) ? b : fallback;

        public double GetNumber(string key, double fallback = 0)
            => GetString(key) is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

        public event EventHandler<string>? Changed;

        internal void RaiseSettingChanged(string key)
        {
            if (_disposed || Changed is not { } handler) return;
            _owner.Guard(_plugin, "Settings.Changed", () => handler(this, key));
        }
    }

    private sealed class Registration : IDisposable
    {
        public static readonly IDisposable None = new Registration(null);
        private Action? _dispose;
        public Registration(Action? dispose) => _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
