using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Helpers;
using Noctis.Models;
using Avalonia.Headless.XUnit;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #165: the data folder can move off the system drive. The pointer in the home root
/// decides where the data is; a move copies, then finishes at the next launch, and the pointer
/// never names a half-copied folder.
/// </summary>
[Collection("MetadataServiceStatics")]
public class DataLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "DataLocation", Guid.NewGuid().ToString("N"));
    private readonly string _home;

    public DataLocationTests()
    {
        _home = Path.Combine(_root, "home");
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        DataLocation.LastStartup = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>A small but realistic data folder.</summary>
    private void SeedData(string root)
    {
        Write(Path.Combine(root, "settings.json"), "{\"uiScalePercent\":100}");
        Write(Path.Combine(root, "library.json"), "[]");
        Write(Path.Combine(root, "library.db"), "db");
        Write(Path.Combine(root, "artwork", "a.jpg"), "cover-a");
        Write(Path.Combine(root, "artwork", "artists", "b.jpg"), "portrait-b");
        Write(Path.Combine(root, "animated_covers", "c.mp4"), new string('x', 50_000));
        Directory.CreateDirectory(Path.Combine(root, "lyrics_cache"));
    }

    // ── Pointer ──

    [Fact]
    public void NoPointer_TheHomeRootHoldsTheData()
    {
        Assert.Null(DataLocation.ReadPointer(_home));
        Assert.Equal(_home, DataLocation.ConfiguredRoot(_home));
    }

    [Fact]
    public void Pointer_RoundTrips_AndAnEmptyOneDeletesTheFile()
    {
        var target = Path.Combine(_root, "D", "NoctisData");
        DataLocation.WritePointer(_home, new DataLocation.Pointer { DataRoot = target });
        Assert.True(DataLocation.SamePath(target, DataLocation.ConfiguredRoot(_home)));
        Assert.False(File.Exists(Path.Combine(_home, DataLocation.PointerFileName + ".tmp")));

        DataLocation.WritePointer(_home, new DataLocation.Pointer());
        Assert.False(File.Exists(Path.Combine(_home, DataLocation.PointerFileName)));
    }

    [Fact]
    public void DamagedPointer_FallsBackToTheHomeRoot()
    {
        File.WriteAllText(Path.Combine(_home, DataLocation.PointerFileName), "{ not json");
        Assert.Equal(_home, DataLocation.ConfiguredRoot(_home));
    }

    // ── Classify / plan ──

    [Fact]
    public void Classify_TellsTheKindsOfFolderApart()
    {
        Assert.Equal(DataLocation.FolderKind.Missing, DataLocation.Classify(Path.Combine(_root, "nope")));

        // The home root after a move away: only the pointer and the Unix instance lock.
        File.WriteAllText(Path.Combine(_home, DataLocation.PointerFileName), "{}");
        File.WriteAllText(Path.Combine(_home, ".instance.lock"), "");
        Assert.Equal(DataLocation.FolderKind.Empty, DataLocation.Classify(_home));

        var data = Path.Combine(_root, "data");
        SeedData(data);
        Assert.Equal(DataLocation.FolderKind.NoctisData, DataLocation.Classify(data));

        var marked = Path.Combine(_root, "marked");
        Write(Path.Combine(marked, DataLocation.MarkerFileName), "");
        Assert.Equal(DataLocation.FolderKind.NoctisData, DataLocation.Classify(marked));

        // A half copy carries settings.json too, but is never offered as data.
        var half = Path.Combine(_root, "half");
        SeedData(half);
        Write(Path.Combine(half, DataLocation.IncompleteFileName), "");
        Assert.Equal(DataLocation.FolderKind.LeftoverMove, DataLocation.Classify(half));

        var docs = Path.Combine(_root, "docs");
        Write(Path.Combine(docs, "thesis.docx"), "x");
        Assert.Equal(DataLocation.FolderKind.Other, DataLocation.Classify(docs));
    }

    [Fact]
    public void PlanChange_EmptyFolder_TakesTheData()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        var plan = DataLocation.PlanChange(_home, empty, _home);
        Assert.Equal(DataLocation.PlanKind.Move, plan.Kind);
        Assert.True(DataLocation.SamePath(empty, plan.Target));

        // A folder that does not exist yet is created by the move.
        var fresh = Path.Combine(_root, "fresh", "Noctis");
        Assert.Equal(DataLocation.PlanKind.Move, DataLocation.PlanChange(_home, fresh, _home).Kind);
        Assert.False(Directory.Exists(fresh)); // the write probe leaves nothing behind
    }

    [Fact]
    public void PlanChange_FolderWithOtherFiles_GetsANoctisFolderInside()
    {
        var drive = Path.Combine(_root, "drive");
        Write(Path.Combine(drive, "Movies", "a.mkv"), "x");
        var plan = DataLocation.PlanChange(_home, drive, _home);
        Assert.Equal(DataLocation.PlanKind.Move, plan.Kind);
        Assert.True(DataLocation.SamePath(Path.Combine(drive, "Noctis"), plan.Target));

        // ...unless that one is taken too.
        Write(Path.Combine(drive, "Noctis", "readme.txt"), "x");
        Assert.Equal(DataLocation.PlanKind.NotEmpty, DataLocation.PlanChange(_home, drive, _home).Kind);
    }

    [Fact]
    public void PlanChange_NoctisDataFromAnotherComputer_IsOfferedAsIs()
    {
        var copied = Path.Combine(_root, "usb", "NoctisData");
        SeedData(copied);
        var plan = DataLocation.PlanChange(_home, copied, _home);
        Assert.Equal(DataLocation.PlanKind.UseExisting, plan.Kind);

        // Picking the drive that holds it as "Noctis" finds it too.
        var drive = Path.Combine(_root, "drive2");
        SeedData(Path.Combine(drive, "Noctis"));
        Write(Path.Combine(drive, "other.txt"), "x");
        var viaParent = DataLocation.PlanChange(_home, drive, _home);
        Assert.Equal(DataLocation.PlanKind.UseExisting, viaParent.Kind);
        Assert.True(DataLocation.SamePath(Path.Combine(drive, "Noctis"), viaParent.Target));
    }

    [Fact]
    public void PlanChange_RefusesTheCurrentFolderAndFoldersInsideIt()
    {
        SeedData(_home);
        Assert.Equal(DataLocation.PlanKind.AlreadyHere, DataLocation.PlanChange(_home, _home, _home).Kind);
        Assert.Equal(DataLocation.PlanKind.AlreadyHere, DataLocation.PlanChange(_home, _home + Path.DirectorySeparatorChar, _home).Kind);
        Assert.Equal(DataLocation.PlanKind.InsideDataFolder,
            DataLocation.PlanChange(_home, Path.Combine(_home, "sub"), _home).Kind);
        // The parent (it holds the home root, so it is "other") resolves to <parent>/Noctis.
        var custom = Path.Combine(_root, "custom");
        SeedData(custom);
        Assert.Equal(DataLocation.PlanKind.InsideDataFolder,
            DataLocation.PlanChange(custom, Path.Combine(_home, "nested"), _home).Kind);
    }

    // ── The move ──

    [Fact]
    public void Move_CopiesThenFinishesAtLaunch_MirroringChangesMadeInBetween()
    {
        SeedData(_home);
        var target = Path.Combine(_root, "D", "NoctisData");

        DataLocation.CopyForMove(_home, _home, target, progress: null, CancellationToken.None);

        // Step 1 done: nothing switched, the copy is flagged incomplete, the move is pending.
        Assert.Equal(_home, DataLocation.ConfiguredRoot(_home));
        Assert.True(File.Exists(Path.Combine(target, DataLocation.IncompleteFileName)));
        Assert.Equal(DataLocation.FolderKind.LeftoverMove, DataLocation.Classify(target));
        Assert.True(DataLocation.SamePath(target, DataLocation.ReadPointer(_home)!.PendingMoveTo!));

        // The app keeps running until the restart: it writes, adds and deletes files.
        File.WriteAllText(Path.Combine(_home, "settings.json"), "{\"uiScalePercent\":150}");
        Write(Path.Combine(_home, "artwork", "new.jpg"), "new-cover");
        File.Delete(Path.Combine(_home, "artwork", "a.jpg"));
        Write(Path.Combine(_home, "library.db-wal"), "wal");

        var start = DataLocation.PrepareAtStartup(_home);

        Assert.Equal(DataLocation.StartupStatus.Ok, start.Status);
        Assert.Null(start.MoveError);
        Assert.True(DataLocation.SamePath(_home, start.MovedFrom!));
        Assert.True(DataLocation.SamePath(target, DataLocation.ConfiguredRoot(_home)));
        Assert.Equal("{\"uiScalePercent\":150}", File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.True(File.Exists(Path.Combine(target, "artwork", "new.jpg")));
        Assert.False(File.Exists(Path.Combine(target, "artwork", "a.jpg")));
        Assert.True(File.Exists(Path.Combine(target, "library.db-wal")));
        Assert.Equal(50_000, new FileInfo(Path.Combine(target, "animated_covers", "c.mp4")).Length);
        Assert.True(Directory.Exists(Path.Combine(target, "lyrics_cache")));          // empty folders too
        Assert.True(File.Exists(Path.Combine(target, DataLocation.MarkerFileName)));
        Assert.False(File.Exists(Path.Combine(target, DataLocation.IncompleteFileName)));

        // The old copy is set aside at once (no longer looks like data), then deleted.
        Assert.False(File.Exists(Path.Combine(_home, "settings.json")));
        Assert.Equal(DataLocation.FolderKind.Empty, DataLocation.Classify(_home));
        var aside = DataLocation.ReadPointer(_home)!.OldDataToDelete!;
        Assert.True(Directory.Exists(aside));

        DataLocation.CleanupOldData(_home, target);
        Assert.False(Directory.Exists(aside));
        var pointer = DataLocation.ReadPointer(_home)!;
        Assert.Null(pointer.OldDataToDelete);
        Assert.Null(pointer.PendingMoveTo);
        Assert.True(File.Exists(Path.Combine(_home, DataLocation.PointerFileName)));   // the pointer stays home
    }

    /// <summary>A FAT32/exFAT stick keeps file times coarser than NTFS: the launch must not take
    /// every file for changed (copying it all again with no window up), nor miss a real change.</summary>
    [Fact]
    public void Move_ToAStickThatRoundsTimes_CopiesOnlyWhatChanged()
    {
        SeedData(_home);
        var target = Path.Combine(_root, "usb", "NoctisData");
        DataLocation.CopyForMove(_home, _home, target, null, CancellationToken.None);

        // What the stick kept: every time off by a second.
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            if (!Path.GetFileName(file).StartsWith(".noctis", StringComparison.Ordinal))
                File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddSeconds(1));
        var coverTime = File.GetLastWriteTimeUtc(Path.Combine(target, "animated_covers", "c.mp4"));
        // Saved again after the copy, same size, within the stick's rounding.
        File.WriteAllText(Path.Combine(_home, "settings.json"), "{\"uiScalePercent\":150}");

        var start = DataLocation.PrepareAtStartup(_home);

        Assert.Null(start.MoveError);
        Assert.Equal("{\"uiScalePercent\":150}", File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.Equal(coverTime, File.GetLastWriteTimeUtc(Path.Combine(target, "animated_covers", "c.mp4"))); // not copied again
    }

    [Fact]
    public void Move_BackToTheDefaultFolder_RemovesThePointerAndTheOldFolder()
    {
        var custom = Path.Combine(_root, "D", "NoctisData");
        SeedData(custom);
        DataLocation.UseExisting(_home, custom);
        Assert.True(DataLocation.SamePath(custom, DataLocation.ConfiguredRoot(_home)));

        var plan = DataLocation.PlanChange(custom, _home, _home);
        Assert.Equal(DataLocation.PlanKind.Move, plan.Kind);
        DataLocation.CopyForMove(_home, custom, plan.Target, null, CancellationToken.None);
        var start = DataLocation.PrepareAtStartup(_home);

        Assert.Null(start.MoveError);
        Assert.Equal(_home, DataLocation.ConfiguredRoot(_home));
        Assert.Null(DataLocation.ReadPointer(_home)!.DataRoot);
        Assert.True(File.Exists(Path.Combine(_home, "library.db")));

        DataLocation.CleanupOldData(_home, _home);
        Assert.False(Directory.Exists(custom));
        Assert.False(File.Exists(Path.Combine(_home, DataLocation.PointerFileName)));
    }

    /// <summary>Default → D → default → E, then Use Default again: the default folder holds
    /// nothing, so it must take the data, not be offered as "Noctis data already here" (the
    /// marker the previous landing left there named an emptied folder).</summary>
    [Fact]
    public void Move_AwayFromTheDefaultFolderAgain_DoesNotLeaveItLookingLikeData()
    {
        var custom = Path.Combine(_root, "D", "NoctisData");
        SeedData(custom);
        DataLocation.UseExisting(_home, custom);
        DataLocation.CopyForMove(_home, custom, _home, null, CancellationToken.None);
        DataLocation.PrepareAtStartup(_home);
        DataLocation.CleanupOldData(_home, _home);
        Assert.True(File.Exists(Path.Combine(_home, DataLocation.MarkerFileName))); // landed home

        var other = Path.Combine(_root, "E", "NoctisData");
        Assert.Equal(DataLocation.PlanKind.Move, DataLocation.PlanChange(_home, other, _home).Kind);
        DataLocation.CopyForMove(_home, _home, other, null, CancellationToken.None);
        var start = DataLocation.PrepareAtStartup(_home);
        Assert.Null(start.MoveError);
        DataLocation.CleanupOldData(_home, other);
        Assert.True(DataLocation.SamePath(other, DataLocation.ConfiguredRoot(_home)));

        Assert.Equal(DataLocation.FolderKind.Empty, DataLocation.Classify(_home));
        Assert.Equal(DataLocation.PlanKind.Move, DataLocation.PlanChange(other, _home, _home).Kind);
    }

    [Fact]
    public void Move_TargetGoneAtLaunch_StaysOnTheOldFolder_AndSaysWhy()
    {
        SeedData(_home);
        var target = Path.Combine(_root, "usb", "NoctisData");
        DataLocation.CopyForMove(_home, _home, target, null, CancellationToken.None);
        Directory.Delete(Path.Combine(_root, "usb"), recursive: true);   // drive unplugged before the restart

        var start = DataLocation.PrepareAtStartup(_home);

        Assert.NotNull(start.MoveError);
        Assert.Equal(DataLocation.StartupStatus.Ok, start.Status);
        Assert.Equal(_home, DataLocation.ConfiguredRoot(_home));
        Assert.Null(DataLocation.ReadPointer(_home)?.PendingMoveTo);
        Assert.True(File.Exists(Path.Combine(_home, "settings.json")));                // untouched
    }

    [Fact]
    public void Move_TargetNoLongerOurCopy_IsNotOverwritten()
    {
        SeedData(_home);
        var target = Path.Combine(_root, "D", "NoctisData");
        DataLocation.CopyForMove(_home, _home, target, null, CancellationToken.None);
        File.Delete(Path.Combine(target, DataLocation.IncompleteFileName));          // someone else's folder now
        Write(Path.Combine(target, "mine.txt"), "keep me");

        var start = DataLocation.PrepareAtStartup(_home);

        Assert.NotNull(start.MoveError);
        Assert.Equal(_home, DataLocation.ConfiguredRoot(_home));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(target, "mine.txt")));
    }

    [Fact]
    public void CopyCancelled_RemovesTheCopy_AndNothingIsPending()
    {
        SeedData(_home);
        var target = Path.Combine(_root, "D", "NoctisData");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            DataLocation.CopyForMove(_home, _home, target, null, cts.Token));

        Assert.Null(DataLocation.ReadPointer(_home));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void SyncTree_KeepsTimes_SoASecondPassCopiesNothing()
    {
        SeedData(_home);
        var target = Path.Combine(_root, "copy");
        DataLocation.SyncTree(_home, target, bestEffort: false, null, CancellationToken.None);

        long reported = -1;
        var progress = new SyncProgress(p => reported = p.Total);
        DataLocation.SyncTree(_home, target, bestEffort: false, progress, CancellationToken.None);
        Assert.Equal(0, reported);
        DataLocation.Verify(_home, target);
    }

    private sealed class SyncProgress(Action<(long Done, long Total)> report) : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) value) => report(value);
    }

    // ── Startup check ──

    [Fact]
    public void Startup_ChosenFolderMissing_IsUnavailable_NeverAFreshEmptyLibrary()
    {
        var gone = Path.Combine(_root, "E", "NoctisData");
        DataLocation.WritePointer(_home, new DataLocation.Pointer { DataRoot = gone });

        var start = DataLocation.PrepareAtStartup(_home);
        Assert.Equal(DataLocation.StartupStatus.Unavailable, start.Status);
        Assert.False(Directory.Exists(gone));

        // A mount point that is there but empty (drive not mounted) is no better.
        Directory.CreateDirectory(gone);
        Assert.Equal(DataLocation.StartupStatus.Unavailable, DataLocation.PrepareAtStartup(_home).Status);

        SeedData(gone);
        Assert.Equal(DataLocation.StartupStatus.Ok, DataLocation.PrepareAtStartup(_home).Status);
    }

    [Fact]
    public void Startup_HomeRoot_IsAlwaysFine_EvenOnAFreshInstall()
    {
        var fresh = Path.Combine(_root, "fresh-home");
        Assert.Equal(DataLocation.StartupStatus.Ok, DataLocation.PrepareAtStartup(fresh).Status);
    }

    [Fact]
    public void CleanupOldData_NeverDeletesTheLiveFolder()
    {
        var live = Path.Combine(_root, "live");
        SeedData(live);
        DataLocation.WritePointer(_home, new DataLocation.Pointer { DataRoot = live, OldDataToDelete = live });

        DataLocation.CleanupOldData(_home, live);

        Assert.True(File.Exists(Path.Combine(live, "settings.json")));
        Assert.Null(DataLocation.ReadPointer(_home)!.OldDataToDelete);
    }

    // ── Settings › Advanced › Data Folder ──

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private SettingsViewModel DataFolderVm()
    {
        DataLocation.LastStartup = null;
        SeedData(_home);
        var vm = new SettingsViewModel(new PersistenceService(Path.Combine(_root, "vm-data")), new FakeLibraryService(), new NoOpPlayHistoryService())
        {
            DataFolderHomeRoot = () => _home,
            DataFolderCurrentRoot = () => _home,
        };
        vm.RefreshDataFolderState();
        return vm;
    }

    [AvaloniaFact]
    public async Task SettingsCard_FolderWithNoctisData_AsksFirst_ThenSwitchesAtRestart_AndCancelTakesItBack()
    {
        var vm = DataFolderVm();
        Assert.Equal(_home, vm.DataFolderPath);
        Assert.False(vm.IsDataFolderCustom);
        var usb = Path.Combine(_root, "usb", "NoctisData");
        SeedData(usb);

        await vm.ApplyDataFolderChoiceAsync(usb);
        Assert.True(vm.IsDataFolderUseExistingVisible);
        Assert.Null(DataLocation.ReadPointer(_home));                                   // nothing yet

        await vm.ConfirmUseExistingDataFolderCommand.ExecuteAsync(null);
        Assert.True(DataLocation.SamePath(usb, DataLocation.ConfiguredRoot(_home)));
        Assert.True(vm.IsDataFolderRestartNeeded);
        Assert.True(vm.IsDataFolderCustom);
        Assert.Contains(usb, vm.DataFolderStatus);
        Assert.True(File.Exists(Path.Combine(_home, "settings.json")));               // current data untouched

        await vm.CancelDataFolderChangeCommand.ExecuteAsync(null);
        Assert.Equal(_home, DataLocation.ConfiguredRoot(_home));
        Assert.False(vm.IsDataFolderRestartNeeded);
    }

    [AvaloniaFact]
    public async Task SettingsCard_EmptyFolder_CopiesAndWaitsForTheRestart()
    {
        var vm = DataFolderVm();
        var target = Path.Combine(_root, "D", "NoctisData");

        await vm.ApplyDataFolderChoiceAsync(target);

        Assert.False(vm.IsDataFolderCopying);
        Assert.True(vm.IsDataFolderRestartNeeded);
        Assert.False(vm.IsDataFolderErrorVisible);
        Assert.True(File.Exists(Path.Combine(target, "library.db")));
        Assert.True(DataLocation.SamePath(target, DataLocation.ReadPointer(_home)!.PendingMoveTo!));
        Assert.Equal(_home, DataLocation.ConfiguredRoot(_home));                          // not switched yet

        // Changing the mind before the restart drops the copy.
        await vm.CancelDataFolderChangeCommand.ExecuteAsync(null);
        Assert.Null(DataLocation.ReadPointer(_home)?.PendingMoveTo);
        Assert.False(Directory.Exists(target));
        Assert.False(vm.IsDataFolderRestartNeeded);
    }

    [AvaloniaFact]
    public async Task SettingsCard_FolderWithOtherFiles_InsideNoctisAlreadyTaken_SaysWhy()
    {
        var vm = DataFolderVm();
        var docs = Path.Combine(_root, "docs");
        Write(Path.Combine(docs, "a.txt"), "x");
        Write(Path.Combine(docs, "Noctis", "b.txt"), "x");

        await vm.ApplyDataFolderChoiceAsync(docs);

        Assert.True(vm.IsDataFolderErrorVisible);
        Assert.Null(DataLocation.ReadPointer(_home));
    }

    // ── Paths saved inside the data ──

    [Fact]
    public void Rebase_FindsTheFileInTheCurrentFolder_WhenTheSavedPathIsGone()
    {
        var data = Path.Combine(_root, "data");
        Write(Path.Combine(data, "profile", "avatar.png"), "x");
        var current = Path.Combine(data, "profile", "avatar.png");

        Assert.Equal(current, DataLocation.Rebase(@"C:\Users\old\AppData\Roaming\Noctis\profile\avatar.png", data, "profile"));
        Assert.Equal(current, DataLocation.Rebase("/home/old/.config/Noctis/profile/avatar.png", data, "profile"));
        Assert.Equal(current, DataLocation.Rebase(current, data, "profile"));
        // Nothing to find: left as saved.
        Assert.Equal("/x/profile/other.png", DataLocation.Rebase("/x/profile/other.png", data, "profile"));
        Assert.Equal("", DataLocation.Rebase("", data, "profile"));
    }

    [Fact]
    public async Task Persistence_RebasesSavedPathsIntoTheFolderItLoadsFrom()
    {
        var oldRoot = Path.Combine(_root, "old");
        var newRoot = Path.Combine(_root, "new");
        var old = new PersistenceService(oldRoot);
        Write(Path.Combine(oldRoot, "profile", "avatar.png"), "a");
        Write(Path.Combine(oldRoot, "lyrics_background", "background.mp4"), "v");
        Write(Path.Combine(oldRoot, "playlist_covers", "p.jpg"), "c");
        await old.SaveSettingsAsync(new AppSettings
        {
            ProfileAvatarPath = Path.Combine(oldRoot, "profile", "avatar.png"),
            LyricsBackgroundMediaPath = Path.Combine(oldRoot, "lyrics_background", "background.mp4"),
        });
        await old.SavePlaylistsAsync(new List<Playlist> { new() { Name = "P", CoverArtPath = Path.Combine(oldRoot, "playlist_covers", "p.jpg") } });

        Directory.Move(oldRoot, newRoot);   // the data folder moved (or was copied to another PC)
        var moved = new PersistenceService(newRoot);
        var settings = await moved.LoadSettingsAsync();
        var playlists = await moved.LoadPlaylistsAsync();

        Assert.Equal(Path.Combine(newRoot, "profile", "avatar.png"), settings.ProfileAvatarPath);
        Assert.Equal(Path.Combine(newRoot, "lyrics_background", "background.mp4"), settings.LyricsBackgroundMediaPath);
        Assert.Equal(Path.Combine(newRoot, "playlist_covers", "p.jpg"), playlists.Single().CoverArtPath);
    }
}
