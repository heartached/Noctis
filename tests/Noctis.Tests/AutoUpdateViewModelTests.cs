using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

[CollectionDefinition("AutoUpdate mode", DisableParallelization = true)]
public class AutoUpdateModeCollection { }

/// <summary>
/// The view-model side of "Update automatically", against a fake GitHub API. Debug builds (which
/// tests run) resolve UpdateService.AutoMode to Off, so these force it process-wide in a
/// non-parallel collection and restore it. Nothing here launches an installer.
/// </summary>
[Collection("AutoUpdate mode")]
public class AutoUpdateViewModelTests : IDisposable
{
    private const string AssetUrl = "https://api.github.com/repos/heartached/Noctis/releases/assets/1";
    private const string SumsUrl = "https://api.github.com/repos/heartached/Noctis/releases/assets/2";
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("not an installer, just test bytes");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    // Downloads land in the real temp folder; whatever these tests add there is removed afterwards.
    private readonly HashSet<string> _tempBefore = UpdaterTempFiles();

    public AutoUpdateViewModelTests() => UpdateService.AutoModeOverride = AutoUpdateMode.InstallAtLaunch;

    public void Dispose()
    {
        UpdateService.AutoModeOverride = null;
        foreach (var f in UpdaterTempFiles().Except(_tempBefore)) { try { File.Delete(f); } catch { } }
        try { Directory.Delete(_root, true); } catch { }
    }

    private static HashSet<string> UpdaterTempFiles() =>
        Directory.GetFiles(Path.GetTempPath(), "Noctis-Update-*").ToHashSet();

    private AutoUpdateStore Store => new(_root);

    private async Task<SettingsViewModel> CreateAsync(FakeGitHub api, bool autoOn, bool prerelease = false)
    {
        await new PersistenceService(_root).SaveSettingsAsync(
            new AppSettings { AutoInstallUpdates = autoOn, IncludePrereleaseUpdates = prerelease });
        var vm = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        vm.SetUpdateService(new UpdateService(new HttpClient(api)));
        return vm;
    }

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 15000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(condition(), "condition not reached in time");
    }

    /// <summary>Silent check with auto-update on, then waits for the queued install.</summary>
    private static async Task QueueAsync(SettingsViewModel vm)
    {
        await vm.CheckForUpdateSilentAsync();
        await PumpUntil(() => vm.IsReadyToInstall && vm.IsAutoInstallPending);
    }

    [AvaloniaFact]
    public async Task SettingOff_SilentCheckShowsTheUpdatePill_AndDownloadsNothing()
    {
        var api = new FakeGitHub("v99.0.0");
        var vm = await CreateAsync(api, autoOn: false);

        await vm.CheckForUpdateSilentAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsUpdateAvailable);
        Assert.True(vm.ShowUpdateBadge);
        Assert.Equal("v99.0.0", vm.LatestVersionTag);
        Assert.Equal(0, api.InstallerRequests);
        Assert.Null(Store.Load());
    }

    [AvaloniaFact]
    public async Task SettingOn_EligibleRelease_IsDownloadedVerifiedAndQueued_ThenPostponed()
    {
        var api = new FakeGitHub("v99.0.0");
        var vm = await CreateAsync(api, autoOn: true);

        await QueueAsync(vm);

        var p = Store.Load()!.Pending!;
        Assert.Equal("v99.0.0", p.Tag);
        Assert.True(File.Exists(p.InstallerPath));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant(), p.Sha256);
        Assert.False(vm.IsUpdateAvailable);
        Assert.False(vm.ShowUpdateBadge); // installs by itself at the next launch
        Assert.True(vm.ShowPostponeButton);
        Assert.Contains("installs the next time", vm.UpdateStatusText);

        vm.PostponeAutoInstallCommand.Execute(null);
        Assert.NotNull(Store.Load()!.Pending!.PostponedUntilUtc);
        Assert.False(vm.ShowPostponeButton);

        // Already queued: the next check downloads nothing.
        await vm.CheckForUpdateSilentAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, api.InstallerRequests);
        Assert.Equal(p.InstallerPath, Store.Load()!.Pending!.InstallerPath);
    }

    /// <summary>macOS only downloads: the badge must still point at Install &amp; Restart.</summary>
    [AvaloniaFact]
    public async Task DownloadOnly_QueuedDownload_KeepsTheUpdateBadge()
    {
        UpdateService.AutoModeOverride = AutoUpdateMode.DownloadOnly;
        var vm = await CreateAsync(new FakeGitHub("v99.0.0"), autoOn: true);

        await QueueAsync(vm);

        Assert.True(vm.ShowUpdateBadge);
        Assert.False(vm.ShowPostponeButton);
        Assert.Contains("Install & Restart", vm.UpdateStatusText);
    }

    /// <summary>A queued file deleted mid-session (temp cleanup) is dropped and counted, and the
    /// Update pill comes back, instead of "already queued" hiding the update for good.</summary>
    [AvaloniaFact]
    public async Task QueuedFileThatVanished_IsDiscardedAndCounted_AndThePillReturns()
    {
        var api = new FakeGitHub("v99.0.0");
        var vm = await CreateAsync(api, autoOn: true);
        await QueueAsync(vm);
        File.Delete(Store.Load()!.Pending!.InstallerPath);

        await vm.CheckForUpdateSilentAsync();
        Dispatcher.UIThread.RunJobs();

        var s = Store.Load()!;
        Assert.Null(s.Pending);
        Assert.Equal(("v99.0.0", 1), (s.Tag, s.Failures));
        Assert.False(vm.IsReadyToInstall);
        Assert.True(vm.IsUpdateAvailable); // backing off: the manual pill is the way meanwhile
        Assert.Equal(1, api.InstallerRequests);
    }

    [AvaloniaFact]
    public async Task LeavingThePrereleaseChannel_StopsAnAutomaticPrereleaseDownload_AndReasksStable()
    {
        var api = new FakeGitHub("v99.1.0-beta.1", prerelease: true) { HangInstaller = true };
        var vm = await CreateAsync(api, autoOn: true, prerelease: true);

        await vm.CheckForUpdateSilentAsync();
        await PumpUntil(() => api.InstallerRequests == 1 && vm.IsDownloadingUpdate);

        vm.IncludePrereleaseUpdates = false;
        await PumpUntil(() => !vm.IsDownloadingUpdate && vm.IsUpToDate);

        Assert.Equal(2, api.ReleaseChecks); // the stable channel was asked again
        Assert.Null(Store.Load()?.Pending);
        Assert.False(vm.IsReadyToInstall);
        Assert.False(vm.IsUpdateAvailable); // no pill for the beta the user just opted out of
    }

    /// <summary>A pre-release download that finishes after the channel went off is not queued
    /// (Install &amp; Restart still offers it, as for any manual download).</summary>
    [AvaloniaFact]
    public async Task PrereleaseFinishingAfterTheChannelWentOff_IsNotQueued()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeGitHub("v99.1.0-beta.1", prerelease: true) { Gate = gate };
        var vm = await CreateAsync(api, autoOn: true, prerelease: true);

        var run = vm.DownloadUpdateCommand.ExecuteAsync(null); // the Update pill: not cancelled below
        await PumpUntil(() => api.InstallerRequests == 1);
        vm.IncludePrereleaseUpdates = false;
        gate.SetResult();
        await run;
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsReadyToInstall);
        Assert.False(vm.IsAutoInstallPending);
        Assert.Null(Store.Load()?.Pending);
    }

    [AvaloniaFact]
    public async Task LeavingThePrereleaseChannel_DiscardsAQueuedPrerelease_AndItsFile()
    {
        var vm = await CreateAsync(new FakeGitHub("v99.1.0-beta.1", prerelease: true), autoOn: true, prerelease: true);
        await QueueAsync(vm);
        var file = Store.Load()!.Pending!.InstallerPath;

        vm.IncludePrereleaseUpdates = false;
        await PumpUntil(() => vm.IsUpToDate); // the stable channel is asked again and has nothing

        Assert.Null(Store.Load()!.Pending);
        Assert.False(File.Exists(file));
        Assert.False(vm.IsReadyToInstall);
        Assert.False(vm.IsAutoInstallPending);
    }

    /// <summary>A failed automatic download is counted (a hash mismatch blocks the tag at once)
    /// and brings back the manual Update pill.</summary>
    [AvaloniaFact]
    public async Task FailedVerification_BlocksTheTag_AndBringsBackThePill()
    {
        var vm = await CreateAsync(new FakeGitHub("v99.0.0") { WrongHash = true }, autoOn: true);

        await vm.CheckForUpdateSilentAsync();
        await PumpUntil(() => vm.IsUpdateAvailable);

        var s = Store.Load()!;
        Assert.Equal(("v99.0.0", 1, true), (s.Tag, s.Failures, s.Blocked));
        Assert.Null(s.Pending);
        Assert.False(vm.IsReadyToInstall);
        Assert.Contains("Use the Update button", vm.UpdateStatusText);
    }

    [AvaloniaFact]
    public async Task Startup_RestoresAQueuedInstall_AfterItsFileReverifies()
    {
        var vm = await CreateAsync(new FakeGitHub("v99.0.0"), autoOn: true);
        var file = Path.Combine(Path.GetTempPath(), $"Noctis-Update-{Guid.NewGuid().ToString("N")[..8]}-Setup.exe");
        File.WriteAllBytes(file, Payload);
        Store.Save(new AutoUpdateState
        {
            Tag = "v99.0.0",
            Pending = new PendingInstall
            {
                Tag = "v99.0.0", FromVersion = "1.0.0", ToVersion = "99.0.0", InstallerPath = file,
                Sha256 = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant()
            }
        });

        vm.RestoreAutoUpdateState();
        await PumpUntil(() => vm.IsReadyToInstall);

        Assert.True(vm.IsAutoInstallPending);
        Assert.Equal("v99.0.0", vm.LatestVersionTag);
        Assert.Contains("installs the next time", vm.UpdateStatusText);
    }

    /// <summary>While a download runs, the "Check for updates" pill must not come back: it was shown
    /// next to the progress (IsUpdateAvailable drops when the download starts) and a click on it
    /// cancelled the download through the shared token.</summary>
    [AvaloniaFact]
    public async Task ManualDownload_HidesTheCheckPill_AndACheckCannotCancelIt()
    {
        var api = new FakeGitHub("v99.0.0") { HangInstaller = true };
        var vm = await CreateAsync(api, autoOn: false);

        var run = vm.DownloadUpdateCommand.ExecuteAsync(null);
        await PumpUntil(() => api.InstallerRequests == 1);

        Assert.True(vm.IsDownloadingUpdate);
        Assert.False(vm.ShowCheckForUpdatesButton);

        await vm.CheckForUpdateCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsDownloadingUpdate); // still running

        vm.CancelUpdateCommand.Execute(null);
        await run;
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsDownloadingUpdate);
        Assert.True(vm.ShowCheckForUpdatesButton);
    }

    /// <summary>A progress report already queued on the UI thread when Cancel lands must not
    /// overwrite "Download cancelled." with a stale "Downloading…" line afterwards.</summary>
    [AvaloniaFact]
    public async Task ProgressQueuedBeforeCancel_DoesNotOverwriteTheCancelledState()
    {
        var head = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeGitHub("v99.0.0") { PartialThenHang = true, HeadGate = head };
        var vm = await CreateAsync(api, autoOn: false);

        var run = vm.DownloadUpdateCommand.ExecuteAsync(null);
        await PumpUntil(() => api.InstallerRequests == 1);
        for (var i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }

        // Hold the UI thread: the first bytes' progress report queues behind it, then Cancel lands.
        head.SetResult();
        Thread.Sleep(500);
        vm.CancelUpdateCommand.Execute(null);
        Thread.Sleep(300);
        await run;
        for (var i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }

        Assert.False(vm.IsDownloadingUpdate);
        Assert.Equal("Download cancelled.", vm.UpdateStatusText);
        Assert.Equal(0, vm.DownloadProgress); // the late 30 % report was dropped, not applied
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    /// <summary>One two-day-old release (soaked) with a Windows installer and its SHA256SUMS.</summary>
    private sealed class FakeGitHub(string tag, bool prerelease = false) : HttpMessageHandler
    {
        public int ReleaseChecks;
        public int InstallerRequests;
        /// <summary>Hold the installer request until it is cancelled.</summary>
        public bool HangInstaller;
        /// <summary>Hold the installer request until this completes.</summary>
        public TaskCompletionSource? Gate;
        /// <summary>SHA256SUMS lists a different hash for the installer.</summary>
        public bool WrongHash;
        /// <summary>Send the first bytes of the installer, then hold the body until it is cancelled.</summary>
        public bool PartialThenHang;
        /// <summary>With <see cref="PartialThenHang"/>: the first bytes wait for this.</summary>
        public TaskCompletionSource? HeadGate;

        private string AssetName => $"Noctis-{tag}-Setup.exe";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.StartsWith("https://api.github.com/repos/heartached/Noctis/releases?", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref ReleaseChecks);
                return Ok(new StringContent(ReleasesJson()));
            }
            if (url == SumsUrl)
                return Ok(new StringContent(
                    $"{(WrongHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant())}  {AssetName}\n"));
            if (url == AssetUrl)
            {
                Interlocked.Increment(ref InstallerRequests);
                if (HangInstaller) await Task.Delay(Timeout.Infinite, ct);
                if (Gate is not null) await Gate.Task.WaitAsync(ct);
                if (PartialThenHang)
                {
                    var partial = new StreamContent(new PartialThenHangStream(Payload[..10], HeadGate));
                    partial.Headers.ContentLength = Payload.Length;
                    return Ok(partial);
                }
                return Ok(new ByteArrayContent(Payload));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Ok(HttpContent content) => new(HttpStatusCode.OK) { Content = content };

        /// <summary>Returns <paramref name="head"/> on the first read, then waits for cancellation.</summary>
        private sealed class PartialThenHangStream(byte[] head, TaskCompletionSource? gate) : Stream
        {
            private bool _sent;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                if (!_sent)
                {
                    if (gate is not null) await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                    _sent = true;
                    head.CopyTo(buffer);
                    return head.Length;
                }
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return 0;
            }
        }

        private string ReleasesJson() => JsonSerializer.Serialize(new[]
        {
            new
            {
                tag_name = tag, prerelease, draft = false, body = "Fixes.",
                html_url = $"https://github.com/heartached/Noctis/releases/tag/{tag}",
                published_at = DateTimeOffset.UtcNow.AddDays(-2),
                assets = new[]
                {
                    new { name = AssetName, url = AssetUrl, browser_download_url = "https://github.com/x", size = (long)Payload.Length },
                    new { name = "SHA256SUMS", url = SumsUrl, browser_download_url = "https://github.com/y", size = 100L }
                }
            }
        });
    }
}
