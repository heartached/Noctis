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

    private (LibraryService Library, PersistenceService Persistence) MakeLibrary(IFileSystemSource source)
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var index = new SqliteLibraryIndexService(persistence);
        var library = new LibraryService(new MetadataService(), persistence, index, new NoOpAudit(), fileSystem: source);
        return (library, persistence);
    }

    [Fact]
    public async Task LocalSource_ScansTheFixtureLikeBefore()
    {
        var (library, _) = MakeLibrary(new LocalFileSystemSource());
        await library.ScanAsync(new[] { Path.Combine(_root, "music") });
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
