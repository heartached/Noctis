using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The plugin host against the real sample plugin assembly (referenced by this test project,
/// so it sits in the test output): discovery by folder, isolated load, visual-layer
/// registration, a throwing plugin contained as "Failed", and the persisted disabled list.
/// </summary>
public class PluginHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-plugins-" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings = new();
    private int _saves;

    private static string SampleDll => Path.Combine(AppContext.BaseDirectory, "Noctis.SamplePlugin.dll");

    private PluginHost NewHost() => new(null, _root, () => _settings, () => _saves++, "test");

    /// <summary>plugins/&lt;folder&gt;/Noctis.SamplePlugin.dll — the folder name selects the plugin type inside.</summary>
    private void Install(string folder)
    {
        var dir = Path.Combine(_root, "plugins", folder);
        Directory.CreateDirectory(dir);
        File.Copy(SampleDll, Path.Combine(dir, Path.GetFileName(SampleDll)), overwrite: true);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* unloaded contexts may hold files briefly on Windows */ }
    }

    [Fact]
    public void SampleAssembly_IsAvailableToTheTests()
        => Assert.True(File.Exists(SampleDll), $"missing {SampleDll} — the test project must reference samples/Noctis.SamplePlugin");

    [AvaloniaFact] // plugin Initialize may touch Avalonia types
    public void LoadAll_LoadsRunningPlugins_AndContainsAThrowingOne()
    {
        Install("PulseRingPlugin");
        Install("ThrowingPlugin");
        var host = NewHost();

        host.LoadAll();

        Assert.Equal(2, host.Plugins.Count);
        var ring = host.Plugins.Single(p => p.FolderName == "PulseRingPlugin");
        Assert.Equal("Running", ring.Status);
        Assert.Equal("Pulse Ring", ring.Name);
        Assert.Equal("1.0.0", ring.Version);
        Assert.Contains("Pulse ring", ring.Extensions);
        Assert.Single(host.VisualLayers);

        var boom = host.Plugins.Single(p => p.FolderName == "ThrowingPlugin");
        Assert.Equal("Failed", boom.Status);
        Assert.Contains("boom from plugin", boom.Error);
        Assert.True(boom.HasError);

        host.UnloadAll();
        Assert.Empty(host.Plugins);
        Assert.Empty(host.VisualLayers);
    }

    [AvaloniaFact]
    public void Disabled_IsPersisted_AndSkippedOnNextLoad()
    {
        Install("PulseRingPlugin");
        var host = NewHost();
        host.LoadAll();
        var ring = host.Plugins.Single();
        var layerEvents = 0;
        host.VisualLayersChanged += (_, _) => layerEvents++;

        host.SetEnabled(ring, false);
        Assert.Equal("Disabled", ring.Status);
        Assert.False(ring.IsEnabled);
        Assert.Contains("PulseRingPlugin", _settings.DisabledPlugins!);
        Assert.Equal(1, _saves);
        Assert.Empty(host.VisualLayers);
        Assert.Equal(1, layerEvents);

        // A fresh host honours the persisted list without starting the plugin.
        var again = NewHost();
        again.LoadAll();
        Assert.Equal("Disabled", again.Plugins.Single().Status);
        Assert.Empty(again.VisualLayers);

        again.SetEnabled(again.Plugins.Single(), true);
        Assert.Equal("Running", again.Plugins.Single().Status);
        Assert.DoesNotContain("PulseRingPlugin", _settings.DisabledPlugins!);
        again.UnloadAll();
        host.UnloadAll();
    }

    [Fact]
    public void LoadAll_EmptyOrMissingFolder_IsFine()
    {
        var host = NewHost();
        host.LoadAll();
        Assert.Empty(host.Plugins);
        Assert.True(Directory.Exists(host.PluginsDirectory)); // created so the user has somewhere to drop files
    }

    [AvaloniaFact]
    public void VisualLayer_CreatesAControl()
    {
        Install("PulseRingPlugin");
        var host = NewHost();
        host.LoadAll();
        var control = host.VisualLayers.Single().CreateLayer();
        Assert.NotNull(control);
        host.UnloadAll();
    }
}
