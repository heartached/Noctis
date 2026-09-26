using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Noctis.Services.Sync;

namespace Noctis.Server;

/// <summary>
/// noctis-server: the headless host.
///
///   noctis-server [serve] [--data DIR] [--music DIR[;DIR]] [--port N] [--no-tls] [--no-scan] [--no-sync] [--rescan-minutes N]
///   noctis-server scan   [--data DIR] [--music DIR[;DIR]]
///   noctis-server user list|add NAME [--admin]|passwd NAME|apikey NAME|remove NAME
///
/// Environment (used when the option is absent): NOCTIS_DATA_DIR, NOCTIS_MUSIC,
/// NOCTIS_PORT, NOCTIS_TLS=0, NOCTIS_RESCAN_MINUTES, NOCTIS_PASSWORD (non-interactive
/// user add/passwd).
///
/// Everything lives under the data directory exactly as the desktop app lays it out
/// (library.json, library.db, artwork/, playlists.json, server/users.db, sync/), so a
/// data directory copied from a desktop install works as-is, and the other way round.
/// </summary>
public static class Program
{
    private const int DefaultPort = 4747;

    public static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options.Error is not null)
        {
            Console.Error.WriteLine(options.Error);
            Console.Error.WriteLine();
            PrintUsage();
            return 2;
        }
        if (options.Help)
        {
            PrintUsage();
            return 0;
        }

        // AppPaths reads NOCTIS_DATA_DIR once, on first touch: set it before anything does.
        if (options.DataDir is not null)
            Environment.SetEnvironmentVariable("NOCTIS_DATA_DIR", options.DataDir);

        DebugLog.DescribeBuild = () => $"noctis-server {Version}";
        DebugLog.AttachSink(line => Console.WriteLine(line), () => { });

        try
        {
            return options.Command switch
            {
                "serve" => await ServeAsync(options),
                "scan" => await ScanOnceAsync(options),
                "user" => UserCommand(options),
                _ => Unknown(options.Command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"noctis-server: {ex.Message}");
            return 1;
        }
    }

    private static string Version =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "dev";

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"noctis-server {Version}");
        Console.WriteLine("  noctis-server [serve] [--data DIR] [--music DIR[;DIR]] [--port N] [--no-tls] [--no-scan] [--no-sync] [--rescan-minutes N]");
        Console.WriteLine("  noctis-server scan   [--data DIR] [--music DIR[;DIR]]");
        Console.WriteLine("  noctis-server user list");
        Console.WriteLine("  noctis-server user add NAME [--admin]     (password: NOCTIS_PASSWORD or prompt)");
        Console.WriteLine("  noctis-server user passwd NAME");
        Console.WriteLine("  noctis-server user apikey NAME            (prints a new API key)");
        Console.WriteLine("  noctis-server user remove NAME");
        Console.WriteLine("Environment: NOCTIS_DATA_DIR NOCTIS_MUSIC NOCTIS_PORT NOCTIS_TLS=0 NOCTIS_RESCAN_MINUTES NOCTIS_PASSWORD");
    }

    // ── composition ──

    private sealed record Core(
        PersistenceService Persistence,
        AppSettings Settings,
        LibraryService Library,
        LibrarySyncService Sync,
        PlayHistoryService PlayHistory);

    private static async Task<Core> BuildCoreAsync(Options options)
    {
        var persistence = new PersistenceService();
        var settings = await persistence.LoadSettingsAsync();

        if (options.MusicDirs.Count > 0)
        {
            settings.MusicFolders = options.MusicDirs.Select(Path.GetFullPath).ToList();
            await persistence.SaveSettingsAsync(settings);
        }
        // Server-side sync is what makes "newest change wins" work across the
        // signed-in devices; the flag lives in the same settings file the desktop uses.
        settings.SyncEnabled = !options.NoSync;
        if (string.IsNullOrWhiteSpace(settings.SyncDeviceName))
            settings.SyncDeviceName = Environment.MachineName;

        var sqlite = new SqliteLibraryIndexService(persistence);
        var audit = new AuditTrailService(persistence);
        var sync = new LibrarySyncService(() => settings, persistence);
        var library = new LibraryService(new MetadataService(), persistence, sqlite, audit, tagWriter: null, syncRecorder: sync);
        await library.LoadAsync();
        return new Core(persistence, settings, library, sync, new PlayHistoryService());
    }

    // ── serve ──

    private static async Task<int> ServeAsync(Options options)
    {
        var core = await BuildCoreAsync(options);
        var dataDir = core.Persistence.DataDirectory;
        Console.WriteLine($"noctis-server {Version}");
        Console.WriteLine($"  data:   {dataDir}");
        Console.WriteLine($"  music:  {(core.Settings.MusicFolders.Count == 0 ? "(none: pass --music or set NOCTIS_MUSIC)" : string.Join("; ", core.Settings.MusicFolders))}");
        Console.WriteLine($"  tracks: {core.Library.Tracks.Count} albums: {core.Library.Albums.Count} artists: {core.Library.Artists.Count}");

        var serverDir = Path.Combine(dataDir, "server");
        Directory.CreateDirectory(serverDir);
        var users = new ServerUserStore(Path.Combine(serverDir, "users.db"));
        if (users.List().Count == 0)
            Console.WriteLine("  users:  none yet. Create one: noctis-server user add NAME");

        var adapter = new LibraryServerAdapter(core.Library, core.Persistence, core.PlayHistory);
        var server = new NoctisServer(adapter, users, $"noctis-server {Version}", core.Sync);
        server.ClientAuthenticated += (_, user) => Console.WriteLine($"[Server] client signed in: {user}");

        var port = options.Port ?? (core.Settings.NoctisServerPort is >= 1 and <= 65535 ? core.Settings.NoctisServerPort : DefaultPort);
        var cert = options.NoTls ? null : ServerCertificate.LoadOrCreate(serverDir);
        await server.StartAsync(port, cert);

        var scheme = cert is null ? "http" : "https";
        Console.WriteLine($"  listening: {scheme}://{LocalAddress() ?? "0.0.0.0"}:{server.Port}/");
        if (cert is not null)
            Console.WriteLine($"  certificate fingerprint (pin this in the client): {ServerCertificate.Fingerprint(cert)}");
        else
            Console.WriteLine("  plain HTTP: put a TLS reverse proxy in front for anything beyond your LAN.");

        using var shutdown = new CancellationTokenSource();
        // The scan loop has its own token, cancelled only after the running scan is
        // checkpointed: cancelling the scan first rolls it back instead of saving progress.
        using var scanStop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
        // docker stop / systemctl stop send SIGTERM. The web host's ConsoleLifetime cancels
        // it, so a ProcessExit hook never fired (and threw on the disposed token after a clean
        // exit); handle it here and keep the process up for the ordered shutdown below.
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; shutdown.Cancel(); });

        var watcher = new LibraryWatcherService(core.Library, () => core.Settings);
        var scanLoop = options.NoScan
            ? Task.CompletedTask
            : ScanLoopAsync(core, watcher, options.RescanMinutes, scanStop.Token);

        try { await Task.Delay(Timeout.Infinite, shutdown.Token); }
        catch (OperationCanceledException) { }

        Console.WriteLine("shutting down…");
        try { await server.StopAsync(); } catch { }
        try { await core.Library.PauseActiveScanForShutdownAsync(TimeSpan.FromSeconds(5)); } catch { }
        scanStop.Cancel();
        try { await scanLoop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        return 0;
    }

    private static async Task ScanLoopAsync(Core core, LibraryWatcherService watcher, int rescanMinutes, CancellationToken ct)
    {
        if (core.Settings.MusicFolders.Count == 0) return;
        var period = TimeSpan.FromMinutes(Math.Max(1, rescanMinutes));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var started = DateTime.UtcNow;
                await core.Library.ScanAsync(core.Settings.MusicFolders, ct);
                Console.WriteLine($"[Scan] {core.Library.Tracks.Count} tracks in {(DateTime.UtcNow - started).TotalSeconds:F0}s");
                // Live changes between full scans (inotify / ReadDirectoryChanges; on a
                // Docker bind mount this may stay quiet, which is what the period is for).
                watcher.Refresh();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Scan] failed: {ex.Message}");
            }
            try { await Task.Delay(period, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static async Task<int> ScanOnceAsync(Options options)
    {
        var core = await BuildCoreAsync(options);
        if (core.Settings.MusicFolders.Count == 0)
        {
            Console.Error.WriteLine("No music folders: pass --music DIR or set NOCTIS_MUSIC.");
            return 2;
        }
        var started = DateTime.UtcNow;
        await core.Library.ScanAsync(core.Settings.MusicFolders);
        Console.WriteLine($"{core.Library.Tracks.Count} tracks, {core.Library.Albums.Count} albums, {core.Library.Artists.Count} artists in {(DateTime.UtcNow - started).TotalSeconds:F0}s");
        await core.Library.PauseActiveScanForShutdownAsync(TimeSpan.FromSeconds(5));
        return 0;
    }

    // ── users ──

    private static int UserCommand(Options options)
    {
        var serverDir = Path.Combine(AppPaths.DataRoot, "server");
        Directory.CreateDirectory(serverDir);
        var users = new ServerUserStore(Path.Combine(serverDir, "users.db"));
        var name = options.Positionals.ElementAtOrDefault(1);

        switch (options.Positionals.ElementAtOrDefault(0))
        {
            case "list":
                var list = users.List();
                if (list.Count == 0) { Console.WriteLine("(no users)"); return 0; }
                foreach (var u in list)
                    Console.WriteLine($"{u.Name}{(u.IsAdmin ? "  (admin)" : "")}{(u.HasApiKey ? "  api-key" : "")}  created {u.CreatedUtc:yyyy-MM-dd}");
                return 0;

            case "add":
                if (string.IsNullOrWhiteSpace(name)) return Usage("user add NAME [--admin]");
                if (users.Exists(name)) { Console.Error.WriteLine($"User '{name}' already exists."); return 1; }
                var password = ReadPassword("Password (8+ characters): ");
                if (password is null || password.Length < 8) { Console.Error.WriteLine("Password must be at least 8 characters."); return 1; }
                users.Create(name, password, options.Admin);
                Console.WriteLine($"Created '{name}'{(options.Admin ? " (admin)" : "")}.");
                return 0;

            case "passwd":
                if (string.IsNullOrWhiteSpace(name)) return Usage("user passwd NAME");
                if (!users.Exists(name)) { Console.Error.WriteLine($"No user '{name}'."); return 1; }
                var newPassword = ReadPassword("New password (8+ characters): ");
                if (newPassword is null || newPassword.Length < 8) { Console.Error.WriteLine("Password must be at least 8 characters."); return 1; }
                users.ChangePassword(name, newPassword);
                Console.WriteLine($"Password changed for '{name}'.");
                return 0;

            case "apikey":
                if (string.IsNullOrWhiteSpace(name)) return Usage("user apikey NAME");
                if (!users.Exists(name)) { Console.Error.WriteLine($"No user '{name}'."); return 1; }
                Console.WriteLine(users.RegenerateApiKey(name));
                return 0;

            case "remove":
                if (string.IsNullOrWhiteSpace(name)) return Usage("user remove NAME");
                Console.WriteLine(users.Delete(name) ? $"Removed '{name}'." : $"No user '{name}'.");
                return 0;

            default:
                return Usage("user list | add NAME [--admin] | passwd NAME | apikey NAME | remove NAME");
        }

        static int Usage(string text)
        {
            Console.Error.WriteLine("Usage: noctis-server " + text);
            return 2;
        }
    }

    private static string? ReadPassword(string prompt)
    {
        var fromEnv = Environment.GetEnvironmentVariable("NOCTIS_PASSWORD");
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine();
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        return new string(chars.ToArray());
    }

    private static string? LocalAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530); // no packet is sent; picks the outbound interface
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    // ── options ──

    private sealed class Options
    {
        public string Command = "serve";
        public List<string> Positionals { get; } = new();
        public string? DataDir;
        public List<string> MusicDirs { get; } = new();
        public int? Port;
        public bool NoTls;
        public bool NoScan;
        public bool NoSync;
        public bool Admin;
        public bool Help;
        public int RescanMinutes = 60;
        public string? Error;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            Func<string, string?> env = Environment.GetEnvironmentVariable;
            if (env("NOCTIS_MUSIC") is { Length: > 0 } music)
                o.MusicDirs.AddRange(SplitDirs(music));
            if (int.TryParse(env("NOCTIS_PORT"), out var envPort)) o.Port = envPort;
            if (env("NOCTIS_TLS") == "0") o.NoTls = true;
            if (int.TryParse(env("NOCTIS_RESCAN_MINUTES"), out var envRescan)) o.RescanMinutes = envRescan;

            var first = true;
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                string? Next() => i + 1 < args.Length ? args[++i] : null;
                switch (a)
                {
                    case "-h" or "--help" or "help": o.Help = true; break;
                    case "--data": o.DataDir = Next(); if (o.DataDir is null) o.Error = "--data needs a directory"; break;
                    case "--music":
                        var dirs = Next();
                        if (dirs is null) { o.Error = "--music needs a directory"; break; }
                        o.MusicDirs.Clear();
                        o.MusicDirs.AddRange(SplitDirs(dirs));
                        break;
                    case "--port":
                        if (int.TryParse(Next(), out var p) && p is >= 1 and <= 65535) o.Port = p;
                        else o.Error = "--port needs a number between 1 and 65535";
                        break;
                    case "--rescan-minutes":
                        if (int.TryParse(Next(), out var m) && m >= 1) o.RescanMinutes = m;
                        else o.Error = "--rescan-minutes needs a positive number";
                        break;
                    case "--no-tls": o.NoTls = true; break;
                    case "--no-scan": o.NoScan = true; break;
                    case "--no-sync": o.NoSync = true; break;
                    case "--admin": o.Admin = true; break;
                    default:
                        if (a.StartsWith("--", StringComparison.Ordinal)) { o.Error = $"Unknown option '{a}'"; break; }
                        if (first) { o.Command = a; first = false; }
                        else o.Positionals.Add(a);
                        break;
                }
                if (o.Error is not null) break;
            }
            return o;
        }

        private static IEnumerable<string> SplitDirs(string value) =>
            value.Split(new[] { ';', Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
