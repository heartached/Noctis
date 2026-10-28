using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Noctis.PluginCheck;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The community plugin submission check (tools/Noctis.PluginCheck, run by
/// .github/workflows/plugin-submission.yml): reading the issue form, the zip checks done with
/// the app's own rules, the red-flag scan, the plugins/index.json entry and the report. No
/// network: the GitHub API answers are passed in as <see cref="RemoteFacts"/>.
/// </summary>
public class PluginSubmissionCheckTests : IDisposable
{
    private readonly PluginSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private const string Id = "dev.someone.ring";
    private const string Repo = "https://github.com/someone/noctis-ring";
    private const string Download = Repo + "/releases/download/v1.0.0/Ring-1.0.0.zip";

    /// <summary>A real, harmless managed DLL: the Pulse Ring sample.</summary>
    private static byte[] SampleDll => File.ReadAllBytes(typeof(Noctis.SamplePlugin.PulseRingPlugin).Assembly.Location);

    private string Zip(string version = "1.0.0", string[]? permissions = null, byte[]? dll = null, params (string, byte[])[] extra)
    {
        var entries = new List<(string, byte[])>
        {
            ("plugin.json", PluginSandbox.Utf8(PluginSandbox.Manifest(id: Id, name: "Ring", version: version, apiVersion: "1.2",
                minAppVersion: "1.6.0", permissions: permissions, extra: "\"platforms\": [\"windows\", \"linux\"]"))),
            ("Test.Plugin.dll", dll ?? SampleDll),
        };
        entries.AddRange(extra);
        return _box.WriteZip($"ring-{Guid.NewGuid():N}.zip", entries.ToArray());
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>An issue body the way GitHub writes one for the form; <paramref name="over"/> replaces answers by label.</summary>
    private static string Body(string sha, params (FormField Field, string Value)[] over)
    {
        var answers = new Dictionary<FormField, string>
        {
            [SubmissionForm.Name] = "Ring",
            [SubmissionForm.Id] = Id,
            [SubmissionForm.Version] = "1.0.0",
            [SubmissionForm.Description] = "A ring that pulses with the beat.",
            [SubmissionForm.Author] = "Someone",
            [SubmissionForm.AuthorUrl] = "_No response_",
            [SubmissionForm.Source] = Repo,
            [SubmissionForm.License] = "MIT",
            [SubmissionForm.Download] = Download,
            [SubmissionForm.Sha256] = sha,
            [SubmissionForm.Type] = "Code plugin (dotnet)",
            [SubmissionForm.Platforms] = "Windows, Linux",
            [SubmissionForm.MinAppVersion] = "1.6.0",
            [SubmissionForm.Network] = "No, it never connects to the internet",
            [SubmissionForm.NetworkDetails] = "_No response_",
            [SubmissionForm.Files] = "No, only its own plugin folder",
            [SubmissionForm.FilesDetails] = "_No response_",
            [SubmissionForm.Notes] = "_No response_",
            [SubmissionForm.Agreements] = string.Join("\n", SubmissionForm.AgreementLabels.Select(l => "- [X] " + l)),
        };
        foreach (var (f, v) in over) answers[f] = v;
        return string.Concat(SubmissionForm.All.Select(f => $"### {f.Label}\n\n{answers[f]}\n\n"));
    }

    private static RemoteFacts GoodRemote(string zip)
        => new(new RepoFacts(true, false, false, false, "MIT"), new AssetFacts(true, true, false, new FileInfo(zip).Length, Sha(zip)), null);

    private static SubmissionResult Check(string zip, string? body = null, RemoteFacts? remote = null, string? index = null)
        => SubmissionCheck.Run(SubmissionForm.Parse(body ?? Body(Sha(zip))), zip, null,
            index ?? File.ReadAllText(PluginSandbox.RepoFile("plugins/index.json")), remote ?? GoodRemote(zip));

    private static IEnumerable<string> At(SubmissionResult r, CheckLevel level) => r.Items.Where(i => i.Level == level).Select(i => i.Text);

    // ── The form ──

    /// <summary>The checker reads the issue body by the form's labels: the template and
    /// SubmissionForm must name every field (and the answers it compares) the same way.</summary>
    [Fact]
    public void Template_AndChecker_AgreeOnEveryFieldLabelAndAnswer()
    {
        var yml = File.ReadAllText(PluginSandbox.RepoFile(".github/ISSUE_TEMPLATE/3-plugin-submission.yml")).ReplaceLineEndings("\n");
        foreach (var f in SubmissionForm.All)
            Assert.Matches($@"\n    id: {f.Id}\n    attributes:\n      label: {System.Text.RegularExpressions.Regex.Escape(f.Label)}\n", yml);
        foreach (var label in SubmissionForm.AgreementLabels)
            Assert.Contains("- label: " + label + "\n          required: true", yml);
        foreach (var option in new[] { "Code plugin (dotnet)", "Content pack (content)", "Windows", "macOS", "Linux", "Yes (explain below)" })
            Assert.Contains("- " + option + "\n", yml);
        Assert.Contains("labels: [\"plugin-submission\", \"needs-review\"]", yml);
    }

    [Fact]
    public void Form_ReadsAnswers_NoResponseIsEmpty_AndTickedBoxes()
    {
        var form = SubmissionForm.Parse(Body("ab", (SubmissionForm.Platforms, "Windows, macOS, Linux"),
            (SubmissionForm.Agreements, "- [X] " + SubmissionForm.AgreementLabels[0] + "\n- [ ] " + SubmissionForm.AgreementLabels[1])).Replace("\n", "\r\n"));

        Assert.Equal("Ring", form[SubmissionForm.Name]);
        Assert.Equal("", form[SubmissionForm.AuthorUrl]);
        Assert.Equal("dotnet", form.TypeValue);
        Assert.Equal(new[] { "windows", "macos", "linux" }, form.PlatformValues);
        Assert.False(form.IsYes(SubmissionForm.Network));
        Assert.True(form.IsTicked(SubmissionForm.AgreementLabels[0]));
        Assert.False(form.IsTicked(SubmissionForm.AgreementLabels[1]));
        Assert.False(form.IsTicked(SubmissionForm.AgreementLabels[2]));
    }

    [Fact]
    public void Form_AHeadingTypedInsideAnAnswer_DoesNotReplaceTheRealAnswer()
    {
        var form = SubmissionForm.Parse(Body("ab", (SubmissionForm.Notes, "### SHA-256\n\nffff\n\n### Version\n\n9.9.9")));
        Assert.Equal("ab", form[SubmissionForm.Sha256]);
        Assert.Equal("1.0.0", form[SubmissionForm.Version]);
    }

    // ── A good submission ──

    [Fact]
    public void GoodSubmission_IsListable_AndTheEntryIsWhatTheAppReads()
    {
        var zip = Zip();
        var r = Check(zip);

        Assert.Empty(At(r, CheckLevel.Problem));
        Assert.Empty(At(r, CheckLevel.RedFlag));
        Assert.True(r.CanBeListed);
        var e = r.Entry!;
        Assert.Equal(Id, (string?)e["id"]);
        Assert.Equal("Someone", (string?)e["author"]);
        Assert.Equal(Repo, (string?)e["homepage"]);
        Assert.Equal(Sha(zip), (string?)e["sha256"]);
        Assert.Equal(new FileInfo(zip).Length, (long?)e["size"]);
        Assert.Equal("1.6.0", (string?)e["minAppVersion"]);
        Assert.Equal(new[] { "windows", "linux" }, e["platforms"]!.AsArray().Select(n => (string?)n));
        Assert.Null(e["authorUrl"]);

        // In the real list next to the existing entries, the app's parser takes it with no warning.
        var index = PluginIndexFile.Upsert(File.ReadAllText(PluginSandbox.RepoFile("plugins/index.json")), e, out var replaced);
        Assert.False(replaced);
        var catalog = PluginCatalog.Parse(index);
        Assert.Empty(catalog.Warnings);
        var listed = Assert.Single(catalog.Plugins, p => p.Id == Id);
        Assert.Equal("Someone", listed.Author);
        Assert.Contains(catalog.Plugins, p => p.Id == "dev.noctis.plugins.mixxx");
    }

    [Fact]
    public void AuthorLink_IsKeptInTheEntry()
    {
        var zip = Zip();
        var r = Check(zip, Body(Sha(zip), (SubmissionForm.AuthorUrl, "https://github.com/someone")));
        Assert.Equal("https://github.com/someone", (string?)r.Entry!["authorUrl"]);
        Assert.True(r.CanBeListed);
    }

    // ── Problems ──

    [Fact]
    public void WrongSha_OrAManifestThatDiffersFromTheForm_BlocksListing()
    {
        var zip = Zip(version: "1.0.1");
        var r = Check(zip, Body(new string('a', 64)));

        Assert.False(r.CanBeListed);
        Assert.Contains(At(r, CheckLevel.Problem), t => t.StartsWith("SHA-256 doesn't match"));
        Assert.Contains(At(r, CheckLevel.Problem), t => t.StartsWith("plugin.json doesn't match the form") && t.Contains("version"));
    }

    [Fact]
    public void AZipTheAppWouldRefuse_IsAProblem_WithTheAppsOwnReason()
    {
        var zip = _box.WriteZip("bad.zip", ("plugin.json", PluginSandbox.Utf8(PluginSandbox.Manifest(id: Id, name: "Ring", apiVersion: "1.2"))));
        var r = Check(zip);
        Assert.False(r.CanBeListed);
        Assert.Contains(At(r, CheckLevel.Problem), t => t.StartsWith("Noctis would refuse this zip") && t.Contains("Test.Plugin.dll"));
    }

    [Fact]
    public void MissingAnswersAndUntickedAgreements_AreProblems()
    {
        var zip = Zip();
        var r = Check(zip, Body(Sha(zip), (SubmissionForm.License, "_No response_"),
            (SubmissionForm.Agreements, "- [ ] " + SubmissionForm.AgreementLabels[2])));
        Assert.Contains(At(r, CheckLevel.Problem), t => t.StartsWith("Not answered") && t.Contains("Licence"));
        Assert.Contains(At(r, CheckLevel.Problem), t => t.StartsWith("Not ticked"));
        Assert.False(r.CanBeListed);
    }

    [Fact]
    public void NetworkCode_WithoutTheNetworkPermission_OrDisclosure_IsAProblem()
    {
        // This test assembly references HttpClient (PluginCatalogTests), so it stands in for a plugin that goes online.
        var zip = Zip(dll: File.ReadAllBytes(typeof(PluginSubmissionCheckTests).Assembly.Location));
        var r = Check(zip);
        Assert.Contains(At(r, CheckLevel.Problem), t => t.Contains("doesn't declare the `network` permission"));
        Assert.Contains(At(r, CheckLevel.Problem), t => t.Contains("the form says it never connects"));

        // Declared in both places, with details: no longer a problem.
        var declared = Zip(permissions: new[] { "network" }, dll: File.ReadAllBytes(typeof(PluginSubmissionCheckTests).Assembly.Location));
        var ok = Check(declared, Body(Sha(declared), (SubmissionForm.Network, "Yes (explain below)"),
            (SubmissionForm.NetworkDetails, "api.example.com for lyrics")));
        Assert.DoesNotContain(At(ok, CheckLevel.Problem), t => t.Contains("network"));
    }

    [Fact]
    public void PrivateRepo_MissingRelease_AndAForeignDigest_AreProblems()
    {
        var zip = Zip();
        var r = Check(zip, remote: new RemoteFacts(new RepoFacts(false, false, false, false, null), new AssetFacts(false, false, false, null, null), null));
        Assert.Contains(At(r, CheckLevel.Problem), t => t.Contains("isn't public"));
        Assert.Contains(At(r, CheckLevel.Problem), t => t.StartsWith("No public release"));

        var digest = Check(zip, remote: new RemoteFacts(new RepoFacts(true, false, false, false, "MIT"),
            new AssetFacts(true, true, false, new FileInfo(zip).Length, new string('b', 64)), null));
        Assert.Contains(At(digest, CheckLevel.Problem), t => t.StartsWith("GitHub's own SHA-256"));
    }

    [Fact]
    public void LinksThatAreNotGitHubReleaseAssets_AreProblems()
    {
        var zip = Zip();
        var r = Check(zip, Body(Sha(zip), (SubmissionForm.Download, "https://example.com/Ring.zip"), (SubmissionForm.Source, "https://gitlab.com/x/y")));
        Assert.Contains(At(r, CheckLevel.Problem), t => t.StartsWith("Release zip"));
        Assert.Contains(At(r, CheckLevel.Problem), t => t.StartsWith("Source code"));
        Assert.Null(r.Entry);
    }

    // ── Red flags (a reviewer looks; listing is not blocked) ──

    [Fact]
    public void ProgramsScriptsNativeBinariesAndArchives_AreRedFlags()
    {
        var zip = Zip(extra: new[]
        {
            ("tools/update.exe", new byte[] { 0x4D, 0x5A, 0, 0 }),
            ("install.ps1", PluginSandbox.Utf8("Write-Host hi")),
            ("run", PluginSandbox.Utf8("#!/bin/sh\necho hi")),
            ("libfoo.so", new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }),
            ("native.dll", new byte[] { 0x4D, 0x5A, 0x90, 0 }),
            ("more.dat", new byte[] { (byte)'P', (byte)'K', 3, 4 }),
            ("Avalonia.Base.dll", SampleDll),
        });
        var r = Check(zip);

        var flags = At(r, CheckLevel.RedFlag).ToList();
        Assert.Contains(flags, t => t.StartsWith("Programs") && t.Contains("tools/update.exe"));
        Assert.Contains(flags, t => t.StartsWith("Scripts") && t.Contains("install.ps1") && t.Contains("`run`"));
        Assert.Contains(flags, t => t.StartsWith("Native") && t.Contains("libfoo.so") && t.Contains("native.dll"));
        Assert.Contains(flags, t => t.StartsWith("Archives") && t.Contains("more.dat"));
        Assert.Contains(At(r, CheckLevel.Note), t => t.Contains("Avalonia.Base.dll"));
        Assert.True(r.CanBeListed);
    }

    [Fact]
    public void ObfuscatorMarkers_AndUnreadableNames_AreRedFlags()
    {
        var zip = Zip(dll: EmitAssembly(obfuscated: true));
        var flags = At(Check(zip), CheckLevel.RedFlag).ToList();
        Assert.Contains(flags, t => t.Contains("looks obfuscated") && t.Contains("ConfusedByAttribute") && t.Contains("unreadable"));

        var clean = PackageScanner.ScanPe("x.dll", EmitAssembly(obfuscated: false))!;
        Assert.Empty(clean.ObfuscationMarkers);
        Assert.Contains(ApiUse.Process, clean.Uses);
    }

    /// <summary>A small assembly built in memory: with obfuscator fingerprints, or a plain one
    /// whose only notable reference is System.Diagnostics.Process.</summary>
    private static byte[] EmitAssembly(bool obfuscated)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName("Emitted"), typeof(object).Assembly);
        var mb = ab.DefineDynamicModule("Emitted");
        if (obfuscated)
        {
            mb.DefineType("ConfusedByAttribute", TypeAttributes.Public, typeof(Attribute)).CreateType();
            for (var i = 0; i < 30; i++) mb.DefineType(new string('​', i + 1), TypeAttributes.NotPublic).CreateType();
        }
        else
        {
            var t = mb.DefineType("Plain", TypeAttributes.Public);
            var m = t.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
            var il = m.GetILGenerator();
            il.Emit(OpCodes.Ldstr, "x");
            il.Emit(OpCodes.Call, typeof(System.Diagnostics.Process).GetMethod(nameof(System.Diagnostics.Process.Start), new[] { typeof(string) })!);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
            t.CreateType();
        }
        using var ms = new MemoryStream();
        ab.Save(ms);
        return ms.ToArray();
    }

    // ── Against the current list ──

    [Fact]
    public void AnUpdate_NeedsAHigherVersion_AndTheSameRepository()
    {
        var zip = Zip();
        var listed = PluginCatalogTests.Index(PluginCatalogTests.Entry(id: Id, name: "Ring", version: "1.0.0",
            extra: ("homepage", "https://github.com/someone-else/ring")));
        var r = Check(zip, index: listed);
        Assert.Contains(At(r, CheckLevel.Problem), t => t.Contains("is already listed"));
        Assert.Contains(At(r, CheckLevel.RedFlag), t => t.Contains("Only its author can update it"));

        var upgrade = Check(zip, index: PluginCatalogTests.Index(PluginCatalogTests.Entry(id: Id, name: "Ring", version: "0.9.0",
            extra: ("homepage", Repo))));
        Assert.Empty(At(upgrade, CheckLevel.Problem));
        Assert.DoesNotContain(At(upgrade, CheckLevel.RedFlag), t => t.Contains("Only its author"));
        Assert.Contains(At(upgrade, CheckLevel.Pass), t => t.StartsWith("Update of a listed plugin"));
    }

    [Fact]
    public void ReservedIds_AreARedFlag()
    {
        var zip = Zip();
        var form = Body(Sha(zip), (SubmissionForm.Id, "dev.noctis.plugins.ring"));
        Assert.Contains(At(Check(zip, form), CheckLevel.RedFlag), t => t.Contains("dev.noctis."));
    }

    // ── The report and the file ──

    [Fact]
    public void Report_ShowsSubmittedTextOnlyAsCode_AndTheEntryCannotBreakItsFence()
    {
        var zip = Zip();
        var evil = "Hi @everyone ```\n## <img src=x> [x](https://evil.example)";
        var r = Check(zip, Body(Sha(zip), (SubmissionForm.Description, evil), (SubmissionForm.Author, "`@someone`")));
        var report = SubmissionReport.Render(SubmissionForm.Parse(Body(Sha(zip))), r);

        Assert.StartsWith(SubmissionReport.Marker + "\n", report);
        Assert.Equal(3, report.Split("```").Length); // one fence pair: the entry
        var json = report.Split("```json\n")[1].Split("\n```")[0];
        Assert.Equal(evil.ReplaceLineEndings(" "), (string?)JsonNode.Parse(json)!["description"]);
        Assert.DoesNotContain("\n## <img", report);
        Assert.Contains("`'@someone'`", report);
    }

    [Fact]
    public void IndexFile_KeepsTheRepositoryLayout_AndReplacesInPlace()
    {
        var text = File.ReadAllText(PluginSandbox.RepoFile("plugins/index.json")).ReplaceLineEndings("\n");
        Assert.Equal(text, PluginIndexFile.Format((JsonObject)JsonNode.Parse(text)!));

        var entry = (JsonObject)JsonNode.Parse(text)!["plugins"]![0]!.DeepClone();
        entry["version"] = "9.0.0";
        var updated = PluginIndexFile.Upsert(text, entry, out var replaced);
        Assert.True(replaced);
        Assert.Equal(text.Replace("\"version\": \"1.0.1\"", "\"version\": \"9.0.0\""), updated);
    }

    [Theory]
    [InlineData("https://github.com/someone/noctis-ring", true)]
    [InlineData("https://github.com/someone/noctis-ring.git", true)]
    [InlineData("https://github.com/someone/noctis-ring/", true)]
    [InlineData("http://github.com/someone/noctis-ring", false)]
    [InlineData("https://github.com/someone/noctis-ring/tree/main", false)]
    [InlineData("https://github.com.evil.example/someone/ring", false)]
    [InlineData("https://github.com/someone/..", false)]
    public void RepoLinks(string url, bool ok) => Assert.Equal(ok, GitHubLinks.TryParseRepo(url, out _, out _));

    [Theory]
    [InlineData(Download, true)]
    [InlineData("https://github.com/someone/ring/releases/download/v1.0.0/Ring%201.0.zip", true)]
    [InlineData("https://github.com/someone/ring/releases/latest/download/Ring.zip", false)]
    [InlineData("https://objects.githubusercontent.com/someone/ring/Ring.zip", false)]
    [InlineData("https://github.com/someone/ring/releases/download/v1.0.0/Ring.zip?x=1", false)]
    public void AssetLinks(string url, bool ok) => Assert.Equal(ok, GitHubLinks.TryParseAsset(url, out _, out _, out _, out _));
}
