using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The scan must not care where files come from: a source that yields entries with no
/// local path (the Android SAF source) gets its tags read through the entry's stream and
/// its change detection from the entry's size/mtime. Desktop behaviour is the
/// LocalFileSystemSource, pinned here against the same fixture.
/// </summary>
/// <remarks>
/// A scan mutates MetadataService's static UseEmbeddedArtwork/MusicRootFolders mirrors
/// (see ScanCoreAsync), same as the other tests in the shared "MetadataServiceStatics"
/// collection — joining it keeps this class from racing them under parallel execution.
/// </remarks>
[Collection("MetadataServiceStatics")]
public class FileSystemSourceScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private readonly string _music;

    public FileSystemSourceScanTests()
    {
        _music = Path.Combine(_root, "music", "Album");
        Directory.CreateDirectory(_music);
        WriteMp3(Path.Combine(_music, "01 - First.mp3"), "First", "Tester", "Fixture Album");
        WriteMp3(Path.Combine(_music, "02 - Second.mp3"), "Second", "Tester", "Fixture Album");
        File.WriteAllText(Path.Combine(_music, "notes.txt"), "not audio");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static void WriteMp3(string path, string title, string artist, string album)
    {
        // A minimal MPEG frame header + silence keeps TagLib happy without a real encoder:
        // one MP3 frame (MPEG-1 Layer III, 128 kbps, 44.1 kHz) padded to its 417-byte size.
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        using var f = TagLib.File.Create(path);
        f.Tag.Title = title; f.Tag.Performers = new[] { artist }; f.Tag.Album = album;
        f.Save();
    }

    private (LibraryService Library, PersistenceService Persistence) MakeLibrary(IFileSystemSource source, bool scanPastUnavailable = false)
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var index = new SqliteLibraryIndexService(persistence);
        var library = new LibraryService(new MetadataService(), persistence, index, new NoOpAudit(), fileSystem: source)
        {
            ScanPastUnavailableFolders = scanPastUnavailable,
        };
        return (library, persistence);
    }

    [Fact]
    public async Task LocalSource_ScansTheFixtureLikeBefore()
    {
        var (library, _) = MakeLibrary(new LocalFileSystemSource());
        await library.ScanAsync(new[] { Path.Combine(_root, "music") });
        if (library.Tracks.Count != 2) Assert.Fail(ScanMissReport.Describe(library, _music));
        Assert.Equal(new[] { "First", "Second" }, library.Tracks.Select(t => t.Title).OrderBy(t => t));
        Assert.All(library.Tracks, t => Assert.True(File.Exists(t.FilePath)));
    }

    [Fact]
    public async Task PathlessSource_ReadsTagsThroughTheStream_AndKeepsTheSourcePathAsFilePath()
    {
        var source = new StreamOnlySource(_music);
        var (library, _) = MakeLibrary(source);
        await library.ScanAsync(new[] { "fake://tree/music" });

        var titles = library.Tracks.Select(t => t.Title).OrderBy(t => t).ToArray();
        Assert.Equal(new[] { "First", "Second" }, titles);
        Assert.All(library.Tracks, t => Assert.StartsWith("fake://tree/music/", t.FilePath));
        Assert.All(library.Tracks, t => Assert.Equal("Tester", t.Artist));
        Assert.All(library.Tracks, t => Assert.True(t.FileSize > 0));
        Assert.Equal(2, source.Opened); // one open per audio file; notes.txt never opened

        // Second scan: unchanged size/mtime -> the fast path, no re-read.
        await library.ScanAsync(new[] { "fake://tree/music" });
        Assert.Equal(2, source.Opened);
        Assert.Equal(2, library.Tracks.Count);
    }

    /// <summary>
    /// Track ids fold case, so a case-only rename on a case-sensitive filesystem
    /// ("01 - first" → "01 - First", size and mtime untouched) maps onto the known track.
    /// The unchanged-file fast path used to keep the old spelling — a path that no longer
    /// exists — through every rescan.
    /// </summary>
    [Fact]
    public async Task CaseOnlyRename_RescanPicksUpTheNewSpelling_AndKeepsUserState()
    {
        var source = new StreamOnlySource(_music) { NameOf = n => n.ToLowerInvariant() };
        var (library, _) = MakeLibrary(source);
        await library.ScanAsync(new[] { "fake://tree/music" }, TestContext.Current.CancellationToken);
        var before = library.Tracks.Single(t => t.Title == "First");
        Assert.Equal("fake://tree/music/01 - first.mp3", before.FilePath);
        before.PlayCount = 7;

        source.NameOf = n => n;
        await library.ScanAsync(new[] { "fake://tree/music" }, TestContext.Current.CancellationToken);

        Assert.Equal(2, library.Tracks.Count);
        var after = library.Tracks.Single(t => t.Title == "First");
        Assert.Equal(before.Id, after.Id);
        Assert.Equal("fake://tree/music/01 - First.mp3", after.FilePath);
        Assert.Equal(7, after.PlayCount);
    }

    /// <summary>
    /// A phone library scanned before the ALAC atom was read from the stream stored its ALAC
    /// files with no bit depth (emulator-5560, 10-14: "48 kHz ALAC" on a 24-bit file). The next
    /// scan reads such a file once more, unchanged or not, keeping its user state; the others
    /// stay on the fast path, and the scan after that reads nothing.
    /// </summary>
    [Fact]
    public async Task PathlessAlac_StoredWithoutABitDepth_IsReadAgainOnce()
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Audio", "alac_96k_tiny.m4a"), Path.Combine(_music, "03 - Alac.m4a"));
        var source = new StreamOnlySource(_music);
        var (library, _) = MakeLibrary(source);
        await library.ScanAsync(new[] { "fake://tree/music" }, TestContext.Current.CancellationToken);
        Assert.Equal(3, source.Opened);
        var alac = library.Tracks.Single(t => t.FilePath.EndsWith(".m4a", StringComparison.Ordinal));
        Assert.Equal(24, alac.BitsPerSample);

        // As the old scan stored it.
        alac.BitsPerSample = 0;
        alac.SampleRate = 48000;
        alac.PlayCount = 5;
        await library.ScanAsync(new[] { "fake://tree/music" }, TestContext.Current.CancellationToken);
        Assert.Equal(4, source.Opened);                  // the ALAC alone
        var after = library.Tracks.Single(t => t.Id == alac.Id);
        Assert.Equal(24, after.BitsPerSample);
        Assert.Equal(96000, after.SampleRate);
        Assert.True(after.IsHiResLossless);
        Assert.Equal(5, after.PlayCount);

        await library.ScanAsync(new[] { "fake://tree/music" }, TestContext.Current.CancellationToken);
        Assert.Equal(4, source.Opened);
    }

    [Fact]
    public async Task MissingRoot_AbortsThroughTheSource_NotThroughDirectoryExists()
    {
        var source = new StreamOnlySource(_music);
        var (library, _) = MakeLibrary(source);
        await library.ScanAsync(new[] { "fake://tree/music" });
        string[]? aborted = null;
        library.ScanAborted += (_, roots) => aborted = roots;

        source.RootAvailable = false;
        await library.ScanAsync(new[] { "fake://tree/music" });

        Assert.NotNull(aborted);
        Assert.Equal(2, library.Tracks.Count); // library untouched
    }

    /// <summary>
    /// The phone (emulator 10-20): with one folder unavailable (a card taken out), removing
    /// another folder did nothing and an added one stayed empty: the scan saw the unavailable
    /// root, abandoned everything and restored the old library. Now the reachable folders'
    /// changes apply and the unavailable folder's songs stay as they were, user state and all.
    /// </summary>
    [Fact]
    public async Task UnavailableFolder_OnThePhone_RemoveAndAddElsewhereStillApply_AndItsSongsStay()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new MultiRootSource();
        foreach (var name in new[] { "A", "B", "C" })
        {
            var dir = Path.Combine(_root, "roots", name);
            Directory.CreateDirectory(dir);
            WriteMp3(Path.Combine(dir, $"01 - {name}1.mp3"), name + "1", "Tester", "Album " + name);
            WriteMp3(Path.Combine(dir, $"02 - {name}2.mp3"), name + "2", "Tester", "Album " + name);
            source.Dirs["fake://tree/" + name] = dir;
        }
        var (library, persistence) = MakeLibrary(source, scanPastUnavailable: true);
        await library.ScanAsync(new[] { "fake://tree/A", "fake://tree/B" }, ct);
        Assert.Equal(4, library.Tracks.Count);
        library.Tracks.Single(t => t.Title == "B1").PlayCount = 9;
        string[]? flagged = null;
        library.ScanAborted += (_, roots) => flagged = roots;

        source.Unavailable.Add("fake://tree/B");
        // Add C while B is out.
        await library.ScanAsync(new[] { "fake://tree/A", "fake://tree/B", "fake://tree/C" }, ct);
        Assert.Equal(new[] { "A1", "A2", "B1", "B2", "C1", "C2" }, library.Tracks.Select(t => t.Title).OrderBy(t => t));
        Assert.Equal(new[] { "fake://tree/B" }, flagged);

        // Remove A while B is still out.
        flagged = null;
        await library.ScanAsync(new[] { "fake://tree/B", "fake://tree/C" }, ct);
        Assert.Equal(new[] { "B1", "B2", "C1", "C2" }, library.Tracks.Select(t => t.Title).OrderBy(t => t));
        Assert.Equal(9, library.Tracks.Single(t => t.Title == "B1").PlayCount);
        Assert.Equal(new[] { "fake://tree/B" }, flagged);

        // On disk too: a restart reads the same library.
        var saved = await persistence.LoadLibraryAsync();
        Assert.Equal(new[] { "B1", "B2", "C1", "C2" }, saved!.Select(t => t.Title).OrderBy(t => t));

        // B back: a plain rescan, nothing flagged, nothing lost.
        source.Unavailable.Clear();
        flagged = null;
        await library.ScanAsync(new[] { "fake://tree/B", "fake://tree/C" }, ct);
        Assert.Null(flagged);
        Assert.Equal(4, library.Tracks.Count);
        Assert.Equal(9, library.Tracks.Single(t => t.Title == "B1").PlayCount);
    }

    /// <summary>The desktop keeps its rule: an unavailable folder abandons the scan, library kept.</summary>
    [Fact]
    public async Task UnavailableFolder_OnTheDesktop_StillKeepsTheLibraryUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new MultiRootSource();
        foreach (var name in new[] { "A", "B" })
        {
            var dir = Path.Combine(_root, "roots", name);
            Directory.CreateDirectory(dir);
            WriteMp3(Path.Combine(dir, $"01 - {name}1.mp3"), name + "1", "Tester", "Album " + name);
            source.Dirs["fake://tree/" + name] = dir;
        }
        var (library, _) = MakeLibrary(source);
        await library.ScanAsync(new[] { "fake://tree/A", "fake://tree/B" }, ct);
        source.Unavailable.Add("fake://tree/B");

        await library.ScanAsync(new[] { "fake://tree/B" }, ct);

        Assert.Equal(new[] { "A1", "B1" }, library.Tracks.Select(t => t.Title).OrderBy(t => t));
    }

    /// <summary>Find All Music (phone): the media index root yields songs by content URI with no
    /// extension in the path ("…/audio/media/7"); the tags are read through the display name's
    /// format. The permission taken away keeps them (with their plays); the option turned off
    /// (the root no longer scanned) takes them out.</summary>
    [Fact]
    public async Task DeviceMusicRoot_ReadsSongsByContentUri_KeepsThemWhenTheRootIsOutOfReach()
    {
        var ct = TestContext.Current.CancellationToken;
        const string root = Noctis.Mobile.Services.DeviceMusicScan.Root;
        var source = new MediaIndexSource(_music);
        var (library, _) = MakeLibrary(source, scanPastUnavailable: true);

        await library.ScanAsync(new[] { root }, ct);
        Assert.Equal(new[] { "First", "Second" }, library.Tracks.Select(t => t.Title).OrderBy(t => t));
        Assert.All(library.Tracks, t => Assert.StartsWith(root + "/", t.FilePath));
        library.Tracks.Single(t => t.Title == "First").PlayCount = 5;

        string[]? flagged = null;
        library.ScanAborted += (_, roots) => flagged = roots;
        source.Available = false;
        await library.ScanAsync(new[] { root }, ct);
        Assert.Equal(new[] { root }, flagged);
        Assert.Equal(5, library.Tracks.Single(t => t.Title == "First").PlayCount);

        source.Available = true;
        await library.ScanAsync(Array.Empty<string>(), ct);
        Assert.Empty(library.Tracks);
    }

    /// <summary>The fixture folder as MediaStore rows: "{root}/{n}" paths, names with their extension.</summary>
    private sealed class MediaIndexSource : IFileSystemSource
    {
        private readonly string _dir;
        public bool Available = true;
        public MediaIndexSource(string dir) => _dir = dir;
        public bool RootExists(string root) => Available;

        public IEnumerable<ScanEntry> EnumerateAudioFiles(string root, IReadOnlyCollection<string> excludedRoots,
            IReadOnlySet<string> ignoredFolderNames, Action<string> reportFailedDirectory)
        {
            var id = 0;
            foreach (var file in Directory.EnumerateFiles(_dir).OrderBy(f => f, StringComparer.Ordinal))
            {
                if (!MetadataService.SupportedExtensions.Contains(Path.GetExtension(file))) continue;
                var fi = new FileInfo(file);
                var captured = file;
                yield return new ScanEntry(Noctis.Mobile.Services.DeviceMusicScan.PathFor(++id), fi.Name, fi.Length, fi.LastWriteTimeUtc,
                    LocalPath: null, OpenRead: () => File.OpenRead(captured));
            }
        }
    }

    /// <summary>Several roots, each served from its own fixture folder; any can be made unavailable.</summary>
    private sealed class MultiRootSource : IFileSystemSource
    {
        public Dictionary<string, string> Dirs { get; } = new();
        public HashSet<string> Unavailable { get; } = new();

        public bool RootExists(string root) => Dirs.ContainsKey(root) && !Unavailable.Contains(root);

        public IEnumerable<ScanEntry> EnumerateAudioFiles(string root, IReadOnlyCollection<string> excludedRoots,
            IReadOnlySet<string> ignoredFolderNames, Action<string> reportFailedDirectory)
        {
            foreach (var file in Directory.EnumerateFiles(Dirs[root]))
            {
                var fi = new FileInfo(file);
                var captured = file;
                yield return new ScanEntry(root + "/" + fi.Name, fi.Name, fi.Length, fi.LastWriteTimeUtc, LocalPath: null,
                    OpenRead: () => File.OpenRead(captured));
            }
        }
    }

    /// <summary>
    /// PathIsUnder decides whether a revoked SAF grant is treated as "root unavailable,
    /// abort and keep the library" (see the ScanCoreAsync guard) or silently wipes every
    /// track under it, so its two branches need direct coverage with real strings: the
    /// URI branch on real Android Storage Access Framework document URIs (including the
    /// "%2F"-encoded-separator case for a document nested under a directory document,
    /// which had zero coverage), and the filesystem branch on a real Windows path.
    /// </summary>
    [Fact]
    public void PathIsUnder_MatchesSafDocumentsUnderTheirTree_AndRealPathsUnderTheirParent()
    {
        const string treeRoot = "content://com.android.externalstorage.documents/tree/primary%3AMusic";
        const string documentUnderTree =
            "content://com.android.externalstorage.documents/tree/primary%3AMusic/document/primary%3AMusic%2FTones%2Ftone_a.mp3";
        const string directoryDocument =
            "content://com.android.externalstorage.documents/tree/primary%3AMusic/document/primary%3AMusic%2FTones";
        const string documentUnderDirectoryDocument =
            "content://com.android.externalstorage.documents/tree/primary%3AMusic/document/primary%3AMusic%2FTones%2Fnested%2Ftone_b.mp3";
        const string siblingTree =
            "content://com.android.externalstorage.documents/tree/primary%3AMusic2/document/primary%3AMusic2%2Fx.mp3";
        const string siblingDirectoryDocument =
            "content://com.android.externalstorage.documents/tree/primary%3AMusic/document/primary%3AMusic%2FTones2%2Fx.mp3";

        Assert.True(LibraryService.PathIsUnder(documentUnderTree, treeRoot));
        // The %2F branch: a document nested under a directory document (not the tree root).
        Assert.True(LibraryService.PathIsUnder(documentUnderDirectoryDocument, directoryDocument));
        Assert.False(LibraryService.PathIsUnder(siblingTree, treeRoot));
        Assert.False(LibraryService.PathIsUnder(siblingDirectoryDocument, directoryDocument));

        // Filesystem branch stays pinned alongside the URI branch.
        var fileUnderRoot = Path.Combine(_music, "01 - First.mp3");
        var siblingFolder = Path.Combine(_root, "music", "Album2", "x.mp3");
        Assert.True(LibraryService.PathIsUnder(fileUnderRoot, _music));
        Assert.False(LibraryService.PathIsUnder(siblingFolder, _music));
    }

    /// <summary>Serves the fixture folder as opaque "fake://" entries with streams only.</summary>
    private sealed class StreamOnlySource : IFileSystemSource
    {
        private readonly string _dir;
        public int Opened;
        public bool RootAvailable = true;
        public Func<string, string> NameOf = n => n;
        public StreamOnlySource(string dir) => _dir = dir;

        public bool RootExists(string root) => RootAvailable;

        public IEnumerable<ScanEntry> EnumerateAudioFiles(string root, IReadOnlyCollection<string> excludedRoots,
            IReadOnlySet<string> ignoredFolderNames, Action<string> reportFailedDirectory)
        {
            foreach (var file in Directory.EnumerateFiles(_dir))
            {
                if (!MetadataService.SupportedExtensions.Contains(Path.GetExtension(file))) continue;
                var fi = new FileInfo(file);
                var captured = file;
                yield return new ScanEntry(root + "/" + NameOf(fi.Name), NameOf(fi.Name), fi.Length, fi.LastWriteTimeUtc, LocalPath: null,
                    OpenRead: () => { Interlocked.Increment(ref Opened); return File.OpenRead(captured); });
            }
        }
    }

    private sealed class NoOpAudit : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
    }
}
