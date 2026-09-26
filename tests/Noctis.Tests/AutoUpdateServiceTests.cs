using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>Installable downloads must come from heartached/Noctis's own release-asset API.</summary>
public class UpdateRepoPinTests
{
    [Theory]
    [InlineData("https://api.github.com/repos/heartached/Noctis/releases/assets/123", true)]
    [InlineData("https://api.github.com/repos/heartached/noctis/releases/assets/587322385", true)]
    [InlineData("http://api.github.com/repos/heartached/Noctis/releases/assets/123", false)]      // not HTTPS
    [InlineData("https://api.github.com/repos/evil/Noctis/releases/assets/123", false)]           // other owner
    [InlineData("https://api.github.com/repos/heartached/Other/releases/assets/123", false)]      // other repo
    [InlineData("https://github.com/heartached/Noctis/releases/download/v1.5.4/SHA256SUMS", false)] // browser URL
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1", false)]
    [InlineData("https://api.github.com/repos/heartached/Noctis/releases/assets/123?token=x", false)] // query
    [InlineData("https://api.github.com:8443/repos/heartached/Noctis/releases/assets/123", false)]    // port
    [InlineData("https://api.github.com/repos/heartached/Noctis/releases/assets/../../../evil/x/releases/assets/1", false)]
    [InlineData("https://api.github.com/repos/heartached/Noctis/releases/assets/", false)]           // no id
    [InlineData("https://api.github.com/repos/heartached/Noctis/releases/assets/12a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsConfiguredRepoAssetUrl_PinsOwnerRepoAndAssetPath(string? url, bool expected)
        => Assert.Equal(expected, UpdateService.IsConfiguredRepoAssetUrl(url));
}

/// <summary>requireChecksums downloads (every normal and automatic update) verify against the
/// release's SHA256SUMS, fail closed, delete what failed, and refuse other repos before any request.</summary>
public class UpdateVerificationTests : IDisposable
{
    private const string AssetUrl = "https://api.github.com/repos/heartached/Noctis/releases/assets/1";
    private const string SumsUrl = "https://api.github.com/repos/heartached/Noctis/releases/assets/2";
    private const string AssetName = "Noctis-v9.9.9-Setup.exe";
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("not an installer, just test bytes");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public UpdateVerificationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Destination => Path.Combine(_dir, "download.bin");

    private static UpdateInfo Info(string? installerUrl = AssetUrl, string? sumsUrl = SumsUrl) => new()
    {
        TagName = "v9.9.9",
        Version = new Version(9, 9, 9),
        InstallerApiUrl = installerUrl,
        InstallerSize = Payload.Length,
        InstallerAssetName = AssetName,
        ChecksumsApiUrl = sumsUrl,
        ReleaseUrl = "https://github.com/heartached/Noctis/releases/tag/v9.9.9",
    };

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private Task<string> Download(UpdateService service, UpdateInfo info) =>
        service.DownloadInstallerAsync(info, ct: TestContext.Current.CancellationToken,
            destinationPath: Destination, requireChecksums: true);

    [Fact]
    public async Task MissingChecksums_FailsClosed()
    {
        var handler = new AssetHandler($"{Sha(Payload)}  {AssetName}\n");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Download(new UpdateService(new HttpClient(handler)), Info(sumsUrl: null)));
        Assert.Contains("SHA256SUMS", ex.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task WrongHash_FailsVerification_AndDeletesTheFile()
    {
        var handler = new AssetHandler($"{new string('0', 64)}  {AssetName}\n");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Download(new UpdateService(new HttpClient(handler)), Info()));
        Assert.Contains("SHA-256", ex.Message);
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task MatchingManifestLine_ReturnsTheVerifiedFile()
    {
        var handler = new AssetHandler($"{new string('a', 64)}  Other.dmg\n{Sha(Payload)}  {AssetName}\n");
        var path = await Download(new UpdateService(new HttpClient(handler)), Info());
        Assert.Equal(Destination, path);
        Assert.Equal(Payload, File.ReadAllBytes(path));
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("https://api.github.com/repos/evil/Noctis/releases/assets/1", SumsUrl)]
    [InlineData(AssetUrl, "https://api.github.com/repos/evil/Noctis/releases/assets/2")]
    [InlineData("https://api.github.com/repos/heartached/Noctis/releases/assets/1?x=1", SumsUrl)]
    public async Task AssetsFromOutsideTheRepo_AreRefusedBeforeAnyRequest(string installerUrl, string sumsUrl)
    {
        var handler = new AssetHandler($"{Sha(Payload)}  {AssetName}\n");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Download(new UpdateService(new HttpClient(handler)), Info(installerUrl, sumsUrl)));
        Assert.Equal(0, handler.Calls);
        Assert.False(File.Exists(Destination));
    }

    /// <summary>Serves the payload for asset 1 and the given manifest for asset 2; counts requests.</summary>
    private sealed class AssetHandler(string manifest) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            HttpContent content = request.RequestUri!.AbsoluteUri == SumsUrl
                ? new StringContent(manifest)
                : new ByteArrayContent(Payload);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}

/// <summary>Queueing a verified download and settling the queue at launch. None of these reach
/// the installer launch: each case ends in Completed / Discard / None or a hash mismatch.</summary>
public class AutoUpdateLaunchInstallTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _tempFiles = new();

    public AutoUpdateLaunchInstallTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var f in _tempFiles) { try { File.Delete(f); } catch { } }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static UpdateService Service() => new(new HttpClient(new NoNetworkHandler()));

    /// <summary>A file named like the updater's own temp download, in the real temp folder.</summary>
    private string OwnedTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Noctis-Update-{Guid.NewGuid().ToString("N")[..8]}-Setup.exe");
        File.WriteAllText(path, "not an installer");
        _tempFiles.Add(path);
        return path;
    }

    private static PendingInstall Pending(string path, string to, int attempts = 0) => new()
    {
        Tag = "v" + to, FromVersion = "1.0.0", ToVersion = to, InstallerPath = path,
        Sha256 = new string('0', 64), LaunchAttempts = attempts, DownloadedUtc = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Schedule_RecordsTheVerifiedHash_AndANewTagStartsItsOwnCount()
    {
        var file = Path.Combine(_dir, "installer.bin");
        await File.WriteAllTextAsync(file, "verified bytes", TestContext.Current.CancellationToken);
        var store = new AutoUpdateStore(_dir);
        store.Save(new AutoUpdateState { Tag = "v9.9.8", Failures = 2, LastAttemptUtc = DateTimeOffset.UtcNow, Blocked = true });

        var update = new UpdateInfo
        {
            TagName = "v9.9.9", Version = new Version(9, 9, 9), IsPrerelease = true,
            ReleaseUrl = "https://github.com/heartached/Noctis/releases/tag/v9.9.9"
        };
        await Service().ScheduleAutoInstallAsync(update, file, store);

        var s = store.Load()!;
        Assert.Equal(0, s.Failures);
        Assert.False(s.Blocked);
        var p = s.Pending!;
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant(), p.Sha256);
        Assert.Equal(UpdateService.CurrentVersion.ToString(3), p.FromVersion);
        Assert.Equal("9.9.9", p.ToVersion);
        Assert.Equal(file, p.InstallerPath);
        Assert.True(p.IsPrerelease);
        Assert.Equal(0, p.LaunchAttempts);
    }

    /// <summary>Re-queueing the same release keeps its failures: an installer that never starts
    /// (or a file that keeps vanishing) can't reset the count by downloading again, so the third
    /// failure blocks the tag instead of re-downloading forever.</summary>
    [Fact]
    public async Task Schedule_SameTagKeepsItsFailures_SoTheThirdFailureBlocks()
    {
        var file = Path.Combine(_dir, "installer.bin"); // not an updater temp file: "vanished" at launch
        await File.WriteAllTextAsync(file, "verified bytes", TestContext.Current.CancellationToken);
        var store = new AutoUpdateStore(_dir);
        store.Save(new AutoUpdateState { Tag = "v98.0.0", Failures = 2, LastAttemptUtc = DateTimeOffset.UtcNow.AddHours(-7) });

        var update = new UpdateInfo
        {
            TagName = "v98.0.0", Version = new Version(98, 0, 0),
            ReleaseUrl = "https://github.com/heartached/Noctis/releases/tag/v98.0.0"
        };
        await Service().ScheduleAutoInstallAsync(update, file, store);
        Assert.Equal((2, false), (store.Load()!.Failures, store.Load()!.Blocked));

        Assert.False(Service().TryInstallPendingUpdateAtLaunch(false, store, AutoUpdateMode.InstallAtLaunch));

        var s = store.Load()!;
        Assert.Null(s.Pending);
        Assert.Equal(("v98.0.0", 3, true), (s.Tag, s.Failures, s.Blocked));
        Assert.Contains("v98.0.0", UpdateService.LaunchInstallNote);
    }

    /// <summary>A queued file deleted before the launch (temp cleanup) counts as a failed download
    /// with the usual backoff, instead of a silent re-download after every launch.</summary>
    [Theory]
    [InlineData(AutoUpdateMode.InstallAtLaunch)]
    [InlineData(AutoUpdateMode.DownloadOnly)] // a purged macOS .dmg
    public void VanishedFile_IsDiscarded_AndCountsAFailure(AutoUpdateMode mode)
    {
        var file = OwnedTempFile();
        File.Delete(file);
        var store = new AutoUpdateStore(_dir);
        store.Save(new AutoUpdateState { Tag = "v99.0.0", Pending = Pending(file, "99.0.0") });

        Assert.False(Service().TryInstallPendingUpdateAtLaunch(false, store, mode));

        var s = store.Load()!;
        Assert.Null(s.Pending);
        Assert.Equal(("v99.0.0", 1, false), (s.Tag, s.Failures, s.Blocked));
        Assert.NotNull(s.LastAttemptUtc);
    }

    /// <summary>A second launch while the first launch's install still runs leaves it alone.</summary>
    [Fact]
    public void RelaunchWhileAnInstallRuns_KeepsTheQueue_AndStartsNothing()
    {
        // The all-zero hash would fail the re-check if this reached the install step.
        var file = OwnedTempFile();
        var store = new AutoUpdateStore(_dir);
        var pending = Pending(file, "99.0.0", attempts: 1);
        pending.LastLaunchUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        store.Save(new AutoUpdateState { Pending = pending });

        Assert.False(Service().TryInstallPendingUpdateAtLaunch(false, store, AutoUpdateMode.InstallAtLaunch));

        var s = store.Load()!;
        Assert.Equal(1, s.Pending!.LaunchAttempts);
        Assert.False(s.Blocked);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void RunningTheQueuedVersion_Completes_ThenTheInstallerIsCleanedUp()
    {
        var file = OwnedTempFile();
        var store = new AutoUpdateStore(_dir);
        store.Save(new AutoUpdateState { Tag = "x", Failures = 1, Pending = Pending(file, UpdateService.CurrentVersion.ToString(3)) });

        Assert.False(Service().TryInstallPendingUpdateAtLaunch(false, store, AutoUpdateMode.InstallAtLaunch));

        var s = store.Load()!;
        Assert.Null(s.Pending);
        Assert.Equal(0, s.Failures);
        Assert.Equal(file, s.StaleInstallerPath);
        Assert.Equal(file, UpdateService.CompletedAutoUpdate?.InstallerPath);

        UpdateService.DeleteStaleInstaller(store);
        Assert.False(File.Exists(file));
        Assert.Null(store.Load()!.StaleInstallerPath);
    }

    [Fact]
    public void ModeOff_DiscardsTheQueue_AndItsFile()
    {
        var file = OwnedTempFile();
        var store = new AutoUpdateStore(_dir);
        store.Save(new AutoUpdateState { Pending = Pending(file, "99.0.0") });

        Assert.False(Service().TryInstallPendingUpdateAtLaunch(false, store, AutoUpdateMode.Off));

        var s = store.Load()!;
        Assert.Null(s.Pending);
        Assert.False(s.Blocked);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void TwoFailedLaunches_DiscardAndBlockTheTag()
    {
        var file = OwnedTempFile();
        var store = new AutoUpdateStore(_dir);
        store.Save(new AutoUpdateState { Pending = Pending(file, "99.0.0", attempts: AutoUpdatePolicy.MaxLaunchAttempts) });

        Assert.False(Service().TryInstallPendingUpdateAtLaunch(false, store, AutoUpdateMode.InstallAtLaunch));

        var s = store.Load()!;
        Assert.Null(s.Pending);
        Assert.True(s.Blocked);
        Assert.Equal("v99.0.0", s.Tag);
        Assert.False(File.Exists(file));
        Assert.Contains("v99.0.0", UpdateService.LaunchInstallNote);
    }

    [Fact]
    public void TamperedFile_FailsTheReCheck_AndIsNeverLaunched()
    {
        // The recorded hash (all zeros) can't match the file, so the launch is refused.
        var file = OwnedTempFile();
        var store = new AutoUpdateStore(_dir);
        store.Save(new AutoUpdateState { Pending = Pending(file, "99.0.0") });

        Assert.False(Service().TryInstallPendingUpdateAtLaunch(false, store, AutoUpdateMode.InstallAtLaunch));

        var s = store.Load()!;
        Assert.Null(s.Pending);
        Assert.True(s.Blocked);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void FilesToOpen_SkipTheInstall_AndKeepTheQueue()
    {
        var file = OwnedTempFile();
        var store = new AutoUpdateStore(_dir);
        store.Save(new AutoUpdateState { Pending = Pending(file, "99.0.0") });

        Assert.False(Service().TryInstallPendingUpdateAtLaunch(true, store, AutoUpdateMode.InstallAtLaunch));

        var p = store.Load()!.Pending!;
        Assert.Equal(0, p.LaunchAttempts);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void QueueNamingAFileThatIsNotOurs_IsDiscarded_WithoutTouchingThatFile()
    {
        var foreign = Path.Combine(_dir, "Noctis-Update-1a2b3c4d-Setup.exe"); // right name, wrong folder
        File.WriteAllText(foreign, "someone else's file");
        var store = new AutoUpdateStore(_dir);
        store.Save(new AutoUpdateState { Pending = Pending(foreign, "99.0.0") });

        Assert.False(Service().TryInstallPendingUpdateAtLaunch(false, store, AutoUpdateMode.InstallAtLaunch));

        Assert.Null(store.Load()!.Pending);
        Assert.True(File.Exists(foreign));
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("no network in these tests");
    }
}
