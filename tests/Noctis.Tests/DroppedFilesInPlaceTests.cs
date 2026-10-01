using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #108: with "Import dropped files" off, dropped files go into the library, a new
/// playlist (named after their folder, as in AIMP) or an existing playlist where they are —
/// nothing moved or copied — and stay there through later scans. Files already in the
/// library reuse their tracks.
/// </summary>
[Collection("MetadataServiceStatics")]
public class DroppedFilesInPlaceTests : IDisposable
{
    // The file names from the issue's AIMP video, in the order they were selected.
    private static readonly string[] VideoFiles =
    {
        "hftfviceballcomedownwip", "hftfviceballgoup", "hftfviceballhit", "hftfviceballcomedown",
        "hftfvicebarragetemp", "hftfvicebarragewip2", "hftfvicebarragewithendp",
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private readonly string _music;
    private readonly string _downloads;

    public DroppedFilesInPlaceTests()
    {
        _music = Path.Combine(_root, "music");
        _downloads = Path.Combine(_root, "Downloads", "hftfviceusgskinconcept");
        Directory.CreateDirectory(Path.Combine(_music, "Album"));
        Directory.CreateDirectory(_downloads);
        WriteMp3(Path.Combine(_music, "Album", "01 - First.mp3"), "First");
        WriteMp3(Path.Combine(_music, "Album", "02 - Second.mp3"), "Second");
        foreach (var name in VideoFiles)
            WriteMp3(Dropped(name), name);
        File.WriteAllText(Path.Combine(_downloads, "readme.txt"), "not audio");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string Dropped(string name) => Path.Combine(_downloads, name + ".mp3");

    private static void WriteMp3(string path, string title)
    {
        // One MPEG-1 Layer III frame repeated (see FileSystemSourceScanTests).
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        using var f = TagLib.File.Create(path);
        f.Tag.Title = title; f.Tag.Performers = new[] { "Tester" }; f.Tag.Album = "Fixture Album";
        f.Save();
    }

    private sealed record Setup(LibraryService Library, PersistenceService Persistence,
        SidebarViewModel Sidebar, DroppedFilesService Drops);

    private async Task<Setup> SetUpAsync()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoOpAudit());
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { _music } });
        await library.ScanAsync(new[] { _music });
        Assert.Equal(2, library.Tracks.Count);
        var sidebar = new SidebarViewModel(persistence, library);
        await sidebar.LoadPlaylistsAsync();
        return new Setup(library, persistence, sidebar, new DroppedFilesService(library, sidebar));
    }

    private static Track ByTitle(ILibraryService library, string title) => library.Tracks.Single(t => t.Title == title);

    [AvaloniaFact]
    public async Task AddToLibrary_AddsTheFilesWhereTheyAre_AndAScanKeepsThem()
    {
        var s = await SetUpAsync();
        var dropped = VideoFiles.Select(Dropped).ToList();

        var result = await s.Drops.AddToLibraryAsync(dropped);

        Assert.Equal(7, result.Added);
        Assert.Equal(VideoFiles, result.Tracks.Select(t => t.Title));
        Assert.Equal(dropped, result.Tracks.Select(t => t.FilePath));
        Assert.All(dropped, p => Assert.True(File.Exists(p), p + " was moved"));
        var settings = await s.Persistence.LoadSettingsAsync();
        Assert.Equal(new[] { _music }, settings.MusicFolders); // no Noctis Imports root added
        Assert.All(result.Tracks, t => Assert.True(t.AddedIndividually));

        await s.Library.ScanAsync(new[] { _music });
        Assert.Equal(9, s.Library.Tracks.Count);
    }

    [AvaloniaFact]
    public async Task NewPlaylist_IsNamedAfterTheFolder_KeepsTheDropOrder_AndSurvivesAScan()
    {
        var s = await SetUpAsync();
        var dropped = new[] { Dropped(VideoFiles[4]), Dropped(VideoFiles[0]), Dropped(VideoFiles[2]) };

        var result = await s.Drops.CreatePlaylistAsync(dropped, "New Playlist");

        var playlist = Assert.IsType<Playlist>(result.Playlist);
        Assert.Equal("hftfviceusgskinconcept", playlist.Name);
        Assert.Equal(result.Tracks.Select(t => t.Id), playlist.TrackIds);
        Assert.Equal(new[] { VideoFiles[4], VideoFiles[0], VideoFiles[2] }, result.Tracks.Select(t => t.Title));
        Assert.Contains(s.Sidebar.PlaylistItems, i => i.PlaylistId == playlist.Id);
        var saved = (await s.Persistence.LoadPlaylistsAsync()).Single(p => p.Id == playlist.Id);
        Assert.Equal(playlist.TrackIds, saved.TrackIds);

        // The point of #108: a full scan must not take the playlist's songs away.
        await s.Library.ScanAsync(new[] { _music });
        Assert.All(playlist.TrackIds, id => Assert.NotNull(s.Library.GetTrackById(id)));
    }

    [AvaloniaFact]
    public async Task NewPlaylist_FromADroppedFolder_TakesItsName_AndItsSongsInFolderOrder()
    {
        var s = await SetUpAsync();

        var result = await s.Drops.CreatePlaylistAsync(new[] { _downloads }, "New Playlist");

        Assert.Equal("hftfviceusgskinconcept", result.Playlist!.Name);
        Assert.Equal(VideoFiles.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), result.Tracks.Select(t => t.Title));
    }

    [AvaloniaFact]
    public async Task DropOntoAPlaylist_AppendsInPlace_AndNeverTwice()
    {
        var s = await SetUpAsync();
        var first = ByTitle(s.Library, "First");
        var mine = await s.Sidebar.CreatePlaylistFromTracksAsync("Mine", new[] { first });

        var added = await s.Drops.AddToPlaylistAsync(mine.Id, new[] { Dropped(VideoFiles[0]), Dropped(VideoFiles[1]) });
        Assert.Equal(2, added.Added);
        Assert.Equal("Mine", added.Playlist!.Name);

        var again = await s.Drops.AddToPlaylistAsync(mine.Id, new[] { Dropped(VideoFiles[1]), Dropped(VideoFiles[2]) });
        Assert.Equal(1, again.Added);

        Assert.Equal(new[] { "First", VideoFiles[0], VideoFiles[1], VideoFiles[2] },
            mine.TrackIds.Select(id => s.Library.GetTrackById(id)!.Title));
        Assert.All(new[] { 0, 1, 2 }, i => Assert.True(File.Exists(Dropped(VideoFiles[i]))));
    }

    [AvaloniaFact]
    public async Task FilesAlreadyInTheLibrary_ReuseTheirTracks_AndTheirPlayCounts()
    {
        var s = await SetUpAsync();
        var first = ByTitle(s.Library, "First");
        first.PlayCount = 5;

        var result = await s.Drops.CreatePlaylistAsync(new[] { first.FilePath, Dropped(VideoFiles[0]) }, "New Playlist");

        Assert.Equal(3, s.Library.Tracks.Count); // only the outside file is new
        Assert.Same(first, result.Tracks[0]);
        Assert.Equal(5, result.Tracks[0].PlayCount);
        Assert.Equal(new[] { first.Id, result.Tracks[1].Id }, result.Playlist!.TrackIds);
        // Under a music folder already: its folder walk keeps it, so it isn't marked.
        Assert.False(first.AddedIndividually);
        Assert.True(result.Tracks[1].AddedIndividually);

        var repeat = await s.Drops.AddToLibraryAsync(new[] { Dropped(VideoFiles[0]), first.FilePath });
        Assert.Equal(0, repeat.Added);
        Assert.Equal(2, repeat.Tracks.Count);
        Assert.Equal(3, s.Library.Tracks.Count);
    }

    [AvaloniaFact]
    public async Task ADropWithoutAudio_AddsNothing_AndMakesNoPlaylist()
    {
        var s = await SetUpAsync();

        var result = await s.Drops.CreatePlaylistAsync(new[] { Path.Combine(_downloads, "readme.txt") }, "New Playlist");

        Assert.Empty(result.Tracks);
        Assert.Null(result.Playlist);
        Assert.Empty(s.Sidebar.Playlists);
    }

    // ── Naming (AIMP: the files' folder) ──

    [Fact]
    public void PlaylistName_FilesFromOneFolder_TakeThatFolder()
        => Assert.Equal("hftfviceusgskinconcept",
            DroppedFilesService.PlaylistNameFor(VideoFiles.Take(3).Select(Dropped).ToList(), "New Playlist"));

    [Fact]
    public void PlaylistName_ADroppedFolder_TakesItsOwnName()
        => Assert.Equal("hftfviceusgskinconcept",
            DroppedFilesService.PlaylistNameFor(new[] { _downloads + Path.DirectorySeparatorChar }, "New Playlist"));

    [Fact]
    public void PlaylistName_SeveralFolders_TakeTheirClosestCommonFolder()
    {
        var sibling = Path.Combine(_root, "Downloads", "other pack");
        Directory.CreateDirectory(sibling);
        Assert.Equal("Downloads",
            DroppedFilesService.PlaylistNameFor(new[] { _downloads, Path.Combine(sibling, "x.mp3") }, "New Playlist"));
        // A folder and a file inside it: still that folder.
        Assert.Equal("hftfviceusgskinconcept",
            DroppedFilesService.PlaylistNameFor(new[] { _downloads, Dropped(VideoFiles[0]) }, "New Playlist"));
    }

    [Fact]
    public void PlaylistName_WithNothingToGoOn_FallsBack()
    {
        var driveRoot = Path.GetPathRoot(_root)!;
        Assert.Equal("New Playlist", DroppedFilesService.PlaylistNameFor(new[] { Path.Combine(driveRoot, "loose.mp3") }, "New Playlist"));
        Assert.Equal("New Playlist", DroppedFilesService.PlaylistNameFor(Array.Empty<string>(), "New Playlist"));
        if (OperatingSystem.IsWindows())
            Assert.Equal("New Playlist", DroppedFilesService.PlaylistNameFor(
                new[] { @"C:\Music\A\x.mp3", @"Q:\Other\y.mp3" }, "New Playlist"));
    }

    // ── Routing (MainWindow's drop decision) ──

    [Theory]
    // "Import dropped files" on: the import as before, whatever the pointer is over...
    [InlineData(true, DropZone.None, false, DropAction.Import)]
    [InlineData(true, DropZone.Library, false, DropAction.Import)]
    // ...except a sidebar playlist, which takes the drop in either mode.
    [InlineData(true, DropZone.None, true, DropAction.AddToPlaylist)]
    // Off: the zone decides; outside every zone it plays / queues like before the zones.
    [InlineData(false, DropZone.None, false, DropAction.PlayOrQueue)]
    [InlineData(false, DropZone.Play, false, DropAction.PlayOrQueue)]
    [InlineData(false, DropZone.Library, false, DropAction.AddToLibrary)]
    [InlineData(false, DropZone.NewPlaylist, false, DropAction.NewPlaylist)]
    [InlineData(false, DropZone.None, true, DropAction.AddToPlaylist)]
    public void DropRouting(bool importDroppedMedia, DropZone zone, bool overPlaylist, DropAction expected)
        => Assert.Equal(expected, DropOverlay.ActionFor(importDroppedMedia, zone, overPlaylist));

    private sealed class NoOpAudit : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
    }
}
