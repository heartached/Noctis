using System.Text.Json;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Exercises the REAL PersistenceService against a temp data root — the fake
/// used elsewhere never touches the atomic-write, crash-recovery or secret-
/// protection paths, which are exactly the data-loss-critical ones.
/// </summary>
public class PersistenceServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    private PersistenceService Create() => new(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task Settings_RoundTrip_PreservesValues()
    {
        var svc = Create();
        await svc.SaveSettingsAsync(new AppSettings
        {
            Volume = 42,
            LastFmUsername = "listener"
        });

        var loaded = await svc.LoadSettingsAsync();
        Assert.Equal(42, loaded.Volume);
        Assert.Equal("listener", loaded.LastFmUsername);
    }

    [Fact]
    public async Task Save_LeavesNoTempFileBehind()
    {
        var svc = Create();
        await svc.SaveSettingsAsync(new AppSettings());
        await svc.SaveLibraryAsync(new List<Track> { new() { Title = "T", FilePath = "x.mp3" } });

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Load_RecoversFromTempFile_WhenMainFileMissing()
    {
        // A crash between serializing the .tmp and the atomic rename leaves only
        // the temp file; load must recover it instead of resetting to defaults.
        var svc = Create();
        var json = JsonSerializer.Serialize(
            new AppSettings { Volume = 77 },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json.tmp"), json);

        var loaded = await svc.LoadSettingsAsync();
        Assert.Equal(77, loaded.Volume);
    }

    [Fact]
    public async Task ConcurrentLibrarySaves_NeverCorruptTheFile()
    {
        // Overlapping saves used to race on the shared library.json.tmp
        // (sharing violation or truncated file). With the per-path write gate
        // every save serializes; the final file must always parse.
        var svc = Create();
        var saves = Enumerable.Range(0, 12).Select(i =>
            svc.SaveLibraryAsync(Enumerable.Range(0, 50)
                .Select(n => new Track
                {
                    Id = Guid.NewGuid(),
                    Title = $"t{i}-{n}",
                    FilePath = $"lib{i}/{n}.mp3"
                })
                .ToList()));

        await Task.WhenAll(saves);

        var loaded = await svc.LoadLibraryAsync();
        Assert.NotNull(loaded);
        Assert.Equal(50, loaded!.Count);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Secrets_AreEncryptedAtRest_AndRoundTrip()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI is Windows-only by design

        var svc = Create();
        await svc.SaveSettingsAsync(new AppSettings
        {
            LastFmSessionKey = "plain-lastfm-session",
            SourceConnections =
            {
                new SourceConnection
                {
                    Name = "navi",
                    Username = "u",
                    TokenOrPassword = "plain-navidrome-pass"
                }
            }
        });

        var raw = await File.ReadAllTextAsync(Path.Combine(_root, "settings.json"));
        Assert.DoesNotContain("plain-lastfm-session", raw);
        Assert.DoesNotContain("plain-navidrome-pass", raw);
        Assert.Contains("enc:dpapi:", raw);

        var loaded = await svc.LoadSettingsAsync();
        Assert.Equal("plain-lastfm-session", loaded.LastFmSessionKey);
        Assert.Equal("plain-navidrome-pass", loaded.SourceConnections[0].TokenOrPassword);
    }

    [Fact]
    public async Task CorruptSettings_AreQuarantined_AndRecoveredFromBackup()
    {
        var svc = Create();
        // Two saves: the second rolls the first (good) file into settings.json.bak.
        await svc.SaveSettingsAsync(new AppSettings { Volume = 33, ProfileName = "before" });
        await svc.SaveSettingsAsync(new AppSettings { Volume = 33, ProfileName = "before" });

        var settingsPath = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(settingsPath, "{ this is not json");

        var loaded = await svc.LoadSettingsAsync();

        Assert.True(svc.SettingsLoadFailed);
        Assert.Equal("before", loaded.ProfileName);       // came back from the .bak
        Assert.Equal(33, loaded.Volume);
        Assert.NotEmpty(Directory.GetFiles(_root, "settings.json.corrupt-*"));
        Assert.Contains("this is not json",
            await File.ReadAllTextAsync(Directory.GetFiles(_root, "settings.json.corrupt-*")[0]));
    }

    [Fact]
    public async Task CorruptSettings_WithNoBackup_FallBackToDefaultsWithoutDestroyingTheFile()
    {
        var svc = Create();
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), "<<<not json>>>");

        var loaded = await svc.LoadSettingsAsync();

        Assert.True(svc.SettingsLoadFailed);
        Assert.Equal(new AppSettings().Volume, loaded.Volume);
        Assert.Single(Directory.GetFiles(_root, "settings.json.corrupt-*"));
    }

    [Fact]
    public async Task DefaultSettings_DecideTheCommunityPluginsSwitch_AsRestricted()
    {
        // Null means "settings.json predates the switch" to the plugin host, which then
        // approves and starts every installed plugin. Defaults must never look like that.
        var svc = Create();
        Assert.False((await svc.LoadSettingsAsync()).CommunityPluginsEnabled); // no file

        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), "<<<not json>>>", TestContext.Current.CancellationToken);
        Assert.False((await Create().LoadSettingsAsync()).CommunityPluginsEnabled); // corrupt, no backup
    }

    [Fact]
    public async Task SettingsFromBeforeTheCommunityPluginsSwitch_StayUndecided()
    {
        var svc = Create();
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), """{"volume":50}""", TestContext.Current.CancellationToken);

        Assert.Null((await svc.LoadSettingsAsync()).CommunityPluginsEnabled);
    }

    /// <summary>CI 10-18 (InboxPageTests): Windows refuses to rename over a file a reader has
    /// open, so a save landing while anything was reading settings.json threw "Access to the
    /// path is denied" and the setting was lost. The save now waits the read out.</summary>
    [Fact]
    public async Task Save_WhileTheFileIsBeingRead_LandsOnceTheReadEnds()
    {
        var svc = Create();
        await svc.SaveSettingsAsync(new AppSettings { Volume = 10 });

        Task save;
        // Opened the way every load opens it, held a little longer than a read takes.
        using (new FileStream(Path.Combine(_root, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            save = svc.SaveSettingsAsync(new AppSettings { Volume = 20 });
            await Task.Delay(150, TestContext.Current.CancellationToken);
        }
        await save;

        Assert.Equal(20, (await svc.LoadSettingsAsync()).Volume);
    }

    /// <summary>CI 10-18 (InboxPageTests): the library's genre pass loaded settings.json to
    /// stamp its schema version, Settings saved the Inbox switch meanwhile, and the pass then
    /// wrote its stale copy over it. A save that comes while an update is under way now waits
    /// for it instead of being undone by it.</summary>
    [Fact]
    public async Task UpdateSettings_ASaveFromElsewhereMeanwhile_IsNotUndone()
    {
        var svc = Create();
        await svc.SaveSettingsAsync(new AppSettings());
        using var loaded = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();

        var update = Task.Run(() => svc.UpdateSettingsAsync(s =>
        {
            loaded.Set();   // the update has read the file...
            resume.Wait(TestContext.Current.CancellationToken);
            s.MetadataSchemaVersion = 12;
        }), TestContext.Current.CancellationToken);
        loaded.Wait(TestContext.Current.CancellationToken);
        var save = svc.SaveSettingsAsync(new AppSettings { InboxEnabled = true });   // ...when Settings saves
        await Task.Delay(100, TestContext.Current.CancellationToken);
        resume.Set();
        await Task.WhenAll(update, save);

        Assert.True((await svc.LoadSettingsAsync()).InboxEnabled);
    }

    [Fact]
    public async Task OutOfRangeNumbers_AreClampedOnLoad()
    {
        var svc = Create();
        Directory.CreateDirectory(_root);
        // Hand-edited settings.json: the bounds were previously enforced by the UI only.
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"),
            """{"volume":400,"webRemotePort":0,"replayGainPreampDb":99,"crossfadeDuration":900}""");

        var loaded = await svc.LoadSettingsAsync();

        Assert.Equal(100, loaded.Volume);
        Assert.Equal(9420, loaded.WebRemotePort);
        Assert.Equal(12, loaded.ReplayGainPreampDb);
        Assert.Equal(12, loaded.CrossfadeDuration);
    }

    // ── GitHub #197: one data folder, two computers ──

    /// <summary>A DPAPI copy made by another computer or Windows user: this one cannot decrypt it.</summary>
    private static string ForeignBlob(byte seed) =>
        "enc:dpapi:" + Convert.ToBase64String(Enumerable.Range(0, 64).Select(i => (byte)(i * 7 + seed)).ToArray());

    private async Task WriteRawSettingsAsync(object settings)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"),
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private async Task<JsonElement> ReadRawSettingsAsync()
    {
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "settings.json")));
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task OtherComputersCredentials_ReadSignedOutHere_AndASaveKeepsThem()
    {
        var connId = Guid.NewGuid();
        string lastFm = ForeignBlob(1), lb = ForeignBlob(2), pass = ForeignBlob(3);
        await WriteRawSettingsAsync(new
        {
            lastFmScrobblingEnabled = true,
            lastFmSessionKey = lastFm,
            lastFmUsername = "listener",
            listenBrainzToken = lb,
            sourceConnections = new[] { new { id = connId, name = "navi", tokenOrPassword = pass } },
        });

        var svc = Create();
        var loaded = await svc.LoadSettingsAsync();
        Assert.Equal("", loaded.LastFmSessionKey);
        Assert.Equal("", loaded.ListenBrainzToken);
        Assert.Equal("", loaded.SourceConnections[0].TokenOrPassword);
        Assert.True(loaded.HasOtherComputerSecret(PersistenceService.LastFmSessionKeyField));
        Assert.True(loaded.HasOtherComputerSecret(PersistenceService.ListenBrainzTokenField));

        // The save every settings change makes here used to write the empty values back,
        // signing the other computer out too.
        await svc.SaveSettingsAsync(loaded);

        var raw = await ReadRawSettingsAsync();
        Assert.Equal(lastFm, raw.GetProperty("lastFmSessionKey").GetString());
        Assert.Equal(lb, raw.GetProperty("listenBrainzToken").GetString());
        Assert.Equal(pass, raw.GetProperty("sourceConnections")[0].GetProperty("tokenOrPassword").GetString());
        Assert.False(raw.TryGetProperty("otherComputerSecrets", out _));
        Assert.Equal("listener", raw.GetProperty("lastFmUsername").GetString());
    }

    [Fact]
    public async Task UpdateSettings_KeepsOtherComputersCredentials()
    {
        var blob = ForeignBlob(4);
        await WriteRawSettingsAsync(new { lastFmSessionKey = blob });

        var svc = Create();
        await svc.UpdateSettingsAsync(s => s.Volume = 33);

        Assert.Equal(blob, (await ReadRawSettingsAsync()).GetProperty("lastFmSessionKey").GetString());
    }

    [Fact]
    public async Task SigningInHere_KeepsTheOtherComputersCopy_AndEachReadsItsOwn()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI is Windows-only by design

        var foreign = ForeignBlob(5);
        await WriteRawSettingsAsync(new { lastFmSessionKey = foreign });
        var svc = Create();
        var loaded = await svc.LoadSettingsAsync();

        loaded.LastFmSessionKey = "signed-in-here";
        await svc.SaveSettingsAsync(loaded);

        var raw = await ReadRawSettingsAsync();
        var primary = raw.GetProperty("lastFmSessionKey").GetString()!;
        Assert.StartsWith("enc:dpapi:", primary);
        Assert.NotEqual(foreign, primary);
        Assert.DoesNotContain("signed-in-here", raw.GetRawText());
        Assert.Equal(new[] { foreign },
            raw.GetProperty("otherComputerSecrets").GetProperty("lastFmSessionKey").EnumerateArray().Select(e => e.GetString()));

        var reloaded = await svc.LoadSettingsAsync();
        Assert.Equal("signed-in-here", reloaded.LastFmSessionKey);
        Assert.True(reloaded.HasOtherComputerSecret(PersistenceService.LastFmSessionKeyField));
    }

    [Fact]
    public async Task ThisComputersCopy_IsFoundAmongTheKeptOnes_WhenTheOtherComputerSignedInLast()
    {
        if (!OperatingSystem.IsWindows()) return;

        var foreign = ForeignBlob(6);
        var mine = PersistenceService.ProtectSecret("my-session");
        await WriteRawSettingsAsync(new
        {
            lastFmSessionKey = foreign,
            otherComputerSecrets = new Dictionary<string, string[]> { ["lastFmSessionKey"] = new[] { mine } },
        });

        var svc = Create();
        var loaded = await svc.LoadSettingsAsync();
        Assert.Equal("my-session", loaded.LastFmSessionKey);

        await svc.SaveSettingsAsync(loaded);
        var raw = await ReadRawSettingsAsync();
        Assert.Equal("my-session", PersistenceService.UnprotectSecret(raw.GetProperty("lastFmSessionKey").GetString()!));
        Assert.Equal(new[] { foreign },
            raw.GetProperty("otherComputerSecrets").GetProperty("lastFmSessionKey").EnumerateArray().Select(e => e.GetString()));

        // Signing out here hands the field back to the other computer, still signed in there.
        loaded.LastFmSessionKey = "";
        await svc.SaveSettingsAsync(loaded);
        raw = await ReadRawSettingsAsync();
        Assert.Equal(foreign, raw.GetProperty("lastFmSessionKey").GetString());
        Assert.False(raw.TryGetProperty("otherComputerSecrets", out _));
    }
}
