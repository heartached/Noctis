using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// docs/PLUGINS.md "Writing your description" (owner 10-29): every plugin in this repo, and every
/// entry of plugins/index.json, describes itself in one short, plain sentence that fits the card's
/// two lines; each setting in one short line. No em dash, no "By X." (the card already says "by X").
/// </summary>
public class PluginDescriptionRulesTests
{
    /// <summary>The card's two lines.</summary>
    private const int MaxDescription = 100;
    /// <summary>One line beside a setting's label.</summary>
    private const int MaxSettingDescription = 55;
    private const char EmDash = (char)0x2014;

    private static readonly Regex ByLine = new(@"\bBy [A-Z]", RegexOptions.CultureInvariant);

    public static IEnumerable<object[]> Manifests()
        => Directory.GetFiles(PluginSandbox.RepoFile("plugins"), "plugin.json", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Select(p => new object[] { Path.GetFileName(Path.GetDirectoryName(p))! });

    private static void AssertPlain(string? text, int max, string where)
    {
        Assert.False(string.IsNullOrWhiteSpace(text), $"{where}: no description");
        Assert.True(text!.Length <= max, $"{where}: {text.Length} characters (at most {max}): \"{text}\"");
        Assert.DoesNotContain(EmDash, text);
        Assert.False(ByLine.IsMatch(text), $"{where}: has a by-line: \"{text}\"");
        Assert.Equal(text.Trim(), text);
    }

    [Theory]
    [MemberData(nameof(Manifests))]
    public void EveryPluginJson_HasAShortPlainDescription(string folder)
    {
        var json = JsonNode.Parse(File.ReadAllText(PluginSandbox.RepoFile($"plugins/{folder}/plugin.json")),
            documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        AssertPlain((string?)json["description"], MaxDescription, folder);

        foreach (var setting in json["settings"]?.AsArray() ?? new JsonArray())
        {
            var where = $"{folder} setting {(string?)setting!["key"]}";
            if ((string?)setting["description"] is { } d)
                AssertPlain(d, MaxSettingDescription, where);
            Assert.DoesNotContain(EmDash, (string?)setting["label"] ?? "");
        }
    }

    [Fact]
    public void TheOfficialList_HasShortPlainDescriptions_ThatMatchTheirPlugins()
    {
        var index = JsonNode.Parse(File.ReadAllText(PluginSandbox.RepoFile("plugins/index.json")))!;
        foreach (var entry in index["plugins"]!.AsArray())
        {
            var id = (string)entry!["id"]!;
            var description = (string?)entry["description"];
            AssertPlain(description, MaxDescription, "index.json " + id);
            // The card shows the list's text before install and the plugin's after: the same words.
            var folder = Directory.GetDirectories(PluginSandbox.RepoFile("plugins"))
                .Select(d => Path.Combine(d, "plugin.json")).Where(File.Exists)
                .Single(p => (string?)JsonNode.Parse(File.ReadAllText(p))!["id"] == id);
            Assert.Equal((string?)JsonNode.Parse(File.ReadAllText(folder))!["description"], description);
        }
    }

    [Fact]
    public void TheRules_CatchWhatTheyAreFor()
    {
        Assert.Throws<Xunit.Sdk.TrueException>(() => AssertPlain(new string('a', 101), MaxDescription, "long"));
        Assert.ThrowsAny<Exception>(() => AssertPlain("Loops a part " + EmDash + " of a song.", MaxDescription, "dash"));
        Assert.Throws<Xunit.Sdk.FalseException>(() => AssertPlain("Loops a part of a song. By Someone.", MaxDescription, "by-line"));
        AssertPlain("Spin a reel of your albums and let it pick what to play.", MaxDescription, "ok");
    }
}
