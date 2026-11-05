using System.Net.Http.Headers;
using Avalonia;
using Avalonia.Controls;

namespace Noctis.Plugins.YouTubeImport;

/// <summary>
/// "Add from YouTube", the official plugin (10-29: the app's Settings → Library entry, the
/// New/Add menu item and the command palette action moved here). Settings → Plugins →
/// Add from YouTube → "Open Add from YouTube" opens its window: search or paste a link, choose
/// the format, download into the library folder. The plugin API has no app-level commands, so
/// the plugin card is the way in.
/// </summary>
public sealed class YouTubeImportPlugin : INoctisPlugin, IPluginViewProvider
{
    public const string Id = "dev.noctis.plugins.youtubeimport";
    public const string DisplayName = "Add from YouTube";
    public const string Version = "1.0.1";
    public const string About = "Save audio from a YouTube link to your library.";
    /// <summary>plugin.json setting: a yt-dlp of the user's own (never replaced).</summary>
    public const string CustomYtDlpSetting = "ytDlpPath";

    private IPluginHost? _host;
    private IDisposable? _view;
    private HttpClient? _http;
    private CancellationTokenSource? _lifetime;
    private YtDlpTool? _tool;
    private YouTubeImporter? _importer;
    private ImportState? _state;

    public PluginInfo Info { get; } = new(Id, DisplayName, Version, "Noctis", About);

    public string Name => DisplayName;

    /// <summary>API 1.5: the view's column is 760 wide at most (plus its margins), so no wider window.</summary>
    public Size? PreferredWindowSize => new Size(840, 680);

    /// <summary>Tests: swaps the importer (and its yt-dlp) the window uses.</summary>
    internal IYouTubeImporter? ImporterOverride { get; set; }

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _lifetime = new CancellationTokenSource();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub's API refuses requests without a User-Agent (the update check).
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Noctis-AddFromYouTube", Version));
        _state = new ImportState(host.DataDirectory, () => LegacyAppSettings.Read(host.DataDirectory, LegacyAppSettings.DownloadFolderKey));
        _tool = new YtDlpTool(_http, ToolsDirectory(host.DataDirectory), CustomYtDlp, host.Log);
        _importer = new YouTubeImporter(_tool, host.LibraryImport, _http, () => _state.DownloadFolder, FfmpegLocator.Find, host.Log);
        _view = host.RegisterView(this);
    }

    /// <summary>
    /// Where yt-dlp is installed: the app's own tools folder (&lt;data&gt;/tools) when the plugin's
    /// data folder sits in the usual &lt;data&gt;/plugin-data/&lt;id&gt;/, so the copy Noctis installed
    /// before this was a plugin (and still uses for lyrics videos) is reused instead of
    /// downloaded twice; otherwise a tools folder inside the plugin's data folder.
    /// </summary>
    internal static string ToolsDirectory(string dataDirectory)
        => LegacyAppSettings.AppDataRoot(dataDirectory) is { } root
            ? Path.Combine(root, "tools")
            : Path.Combine(dataDirectory, "tools");

    /// <summary>The plugin's own setting; empty = the yt-dlp path set in Noctis before this was a
    /// plugin (the app still uses that one for lyrics videos), else the plugin manages yt-dlp.</summary>
    private string CustomYtDlp()
    {
        string own;
        try { own = _host?.Settings.GetString(CustomYtDlpSetting)?.Trim() ?? string.Empty; }
        catch { own = string.Empty; }
        return own.Length > 0 || _host is null ? own : LegacyAppSettings.Read(_host.DataDirectory, LegacyAppSettings.YtDlpPathKey);
    }

    public Control CreateView()
    {
        if (_host is null || _http is null || _state is null || _importer is null || _lifetime is null)
            throw new InvalidOperationException("Add from YouTube is not running.");
        var vm = new YouTubeImportViewModel(ImporterOverride ?? _importer, _http, _state, _lifetime.Token);
        return new YouTubeImportView(vm);
    }

    public void Shutdown()
    {
        _view?.Dispose();
        _view = null;
        // Ends downloads still running (yt-dlp is killed with its process tree).
        try { _lifetime?.Cancel(); } catch { }
        _lifetime?.Dispose();
        _lifetime = null;
        _http?.Dispose();
        _http = null;
        _tool = null;
        _importer = null;
        _state = null;
        _host = null;
    }
}
