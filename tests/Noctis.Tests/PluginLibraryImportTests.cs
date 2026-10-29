using Avalonia.Headless.XUnit;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Plugin API 1.5, "library.import" (the "Add from YouTube" plugin's way into the library):
/// gated by its permission, adds only existing files Noctis plays through the drop's additive
/// import (on the UI thread), returns the library's copies, reads the music folders, and goes
/// inert when the plugin stops. Kit and manifest stay backward compatible.
/// </summary>
public class PluginLibraryImportTests : IDisposable
{
    private readonly PluginSandbox _box = new();
    private readonly string _files = Path.Combine(Path.GetTempPath(), "noctis-import-api-" + Guid.NewGuid().ToString("N")[..8]);

    public PluginLibraryImportTests() => Directory.CreateDirectory(_files);

    public void Dispose()
    {
        _box.Dispose();
        try { Directory.Delete(_files, true); } catch { }
    }

    private const string Id = "dev.test.import";

    private static string M(params string[] permissions) => PluginSandbox.Manifest(id: Id, apiVersion: "1.5", minAppVersion: "1.6.6", permissions: permissions);

    private (ScriptedPlugin Script, LoadedPlugin Plugin, PluginHost Host) Start(FakeLibraryService? library, params string[] permissions)
    {
        _box.Approve((Id, permissions));
        var host = _box.NewHost(library: library, appVersion: "1.6.6");
        var script = new ScriptedPlugin();
        var plugin = _box.AddInProcess(host, script, M(permissions));
        Assert.Equal(PluginStatus.Running, plugin.Status);
        return (script, plugin, host);
    }

    private string File(string name)
    {
        var path = Path.Combine(_files, name);
        System.IO.File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    [Fact]
    public void Kit_Is15_AndKnowsTheNewPermission()
    {
        Assert.True(PluginApi.Minor >= 5);
        Assert.Contains("library.import", PluginPermissions.All);
        Assert.Equal("library.import", PluginPermissions.LibraryImport);
        var manifest = PluginManifest.Parse(M("library.import", "network"));
        Assert.Equal(new[] { "library.import", "network" }, manifest.Permissions);
        Assert.Empty(manifest.Warnings);
        Assert.Null(manifest.CheckCompatibility("1.6.6"));
        Assert.Equal(Loc.T("Plugins.PermShort.LibraryImport"), PluginPermissionText.Short("library.import"));
        Assert.NotEqual("library.import", PluginPermissionText.Short("library.import"));
        Assert.Contains("library", PluginPermissionText.Describe("library.import"));
    }

    /// <summary>A host built against an older kit (or a plugin's own fake) still compiles and says which API is missing.</summary>
    [Fact]
    public void OlderHosts_StillCompile_AndSayWhichApiIsMissing()
    {
        IPluginHost old = new OldHost();
        var ex = Assert.Throws<NotSupportedException>(() => old.LibraryImport);
        Assert.Contains("1.5", ex.Message);
    }

    private sealed class OldHost : IPluginHost
    {
        public string AppVersion => "1.6.5";
        public string DataDirectory => "";
        public INowPlaying NowPlaying => null!;
        public IBeatSource Beat => null!;
        public ISpectrumSource Spectrum => null!;
        public void Log(string message) { }
        public void RegisterVisualLayer(IVisualLayerProvider provider) { }
    }

    [AvaloniaFact]
    public void LibraryImport_ThrowsWithoutItsPermission()
    {
        var (script, _, _) = Start(new FakeLibraryService(), "library.read", "network");
        Assert.Equal("library.import", Assert.Throws<PluginPermissionException>(() => script.Host!.LibraryImport).Permission);
    }

    [AvaloniaFact]
    public void MusicFolders_AreCopiesOfTheSettings_WithoutBlanks()
    {
        _box.Settings.MusicFolders = new List<string> { @"B:\ALAC\Drake", "  ", @"B:\ALAC\Taylor Swift" };
        var (script, _, _) = Start(new FakeLibraryService(), "library.import");
        var folders = script.Host!.LibraryImport.MusicFolders;
        Assert.Equal(new[] { @"B:\ALAC\Drake", @"B:\ALAC\Taylor Swift" }, folders);
        _box.Settings.MusicFolders.Clear();
        Assert.Equal(2, folders.Count); // a copy: later changes do not show through
        Assert.Empty(script.Host.LibraryImport.MusicFolders);
    }

    [AvaloniaFact]
    public async Task ImportFiles_AddsOnlyExistingPlayableAbsolutePaths_OnTheUiThread_AndReturnsTheTracks()
    {
        var library = new FakeLibraryService { ImportAddsTracks = true };
        var (script, _, _) = Start(library, "library.import");
        var song = File("Artist - Song.m4a");
        var opus = File("Other.opus");
        var text = File("notes.txt");

        var tracks = await script.Host!.LibraryImport.ImportFilesAsync(new[]
        {
            song,
            Path.Combine(_files, ".", "Artist - Song.m4a"), // the same file again
            opus,
            text,                                         // not audio
            Path.Combine(_files, "missing.mp3"),           // not there
            "relative/song.mp3",                          // not absolute
            "",
        });

        var call = Assert.Single(library.ImportCalls);
        Assert.Equal(new[] { song, opus }, call);
        Assert.True(Assert.Single(library.ImportOnUiThread));
        Assert.Equal(new[] { song, opus }.OrderBy(p => p), tracks.Select(t => t.FilePath).OrderBy(p => p));
        Assert.All(tracks, t => Assert.True(Guid.TryParse(t.Id, out _)));
    }

    [AvaloniaFact]
    public async Task ImportFiles_FileTheLibraryRefused_IsMissingFromTheResult()
    {
        var library = new FakeLibraryService { ImportAddsTracks = false }; // an unreadable file: the import skips it
        var (script, _, _) = Start(library, "library.import");
        var tracks = await script.Host!.LibraryImport.ImportFilesAsync(new[] { File("broken.mp3") });
        Assert.Single(library.ImportCalls);
        Assert.Empty(tracks);
    }

    [AvaloniaFact]
    public async Task ImportFiles_NothingUsable_DoesNotTouchTheLibrary()
    {
        var library = new FakeLibraryService { ImportAddsTracks = true };
        var (script, _, _) = Start(library, "library.import");
        Assert.Empty(await script.Host!.LibraryImport.ImportFilesAsync(Array.Empty<string>()));
        Assert.Empty(await script.Host.LibraryImport.ImportFilesAsync(new[] { File("a.txt") }));
        Assert.Empty(library.ImportCalls);
        await Assert.ThrowsAsync<ArgumentNullException>(() => script.Host.LibraryImport.ImportFilesAsync(null!));
    }

    [Fact]
    public void Accept_CapsOneCall()
    {
        var many = Enumerable.Range(0, PluginLibraryImporter.MaxPaths + 20).Select(i => File($"t{i}.mp3")).ToList();
        Assert.Equal(PluginLibraryImporter.MaxPaths, PluginLibraryImporter.Accept(many).Count);
    }

    [AvaloniaFact]
    public async Task ImportFiles_FromAStoppedPlugin_IsDropped()
    {
        var library = new FakeLibraryService { ImportAddsTracks = true };
        var (script, plugin, host) = Start(library, "library.import");
        var import = script.Host!.LibraryImport;
        host.SetEnabled(plugin, false);
        Assert.Empty(await import.ImportFilesAsync(new[] { File("late.mp3") }));
        Assert.Empty(library.ImportCalls);
    }
}
