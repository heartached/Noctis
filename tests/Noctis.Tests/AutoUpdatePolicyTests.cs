using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>Which copies may update themselves unattended (AutoUpdatePolicy.ResolveMode).</summary>
public class AutoUpdateModeTests
{
    [Theory]
    //          win    mac    linux  source                     appImg dirW   x64    debug  expected
    [InlineData(true,  false, false, InstallSource.Installed,  false, false, true,  false, AutoUpdateMode.InstallAtLaunch)]
    [InlineData(true,  false, false, InstallSource.Scoop,      false, false, true,  false, AutoUpdateMode.Off)]
    [InlineData(true,  false, false, InstallSource.Portable,   false, false, true,  false, AutoUpdateMode.Off)]
    [InlineData(false, true,  false, InstallSource.Installed,  false, false, false, false, AutoUpdateMode.DownloadOnly)]
    [InlineData(false, false, true,  InstallSource.Installed,  true,  true,  true,  false, AutoUpdateMode.InstallAtLaunch)] // AppImage, writable
    [InlineData(false, false, true,  InstallSource.Installed,  true,  false, true,  false, AutoUpdateMode.Off)]             // AppImage, read-only folder
    [InlineData(false, false, true,  InstallSource.Installed,  true,  true,  false, false, AutoUpdateMode.Off)]             // AppImage on arm64
    [InlineData(false, false, true,  InstallSource.Installed,  false, false, true,  false, AutoUpdateMode.InstallAtLaunch)] // writable tarball
    [InlineData(false, false, true,  InstallSource.Portable,   false, false, true,  false, AutoUpdateMode.Off)]             // /opt, Flatpak, distro
    [InlineData(true,  false, false, InstallSource.Installed,  false, false, true,  true,  AutoUpdateMode.Off)]             // Debug build
    [InlineData(false, true,  false, InstallSource.Installed,  false, false, false, true,  AutoUpdateMode.Off)]
    [InlineData(false, false, true,  InstallSource.Installed,  true,  true,  true,  true,  AutoUpdateMode.Off)]
    public void ResolveMode_MatchesPlatformAndInstallSource(
        bool isWindows, bool isMacOS, bool isLinux, InstallSource source,
        bool isAppImage, bool appImageDirWritable, bool isX64, bool isDebugBuild, AutoUpdateMode expected)
    {
        Assert.Equal(expected, AutoUpdatePolicy.ResolveMode(
            isWindows, isMacOS, isLinux, source, isAppImage, appImageDirWritable, isX64, isDebugBuild));
    }
}

/// <summary>Which releases download unattended: both assets, no warning, 24 h soak, backoff, per-tag block.</summary>
public class AutoUpdateShouldDownloadTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static UpdateInfo Release(
        string tag = "v1.5.5", bool installer = true, bool sums = true,
        string? warning = null, TimeSpan? age = null, bool published = true) => new()
    {
        TagName = tag,
        Version = UpdateService.ParseTag(tag)!,
        InstallerApiUrl = installer ? "https://api.github.com/repos/heartached/Noctis/releases/assets/1" : null,
        ChecksumsApiUrl = sums ? "https://api.github.com/repos/heartached/Noctis/releases/assets/2" : null,
        WarningText = warning,
        PublishedAt = published ? Now - (age ?? TimeSpan.FromHours(25)) : null,
        ReleaseUrl = $"https://github.com/heartached/Noctis/releases/tag/{tag}",
    };

    private static AutoUpdateState Failed(int failures, TimeSpan ago, bool blocked = false, string tag = "v1.5.5") => new()
    {
        Tag = tag, Failures = failures, LastAttemptUtc = Now - ago, Blocked = blocked
    };

    [Fact]
    public void NoInstallerAsset_IsSkipped()
    {
        Assert.False(AutoUpdatePolicy.ShouldAutoDownload(null, Release(installer: false), Now, out var why));
        Assert.Equal("no installer asset", why);
    }

    [Fact]
    public void NoChecksums_IsSkipped_ButNotBlocked()
    {
        var state = new AutoUpdateState { Tag = "v1.5.5" };
        Assert.False(AutoUpdatePolicy.ShouldAutoDownload(state, Release(sums: false), Now, out var why));
        Assert.Equal("no SHA256SUMS yet", why);
        Assert.False(state.Blocked);
        Assert.Equal(0, state.Failures);
    }

    [Fact]
    public void WarningInReleaseNotes_IsSkipped()
    {
        Assert.False(AutoUpdatePolicy.ShouldAutoDownload(null, Release(warning: "Crashes on start."), Now, out var why));
        Assert.Equal("release notes carry a warning", why);
    }

    [Fact]
    public void UnknownPublishDate_IsSkipped()
    {
        Assert.False(AutoUpdatePolicy.ShouldAutoDownload(null, Release(published: false), Now, out var why));
        Assert.Equal("published less than 24 h ago", why);
    }

    [Fact]
    public void ReleaseInsideSoakPeriod_IsSkipped()
    {
        Assert.False(AutoUpdatePolicy.ShouldAutoDownload(null, Release(age: TimeSpan.FromHours(2)), Now, out var why));
        Assert.Equal("published less than 24 h ago", why);
    }

    [Fact]
    public void BlockedTag_IsSkipped()
    {
        Assert.False(AutoUpdatePolicy.ShouldAutoDownload(Failed(3, TimeSpan.FromDays(3), blocked: true), Release(), Now, out var why));
        Assert.Equal("blocked after failures", why);
    }

    [Theory]
    [InlineData(1, 30)]        // 1 failure: 1 h backoff, 30 min elapsed
    [InlineData(2, 5 * 60)]    // 2 failures: 6 h backoff, 5 h elapsed
    public void InsideBackoff_IsSkipped(int failures, int minutesAgo)
    {
        Assert.False(AutoUpdatePolicy.ShouldAutoDownload(
            Failed(failures, TimeSpan.FromMinutes(minutesAgo)), Release(), Now, out var why));
        Assert.StartsWith("backing off until", why);
    }

    [Fact]
    public void SoakedReleaseWithBothAssets_Downloads()
        => Assert.True(AutoUpdatePolicy.ShouldAutoDownload(null, Release(), Now, out _));

    [Theory]
    [InlineData(1, 2 * 60)]
    [InlineData(2, 7 * 60)]
    public void AfterBackoff_DownloadsAgain(int failures, int minutesAgo)
        => Assert.True(AutoUpdatePolicy.ShouldAutoDownload(
            Failed(failures, TimeSpan.FromMinutes(minutesAgo)), Release(), Now, out _));

    [Fact]
    public void BlockIsPerTag_ANewerReleaseStillDownloads()
        => Assert.True(AutoUpdatePolicy.ShouldAutoDownload(
            Failed(3, TimeSpan.FromMinutes(5), blocked: true, tag: "v1.5.5"), Release("v1.5.6"), Now, out _));
}

public class AutoUpdateFailureTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SoftFailures_CountUp_ThenBlockOnTheThird()
    {
        var s = AutoUpdatePolicy.RecordDownloadFailure(null, "v1.5.5", Now, hard: false);
        Assert.Equal("v1.5.5", s.Tag);
        Assert.Equal((1, false), (s.Failures, s.Blocked));
        Assert.Equal(Now, s.LastAttemptUtc);

        var later = Now.AddHours(2);
        s = AutoUpdatePolicy.RecordDownloadFailure(s, "v1.5.5", later, hard: false);
        Assert.Equal((2, false), (s.Failures, s.Blocked));
        Assert.Equal(later, s.LastAttemptUtc);

        s = AutoUpdatePolicy.RecordDownloadFailure(s, "v1.5.5", later.AddHours(7), hard: false);
        Assert.Equal((3, true), (s.Failures, s.Blocked));
    }

    [Fact]
    public void HashMismatch_BlocksOnTheFirstAttempt()
    {
        var s = AutoUpdatePolicy.RecordDownloadFailure(null, "v1.5.5", Now, hard: true);
        Assert.Equal(1, s.Failures);
        Assert.True(s.Blocked);
    }

    [Fact]
    public void FailureForANewTag_StartsItsOwnCount_AndKeepsTheQueue()
    {
        var pending = new PendingInstall { Tag = "v1.5.5", ToVersion = "1.5.5" };
        var old = new AutoUpdateState { Tag = "v1.5.4", Failures = 3, Blocked = true, Pending = pending, StaleInstallerPath = "x" };

        var s = AutoUpdatePolicy.RecordDownloadFailure(old, "v1.5.6", Now, hard: false);

        Assert.Equal("v1.5.6", s.Tag);
        Assert.Equal((1, false), (s.Failures, s.Blocked));
        Assert.Same(pending, s.Pending);
        Assert.Equal("x", s.StaleInstallerPath);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 6)]
    public void Backoff_IsOneHourThenSix(int failures, int hours)
        => Assert.Equal(TimeSpan.FromHours(hours), AutoUpdatePolicy.BackoffAfter(failures));
}

public class AutoUpdateLaunchActionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Version Current = new(1, 5, 4, 0); // assembly versions are 4-part

    private static PendingInstall Pending(string to = "1.5.5", int attempts = 0, DateTimeOffset? postponedUntil = null,
        DateTimeOffset? lastLaunch = null) => new()
    {
        Tag = "v" + to, FromVersion = "1.5.4", ToVersion = to, InstallerPath = "x", Sha256 = new string('a', 64),
        LaunchAttempts = attempts, PostponedUntilUtc = postponedUntil, LastLaunchUtc = lastLaunch
    };

    private static LaunchAction Decide(PendingInstall? p, Version? current = null,
        AutoUpdateMode mode = AutoUpdateMode.InstallAtLaunch, bool files = false, bool fileOk = true)
        => AutoUpdatePolicy.DecideLaunchAction(p, current ?? Current, mode, files, fileOk, Now, out _);

    [Fact]
    public void NothingQueued_None() => Assert.Equal(LaunchAction.None, Decide(null));

    [Fact]
    public void UnreadableVersion_Discard() => Assert.Equal(LaunchAction.Discard, Decide(Pending(to: "garbage")));

    [Fact]
    public void RunningTheQueuedVersion_Completed_DespiteFourPartAssemblyVersion()
    {
        // Version("1.5.5") has Revision -1 and compares LESS than 1.5.5.0 without normalising.
        Assert.True(new Version("1.5.5") < new Version(1, 5, 5, 0));
        Assert.Equal(LaunchAction.Completed, Decide(Pending(to: "1.5.5"), current: new Version(1, 5, 5, 0)));
    }

    [Fact]
    public void OlderThanThisBuild_Discard_NeverDowngrades()
        => Assert.Equal(LaunchAction.Discard, Decide(Pending(to: "1.5.3")));

    [Fact]
    public void ModeOff_Discard() => Assert.Equal(LaunchAction.Discard, Decide(Pending(), mode: AutoUpdateMode.Off));

    [Fact]
    public void DownloadOnly_None() => Assert.Equal(LaunchAction.None, Decide(Pending(), mode: AutoUpdateMode.DownloadOnly));

    [Fact]
    public void FileMissingOrNotOurs_Discard() => Assert.Equal(LaunchAction.Discard, Decide(Pending(), fileOk: false));

    [Fact]
    public void DownloadOnly_FileMissing_Discard_SoAPurgedDmgDoesNotStayQueued()
        => Assert.Equal(LaunchAction.Discard, Decide(Pending(), mode: AutoUpdateMode.DownloadOnly, fileOk: false));

    /// <summary>A relaunch while the first launch's install still runs must not start a second one
    /// (the Linux AppImage swap has already moved the file away, hence fileOk: false).</summary>
    [Theory]
    [InlineData(2, false, true)]
    [InlineData(2, true, true)]
    [InlineData(10, true, false)]
    [InlineData(-60, true, false)] // clock set back: not "just started"
    public void InstallStartedMinutesAgo_LeftAlone(int minutesAgo, bool fileOk, bool leftAlone)
        => Assert.Equal(leftAlone ? LaunchAction.None : LaunchAction.Install,
            Decide(Pending(attempts: 1, lastLaunch: Now.AddMinutes(-minutesAgo)), fileOk: fileOk));

    [Fact]
    public void TwoLaunchAttempts_Discard() => Assert.Equal(LaunchAction.Discard, Decide(Pending(attempts: 2)));

    [Fact]
    public void FilesToOpen_None() => Assert.Equal(LaunchAction.None, Decide(Pending(), files: true));

    [Fact]
    public void PostponedIntoTheFuture_Postponed()
        => Assert.Equal(LaunchAction.Postponed, Decide(Pending(postponedUntil: Now.AddHours(3))));

    [Fact]
    public void PostponeExpired_Install()
        => Assert.Equal(LaunchAction.Install, Decide(Pending(postponedUntil: Now.AddHours(-1))));

    [Fact]
    public void HappyPath_Install() => Assert.Equal(LaunchAction.Install, Decide(Pending(attempts: 1)));
}

/// <summary>Only the updater's own temp downloads may be launched or deleted.</summary>
public class AutoUpdateOwnedFileTests
{
    private static readonly string Temp = Path.Combine(Path.GetTempPath(), "nx-owned-temp");
    private const string Name = "Noctis-Update-1a2b3c4d-Setup.exe";

    [Theory]
    [InlineData("Noctis-Update-1a2b3c4d-Setup.exe")]
    [InlineData("Noctis-Update-1a2b3c4d.dmg")]
    [InlineData("Noctis-Update-1a2b3c4d.AppImage")]
    [InlineData("Noctis-Update-1a2b3c4d.tar.gz")]
    public void UpdaterTempNames_AreOwned(string name)
        => Assert.True(AutoUpdatePolicy.IsOwnedUpdateFile(Path.Combine(Temp, name), Temp));

    [Theory]
    [InlineData("Noctis-Update-1A2B3C4D-Setup.exe")]   // uppercase run tag
    [InlineData("Noctis-Update-1a2b3c4-Setup.exe")]    // 7 hex
    [InlineData("Noctis-Update-1a2b3c4d5-Setup.exe")]  // 9 hex
    [InlineData("Noctis-Update-1a2b3c4d.exe")]
    [InlineData("Noctis-Update-1a2b3c4d.msi")]
    [InlineData("Noctis-Update-1a2b3c4d-Setup.exe.bak")]
    [InlineData("Noctis-v1.5.5-Setup.exe")]
    [InlineData("evil.exe")]
    public void OtherNames_AreNotOwned(string name)
        => Assert.False(AutoUpdatePolicy.IsOwnedUpdateFile(Path.Combine(Temp, name), Temp));

    [Fact]
    public void OtherDirectories_AreNotOwned()
    {
        var downloads = Path.Combine(Path.GetTempPath(), "nx-downloads");
        Assert.False(AutoUpdatePolicy.IsOwnedUpdateFile(Path.Combine(downloads, Name), Temp));
        Assert.False(AutoUpdatePolicy.IsOwnedUpdateFile(Path.Combine(Temp, "sub", Name), Temp));
    }

    [Fact]
    public void Traversal_IsNotOwned()
    {
        var sep = Path.DirectorySeparatorChar;
        Assert.False(AutoUpdatePolicy.IsOwnedUpdateFile($"{Temp}{sep}..{sep}{Name}", Temp));
        Assert.False(AutoUpdatePolicy.IsOwnedUpdateFile($"{Temp}{sep}sub{sep}..{sep}{Name}", Temp));
        Assert.False(AutoUpdatePolicy.IsOwnedUpdateFile($"{Temp}{sep}.{sep}{Name}", Temp));
        Assert.False(AutoUpdatePolicy.IsOwnedUpdateFile(Name, Temp)); // relative
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmpty_IsNotOwned(string? path)
        => Assert.False(AutoUpdatePolicy.IsOwnedUpdateFile(path, Temp));

    [Fact]
    public void TrailingSeparatorOnTempDir_IsIgnored()
        => Assert.True(AutoUpdatePolicy.IsOwnedUpdateFile(Path.Combine(Temp, Name), Temp + Path.DirectorySeparatorChar));

    [Fact]
    public void DirectoryCase_MattersOnlyOffWindows()
        => Assert.Equal(OperatingSystem.IsWindows(),
            AutoUpdatePolicy.IsOwnedUpdateFile(Path.Combine(Temp, Name), Temp.ToUpperInvariant()));

    [Theory]
    [InlineData("v1.5.5", true)]
    [InlineData("v1.5.5-beta.1", true)]
    [InlineData("1.5.5", true)]
    [InlineData("v1.5.5-x/../../evil", false)]
    [InlineData("v1.5.5?x=1", false)]
    [InlineData("latest", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSafeTag_OnlyVersionTags(string? tag, bool expected)
        => Assert.Equal(expected, AutoUpdatePolicy.IsSafeTag(tag));
}

public class AutoUpdateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var store = new AutoUpdateStore(_dir);
        var downloaded = new DateTimeOffset(2026, 9, 24, 8, 30, 0, TimeSpan.Zero);
        store.Save(new AutoUpdateState
        {
            Tag = "v1.5.5", Failures = 2, LastAttemptUtc = downloaded.AddHours(1), Blocked = true,
            StaleInstallerPath = "stale",
            Pending = new PendingInstall
            {
                Tag = "v1.5.5", FromVersion = "1.5.4", ToVersion = "1.5.5", InstallerPath = "p",
                Sha256 = new string('b', 64), IsPrerelease = true, ReleaseUrl = "https://github.com/heartached/Noctis",
                DownloadedUtc = downloaded, PostponedUntilUtc = downloaded.AddDays(1), LaunchAttempts = 1,
                LastLaunchUtc = downloaded.AddDays(2)
            }
        });

        var s = store.Load();
        Assert.NotNull(s);
        Assert.Equal("v1.5.5", s!.Tag);
        Assert.Equal("stale", s.StaleInstallerPath);
        Assert.Equal((2, true), (s.Failures, s.Blocked));
        Assert.Equal(downloaded.AddHours(1), s.LastAttemptUtc);
        var p = s.Pending!;
        Assert.Equal(("v1.5.5", "1.5.4", "1.5.5", "p"), (p.Tag, p.FromVersion, p.ToVersion, p.InstallerPath));
        Assert.Equal(new string('b', 64), p.Sha256);
        Assert.True(p.IsPrerelease);
        Assert.Equal("https://github.com/heartached/Noctis", p.ReleaseUrl);
        Assert.Equal(downloaded, p.DownloadedUtc);
        Assert.Equal(downloaded.AddDays(1), p.PostponedUntilUtc);
        Assert.Equal(1, p.LaunchAttempts);
        Assert.Equal(downloaded.AddDays(2), p.LastLaunchUtc);
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public void NullPending_RoundTrips()
    {
        var store = new AutoUpdateStore(_dir);
        store.Save(new AutoUpdateState { Tag = "v1.5.5", Pending = null });
        Assert.Null(store.Load()!.Pending);
    }

    [Fact]
    public void MissingOrCorruptFile_LoadsAsNull()
    {
        var store = new AutoUpdateStore(_dir);
        Assert.Null(store.Load());

        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.FilePath, "{ not json");
        Assert.Null(store.Load());
    }

    [Fact]
    public void Delete_RemovesTheFile()
    {
        var store = new AutoUpdateStore(_dir);
        store.Save(new AutoUpdateState());
        Assert.True(File.Exists(store.FilePath));
        store.Delete();
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void File_IsOwnerOnly_OnUnix()
    {
        if (OperatingSystem.IsWindows()) return; // per-user %APPDATA% ACL; no POSIX mode to check
        var store = new AutoUpdateStore(_dir);
        store.Save(new AutoUpdateState());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
    }
}
