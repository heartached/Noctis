using Avalonia.Headless.XUnit;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

/// <summary>Install from .zip (layouts, duplicates, updates, zip-slip), and Remove.</summary>
public class PluginInstallTests : IDisposable
{
    private readonly PluginSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private const string Id = "dev.test.zipped";

    private string Zip(string version = "1.0.0", string prefix = "", params (string, byte[])[] extra)
    {
        var entries = new List<(string, byte[])>
        {
            (prefix + "plugin.json", PluginSandbox.Utf8(PluginSandbox.Manifest(id: Id, version: version, permissions: new[] { "notifications" }))),
            (prefix + "Test.Plugin.dll", new byte[] { 0x4D, 0x5A }),
            (prefix + "assets/readme.txt", PluginSandbox.Utf8("v" + version)),
        };
        entries.AddRange(extra);
        return _box.WriteZip($"{Id}-{version}-{Guid.NewGuid():N}.zip", entries.ToArray());
    }

    private PluginHost Host()
    {
        _box.Settings.CommunityPluginsEnabled ??= true;
        var host = _box.NewHost();
        host.LoadAll();
        return host;
    }

    [AvaloniaFact]
    public void Install_ExtractsIntoTheIdFolder_AndWaitsForApproval()
    {
        var host = Host();
        var result = host.InstallPackage(Zip(), allowUpdate: false);

        Assert.Equal(PluginInstallOutcome.Installed, result.Outcome);
        var dir = Path.Combine(_box.PluginsDir, Id);
        Assert.Equal(dir, result.Plugin!.Directory);
        Assert.True(File.Exists(Path.Combine(dir, "plugin.json")));
        Assert.True(File.Exists(Path.Combine(dir, "assets", "readme.txt")));
        Assert.Same(result.Plugin, host.Plugins.Single());
        Assert.Equal(PluginStatus.Disabled, result.Plugin.Status); // not approved yet: nothing ran
        Assert.False(result.Plugin.IsRunning);
        Assert.DoesNotContain(Directory.GetDirectories(_box.PluginsDir), d => Path.GetFileName(d).StartsWith(".staging"));
    }

    [AvaloniaFact]
    public void Install_AcceptsASingleTopLevelFolder()
    {
        var host = Host();
        var result = host.InstallPackage(Zip(prefix: "MyPlugin/"), allowUpdate: false);
        Assert.Equal(PluginInstallOutcome.Installed, result.Outcome);
        Assert.True(File.Exists(Path.Combine(_box.PluginsDir, Id, "Test.Plugin.dll")));
        Assert.False(Directory.Exists(Path.Combine(_box.PluginsDir, Id, "MyPlugin")));
    }

    [AvaloniaFact]
    public void Install_IntoAFreshSetup_StaysRestricted()
    {
        // Settings never decided: installing the first plugin must not count as "plugins were already there".
        var host = _box.NewHost();
        var result = host.InstallPackage(Zip(), allowUpdate: false);
        Assert.Equal(PluginInstallOutcome.Installed, result.Outcome);
        Assert.False(_box.Settings.CommunityPluginsEnabled);
        Assert.Equal(PluginStatus.Restricted, result.Plugin!.Status);
    }

    [AvaloniaFact]
    public void Duplicates_AreRefused_OlderToo_NewerOnlyWithUpdate()
    {
        var host = Host();
        Assert.Equal(PluginInstallOutcome.Installed, host.InstallPackage(Zip("1.2.0"), false).Outcome);

        Assert.Equal(PluginInstallOutcome.AlreadyInstalled, host.InstallPackage(Zip("1.2.0"), allowUpdate: true).Outcome);
        Assert.Equal(PluginInstallOutcome.NotNewer, host.InstallPackage(Zip("1.1.9"), allowUpdate: true).Outcome);
        Assert.Equal(PluginInstallOutcome.AlreadyInstalled, host.InstallPackage(Zip("1.3.0"), allowUpdate: false).Outcome);
        Assert.Equal("v1.2.0", File.ReadAllText(Path.Combine(_box.PluginsDir, Id, "assets", "readme.txt")));

        var (package, existing) = host.InspectPackage(Zip("1.3.0"));
        Assert.Equal("1.3.0", package.Manifest.Version);
        Assert.Equal("1.2.0", existing!.Version);
    }

    [AvaloniaFact]
    public void Update_ReplacesTheFolder_KeepsDataSettingsAndApproval()
    {
        var host = Host();
        var first = host.InstallPackage(Zip("1.0.0", extra: ("old-only.txt", new byte[] { 1 })), false).Plugin!;
        Directory.CreateDirectory(first.DataDirectory);
        File.WriteAllText(Path.Combine(first.DataDirectory, "state.json"), "{}");
        _box.Settings.PluginSettingValues[Id] = new Dictionary<string, string> { ["k"] = "v" };
        _box.Settings.PluginPermissionGrants[Id] = new List<string> { "notifications" };

        var result = host.InstallPackage(Zip("2.0.0"), allowUpdate: true);

        Assert.Equal(PluginInstallOutcome.Updated, result.Outcome);
        Assert.Equal("2.0.0", result.Plugin!.Version);
        Assert.Single(host.Plugins);
        Assert.Equal("v2.0.0", File.ReadAllText(Path.Combine(_box.PluginsDir, Id, "assets", "readme.txt")));
        Assert.False(File.Exists(Path.Combine(_box.PluginsDir, Id, "old-only.txt")));
        Assert.True(File.Exists(Path.Combine(first.DataDirectory, "state.json")));
        Assert.Equal("v", _box.Settings.PluginSettingValues[Id]["k"]);
        Assert.True(host.IsApproved(result.Plugin));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("assets/../../evil.txt")]
    [InlineData("/abs/evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("C:evil.txt")]
    [InlineData("file.txt:stream")]
    public void ZipSlip_RejectsTheWholePackage(string evil)
    {
        var host = new PluginHost(null, _box.Root, () => { _box.Settings.CommunityPluginsEnabled ??= true; return _box.Settings; }, () => { }, "1.5.2");
        var zip = Zip(extra: (evil, PluginSandbox.Utf8("pwned")));

        var result = host.InstallPackage(zip, allowUpdate: false);

        Assert.Equal(PluginInstallOutcome.Failed, result.Outcome);
        Assert.Contains("unsafe path", result.Message);
        Assert.False(Directory.Exists(Path.Combine(_box.PluginsDir, Id)));
        Assert.False(File.Exists(Path.Combine(_box.Root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_box.PluginsDir, "evil.txt")));
        Assert.Empty(host.Plugins);
    }

    [Fact]
    public void Extract_DoubleChecksEveryResolvedPath()
    {
        // Belt and braces: even if an entry name slipped past the name check, the resolved
        // path must stay inside the destination.
        Assert.False(PluginInstaller.IsSafeEntryName("a/../../b"));
        Assert.False(PluginInstaller.IsSafeEntryName("./a"));
        Assert.False(PluginInstaller.IsSafeEntryName("a\u0000b"));
        Assert.True(PluginInstaller.IsSafeEntryName("lib/x64/native.dll"));
        Assert.True(PluginInstaller.IsSafeEntryName("dir/"));
    }

    [Theory]
    [InlineData("no-manifest", "No plugin.json")]
    [InlineData("two-manifests", "more than one plugin.json")]
    [InlineData("missing-entry", "no such file")]
    [InlineData("bad-manifest", "\"version\" is required")]
    [InlineData("not-a-zip", "not a readable .zip")]
    public void BrokenPackages_AreRefusedWithAReason(string kind, string expected)
    {
        var host = Host();
        var manifest = PluginSandbox.Utf8(PluginSandbox.Manifest(id: Id));
        string zip = kind switch
        {
            "no-manifest" => _box.WriteZip("a.zip", ("Test.Plugin.dll", new byte[1])),
            "two-manifests" => _box.WriteZip("b.zip", ("a/plugin.json", manifest), ("b/plugin.json", manifest)),
            "missing-entry" => _box.WriteZip("c.zip", ("plugin.json", manifest)),
            "bad-manifest" => _box.WriteZip("d.zip", ("plugin.json", PluginSandbox.Utf8("{ \"id\": \"dev.x.y\", \"name\": \"x\" }"))),
            _ => WriteNotAZip(),
        };
        var result = host.InstallPackage(zip, false);
        Assert.Equal(PluginInstallOutcome.Failed, result.Outcome);
        Assert.Contains(expected, result.Message);
    }

    private string WriteNotAZip()
    {
        var path = Path.Combine(_box.Root, "fake.zip");
        Directory.CreateDirectory(_box.Root);
        File.WriteAllText(path, "hello");
        return path;
    }

    [AvaloniaFact]
    public void Remove_DeletesTheFolder_KeepsDataUnlessAsked()
    {
        var host = Host();
        var plugin = host.InstallPackage(Zip(), false).Plugin!;
        Directory.CreateDirectory(plugin.DataDirectory);
        File.WriteAllText(Path.Combine(plugin.DataDirectory, "keep.txt"), "x");
        _box.Settings.PluginSettingValues[Id] = new Dictionary<string, string> { ["k"] = "v" };
        _box.Settings.PluginPermissionGrants[Id] = new List<string> { "notifications" };
        _box.Settings.DisabledPlugins.Add(Id);

        Assert.True(host.Remove(plugin, deleteData: false));
        Assert.False(Directory.Exists(plugin.Directory));
        Assert.True(File.Exists(Path.Combine(plugin.DataDirectory, "keep.txt")));
        Assert.True(_box.Settings.PluginSettingValues.ContainsKey(Id));
        Assert.False(_box.Settings.PluginPermissionGrants.ContainsKey(Id)); // a reinstall asks again
        Assert.DoesNotContain(Id, _box.Settings.DisabledPlugins);
        Assert.Empty(host.Plugins);

        var again = host.InstallPackage(Zip(), false).Plugin!;
        Assert.True(host.Remove(again, deleteData: true));
        Assert.False(Directory.Exists(again.DataDirectory));
        Assert.False(_box.Settings.PluginSettingValues.ContainsKey(Id));
    }
}
