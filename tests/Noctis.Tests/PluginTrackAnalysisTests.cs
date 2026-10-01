using Avalonia.Headless.XUnit;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.Services;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Plugin API 1.2: BPM/key on TrackInfo, the whole-library read, and "library.write.analysis"
/// (gated, library-only, fill-empty by default, one save per batch, inert after stop).
/// </summary>
public class PluginTrackAnalysisTests : IDisposable
{
    private readonly PluginSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private const string Id = "dev.test.analysis";

    private static string M(params string[] permissions) => PluginSandbox.Manifest(id: Id, apiVersion: "1.2", permissions: permissions);

    private static Track T(string title, int bpm = 0, string key = "") => new()
    {
        Title = title, Artist = "Artist", Album = "Album", AlbumArtist = "Artist",
        Duration = TimeSpan.FromSeconds(200), FilePath = Path.Combine(Path.GetTempPath(), title + ".mp3"),
        Bpm = bpm, MusicalKey = key,
    };

    private (ScriptedPlugin Script, LoadedPlugin Plugin, PluginHost Host) Start(FakeLibraryService? library, params string[] permissions)
    {
        _box.Approve((Id, permissions));
        var host = _box.NewHost(library: library);
        var script = new ScriptedPlugin();
        var plugin = _box.AddInProcess(host, script, M(permissions));
        Assert.Equal(PluginStatus.Running, plugin.Status);
        return (script, plugin, host);
    }

    [Fact]
    public void Kit_Is12_AndKnowsTheNewPermission()
    {
        Assert.Equal("1.2", PluginApi.Version);
        Assert.Contains("library.write.analysis", PluginPermissions.All);
        var manifest = PluginManifest.Parse(M("library.write.analysis", "library.read"));
        Assert.Equal(new[] { "library.write.analysis", "library.read" }, manifest.Permissions);
        Assert.Empty(manifest.Warnings);
        Assert.Equal(Loc.T("Plugins.PermShort.LibraryWriteAnalysis"), PluginPermissionText.Short("library.write.analysis"));
        Assert.Contains("never in your files", PluginPermissionText.Describe("library.write.analysis"));
    }

    [AvaloniaFact]
    public void TrackAnalysis_ThrowsWithoutItsPermission()
    {
        var (script, _, _) = Start(new FakeLibraryService(), "library.read");
        Assert.Equal("library.write.analysis", Assert.Throws<PluginPermissionException>(() => script.Host!.TrackAnalysis).Permission);
    }

    [AvaloniaFact]
    public void GetAll_CopiesEveryTrack_WithBpmAndKey_PastTheSearchCap()
    {
        var library = new FakeLibraryService();
        for (var i = 0; i < 600; i++) library.TrackList.Add(T("t" + i));
        library.TrackList[7].Bpm = 128;
        library.TrackList[7].MusicalKey = "A minor";
        var (script, _, _) = Start(library, "library.read");

        var all = script.Host!.Library.GetAll();
        Assert.Equal(600, all.Count);
        Assert.Equal(500, script.Host.Library.Search("", 100_000).Count);
        Assert.Equal((128, "A minor"), (all[7].Bpm, all[7].MusicalKey));
        Assert.Equal((0, ""), (all[8].Bpm, all[8].MusicalKey));
        Assert.Equal(library.TrackList[7].Id.ToString("D"), all[7].Id);
    }

    [AvaloniaFact]
    public async Task SetTrackAnalysis_FillsOnlyEmptyValues_ByDefault_AndSavesOnce()
    {
        var library = new FakeLibraryService();
        var empty = T("empty");
        var full = T("full", 120, "C major");
        var keyOnly = T("keyOnly", 0, "D minor");
        library.TrackList.AddRange(new[] { empty, full, keyOnly });
        var (script, _, _) = Start(library, "library.write.analysis");

        var changed = await script.Host!.TrackAnalysis.SetTrackAnalysisAsync(new[]
        {
            new TrackAnalysisUpdate(empty.Id.ToString("D"), 128, " A minor "),
            new TrackAnalysisUpdate(full.Id.ToString("D"), 90, "E minor"),
            new TrackAnalysisUpdate(keyOnly.Id.ToString("D"), 100, "F major"),
            new TrackAnalysisUpdate(Guid.NewGuid().ToString("D"), 100, "F major"), // not in the library
            new TrackAnalysisUpdate("not a guid", 100),
        });

        Assert.Equal(2, changed);
        Assert.Equal((128, "A minor"), (empty.Bpm, empty.MusicalKey));
        Assert.Equal((120, "C major"), (full.Bpm, full.MusicalKey));
        Assert.Equal((100, "D minor"), (keyOnly.Bpm, keyOnly.MusicalKey));
        Assert.Equal(1, library.SaveCount);
        Assert.Equal(1, library.MetadataChangedCount);
    }

    [AvaloniaFact]
    public async Task SetTrackAnalysis_Overwrite_ReplacesValues_AndNullLeavesOneAlone()
    {
        var library = new FakeLibraryService();
        var a = T("a", 120, "C major");
        var b = T("b", 95, "G major");
        library.TrackList.AddRange(new[] { a, b });
        var (script, _, _) = Start(library, "library.write.analysis");

        var changed = await script.Host!.TrackAnalysis.SetTrackAnalysisAsync(new[]
        {
            new TrackAnalysisUpdate(a.Id.ToString("D"), 124, null),
            new TrackAnalysisUpdate(b.Id.ToString("D"), 95, "G major"), // same values: not a change
        }, overwrite: true);

        Assert.Equal(1, changed);
        Assert.Equal((124, "C major"), (a.Bpm, a.MusicalKey));
        Assert.Equal(1, library.SaveCount);
    }

    [AvaloniaFact]
    public async Task SetTrackAnalysis_SkipsOutOfRangeValues_WithoutSaving()
    {
        var library = new FakeLibraryService();
        var t = T("t");
        library.TrackList.Add(t);
        var (script, _, _) = Start(library, "library.write.analysis");
        var id = t.Id.ToString("D");

        var changed = await script.Host!.TrackAnalysis.SetTrackAnalysisAsync(new[]
        {
            new TrackAnalysisUpdate(id, 0, "   "),
            new TrackAnalysisUpdate(id, -5, new string('x', 33)),
            new TrackAnalysisUpdate(id, 1000, "A\nminor"),
        }, overwrite: true);

        Assert.Equal(0, changed);
        Assert.Equal((0, ""), (t.Bpm, t.MusicalKey));
        Assert.Equal(0, library.SaveCount);
        Assert.Equal(0, await script.Host.TrackAnalysis.SetTrackAnalysisAsync(Array.Empty<TrackAnalysisUpdate>()));
        Assert.Equal(1, PluginTrackAnalysisWriter.Apply(library, new[] { new TrackAnalysisUpdate(id, 999, new string('k', 32)) }, overwrite: false));
    }

    [AvaloniaFact]
    public async Task SetTrackAnalysis_FromAStoppedPlugin_ChangesNothing()
    {
        var library = new FakeLibraryService();
        var t = T("t");
        library.TrackList.Add(t);
        var (script, plugin, host) = Start(library, "library.write.analysis");
        var writer = script.Host!.TrackAnalysis;

        host.SetEnabled(plugin, false);
        var changed = await writer.SetTrackAnalysisAsync(new[] { new TrackAnalysisUpdate(t.Id.ToString("D"), 128, "A minor") });

        Assert.Equal(0, changed);
        Assert.Equal((0, ""), (t.Bpm, t.MusicalKey));
        Assert.Equal(0, library.SaveCount);
    }

    [AvaloniaFact]
    public void ToTrackInfo_CarriesBpmAndKey_AndOldConstructionStillWorks()
    {
        var info = PluginHost.ToTrackInfo(T("t", 140, "8A"));
        Assert.Equal((140, "8A"), (info.Bpm, info.MusicalKey));
        // A 1.1 plugin builds TrackInfo with the 13 positional values; the 1.2 members default.
        var old = new TrackInfo("id", "t", "a", "al", "aa", TimeSpan.Zero, 0, "", 0, "", false, 0, 0);
        Assert.Equal((0, ""), (old.Bpm, old.MusicalKey));
    }

    [Fact]
    public void OlderHosts_ReportApi12AsMissing()
    {
        IPluginHost host = new Api10Host();
        Assert.Contains("1.2", Assert.Throws<NotSupportedException>(() => host.TrackAnalysis).Message);
        Assert.Contains("1.1", Assert.Throws<NotSupportedException>(() => host.Library).Message);
        ILibraryReader reader = new Api11Reader();
        Assert.Contains("1.2", Assert.Throws<NotSupportedException>(() => reader.GetAll()).Message);
    }

    /// <summary>An IPluginHost written against kit 1.0: only the members 1.0 had.</summary>
    private sealed class Api10Host : IPluginHost
    {
        public string AppVersion => "1.5.0";
        public string DataDirectory => "";
        public INowPlaying NowPlaying => throw new NotImplementedException();
        public IBeatSource Beat => throw new NotImplementedException();
        public ISpectrumSource Spectrum => throw new NotImplementedException();
        public void Log(string message) { }
        public void RegisterVisualLayer(IVisualLayerProvider provider) { }
    }

    /// <summary>An ILibraryReader written against kit 1.1.</summary>
    private sealed class Api11Reader : ILibraryReader
    {
        public int TrackCount => 0;
        public IReadOnlyList<TrackInfo> Search(string query, int limit = 50) => Array.Empty<TrackInfo>();
    }
}
