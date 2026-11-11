using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #230: one data folder on a music drive, opened from two computers in turn — and
/// sometimes with an older Noctis on one of them. Each "computer" here is its own
/// <see cref="SecretProtector"/>: it can only read back what it protected itself, as DPAPI does.
/// </summary>
public class SharedDataFolderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class Computer : SecretProtector
    {
        private readonly string _name;
        public Computer(string name) => _name = name;
        public override string ComputerId => "pc-" + _name;
        public override string Protect(string value) =>
            string.IsNullOrEmpty(value) || value.StartsWith("enc:dpapi:", StringComparison.Ordinal)
                ? value
                : "enc:dpapi:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(_name + "|" + Guid.NewGuid().ToString("N") + "|" + value));
        public override string Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value) || !value.StartsWith("enc:dpapi:", StringComparison.Ordinal)) return value;
            try
            {
                var parts = Encoding.UTF8.GetString(Convert.FromBase64String(value["enc:dpapi:".Length..])).Split('|', 3);
                return parts.Length == 3 && parts[0] == _name ? parts[2] : string.Empty;
            }
            catch (FormatException) { return string.Empty; }
        }
    }

    private static readonly Computer A = new("A"), B = new("B");

    private PersistenceService On(Computer pc, string version = "1.7.0") =>
        new(_root) { Protector = pc, RunningVersion = version };

    private string SettingsPath => Path.Combine(_root, "settings.json");

    /// <summary>A launch of this build: settings are read, then saved (every start and close saves).</summary>
    private async Task<AppSettings> OpenAsync(Computer pc, Action<AppSettings>? change = null)
    {
        var svc = On(pc);
        var s = await svc.LoadSettingsAsync();
        change?.Invoke(s);
        await svc.SaveSettingsAsync(s);
        return s;
    }

    /// <summary>
    /// A launch of Noctis 1.6.7 / 1.6.8 (v1.6.8 PersistenceService.LoadSettingsAsync/SaveSettingsAsync):
    /// the session key is read with UnprotectSecret (another computer's copy reads as ""), the
    /// file is rewritten from its own AppSettings (members it does not know are gone) and the
    /// key protected again — "" stays "".
    /// </summary>
    private void OpenWithOldBuild(Computer pc, string? signInAs = null, params string[] knownToOldBuildToo)
    {
        var obj = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        var plain = pc.Unprotect(obj["lastFmSessionKey"]?.GetValue<string>() ?? "");
        if (signInAs != null) plain = signInAs;
        var rewritten = new JsonObject();
        foreach (var (key, value) in obj)
            if (OldBuildKeys.Contains(key) || knownToOldBuildToo.Contains(key))
                rewritten[key] = value?.DeepClone();
        rewritten["lastFmSessionKey"] = pc.Protect(plain);
        File.WriteAllText(SettingsPath, rewritten.ToJsonString());
    }

    private bool OldBuildIsSignedIn(Computer pc) =>
        pc.Unprotect(JsonNode.Parse(File.ReadAllText(SettingsPath))!["lastFmSessionKey"]?.GetValue<string>() ?? "").Length > 0;

    private static readonly HashSet<string> OldBuildKeys = new(StringComparer.Ordinal)
    {
        "lastFmSessionKey", "lastFmUsername", "lastFmScrobblingEnabled", "volume", "windowX", "uiScalePercent",
    };

    // ── Part 1: sign-ins ──

    [Fact]
    public async Task SignedInOnceOnEachComputer_BothStaySignedIn_AcrossManySwitches()
    {
        await OpenAsync(A, s => { s.LastFmSessionKey = "key-A"; s.LastFmUsername = "listener"; });
        var onB = await OpenAsync(B);
        Assert.Equal("", onB.LastFmSessionKey); // never signed in on B: B cannot read A's copy
        Assert.True(onB.HasOtherComputerSecret(PersistenceService.LastFmSessionKeyField));
        await OpenAsync(B, s => s.LastFmSessionKey = "key-B");

        for (var i = 0; i < 6; i++)
        {
            Assert.Equal("key-A", (await OpenAsync(A)).LastFmSessionKey);
            Assert.Equal("key-B", (await OpenAsync(B)).LastFmSessionKey);
        }
        // The shared, unprotected part stays shared.
        Assert.Equal("listener", (await On(A).LoadSettingsAsync()).LastFmUsername);
        Assert.DoesNotContain("key-A", File.ReadAllText(Path.Combine(_root, "computers.json")));
    }

    [Fact]
    public async Task OlderBuildOnTheOtherComputer_CannotSignThisOneOut()
    {
        // A runs this build and signs in; B still runs 1.6.8.
        await OpenAsync(A, s => s.LastFmSessionKey = "key-A");
        OpenWithOldBuild(B);                       // B: A's copy reads "", written back as ""
        Assert.Equal("", JsonNode.Parse(File.ReadAllText(SettingsPath))!["lastFmSessionKey"]!.GetValue<string>());
        Assert.Equal("key-A", (await OpenAsync(A)).LastFmSessionKey);

        // B signs in once with its old build; from then on both stay signed in.
        OpenWithOldBuild(B, signInAs: "key-B");
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal("key-A", (await OpenAsync(A)).LastFmSessionKey);
            // A leaves B's copy in the field the old build reads, its own is in computers.json.
            Assert.True(OldBuildIsSignedIn(B));
            OpenWithOldBuild(B);
        }
    }

    [Fact]
    public async Task Build169OnTheOtherComputer_BothStaySignedIn()
    {
        await OpenAsync(A, s => s.LastFmSessionKey = "key-A");
        // 1.6.9 on B: reads the field and otherComputerSecrets, keeps what it cannot read,
        // writes its own copy into the field (v1.6.9 PersistenceService.ProtectField).
        void OpenWith169(Computer pc, string? signInAs = null)
        {
            var obj = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
            var candidates = new List<string>();
            if (obj["lastFmSessionKey"]?.GetValue<string>() is { Length: > 0 } f) candidates.Add(f);
            if (obj["otherComputerSecrets"]?["lastFmSessionKey"] is JsonArray arr)
                candidates.AddRange(arr.Select(n => n!.GetValue<string>()));
            var plain = candidates.Select(pc.Unprotect).FirstOrDefault(p => p.Length > 0) ?? "";
            var kept = candidates.Where(c => pc.Unprotect(c).Length == 0).ToList();
            if (signInAs != null) plain = signInAs;
            var rewritten = new JsonObject();
            foreach (var (key, value) in obj)
                if (OldBuildKeys.Contains(key)) rewritten[key] = value?.DeepClone();
            if (plain.Length == 0 && kept.Count > 0) { rewritten["lastFmSessionKey"] = kept[0]; kept.RemoveAt(0); }
            else rewritten["lastFmSessionKey"] = pc.Protect(plain);
            if (kept.Count > 0)
                rewritten["otherComputerSecrets"] = new JsonObject { ["lastFmSessionKey"] = new JsonArray(kept.Select(k => (JsonNode?)k).ToArray()) };
            File.WriteAllText(SettingsPath, rewritten.ToJsonString());
        }

        OpenWith169(B, signInAs: "key-B");
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal("key-A", (await OpenAsync(A)).LastFmSessionKey);
            Assert.True(OldBuildIsSignedIn(B));
            OpenWith169(B);
        }
    }

    [Fact]
    public async Task SigningOutOnOneComputer_LeavesTheOtherSignedIn()
    {
        await OpenAsync(A, s => s.LastFmSessionKey = "key-A");
        await OpenAsync(B, s => s.LastFmSessionKey = "key-B");
        await OpenAsync(A, s => s.LastFmSessionKey = "");

        Assert.Equal("", (await OpenAsync(A)).LastFmSessionKey);
        Assert.Equal("key-B", (await OpenAsync(B)).LastFmSessionKey);
        Assert.Equal("", (await OpenAsync(A)).LastFmSessionKey);
    }

    [Fact]
    public async Task ListenBrainzAndServerPasswords_ArePerComputerToo()
    {
        var connId = Guid.NewGuid();
        await OpenAsync(A, s =>
        {
            s.ListenBrainzToken = "lb-A";
            s.SourceConnections.Add(new SourceConnection { Id = connId, Type = SourceType.Navidrome, TokenOrPassword = "pw-A" });
        });
        await OpenAsync(B, s => { s.ListenBrainzToken = "lb-B"; s.SourceConnections[0].TokenOrPassword = "pw-B"; });

        var onA = await OpenAsync(A);
        Assert.Equal("lb-A", onA.ListenBrainzToken);
        Assert.Equal("pw-A", onA.SourceConnections[0].TokenOrPassword);
        var onB = await OpenAsync(B);
        Assert.Equal("lb-B", onB.ListenBrainzToken);
        Assert.Equal("pw-B", onB.SourceConnections[0].TokenOrPassword);
    }

    [Fact]
    public void ServerCertificate_OfAnotherComputer_IsKept_AndThisOneKeepsItsOwn()
    {
        if (!OperatingSystem.IsWindows()) return; // the password file is only protected on Windows

        var dir = Path.Combine(_root, "server");
        Directory.CreateDirectory(dir);
        var otherPfx = new byte[] { 1, 2, 3 };
        const string otherKey = "enc:dpapi:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        File.WriteAllBytes(Path.Combine(dir, Noctis.Services.Server.ServerCertificate.FileName), otherPfx);
        File.WriteAllText(Path.Combine(dir, Noctis.Services.Server.ServerCertificate.KeyFileName), otherKey);

        using var first = Noctis.Services.Server.ServerCertificate.LoadOrCreate(dir);
        using var second = Noctis.Services.Server.ServerCertificate.LoadOrCreate(dir);

        // Same certificate on every start here (phones paired with this computer stay paired) …
        Assert.Equal(first.Thumbprint, second.Thumbprint);
        // … and the other computer's pair is left exactly as it was.
        Assert.Equal(otherPfx, File.ReadAllBytes(Path.Combine(dir, Noctis.Services.Server.ServerCertificate.FileName)));
        Assert.Equal(otherKey, File.ReadAllText(Path.Combine(dir, Noctis.Services.Server.ServerCertificate.KeyFileName)));
    }

    // ── Part 3: settings that belong to the computer ──

    [Fact]
    public async Task WindowPlaceOutputDeviceAndInterfaceSize_ArePerComputer()
    {
        await OpenAsync(A, s => { s.WindowX = 10; s.AudioOutputDeviceId = "dev-A"; s.UiScalePercent = 150; s.Volume = 40; s.FfmpegPath = @"C:\Tools\ffmpeg.exe"; s.MultichannelOutput = true; });
        await OpenAsync(B, s => { s.WindowX = 2000; s.AudioOutputDeviceId = "dev-B"; s.UiScalePercent = 100; s.MiniPlayerX = 5; });

        var onA = await On(A).LoadSettingsAsync();
        Assert.Equal(10, onA.WindowX);
        Assert.Equal("dev-A", onA.AudioOutputDeviceId);
        Assert.Equal(150, onA.UiScalePercent);
        Assert.Equal(@"C:\Tools\ffmpeg.exe", onA.FfmpegPath);
        Assert.True(onA.MultichannelOutput);
        Assert.Null(onA.MiniPlayerX);           // B's mini player place is B's
        Assert.Equal(40, onA.Volume);           // shared settings stay shared

        var onB = await On(B).LoadSettingsAsync();
        Assert.Equal(2000, onB.WindowX);
        Assert.Equal("dev-B", onB.AudioOutputDeviceId);
        Assert.Equal(5, onB.MiniPlayerX);
        Assert.Equal(@"C:\Tools\ffmpeg.exe", onB.FfmpegPath); // B had none of its own yet: the shared value
        Assert.True(onB.MultichannelOutput);

        // settings.json still has the last saved values, for older Noctis and new computers.
        Assert.Equal(2000, JsonNode.Parse(File.ReadAllText(SettingsPath))!["windowX"]!.GetValue<double>());
    }

    [Fact]
    public async Task LaunchReaders_PreferThisComputersSlot()
    {
        var svc = new PersistenceService(_root); // this machine's real identity, as Program.Main uses
        var s = await svc.LoadSettingsAsync();
        s.UiScalePercent = 175;
        s.AudioOutputDeviceId = "this-pc";
        await svc.SaveSettingsAsync(s);
        await OpenAsync(B, x => { x.UiScalePercent = 100; x.AudioOutputDeviceId = "other-pc"; });

        Assert.Equal(175, PersistenceService.ReadIntSettingAtLaunch(_root, nameof(AppSettings.UiScalePercent), 100));
        Assert.Equal("this-pc", PersistenceService.ReadStringSettingAtLaunch(_root, nameof(AppSettings.AudioOutputDeviceId)));
    }

    [Fact]
    public async Task ComputersFile_KeepsOtherSlotsAndUnknownMembers()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "computers.json"),
            """{"future":{"x":1},"computers":{"pc-Z":{"secrets":{"lastFmSessionKey":"enc:dpapi:zzz"},"later":[1,2]}}}""");

        await OpenAsync(A, s => s.LastFmSessionKey = "key-A");

        var file = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "computers.json")))!;
        Assert.Equal(1, file["future"]!["x"]!.GetValue<int>());
        Assert.Equal("enc:dpapi:zzz", file["computers"]!["pc-Z"]!["secrets"]!["lastFmSessionKey"]!.GetValue<string>());
        Assert.Equal(2, file["computers"]!["pc-Z"]!["later"]!.AsArray().Count);
        Assert.NotNull(file["computers"]!["pc-A"]!["secrets"]!["lastFmSessionKey"]);
    }

    // ── Part 2: settings a newer Noctis saved ──

    private const string Unknowns = """
        "futureFlag": true,
        "futureNumber": 12.5,
        "futureText": "hello",
        "futureNull": null,
        "futureList": [1, "two", {"three": [3]}],
        "futureObject": {"mode": "Wide", "nested": {"deep": [true, false], "n": 7}}
        """;

    [Fact]
    public async Task NewerSettingsFile_RoundTripsUnknownMembersAtEveryLevel()
    {
        Directory.CreateDirectory(_root);
        var connId = Guid.NewGuid();
        await File.WriteAllTextAsync(SettingsPath, $$"""
        {
          "volume": 33,
          {{Unknowns}},
          "customThemes": [ { "id": "t1", "name": "Mine", {{Unknowns}} } ],
          "folderRules": [ { "path": "D:\\Music", "include": true, {{Unknowns}} } ],
          "sourceConnections": [ { "id": "{{connId}}", "name": "navi", "type": "Navidrome", {{Unknowns}} } ],
          "parametricEqBands": [ { "frequencyHz": 100, "gainDb": 1, "q": 1, {{Unknowns}} } ],
          "userEqPresets": [ { "name": "P", "bands": [ { "frequencyHz": 60, {{Unknowns}} } ], {{Unknowns}} } ],
          "writtenByVersion": "1.8.0"
        }
        """);

        var svc = On(A);
        var loaded = await svc.LoadSettingsAsync();
        Assert.Equal(33, loaded.Volume);
        Assert.Equal("1.8.0", loaded.OpenedFromNewerVersion);
        loaded.Volume = 34;
        await svc.SaveSettingsAsync(loaded);
        // And once more through a fresh load, as the next launch would.
        await svc.SaveSettingsAsync(await svc.LoadSettingsAsync());

        var raw = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath))!;
        Assert.Equal(34, raw["volume"]!.GetValue<int>());
        AssertUnknowns(raw);
        AssertUnknowns(raw["customThemes"]![0]!);
        AssertUnknowns(raw["folderRules"]![0]!);
        AssertUnknowns(raw["sourceConnections"]![0]!);
        AssertUnknowns(raw["parametricEqBands"]![0]!);
        AssertUnknowns(raw["userEqPresets"]![0]!);
        AssertUnknowns(raw["userEqPresets"]![0]!["bands"]![0]!);
        // An older build never lowers the stamp, so the newer one is still recognised later.
        Assert.Equal("1.8.0", raw["writtenByVersion"]!.GetValue<string>());
    }

    private static void AssertUnknowns(JsonNode node)
    {
        var obj = node.AsObject();
        Assert.True(obj["futureFlag"]!.GetValue<bool>());
        Assert.Equal(12.5, obj["futureNumber"]!.GetValue<double>());
        Assert.Equal("hello", obj["futureText"]!.GetValue<string>());
        Assert.True(obj.ContainsKey("futureNull"));
        Assert.Null(obj["futureNull"]);
        Assert.Equal("""[1,"two",{"three":[3]}]""", obj["futureList"]!.ToJsonString());
        Assert.Equal("""{"mode":"Wide","nested":{"deep":[true,false],"n":7}}""", obj["futureObject"]!.ToJsonString());
    }

    [Fact]
    public void UnknownMembers_SurviveTheViewModelsListRebuilds()
    {
        var before = new List<CustomThemeDefinition>
        {
            new() { Id = "t1", ExtensionData = new() { ["futureFlag"] = JsonDocument.Parse("true").RootElement.Clone() } },
        };
        var rebuilt = JsonExtensible.CarryOver(before, new List<CustomThemeDefinition> { new() { Id = "t1" }, new() { Id = "t2" } }, t => t.Id);
        Assert.True(rebuilt[0].ExtensionData!["futureFlag"].GetBoolean());
        Assert.Null(rebuilt[1].ExtensionData);
    }

    [Fact]
    public async Task LibraryPlaylistsAndQueue_RoundTripUnknownMembers()
    {
        Directory.CreateDirectory(_root);
        var trackId = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(_root, "library.json"),
            $$"""[ { "id": "{{trackId}}", "title": "T", "filePath": "x.mp3", {{Unknowns}} } ]""");
        await File.WriteAllTextAsync(Path.Combine(_root, "playlists.json"),
            $$"""[ { "name": "P", "isSmart": true, "rules": [ { "field": "Title", "operator": "Contains", "value": "a", {{Unknowns}} } ], {{Unknowns}} } ]""");
        await File.WriteAllTextAsync(Path.Combine(_root, "queue.json"),
            $$"""{ "currentTrackId": "{{trackId}}", {{Unknowns}} }""");

        var svc = On(A);
        await svc.SaveLibraryAsync((await svc.LoadLibraryAsync())!);
        await svc.SavePlaylistsAsync(await svc.LoadPlaylistsAsync());
        await svc.SaveQueueStateAsync((await svc.LoadQueueStateAsync())!);

        AssertUnknowns(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "library.json")))![0]!);
        var playlists = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "playlists.json")))!;
        AssertUnknowns(playlists[0]!);
        AssertUnknowns(playlists[0]!["rules"]![0]!);
        AssertUnknowns(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "queue.json")))!);
    }

    [Fact]
    public async Task UnreadablePlaylists_AreNeverOverwrittenWithAnEmptyList()
    {
        Directory.CreateDirectory(_root);
        // A rule field a newer Noctis added: this build cannot parse the file.
        const string newer = """[ { "name": "Mine", "rules": [ { "field": "SomethingNew", "operator": "Contains" } ] } ]""";
        await File.WriteAllTextAsync(Path.Combine(_root, "playlists.json"), newer);

        var svc = On(A);
        Assert.Empty(await svc.LoadPlaylistsAsync());
        await svc.SavePlaylistsAsync(new List<Playlist>());

        Assert.Equal(newer, await File.ReadAllTextAsync(Path.Combine(_root, "playlists.json")));
    }

    [Fact]
    public async Task VersionStamp_IsTheNewestWriter_AndAnOlderReaderIsTold()
    {
        await On(A, "1.7.0").SaveSettingsAsync(new AppSettings());
        Assert.Equal("1.7.0", JsonNode.Parse(File.ReadAllText(SettingsPath))!["writtenByVersion"]!.GetValue<string>());
        Assert.Null((await On(A, "1.7.0").LoadSettingsAsync()).OpenedFromNewerVersion);

        await On(B, "1.7.2").SaveSettingsAsync(await On(B, "1.7.2").LoadSettingsAsync());
        var older = On(A, "1.7.0");
        var s = await older.LoadSettingsAsync();
        Assert.Equal("1.7.2", s.OpenedFromNewerVersion);
        await older.SaveSettingsAsync(s);
        Assert.Equal("1.7.2", JsonNode.Parse(File.ReadAllText(SettingsPath))!["writtenByVersion"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("1.7.1", "1.7.0", true)]
    [InlineData("1.7.0", "1.7.0", false)]
    [InlineData("1.6.9", "1.7.0", false)]
    [InlineData("1.10.0", "1.9.9", true)]
    [InlineData("1.7.1-beta+abc", "1.7.0", true)]
    [InlineData("1.7.0", "", true)]
    [InlineData("", "1.7.0", false)]
    [InlineData("garbage", "1.7.0", false)]
    public void IsNewerVersion_ComparesNumerically(string candidate, string baseline, bool expected) =>
        Assert.Equal(expected, PersistenceService.IsNewerVersion(candidate, baseline));
}
