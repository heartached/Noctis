using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>Settings › Library › Scan Library (Discord #mobile-beta, beta 6): a start scans every
/// time, on the first start of a day, a week after the last scan, or never; Rescan always scans.</summary>
public class MobileScanScheduleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly DateTime Now = new(2026, 11, 27, 9, 30, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(ScanSchedule.EveryStart, 0.0, true)]          // just scanned: still every start
    [InlineData(ScanSchedule.Manual, 400.0, false)]           // never on its own
    [InlineData(ScanSchedule.Daily, 2.0, false)]              // earlier today
    [InlineData(ScanSchedule.Daily, 10.0, true)]              // 23:30 yesterday, only 10 h ago
    [InlineData(ScanSchedule.Daily, 9.5, false)]              // 00:00 today
    [InlineData(ScanSchedule.Weekly, 24.0 * 6, false)]        // six days
    [InlineData(ScanSchedule.Weekly, 24.0 * 7, true)]         // a week
    [InlineData(ScanSchedule.Weekly, 24.0 * 6 + 10, true)]    // seven calendar days back, under 7 x 24 h
    public void IsDue_CountsCalendarDaysFromTheLastScan(ScanSchedule schedule, double hoursAgo, bool due) =>
        Assert.Equal(due, ScanSchedules.IsDue(schedule, Now.AddHours(-hoursAgo), Now, TimeZoneInfo.Utc));

    [Theory]
    [InlineData(ScanSchedule.EveryStart, true)]
    [InlineData(ScanSchedule.Daily, true)]
    [InlineData(ScanSchedule.Weekly, true)]
    [InlineData(ScanSchedule.Manual, false)]
    public void IsDue_WithNoScanRecorded_ScansUnlessManual(ScanSchedule schedule, bool due) =>
        Assert.Equal(due, ScanSchedules.IsDue(schedule, null, Now, TimeZoneInfo.Utc));

    /// <summary>The clock set back before the last scan: that time is not trusted.</summary>
    [Fact]
    public void IsDue_LastScanInTheFuture_IsDue()
    {
        Assert.True(ScanSchedules.IsDue(ScanSchedule.Daily, Now.AddHours(3), Now, TimeZoneInfo.Utc));
        Assert.False(ScanSchedules.IsDue(ScanSchedule.Manual, Now.AddHours(3), Now, TimeZoneInfo.Utc));
    }

    /// <summary>Days are the phone's own: 23:00 and 01:00 local are different days even when they
    /// are the same UTC day.</summary>
    [Fact]
    public void IsDue_Daily_UsesTheGivenTimeZonesDays()
    {
        var tokyo = TimeZoneInfo.CreateCustomTimeZone("UTC+9", TimeSpan.FromHours(9), "UTC+9", "UTC+9");
        var last = new DateTime(2026, 11, 27, 14, 0, 0, DateTimeKind.Utc);  // 23:00 there
        var now = new DateTime(2026, 11, 27, 16, 0, 0, DateTimeKind.Utc);   // 01:00 the next day there
        Assert.True(ScanSchedules.IsDue(ScanSchedule.Daily, last, now, tokyo));
        Assert.False(ScanSchedules.IsDue(ScanSchedule.Daily, last, now, TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData("", ScanSchedule.EveryStart)]
    [InlineData(null, ScanSchedule.EveryStart)]
    [InlineData("Daily", ScanSchedule.Daily)]
    [InlineData("weekly", ScanSchedule.Weekly)]
    [InlineData("Manual", ScanSchedule.EveryStart)]   // Manual is ScanOnStartup off, never this field
    [InlineData("2", ScanSchedule.EveryStart)]
    [InlineData("zz", ScanSchedule.EveryStart)]
    public void ParseInterval_ReadsTheSavedValue(string? key, ScanSchedule expected) =>
        Assert.Equal(expected, ScanSchedules.ParseInterval(key));

    [Fact]
    public void Key_SavesEveryStartAsEmpty() =>
        Assert.Equal(new[] { "", "Daily", "Weekly", "" }, Enum.GetValues<ScanSchedule>().Select(ScanSchedules.Key));

    private sealed class NoPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public void ReleaseAccess(string folder) { }
    }

    private const string Tones = "content://com.android.externalstorage.documents/tree/primary%3AMusic%2FTones";

    private async Task<(LibraryViewModel Vm, FakeLibraryService Library, PersistenceService Persistence)> Start(
        Action<AppSettings> saved, Func<DateTime> utcNow)
    {
        var persistence = new PersistenceService(_root);
        var settings = await persistence.LoadSettingsAsync();
        settings.MusicFolders.Add(Tones);
        saved(settings);
        await persistence.SaveSettingsAsync(settings);
        var library = new FakeLibraryService();
        var vm = new LibraryViewModel(library, persistence, new NoPicker(), marshal: a => a()) { UtcNow = utcNow };
        await vm.InitializeAsync();
        return (vm, library, persistence);
    }

    private static async Task Until(Func<Task<bool>> condition)
    {
        for (var waited = 0; !await condition() && waited < 5000; waited += 10) await Task.Delay(10);
    }

    /// <summary>Daily: the start skips the scan while today's is saved, scans once a day has
    /// passed, and saves that scan's time; the saved time shows on the Rescan row too.</summary>
    [Fact]
    public async Task Daily_StartScansOnlyWhenDue_AndSavesTheScansTime()
    {
        var clock = Now;
        var (vm, library, persistence) = await Start(s =>
        {
            s.PhoneScanSchedule = "Daily";
            s.PhoneLastScanUtc = Now.AddHours(-1);
        }, () => clock);
        Assert.Equal(ScanSchedule.Daily, vm.ScanSchedule);
        Assert.Equal(Now.AddHours(-1), vm.LastScanLocal?.ToUniversalTime());

        await vm.ScanAtStartupAsync();
        Assert.Empty(library.ScanPasses);

        clock = Now.AddDays(1);
        await vm.ScanAtStartupAsync();
        Assert.Single(library.ScanPasses);
        await Until(async () => (await persistence.LoadSettingsAsync()).PhoneLastScanUtc == clock);
        Assert.Equal(clock, (await persistence.LoadSettingsAsync()).PhoneLastScanUtc);
    }

    /// <summary>Manual Only: no start scan however old the last one, yet Rescan still scans.</summary>
    [Fact]
    public async Task ManualOnly_NeverScansAtStart_ButRescanStillScans()
    {
        var (vm, library, _) = await Start(s => s.ScanOnStartup = false, () => Now);
        Assert.Equal(ScanSchedule.Manual, vm.ScanSchedule);

        await vm.ScanAtStartupAsync();
        Assert.Empty(library.ScanPasses);

        await vm.RescanCommand.ExecuteAsync(null);
        Assert.Single(library.ScanPasses);
    }

    /// <summary>A pick is saved in one write (Manual as Scan on Startup off, the interval kept for
    /// when it comes back on) and read back by the next start.</summary>
    [Fact]
    public async Task SetScanSchedule_IsSaved_AndReadBackAtTheNextStart()
    {
        var (vm, _, persistence) = await Start(_ => { }, () => Now);
        Assert.Equal(ScanSchedule.EveryStart, vm.ScanSchedule);

        vm.SetScanSchedule(ScanSchedule.Weekly);
        Assert.Equal(ScanSchedule.Weekly, vm.ScanSchedule);
        await Until(async () => (await persistence.LoadSettingsAsync()).PhoneScanSchedule == "Weekly");
        var saved = await persistence.LoadSettingsAsync();
        Assert.Equal("Weekly", saved.PhoneScanSchedule);
        Assert.True(saved.ScanOnStartup);

        vm.SetScanSchedule(ScanSchedule.Manual);
        Assert.Equal(ScanSchedule.Manual, vm.ScanSchedule);
        Assert.False(vm.ScanOnStartup);
        await Until(async () => !(await persistence.LoadSettingsAsync()).ScanOnStartup);
        saved = await persistence.LoadSettingsAsync();
        Assert.False(saved.ScanOnStartup);
        Assert.Equal("Weekly", saved.PhoneScanSchedule);

        var next = new LibraryViewModel(new FakeLibraryService(), persistence, new NoPicker(), marshal: a => a());
        await next.InitializeAsync();
        Assert.Equal(ScanSchedule.Manual, next.ScanSchedule);
        next.ScanOnStartup = true;   // the old switch's way back on: the kept interval
        Assert.Equal(ScanSchedule.Weekly, next.ScanSchedule);
    }
}
