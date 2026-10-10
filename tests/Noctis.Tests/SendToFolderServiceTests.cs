using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Send to Folder's planner and copier against real temp folders (never the owner's music):
/// lyrics sidecars, names the destination can't hold, cancel mid-file, a vanished drive,
/// never overwriting. The "Bug:" cases failed on the pre-revamp code (cfb840e7).
/// </summary>
public sealed class SendToFolderServiceTests : IDisposable
{
    private const string Pattern = "{AlbumArtist}/{Album}/{TrackNo} {Title}";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "stf-" + Guid.NewGuid().ToString("N"));
    private readonly string _src;
    private readonly string _dst;

    public SendToFolderServiceTests()
    {
        _src = Path.Combine(_root, "Library");
        _dst = Path.Combine(_root, "USB");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_dst);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Src(string name, int bytes = 100)
    {
        var path = Path.Combine(_src, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[bytes];
        new Random(bytes).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static Track T(string path, string title = "Song", int no = 1) => new()
    {
        Id = Guid.NewGuid(), FilePath = path, Title = title, Artist = "Artist", AlbumArtist = "Artist",
        Album = "Album", TrackNumber = no,
    };

    private static Task<SendToFolderResult> Copy(IReadOnlyList<SendToFolderItem> plan, CancellationToken ct = default)
        => new SendToFolderService().CopyAsync(plan, null, ct);

    // ── Lyrics ──

    /// <summary>Bug: only "song.lrc" went along; the .elrc / .ttml / .lyricsfile the lyrics page
    /// reads (word-synced lyrics from Lyrics Studio, Apple-style TTML) stayed behind.</summary>
    [Fact]
    public async Task EveryLyricsFormat_GoesAlong()
    {
        var song = Src("a.flac");
        foreach (var ext in new[] { ".lrc", ".elrc", ".ttml", ".lyricsfile" })
            File.WriteAllText(Path.ChangeExtension(song, ext), "lyrics" + ext);
        File.WriteAllText(Path.ChangeExtension(song, ".txt"), "not lyrics");

        var plan = new SendToFolderService().Plan(new[] { T(song) }, _dst, null, includeLyrics: true);
        var result = await Copy(plan);

        Assert.Equal(1, result.Copied);
        Assert.Equal(4, result.LyricsCopied);
        foreach (var ext in new[] { ".lrc", ".elrc", ".ttml", ".lyricsfile" })
            Assert.Equal("lyrics" + ext, File.ReadAllText(Path.Combine(_dst, "a" + ext)));
        Assert.False(File.Exists(Path.Combine(_dst, "a.txt")));
    }

    [Fact]
    public async Task LyricsOff_CopiesNoSidecar()
    {
        var song = Src("a.flac");
        File.WriteAllText(Path.ChangeExtension(song, ".lrc"), "x");
        var plan = new SendToFolderService().Plan(new[] { T(song) }, _dst, null, includeLyrics: false);
        Assert.Empty(plan[0].Sidecars);
        await Copy(plan);
        Assert.False(File.Exists(Path.Combine(_dst, "a.lrc")));
    }

    /// <summary>Bug: a song already in the folder was skipped together with its lyrics, so
    /// a second run with "Include lyrics" on added nothing. Now the missing lyrics go over and
    /// lyrics already there are left alone.</summary>
    [Fact]
    public async Task AlreadyThere_GetsOnlyTheLyricsItLacks()
    {
        var song = Src("a.flac");
        File.WriteAllText(Path.ChangeExtension(song, ".lrc"), "new lrc");
        File.WriteAllText(Path.ChangeExtension(song, ".ttml"), "new ttml");
        File.Copy(song, Path.Combine(_dst, "a.flac"));
        File.WriteAllText(Path.Combine(_dst, "a.lrc"), "user edited");

        var plan = new SendToFolderService().Plan(new[] { T(song) }, _dst, null, includeLyrics: true);
        Assert.Equal(SendToFolderAction.SkipIdentical, plan[0].Action);
        Assert.Equal(new[] { Path.Combine(_dst, "a.ttml") }, plan[0].Sidecars.Select(s => s.Target));

        var result = await Copy(plan);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, result.LyricsCopied);
        Assert.Equal("new ttml", File.ReadAllText(Path.Combine(_dst, "a.ttml")));
        Assert.Equal("user edited", File.ReadAllText(Path.Combine(_dst, "a.lrc")));
    }

    /// <summary>Bug: "song.flac" and "song.mp3" (different folders) both planned "song.lrc" at
    /// the destination; the second was dropped and the .mp3 showed the .flac's lyrics.</summary>
    [Fact]
    public void SameNameDifferentFormat_KeepTheirOwnLyrics()
    {
        var a = T(TestPaths.Primary("Music", "x", "song.flac"));
        var b = T(TestPaths.Primary("Music", "y", "song.mp3"));
        var files = new Dictionary<string, long>
        {
            [a.FilePath] = 1, [b.FilePath] = 2,
            [Path.ChangeExtension(a.FilePath, ".lrc")] = 3, [Path.ChangeExtension(b.FilePath, ".lrc")] = 4,
        };
        var root = TestPaths.Other("USB");
        var plan = SendToFolderPlanner.Plan(new[] { a, b }, root, null, true,
            p => files.TryGetValue(p, out var l) ? new FileProbe(l) : null);

        Assert.Equal(Path.Combine(root, "song.flac"), plan[0].TargetPath);
        Assert.Equal(Path.Combine(root, "song.lrc"), plan[0].SidecarTarget);
        Assert.Equal(Path.Combine(root, "song (2).mp3"), plan[1].TargetPath);
        Assert.Equal(Path.Combine(root, "song (2).lrc"), plan[1].SidecarTarget);
        Assert.Equal(SendToFolderAction.Renamed, plan[1].Action);
    }

    /// <summary>A lyrics file spelled "Song.LRC" (case-sensitive Linux) is found the way the
    /// lyrics page finds it, and keeps its spelling.</summary>
    [Fact]
    public void UpperCaseLyricsExtension_IsFound()
    {
        var a = T(TestPaths.Primary("Music", "a.flac"));
        var upper = Path.Combine(Path.GetDirectoryName(a.FilePath)!, "a.LRC");
        var files = new Dictionary<string, long>(StringComparer.Ordinal) { [a.FilePath] = 1, [upper] = 2 };
        var root = TestPaths.Other("USB");
        var plan = SendToFolderPlanner.Plan(new[] { a }, root, null, true,
            p => files.TryGetValue(p, out var l) ? new FileProbe(l) : null);
        var sidecar = Assert.Single(plan[0].Sidecars);
        Assert.Equal(upper, sidecar.Source);
        Assert.Equal(Path.Combine(root, "a.LRC"), sidecar.Target);
    }

    // ── Names ──

    /// <summary>Bug: the same track twice in the selection (a playlist holding it twice), with
    /// folders on, landed as "01 Song (2).flac" — the organizer's collision suffix for the
    /// duplicate overwrote the first entry's path under the shared track Id.</summary>
    [Fact]
    public void SameTrackTwice_Organized_IsOneCopyWithoutASuffix()
    {
        var a = T(TestPaths.Primary("Music", "a.flac"));
        var root = TestPaths.Other("USB");
        var plan = SendToFolderPlanner.Plan(new[] { a, a }, root, Pattern, false,
            p => p == a.FilePath ? new FileProbe(1) : null);
        Assert.Equal(Path.Combine(root, "Artist", "Album", "01 Song.flac"), Assert.Single(plan).TargetPath);
    }

    /// <summary>Bug: a 300-character title made a 300-character file name, which no file system
    /// (NTFS, exFAT, FAT32: 255 max) can hold — the copy failed with "The filename, directory
    /// name, or volume label syntax is incorrect".</summary>
    [Fact]
    public async Task OverlongTag_IsShortened_AndCopies()
    {
        var song = Src("a.flac");
        var track = T(song, title: new string('x', 300));
        track.Album = new string('y', 280);
        var plan = new SendToFolderService().Plan(new[] { track }, _dst, Pattern, false);
        var target = plan[0].TargetPath;
        foreach (var part in Path.GetRelativePath(_dst, target).Split(Path.DirectorySeparatorChar))
            Assert.True(part.Length <= SendToFolderPlanner.MaxNameLength, $"{part.Length}-character name");
        Assert.EndsWith(".flac", target);

        var result = await Copy(plan);
        Assert.Equal(0, result.Failed);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void ClampNames_NeverSplitsASurrogatePair()
    {
        var root = TestPaths.Other("USB");
        // "𝄞" is two UTF-16 units; put one across the cut.
        var name = new string('a', SendToFolderPlanner.MaxNameLength - 5) + "𝄞𝄞𝄞𝄞";
        var clamped = SendToFolderPlanner.ClampNames(root, Path.Combine(root, name + ".flac"));
        var stem = Path.GetFileNameWithoutExtension(clamped);
        Assert.False(char.IsHighSurrogate(stem[^1]));
        Assert.True(stem.Length + 5 <= SendToFolderPlanner.MaxNameLength);
    }

    [Fact]
    public void InvalidCharactersInTags_BecomeUnderscores()
    {
        var a = T(TestPaths.Primary("Music", "a.flac"), title: "Intro: Live? <Edit>");
        a.AlbumArtist = "AC/DC";
        var root = TestPaths.Other("USB");
        var plan = SendToFolderPlanner.Plan(new[] { a }, root, Pattern, false, p => p == a.FilePath ? new FileProbe(1) : null);
        var rel = Path.GetRelativePath(root, plan[0].TargetPath);
        Assert.Equal(Path.Combine("AC_DC", "Album", "01 Intro_ Live_ _Edit_.flac"), rel);
    }

    // ── Copying ──

    /// <summary>Destination is the song's own folder: identical file, nothing written (the
    /// source is never opened for writing).</summary>
    [Fact]
    public async Task DestinationIsTheLibraryFolder_SkipsTheSameFile()
    {
        var song = Src("a.flac");
        var before = File.ReadAllBytes(song);
        var plan = new SendToFolderService().Plan(new[] { T(song) }, _src, null, false);
        Assert.Equal(SendToFolderAction.SkipIdentical, plan[0].Action);
        Assert.Equal(song, plan[0].TargetPath);

        var result = await Copy(plan);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(before, File.ReadAllBytes(song));
    }

    /// <summary>A file that appears at the target after planning is never overwritten.</summary>
    [Fact]
    public async Task TargetAppearsAfterPlanning_IsNotOverwritten()
    {
        var song = Src("a.flac");
        var plan = new SendToFolderService().Plan(new[] { T(song) }, _dst, null, false);
        File.WriteAllText(plan[0].TargetPath, "someone else's");

        var result = await Copy(plan);
        Assert.Equal(1, result.Failed);
        Assert.Single(result.Errors);
        Assert.Equal("someone else's", File.ReadAllText(plan[0].TargetPath));
    }

    [Fact]
    public async Task Copy_KeepsContentAndModifiedTime()
    {
        var song = Src("a.flac", bytes: 3 * 1024 * 1024 + 17);
        var stamp = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(song, stamp);
        var plan = new SendToFolderService().Plan(new[] { T(song) }, _dst, Pattern, false);
        var result = await Copy(plan);
        Assert.Equal(1, result.Copied);
        Assert.Equal(File.ReadAllBytes(song), File.ReadAllBytes(plan[0].TargetPath));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(plan[0].TargetPath));
    }

    /// <summary>Stop (or closing the dialog) mid-file stops within a block and removes the
    /// half-written file; files finished before it stay.</summary>
    [Fact]
    public async Task CancelMidFile_RemovesThePartialFile_KeepsFinishedOnes()
    {
        var first = Src("a.flac", bytes: 1000);
        var big = Src("b.flac", bytes: 4 * 1024 * 1024);
        var plan = new SendToFolderService().Plan(new[] { T(first), T(big) }, _dst, null, false);
        using var cts = new CancellationTokenSource();
        var reports = new List<SendToFolderProgress>();
        var progress = new SyncProgress(reports.Add);

        var result = await SendToFolderService.RunAsync(plan, progress, cts.Token, written =>
        {
            // First block of the big file is on disk: stop now.
            if (written.EndsWith("b.flac", StringComparison.Ordinal)) cts.Cancel();
        });

        Assert.True(result.Cancelled);
        Assert.Equal(1, result.Copied);
        Assert.True(File.Exists(Path.Combine(_dst, "a.flac")));
        Assert.False(File.Exists(Path.Combine(_dst, "b.flac")), "half-written file left behind");
        Assert.Contains(reports, r => r.Index == 0 && r.Outcome == SendToFolderOutcome.Copied);
        Assert.DoesNotContain(reports, r => r.Index == 1 && r.Outcome == SendToFolderOutcome.Copied);
    }

    /// <summary>The drive disappears mid-run (stick pulled): one clear line and the run stops,
    /// instead of one "could not find a part of the path" per remaining song. Simulated with a
    /// drive letter that isn't mounted.</summary>
    [Fact]
    public async Task DestinationDriveGone_StopsWithOneClearError()
    {
        if (!OperatingSystem.IsWindows()) return;
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var free = "QRSTUVWXYZ".FirstOrDefault(c => !used.Contains(c));
        if (free == default) return;
        var gone = $"{free}:\\Music";
        var tracks = new[] { T(Src("a.flac")), T(Src("b.flac")), T(Src("c.flac")) };
        var plan = tracks.Select(t => new SendToFolderItem(t, t.FilePath, Path.Combine(gone, Path.GetFileName(t.FilePath)),
            SendToFolderAction.Copy, null, null)).ToList();
        var reports = new List<SendToFolderProgress>();

        var result = await SendToFolderService.RunAsync(plan, new SyncProgress(reports.Add), CancellationToken.None, null);

        Assert.Equal(3, result.Failed);
        Assert.Equal(2, result.Errors.Count); // the first file's own error, then the one summary
        Assert.Equal(Noctis.Localization.Loc.T("SendToFolder.DriveGone"), result.Errors[^1]);
        Assert.Equal(3, reports.Count(r => r.Outcome == SendToFolderOutcome.Failed));
    }

    [Fact]
    public async Task Progress_ReportsEachSongStartAndEnd_InOrder()
    {
        var a = Src("a.flac");
        var b = Src("b.flac");
        File.Copy(b, Path.Combine(_dst, "b.flac"));
        var plan = new SendToFolderService().Plan(new[] { T(a), T(b) }, _dst, null, false);
        var reports = new List<SendToFolderProgress>();
        await SendToFolderService.RunAsync(plan, new SyncProgress(reports.Add), CancellationToken.None, null);

        Assert.Equal(new[]
        {
            (0, 0, SendToFolderOutcome.Working), (1, 0, SendToFolderOutcome.Copied),
            (1, 1, SendToFolderOutcome.Working), (2, 1, SendToFolderOutcome.Skipped),
            (2, -1, SendToFolderOutcome.Working),
        }, reports.Select(r => (r.Done, r.Index, r.Outcome)));
    }

    private sealed class SyncProgress(Action<SendToFolderProgress> apply) : IProgress<SendToFolderProgress>
    {
        public void Report(SendToFolderProgress value) => apply(value);
    }
}
