using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 11-27 follow-up: a verified official plugin was trusted for its zip alone, so a build copied by
/// hand over it at the same version still ran with Community plugins off. The install now
/// records a hash of the installed files (PluginPayloadHash) and every load compares it.
/// </summary>
public class PluginPayloadTrustTests : IDisposable
{
    private readonly PluginSandbox _box = new();
    private readonly ITestOutputHelper _output;

    public PluginPayloadTrustTests(ITestOutputHelper output)
    {
        _output = output;
        PluginPayloadHash.ClearCacheForTests();
    }

    public void Dispose() => _box.Dispose();

    private const string Id = "dev.noctis.samples.tracktools";
    private static readonly List<string> Grants = new() { "menu.commands", "library.read", "lyrics.provider", "notifications" };

    private static string SampleManifest(string version = "1.0.0")
        => File.ReadAllText(PluginSandbox.RepoFile("samples/Noctis.SamplePlugin.TrackTools/plugin.json"))
            .Replace("\"version\": \"1.0.0\"", $"\"version\": \"{version}\"");

    private static byte[] SampleDll()
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Noctis.SamplePlugin.TrackTools.dll"));

    /// <summary>The Track Tools zip and an official listing that lists exactly it.</summary>
    private (string Zip, PluginCatalogEntry Listing) OfficialZip(string version = "1.0.0")
    {
        var zip = _box.WriteZip($"tracktools-{version}.zip",
            ("plugin.json", PluginSandbox.Utf8(SampleManifest(version))),
            ("Noctis.SamplePlugin.TrackTools.dll", SampleDll()));
        var bytes = File.ReadAllBytes(zip);
        var list = PluginCatalog.Parse(PluginCatalogTests.Index(PluginCatalogTests.Entry(
            id: Id, name: "Track Tools", version: version, sha256: PluginCatalogTests.Hash(bytes), size: bytes.LongLength, minAppVersion: null)));
        return (zip, list.Plugins.Single());
    }

    /// <summary>Community plugins off, Track Tools approved, a host that checks against <paramref name="listing"/>.</summary>
    private PluginHost Host(PluginCatalogEntry listing)
    {
        _box.Settings.CommunityPluginsEnabled = false;
        _box.Settings.PluginPermissionGrants[Id] = Grants.ToList();
        var host = _box.NewHost();
        host.SetOfficialList(PluginCatalog.Parse(PluginCatalogTests.Index(PluginCatalogTests.Entry(
            id: listing.Id, name: listing.Name, version: listing.Version, sha256: listing.Sha256, size: listing.Size, minAppVersion: null))));
        return host;
    }

    private static string[] RecordParts(PluginSandbox box) => box.Settings.VerifiedOfficialPlugins[Id].Split('|');

    [AvaloniaFact]
    public void Install_RecordsThePayload_AndABuildCopiedOverItAtTheSameVersion_IsALocalCopy()
    {
        var (zip, listing) = OfficialZip();
        var host = Host(listing);
        var result = host.InstallPackage(zip, allowUpdate: true, listing);
        var plugin = result.Plugin!;

        var parts = RecordParts(_box);
        Assert.Equal(new[] { "1.0.0", listing.Sha256 }, parts[..2]);
        Assert.Equal(PluginPayloadHash.OfZip(zip), parts[2]);   // the folder is exactly the zip
        Assert.Equal(3, parts.Length);                           // not pinned: checked at install
        Assert.True(host.IsTrustedOfficial(plugin));
        Assert.True(plugin.CanToggle);

        // A build copied over it by hand: same plugin.json, same version, other DLL bytes.
        var dll = Path.Combine(plugin.Directory, "Noctis.SamplePlugin.TrackTools.dll");
        File.AppendAllText(dll, " ");
        var next = Host(listing);
        next.LoadAll();
        var copy = next.Plugins.Single();

        Assert.False(next.IsTrustedOfficial(copy));
        Assert.False(next.IsVerifiedOfficial(copy, listing)); // the card: Local copy, Replace offered
        Assert.Equal(PluginStatus.Restricted, copy.Status);
        Assert.Empty(next.TrackCommands);

        // With Community plugins on it runs, as any community plugin.
        next.SetCommunityPluginsEnabled(true);
        Assert.Equal(PluginStatus.Running, copy.Status);
    }

    [AvaloniaFact]
    public void ItsDataFolder_AndFileManagerFiles_LeaveItVerified_AnAddedFileDoesNot()
    {
        var (zip, listing) = OfficialZip();
        var host = Host(listing);
        var plugin = host.InstallPackage(zip, allowUpdate: true, listing).Plugin!;

        // The plugin writes its data and cache where it is meant to: outside its folder.
        Assert.False(plugin.DataDirectory.StartsWith(plugin.Directory, StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(Path.Combine(plugin.DataDirectory, "cache"));
        File.WriteAllText(Path.Combine(plugin.DataDirectory, "cache", "state.json"), "{}");
        // Opening the folder from Settings in Finder or Explorer.
        File.WriteAllText(Path.Combine(plugin.Directory, ".DS_Store"), "x");
        File.WriteAllText(Path.Combine(plugin.Directory, "Thumbs.db"), "x");

        var next = Host(listing);
        next.LoadAll();
        Assert.True(next.IsTrustedOfficial(next.Plugins.Single()));
        Assert.Equal(PluginStatus.Running, next.Plugins.Single().Status);

        // Anything else added next to the DLL is part of what it ships.
        Directory.CreateDirectory(Path.Combine(plugin.Directory, "runtimes", "win-x64", "native"));
        File.WriteAllBytes(Path.Combine(plugin.Directory, "runtimes", "win-x64", "native", "extra.dll"), new byte[] { 1, 2 });
        var third = Host(listing);
        third.LoadAll();
        Assert.False(third.IsTrustedOfficial(third.Plugins.Single()));
        Assert.Equal(PluginStatus.Restricted, third.Plugins.Single().Status);
    }

    [AvaloniaFact]
    public void Update_WritesTheNewPayload_AndRemove_ClearsTheRecord()
    {
        var (zip1, listing1) = OfficialZip("1.0.0");
        var host = Host(listing1);
        host.InstallPackage(zip1, allowUpdate: true, listing1);
        var first = RecordParts(_box)[2];

        var (zip2, listing2) = OfficialZip("1.1.0");
        host.SetOfficialList(PluginCatalog.Parse(PluginCatalogTests.Index(PluginCatalogTests.Entry(
            id: Id, name: "Track Tools", version: "1.1.0", sha256: listing2.Sha256, size: listing2.Size, minAppVersion: null))));
        var updated = host.InstallPackage(zip2, allowUpdate: true, listing2);
        Assert.Equal(PluginInstallOutcome.Updated, updated.Outcome);

        var parts = RecordParts(_box);
        Assert.Equal(new[] { "1.1.0", listing2.Sha256, PluginPayloadHash.OfZip(zip2) }, parts);
        Assert.NotEqual(first, parts[2]);
        Assert.True(host.IsTrustedOfficial(updated.Plugin!));

        host.Remove(updated.Plugin!, deleteData: false);
        Assert.False(_box.Settings.VerifiedOfficialPlugins.ContainsKey(Id));
    }

    [AvaloniaFact]
    public void RecordFromBefore_IsPinnedAtFirstLoad_ThenTheListedZipSettlesIt()
    {
        var (zip, listing) = OfficialZip();
        var host = Host(listing);
        host.InstallPackage(zip, allowUpdate: true, listing);
        var payload = RecordParts(_box)[2];
        // What 1.7.3 wrote: version and zip hash only.
        _box.Settings.VerifiedOfficialPlugins[Id] = "1.0.0|" + listing.Sha256;

        var next = Host(listing);
        next.LoadAll();
        var plugin = next.Plugins.Single();
        Assert.Equal(new[] { "1.0.0", listing.Sha256, payload, "pinned" }, RecordParts(_box)); // trust on first use
        Assert.True(next.IsTrustedOfficial(plugin));
        Assert.True(next.NeedsPinnedConfirmation(plugin, listing));

        // The Plugins page downloaded the listed zip: the folder is that build.
        next.ConfirmPinnedInstall(plugin, zip);
        Assert.Equal(new[] { "1.0.0", listing.Sha256, payload }, RecordParts(_box));
        Assert.False(next.NeedsPinnedConfirmation(plugin, listing));
        Assert.True(next.IsTrustedOfficial(plugin));
        Assert.Equal(PluginStatus.Running, plugin.Status);
    }

    [AvaloniaFact]
    public void RecordFromBefore_OverAHandCopiedBuild_IsALocalCopyOnceTheListedZipIsChecked()
    {
        var (zip, listing) = OfficialZip();
        var host = Host(listing);
        var installed = host.InstallPackage(zip, allowUpdate: true, listing).Plugin!;
        _box.Settings.VerifiedOfficialPlugins[Id] = "1.0.0|" + listing.Sha256;
        File.AppendAllText(Path.Combine(installed.Directory, "Noctis.SamplePlugin.TrackTools.dll"), " ");

        var next = Host(listing);
        next.LoadAll();
        var plugin = next.Plugins.Single();
        Assert.True(next.IsTrustedOfficial(plugin)); // pinned: nothing could tell before the download
        Assert.Equal(PluginStatus.Running, plugin.Status);

        next.ConfirmPinnedInstall(plugin, zip);
        Assert.Equal(new[] { "1.0.0", listing.Sha256, PluginPayloadHash.OfZip(zip) }, RecordParts(_box));
        Assert.False(next.IsTrustedOfficial(plugin));
        Assert.Equal(PluginStatus.Restricted, plugin.Status);
        Assert.Empty(next.TrackCommands);
    }

    [Fact]
    public void FolderHash_IsTheZipsHash_AndChangesWithAnyFileChange()
    {
        var (zip, _) = OfficialZip();
        var dir = Path.Combine(_box.Root, "extracted");
        PluginInstaller.Extract(PluginInstaller.Inspect(zip), dir);
        var hash = PluginPayloadHash.OfFolder(dir, keepsDataInFolder: false);
        Assert.Equal(PluginPayloadHash.OfZip(zip), hash);
        Assert.Equal(hash, PluginPayloadHash.OfFolder(dir, keepsDataInFolder: false)); // cached, same

        // Same size, new bytes, a new write time: read again.
        var json = Path.Combine(dir, "plugin.json");
        var text = File.ReadAllText(json);
        File.WriteAllText(json, text.Replace("Track Tools", "Track Toolz"));
        File.SetLastWriteTimeUtc(json, DateTime.UtcNow.AddMinutes(1));
        Assert.NotEqual(hash, PluginPayloadHash.OfFolder(dir, keepsDataInFolder: false));

        // A file renamed is a change too.
        File.WriteAllText(json, text);
        File.Move(Path.Combine(dir, "Noctis.SamplePlugin.TrackTools.dll"), Path.Combine(dir, "Other.dll"));
        Assert.NotEqual(hash, PluginPayloadHash.OfFolder(dir, keepsDataInFolder: false));
    }

    [Fact]
    public void StartupCost_ForTheOfficialPlugins_IsMeasured()
    {
        // Five folders like the official plugins (their zips are 12-82 KB; a generous 1 MB each of
        // real DLL bytes and filler), hashed cold as at launch, then again as the later checks of
        // the same launch do.
        var dll = SampleDll();
        var folders = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var dir = Path.Combine(_box.Root, "measure", "p" + i);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "plugin.json"), SampleManifest());
            File.WriteAllBytes(Path.Combine(dir, "Plugin.dll"), dll);
            File.WriteAllBytes(Path.Combine(dir, "Dependency.dll"), Enumerable.Range(0, 1024 * 1024 - dll.Length).Select(b => (byte)b).ToArray());
            folders.Add(dir);
        }

        var cold = Stopwatch.StartNew();
        foreach (var dir in folders) PluginPayloadHash.OfFolder(dir, keepsDataInFolder: false);
        cold.Stop();
        var warm = Stopwatch.StartNew();
        foreach (var dir in folders) PluginPayloadHash.OfFolder(dir, keepsDataInFolder: false);
        warm.Stop();

        _output.WriteLine($"payload hash, 5 plugins x 1 MB: cold {cold.Elapsed.TotalMilliseconds:F1} ms, cached {warm.Elapsed.TotalMilliseconds:F2} ms");
        Assert.True(cold.ElapsedMilliseconds < 2000, $"cold {cold.ElapsedMilliseconds} ms");
    }
}
