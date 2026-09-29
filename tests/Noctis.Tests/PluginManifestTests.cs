using Noctis.Plugins;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

/// <summary>plugin.json parsing, validation messages, version gating and semver ordering.</summary>
public class PluginManifestTests
{
    [Fact]
    public void Parse_FullManifest_ReadsEveryField()
    {
        var m = PluginManifest.Parse("""
            {
              // comments and trailing commas are tolerated
              "id": "dev.example.hello",
              "name": "Hello",
              "version": "1.2.3-beta.1",
              "author": "Example",
              "description": "Says hello.",
              "type": "dotnet",
              "entry": "Hello.dll",
              "entryType": "Hello.Plugin",
              "apiVersion": "1.1",
              "minAppVersion": "1.5.2",
              "platforms": ["windows", "LINUX"],
              "permissions": ["playback.control", "playback.read", "library.read", "network"],
              "settings": [
                { "key": "greeting", "label": "Greeting", "type": "string", "default": "hi" },
                { "key": "loud", "type": "bool", "default": true },
                { "key": "count", "label": "Count", "type": "number", "default": 3, "min": 1, "max": 10 },
                { "key": "mode", "label": "Mode", "type": "choice", "choices": ["a", "b"], "default": "b" },
              ],
              "homepage": "https://example.dev/hello",
            }
            """);

        Assert.Equal("dev.example.hello", m.Id);
        Assert.Equal("Hello", m.Name);
        Assert.Equal("1.2.3-beta.1", m.Version);
        Assert.Equal("Example", m.Author);
        Assert.Equal("Hello.dll", m.Entry);
        Assert.Equal("Hello.Plugin", m.EntryType);
        Assert.Equal("1.1", m.ApiVersion);
        Assert.Equal("1.5.2", m.MinAppVersion);
        Assert.Equal(new[] { "windows", "linux" }, m.Platforms);
        // playback.read is implicit and not listed.
        Assert.Equal(new[] { "playback.control", "library.read", "network" }, m.Permissions);
        Assert.Equal("https://example.dev/hello", m.Homepage);
        Assert.Empty(m.Warnings);

        Assert.Equal(4, m.Settings.Count);
        Assert.Equal(("greeting", PluginSettingType.String, "hi"), (m.Settings[0].Key, m.Settings[0].Type, m.Settings[0].Default));
        Assert.Equal("loud", m.Settings[1].Label); // label defaults to the key
        Assert.Equal("true", m.Settings[1].Default);
        Assert.Equal("3", m.Settings[2].Default);
        Assert.Equal(1, m.Settings[2].Min);
        Assert.Equal("b", m.Settings[3].Default);
    }

    [Theory]
    [InlineData("""{ "name": "x", "version": "1.0.0", "entry": "x.dll", "apiVersion": "1.1" }""", "\"id\" is required")]
    [InlineData("""{ "id": "dev.x.y", "version": "1.0.0", "entry": "x.dll", "apiVersion": "1.1" }""", "\"name\" is required")]
    [InlineData("""{ "id": "dev.x.y", "name": "x", "entry": "x.dll", "apiVersion": "1.1" }""", "\"version\" is required")]
    [InlineData("""{ "id": "dev.x.y", "name": "x", "version": "1", "entry": "x.dll", "apiVersion": "1.1" }""", "not a semantic version")]
    [InlineData("""{ "id": "dev.x.y", "name": "x", "version": "1.0.0", "apiVersion": "1.1" }""", "\"entry\"")]
    [InlineData("""{ "id": "dev.x.y", "name": "x", "version": "1.0.0", "entry": "../x.dll", "apiVersion": "1.1" }""", "without a path")]
    [InlineData("""{ "id": "dev.x.y", "name": "x", "version": "1.0.0", "entry": "x.exe", "apiVersion": "1.1" }""", "must be a DLL")]
    [InlineData("""{ "id": "dev.x.y", "name": "x", "version": "1.0.0", "entry": "x.dll" }""", "\"apiVersion\" is required")]
    [InlineData("""{ "id": "dev.x.y", "name": "x", "version": "1.0.0", "entry": "x.dll", "apiVersion": "one" }""", "\"apiVersion\"")]
    [InlineData("""{ "id": "dev.x.y", "name": "x", "version": "1.0.0", "entry": "x.dll", "apiVersion": "1.1", "type": "python" }""", "\"type\"")]
    [InlineData("""{ "id": "dev.x.y", "name": "x", "version": "1.0.0", "entry": "x.dll", "apiVersion": "1.1", "minAppVersion": "soon" }""", "minAppVersion")]
    [InlineData("""{ "id": "dev.x.y", "name": "x", "version": "1.0.0", "entry": "x.dll", "apiVersion": "1.1", "permissions": "all" }""", "array of strings")]
    [InlineData("""[1, 2]""", "JSON object")]
    [InlineData("""{ not json""", "not valid JSON")]
    public void Parse_Rejects_WithAClearMessage(string json, string expected)
    {
        var ex = Assert.Throws<PluginManifestException>(() => PluginManifest.Parse(json));
        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("NoDots")]
    [InlineData("Dev.Example.Upper")]
    [InlineData("dev..double")]
    [InlineData("../escape.x")]
    [InlineData("dev.example.")]
    [InlineData("dev example.x")]
    public void Parse_RejectsIdsThatAreNotReverseDns(string id)
    {
        var ex = Assert.Throws<PluginManifestException>(() => PluginManifest.Parse(PluginSandbox.Manifest(id: id)));
        Assert.Contains("reverse-DNS", ex.Message);
    }

    [Fact]
    public void Parse_UnknownPermissionsAndPlatforms_AreWarnings()
    {
        var m = PluginManifest.Parse(PluginSandbox.Manifest(
            permissions: new[] { "notifications", "clipboard.write" },
            extra: "  \"platforms\": [\"windows\", \"beos\"],\n  \"homepage\": \"ftp://x\""));
        Assert.Equal(new[] { "notifications" }, m.Permissions);
        Assert.Equal(new[] { "windows" }, m.Platforms);
        Assert.Null(m.Homepage);
        Assert.Equal(3, m.Warnings.Count);
        Assert.Contains(m.Warnings, w => w.Contains("clipboard.write"));
    }

    [Theory]
    [InlineData("""[{ "key": "m", "type": "choice" }]""", "non-empty \"choices\"")]
    [InlineData("""[{ "key": "m", "type": "choice", "choices": ["a"], "default": "z" }]""", "not one of its choices")]
    [InlineData("""[{ "key": "a", "type": "bool" }, { "key": "A", "type": "bool" }]""", "declared twice")]
    [InlineData("""[{ "key": "n", "type": "number", "default": "3" }]""", "must be a number")]
    [InlineData("""[{ "key": "b", "type": "bool", "default": "yes" }]""", "true or false")]
    [InlineData("""[{ "key": "x", "type": "color" }]""", "type must be")]
    [InlineData("""[{ "key": "has space", "type": "bool" }]""", "may only use")]
    public void Parse_RejectsBadSettings(string settings, string expected)
    {
        var ex = Assert.Throws<PluginManifestException>(() => PluginManifest.Parse(PluginSandbox.Manifest(settingsJson: settings)));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void ContentType_NeedsNoEntry()
    {
        var m = PluginManifest.Parse("""{ "id": "dev.x.theme", "name": "Theme", "version": "1.0.0", "type": "content", "contents": { "themes": ["t.json"] } }""");
        Assert.False(m.IsDotnet);
        Assert.True(m.IsContent);
        Assert.Equal("", m.Entry);
        Assert.Equal(new[] { "t.json" }, m.Contents!.Themes);
    }

    [Theory]
    [InlineData("1.0", null)]
    [InlineData("1.1", null)]
    [InlineData("1.9", null)]          // newer minor: additive, still the same major
    [InlineData("2.0", "plugin API 2.0")]
    [InlineData("0.9", "plugin API 0.9")]
    public void Compatibility_GatesOnApiMajor(string api, string? expected)
    {
        var m = PluginManifest.Parse(PluginSandbox.Manifest(apiVersion: api));
        var why = m.CheckCompatibility("1.5.2");
        if (expected is null) Assert.Null(why);
        else Assert.Contains(expected, why);
    }

    [Theory]
    [InlineData("1.5.2", "1.5.2", false)]
    [InlineData("1.5.2", "1.5.3", true)]
    [InlineData("1.5.2", "1.6", true)]
    [InlineData("1.5.2", "1.4.9", false)]
    [InlineData("Version 1.5.2", "1.5.3", true)] // the old display string still parses
    public void Compatibility_GatesOnMinAppVersion(string app, string min, bool refused)
    {
        var m = PluginManifest.Parse(PluginSandbox.Manifest(minAppVersion: min));
        var why = m.CheckCompatibility(app);
        Assert.Equal(refused, why is not null);
        if (refused) Assert.Contains("Needs Noctis " + min, why);
    }

    [Fact]
    public void Compatibility_GatesOnPlatform()
    {
        var other = PluginManifest.CurrentPlatform == "linux" ? "windows" : "linux";
        var m = PluginManifest.Parse(PluginSandbox.Manifest(extra: $"  \"platforms\": [\"{other}\"]"));
        Assert.Contains("not available on " + PluginManifest.CurrentPlatform, m.CheckCompatibility("1.5.2"));
        var here = PluginManifest.Parse(PluginSandbox.Manifest(extra: $"  \"platforms\": [\"{PluginManifest.CurrentPlatform}\"]"));
        Assert.Null(here.CheckCompatibility("1.5.2"));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("2.0.0", "2.0.0", 0)]
    [InlineData("1.0.0-beta", "1.0.0", -1)]
    [InlineData("1.0.0-alpha.2", "1.0.0-alpha.10", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-rc.1", "1.0.0-beta.9", 1)]
    [InlineData("1.0.0+build.5", "1.0.0", 0)]
    [InlineData("garbage", "1.0.0", -1)]
    public void Version_ComparesBySemver(string a, string b, int expected)
        => Assert.Equal(expected, Math.Sign(PluginVersion.Compare(a, b)));

    [Fact]
    public void ApiVersionConstants_MatchTheKitVersion()
    {
        var v = typeof(PluginApi).Assembly.GetName().Version!;
        Assert.Equal((PluginApi.Major, PluginApi.Minor), (v.Major, v.Minor));
        Assert.Equal("1.1", PluginApi.Version);
    }

    [Theory]
    [InlineData("samples/Noctis.SamplePlugin/plugin.json", "Noctis.SamplePlugin.dll")]
    [InlineData("samples/Noctis.SamplePlugin.TrackTools/plugin.json", "Noctis.SamplePlugin.TrackTools.dll")]
    [InlineData("plugins/Noctis.Plugins.Kawarp/plugin.json", "Noctis.Plugins.Kawarp.dll")]
    public void RepositorySampleManifests_AreValid(string relative, string entry)
    {
        var m = PluginManifest.Parse(File.ReadAllText(PluginSandbox.RepoFile(relative)));
        Assert.Equal(entry, m.Entry);
        Assert.Empty(m.Warnings);
        Assert.Null(m.CheckCompatibility("1.5.3"));
    }
}
