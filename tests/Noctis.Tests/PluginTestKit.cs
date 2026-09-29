using System.IO.Compression;
using System.Text;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.Services;
using Noctis.Services.Plugins;
using Noctis.ViewModels;

namespace Noctis.Tests;

/// <summary>An in-process plugin whose behaviour each test scripts.</summary>
internal sealed class ScriptedPlugin : INoctisPlugin
{
    public Action<IPluginHost>? OnInit;
    public Action? OnShutdown;
    public IPluginHost? Host;
    public int Initialized;
    public int ShutDown;

    public PluginInfo Info { get; set; } = new("dev.test.scripted", "Scripted", "1.0.0", "Tests", "Scripted test plugin.");

    public void Initialize(IPluginHost host)
    {
        Host = host;
        Initialized++;
        OnInit?.Invoke(host);
    }

    public void Shutdown()
    {
        ShutDown++;
        OnShutdown?.Invoke();
    }
}

/// <summary>A temp data folder, settings, and helpers to write manifests, folders and zips.</summary>
internal sealed class PluginSandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "noctis-plugins-" + Guid.NewGuid().ToString("N"));
    public AppSettings Settings { get; } = new();
    public int Saves;

    public string PluginsDir => Path.Combine(Root, "plugins");
    public string DataRoot => Path.Combine(Root, "plugin-data");

    public PluginHost NewHost(PlayerViewModel? player = null, ILibraryService? library = null, string appVersion = "1.5.3")
        => new(player, Root, () => Settings, () => Saves++, appVersion, library);

    public static string Manifest(
        string id = "dev.test.plugin",
        string name = "Test Plugin",
        string version = "1.0.0",
        string entry = "Test.Plugin.dll",
        string apiVersion = "1.1",
        string? minAppVersion = null,
        string[]? permissions = null,
        string? settingsJson = null,
        string? extra = null)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"id\": \"{id}\",\n  \"name\": \"{name}\",\n  \"version\": \"{version}\",\n  \"author\": \"Tests\",\n");
        sb.Append($"  \"type\": \"dotnet\",\n  \"entry\": \"{entry}\",\n  \"apiVersion\": \"{apiVersion}\",\n");
        if (minAppVersion is not null) sb.Append($"  \"minAppVersion\": \"{minAppVersion}\",\n");
        if (settingsJson is not null) sb.Append($"  \"settings\": {settingsJson},\n");
        if (extra is not null) sb.Append(extra).Append(",\n");
        sb.Append("  \"permissions\": [").Append(string.Join(", ", (permissions ?? Array.Empty<string>()).Select(p => $"\"{p}\""))).Append("]\n}");
        return sb.ToString();
    }

    public static PluginManifest ParseManifest(string json) => PluginManifest.Parse(json);

    /// <summary>plugins/&lt;folder&gt;/ with an optional plugin.json and files.</summary>
    public string WriteFolder(string folder, string? manifestJson, params (string Name, byte[] Bytes)[] files)
    {
        var dir = Path.Combine(PluginsDir, folder);
        Directory.CreateDirectory(dir);
        if (manifestJson is not null) File.WriteAllText(Path.Combine(dir, PluginManifest.FileName), manifestJson);
        foreach (var (n, b) in files)
        {
            var path = Path.Combine(dir, n);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, b);
        }
        return dir;
    }

    /// <summary>A zip whose entries are written exactly as named (so tests can craft "../x").</summary>
    public string WriteZip(string fileName, params (string Name, byte[] Bytes)[] entries)
    {
        Directory.CreateDirectory(Path.Combine(Root, "zips"));
        var path = Path.Combine(Root, "zips", fileName);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            var e = zip.CreateEntry(name);
            using var s = e.Open();
            s.Write(bytes);
        }
        return path;
    }

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>Registers <paramref name="plugin"/> in-process under plugins/&lt;id&gt;.</summary>
    public LoadedPlugin AddInProcess(PluginHost host, ScriptedPlugin plugin, string manifestJson)
    {
        var manifest = PluginManifest.Parse(manifestJson);
        var dir = Path.Combine(PluginsDir, manifest.Id);
        Directory.CreateDirectory(dir);
        return host.AddInProcess(dir, manifest, () => plugin);
    }

    /// <summary>Community plugins on, and <paramref name="ids"/> approved for what they declare.</summary>
    public void Approve(params (string Id, string[] Permissions)[] ids)
    {
        Settings.CommunityPluginsEnabled = true;
        foreach (var (id, perms) in ids) Settings.PluginPermissionGrants[id] = perms.ToList();
    }

    /// <summary>A file of the repository (samples' plugin.json), found by walking up from the test output.</summary>
    public static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Noctis.sln"))) dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("repository root (Noctis.sln) not found above " + AppContext.BaseDirectory);
        return Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}
