namespace Noctis.Plugins.Mixxx;

/// <summary>
/// Mixxx: copies the BPM and musical key Mixxx analysed into Noctis's library, matching tracks
/// by file path. Mixxx's database is only read. Noctis's values change only through
/// <see cref="ITrackAnalysisWriter"/> (library only, never file tags), and by default only
/// where Noctis has none. Run it from any track's menu: it imports for the whole library.
/// </summary>
public sealed class MixxxPlugin : INoctisPlugin
{
    /// <summary>Track menu label.</summary>
    public const string CommandLabel = "Import BPM and key from Mixxx";

    // A down arrow into a tray, 24×24.
    private const string ImportIcon = "M11 4h2v8.17l3.59-3.58L18 10l-6 6-6-6 1.41-1.41L11 12.17zM5 18h14v2H5z";

    private readonly List<IDisposable> _registrations = new();
    private IPluginHost? _host;
    private int _importing;

    public PluginInfo Info { get; } = new(
        Id: "dev.noctis.plugins.mixxx",
        Name: "Mixxx",
        Version: "1.0.0",
        Author: "Noctis",
        Description: "Imports the BPM and musical key Mixxx analysed for your tracks.");

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _registrations.Add(host.RegisterTrackCommand(CommandLabel, ImportIcon, _ => ImportAsync()));
    }

    public void Shutdown()
    {
        foreach (var r in _registrations) r.Dispose();
        _registrations.Clear();
        _host = null;
    }

    /// <summary>Reads Mixxx's library and fills in BPM/key across Noctis's library. Returns the
    /// notice it showed (empty when the plugin is stopped or an import is already running).</summary>
    public async Task<string> ImportAsync()
    {
        var host = _host;
        if (host is null || Interlocked.Exchange(ref _importing, 1) == 1) return "";
        try
        {
            var configured = host.Settings.GetString("databasePath");
            var overwrite = host.Settings.GetBool("overwrite");
            var library = host.Library;
            var writer = host.TrackAnalysis;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // Off the UI thread: the file may sit on a slow drive, and big libraries take a moment.
            string path = "";
            ImportPlan plan;
            try
            {
                plan = await Task.Run(() =>
                {
                    path = MixxxDatabase.ResolvePath(configured, MixxxDatabase.DefaultPath, home);
                    var rows = MixxxDatabase.Read(path);
                    return MixxxImport.Plan(library.GetAll(), rows, overwrite,
                        windows: OperatingSystem.IsWindows(), ignoreCase: OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
                });
            }
            catch (FileNotFoundException)
            {
                return Tell(host, $"Mixxx library not found at {path}. Set where it is in Settings → Plugins → Mixxx.");
            }
            catch (Exception ex)
            {
                host.Log($"reading {path} failed: {ex}");
                return Tell(host, $"Could not read the Mixxx library: {ex.Message}");
            }

            var changed = plan.Updates.Count == 0 ? 0 : await writer.SetTrackAnalysisAsync(plan.Updates, overwrite);
            host.Log($"{path}: {plan.Matched} matched, {changed} changed, {plan.AlreadySet} already set, {plan.NotInMixxx} not in Mixxx (overwrite {overwrite})");
            return Tell(host, MixxxImport.Summary(changed, plan));
        }
        finally
        {
            Volatile.Write(ref _importing, 0);
        }
    }

    private static string Tell(IPluginHost host, string message)
    {
        host.Notify(message);
        return message;
    }
}
