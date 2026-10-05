using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Luwi, 10-03): hide a folder (genre, artist, ...) from the library for a while
/// without removing anything. Tracks under a hidden folder drop out of Tracks / Albums /
/// Artists / GetTrackById but stay in AllTracks, library.json and the Folders tree; showing
/// the folder again brings them straight back, user state intact, with no rescan.
/// </summary>
[Collection("MetadataServiceStatics")]
public class HiddenLibraryFoldersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private readonly string _music;
    private readonly string _rock;
    private readonly Track _rockTrack;
    private readonly Track _classicsTrack;
    private readonly Track _jazzTrack;

    public HiddenLibraryFoldersTests()
    {
        _music = Path.Combine(_root, "music");
        _rock = Path.Combine(_music, "Rock");
        _rockTrack = MakeTrack(Path.Combine(_rock, "Rocker", "Album", "01.mp3"), "Rocker", "Rock Album");
        _rockTrack.IsFavorite = true;
        _rockTrack.PlayCount = 7;
        _classicsTrack = MakeTrack(Path.Combine(_music, "Rock Classics", "02.mp3"), "Classic", "Classics Album");
        _jazzTrack = MakeTrack(Path.Combine(_music, "Jazz", "03.mp3"), "Jazzer", "Jazz Album");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static Track MakeTrack(string path, string artist, string album) => new()
    {
        Id = LibraryService.TrackIdForPath(path),
        FilePath = path,
        Title = Path.GetFileNameWithoutExtension(path),
        Artist = artist,
        Album = album,
        AlbumId = Guid.NewGuid(),
    };

    // ── Path matching ──

    [Theory]
    [InlineData(@"C:\Music\Rock\a.mp3", @"C:\Music\Rock", true)]
    [InlineData(@"C:\Music\Rock\Artist\Album\a.mp3", @"C:\Music\Rock", true)]
    [InlineData(@"C:\Music\Rock\a.mp3", @"C:\Music\Rock\", true)]
    [InlineData("C:/Music/Rock/a.mp3", @"C:\Music\Rock", true)]
    [InlineData(@"C:\Music\Rock\a.mp3", "C:/Music/Rock/", true)]
    [InlineData("/home/u/Music/Rock/a.flac", "/home/u/Music/Rock", true)]
    [InlineData(@"C:\Music\Rock Classics\a.mp3", @"C:\Music\Rock", false)]
    [InlineData(@"C:\Music\RockX.mp3", @"C:\Music\Rock", false)]
    [InlineData(@"C:\Music\Rock", @"C:\Music\Rock", false)]
    [InlineData(@"C:\Music\Jazz\a.mp3", @"C:\Music\Rock", false)]
    [InlineData(@"C:\Music\a.mp3", @"C:\Music\Rock", false)]
    [InlineData("", @"C:\Music\Rock", false)]
    [InlineData(@"C:\Music\Rock\a.mp3", "", false)]
    [InlineData(@"C:\Music\Rock\a.mp3", "   ", false)]
    public void IsInside_MatchesWholeFolderSegments(string path, string folder, bool expected)
        => Assert.Equal(expected, FolderPathMatch.IsInside(path, folder));

    [Fact]
    public void IsInside_CaseFollowsThePlatform()
    {
        // Case-insensitive on Windows/macOS, case-sensitive on Linux (PathComparison).
        var expected = !OperatingSystem.IsLinux();
        Assert.Equal(expected, FolderPathMatch.IsInside(@"C:\MUSIC\rock\a.mp3", @"C:\Music\Rock"));
        Assert.Equal(expected, FolderPathMatch.IsSame(@"c:\music\rock", @"C:\Music\Rock\"));
    }

    [Theory]
    [InlineData(@"C:\Music\Rock", @"C:\Music\Rock\", true)]
    [InlineData(@"C:\Music\Rock", "C:/Music/Rock", true)]
    [InlineData(@"C:\Music\Rock", @"C:\Music\Rock Classics", false)]
    [InlineData(@"C:\Music\Rock", @"C:\Music", false)]
    [InlineData(null, @"C:\Music", false)]
    public void IsSame_IgnoresSeparatorsAndTrailingSlash(string? a, string b, bool expected)
        => Assert.Equal(expected, FolderPathMatch.IsSame(a, b));

    // ── LibraryService ──

    private async Task<(LibraryService Library, PersistenceService Persistence)> LoadedLibraryAsync(PersistenceService? existing = null)
    {
        var persistence = existing ?? new PersistenceService(Path.Combine(_root, "data"));
        if (existing == null)
        {
            // Schema already current: the backfills would try to re-read these (absent) files.
            await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { _music }, MetadataSchemaVersion = 1000 });
            await persistence.SaveLibraryAsync(new() { _rockTrack, _classicsTrack, _jazzTrack });
        }
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoOpAudit());
        await library.LoadAsync();
        await library.BackgroundInit;
        return (library, persistence);
    }

    private static string[] Titles(System.Collections.Generic.IEnumerable<Track> tracks)
        => tracks.Select(t => t.Title).OrderBy(t => t).ToArray();

    [Fact]
    public async Task HidingAFolder_RemovesItsTracksFromEveryLibraryList_AndShowingBringsThemBack()
    {
        var (library, persistence) = await LoadedLibraryAsync();
        Assert.Equal(3, library.Tracks.Count);
        var updates = 0;
        library.LibraryUpdated += (_, _) => updates++;

        await library.SetFolderHiddenAsync(_rock, hidden: true);

        Assert.Equal(1, updates);
        Assert.Equal(new[] { "02", "03" }, Titles(library.Tracks));
        Assert.Equal(3, library.AllTracks.Count);
        Assert.DoesNotContain(library.Albums, a => a.Name == "Rock Album");
        Assert.Contains(library.Albums, a => a.Name == "Classics Album");
        Assert.DoesNotContain(library.Artists, a => a.Name == "Rocker");
        Assert.Null(library.GetTrackById(_rockTrack.Id));
        Assert.Empty(library.GetAlbumsByArtist("Rocker"));
        Assert.Equal(new[] { _rock }, library.HiddenFolders);
        Assert.Equal(new[] { _rock }, (await persistence.LoadSettingsAsync()).HiddenLibraryFolders);

        // Shown again, matched regardless of a trailing separator.
        await library.SetFolderHiddenAsync(_rock + Path.DirectorySeparatorChar, hidden: false);

        Assert.Equal(2, updates);
        Assert.Equal(new[] { "01", "02", "03" }, Titles(library.Tracks));
        Assert.Contains(library.Albums, a => a.Name == "Rock Album");
        Assert.Contains(library.Artists, a => a.Name == "Rocker");
        var back = library.GetTrackById(_rockTrack.Id);
        Assert.NotNull(back);
        Assert.True(back!.IsFavorite);
        Assert.Equal(7, back.PlayCount);
        Assert.Empty(library.HiddenFolders);
        Assert.Empty((await persistence.LoadSettingsAsync()).HiddenLibraryFolders);
    }

    [Fact]
    public async Task HidingTwice_OrShowingAVisibleFolder_IsANoOp()
    {
        var (library, _) = await LoadedLibraryAsync();
        await library.SetFolderHiddenAsync(_rock, hidden: true);
        var updates = 0;
        library.LibraryUpdated += (_, _) => updates++;

        await library.SetFolderHiddenAsync(_rock, hidden: true);
        await library.SetFolderHiddenAsync(Path.Combine(_music, "Jazz"), hidden: false);

        Assert.Equal(0, updates);
        Assert.Single(library.HiddenFolders);
    }

    [Fact]
    public async Task HiddenFolders_SurviveARestart_AndNothingIsDeleted()
    {
        var (first, persistence) = await LoadedLibraryAsync();
        await first.SetFolderHiddenAsync(_rock, hidden: true);
        await first.SaveAsync();

        var (second, _) = await LoadedLibraryAsync(persistence);

        Assert.Equal(new[] { _rock }, second.HiddenFolders);
        Assert.Equal(new[] { "02", "03" }, Titles(second.Tracks));
        Assert.DoesNotContain(second.Albums, a => a.Name == "Rock Album");
        Assert.Equal(3, second.AllTracks.Count);
        Assert.Equal(3, (await persistence.LoadLibraryAsync())!.Count);

        // The index cache written while hidden must not keep the folder out once shown.
        await second.SetFolderHiddenAsync(_rock, hidden: false);
        var (third, _) = await LoadedLibraryAsync(persistence);
        Assert.Equal(new[] { "01", "02", "03" }, Titles(third.Tracks));
        Assert.Contains(third.Albums, a => a.Name == "Rock Album");
    }

    [Fact]
    public async Task Rescan_KeepsAHiddenFolderHidden()
    {
        WriteMp3(Path.Combine(_rock, "r.mp3"), "RockSong");
        WriteMp3(Path.Combine(_music, "Jazz", "j.mp3"), "JazzSong");
        var persistence = new PersistenceService(Path.Combine(_root, "scan-data"));
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { _music } });
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoOpAudit());
        await library.ScanAsync(new[] { _music });
        Assert.Equal(2, library.Tracks.Count);

        await library.SetFolderHiddenAsync(_rock, hidden: true);
        await library.ScanAsync(new[] { _music });

        Assert.Equal(new[] { "JazzSong" }, library.Tracks.Select(t => t.Title));
        Assert.Equal(2, library.AllTracks.Count);
        Assert.Equal(new[] { _rock }, (await persistence.LoadSettingsAsync()).HiddenLibraryFolders);
    }

    /// <summary>A scan used to look known files up in the VISIBLE index only, so every hidden
    /// track came back as a freshly tagged object with no favorite or play count; once shown
    /// again, the next state save journaled that blank row over the real one.</summary>
    [Fact]
    public async Task Rescan_WhileHidden_KeepsUserStateOfHiddenTracks()
    {
        WriteMp3(Path.Combine(_rock, "r.mp3"), "RockSong");
        WriteMp3(Path.Combine(_music, "Jazz", "j.mp3"), "JazzSong");
        var persistence = new PersistenceService(Path.Combine(_root, "scan-state"));
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { _music } });
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoOpAudit());
        await library.ScanAsync(new[] { _music });
        var rock = library.Tracks.Single(t => t.Title == "RockSong");
        rock.IsFavorite = true;
        rock.PlayCount = 7;
        await library.SaveTrackUserStateAsync(new[] { rock });

        await library.SetFolderHiddenAsync(_rock, hidden: true);
        await library.ScanAsync(new[] { _music });
        await library.SetFolderHiddenAsync(_rock, hidden: false);

        var back = library.Tracks.Single(t => t.Title == "RockSong");
        Assert.True(back.IsFavorite);
        Assert.Equal(7, back.PlayCount);
    }

    private static void WriteMp3(string path, string title)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // One MPEG-1 Layer III frame repeated (see FileSystemSourceScanTests).
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        using var f = TagLib.File.Create(path);
        f.Tag.Title = title; f.Tag.Performers = new[] { "Tester" }; f.Tag.Album = title + " Album";
        f.Save();
    }

    [Fact]
    public void PlaylistReorder_KeepsTheIdsOfHiddenTracksInPlace()
    {
        var a = new Track { Title = "a" };
        var b = new Track { Title = "b" };
        var c = new Track { Title = "c" };
        var hiddenId = Guid.NewGuid();
        var ids = new System.Collections.Generic.List<Guid> { a.Id, hiddenId, b.Id, c.Id };

        // The playlist page shows a, b, c (the hidden track does not resolve); the user
        // drags c to the top.
        PlaylistViewModel.ApplyDisplayedOrder(ids, new[] { c, a, b });

        Assert.Equal(new[] { c.Id, hiddenId, a.Id, b.Id }, ids);
    }

    // ── Folders view ──

    private static Task RefreshAsync(LibraryFoldersViewModel vm)
    {
        vm.MarkDirty();
        var method = typeof(LibraryFoldersViewModel).GetMethod("RefreshAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(vm, null)!;
    }

    [AvaloniaFact]
    public async Task FoldersMenu_HideFromLibrary_TogglesTheFolder_AndKeepsItInTheTree()
    {
        var (library, persistence) = await LoadedLibraryAsync();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, persistence, new FakeAnimatedCoverService());
        var vm = new LibraryFoldersViewModel(library, player, persistence, new SidebarViewModel(persistence, library));
        await RefreshAsync(vm);

        var menu = new FolderContextMenuBuilder();
        menu.Build();
        FolderNode RockNode() => vm.RootNodes.Single().Children.Single(n => n.DisplayName == "Rock");
        void Bind(FolderNode node) => menu.Bind(node, vm.PlayNodeCommand, vm.ShuffleNodeCommand, vm.PlayNodeNextCommand,
            vm.AddNodeToQueueCommand, vm.AddNodeToNewPlaylistCommand, vm.ShowNodeInExplorerCommand, vm.ToggleNodeHiddenCommand);
        Task Click() => ((IAsyncRelayCommand)menu.ToggleHidden.Command!).ExecuteAsync(menu.ToggleHidden.CommandParameter);

        Bind(RockNode());
        Assert.Equal("Hide from Library", menu.ToggleHidden.Header);
        await Click();
        await RefreshAsync(vm);

        var rock = RockNode();
        Assert.True(rock.IsHiddenFromLibrary);
        Assert.True(rock.IsInHiddenFolder);
        var artistNode = rock.Children.Single();
        Assert.False(artistNode.IsHiddenFromLibrary);
        Assert.True(artistNode.IsInHiddenFolder);
        Assert.Null(library.GetTrackById(_rockTrack.Id));

        // The hidden folder's songs stay out of the track pane too.
        vm.SelectedNode = vm.RootNodes.Single();
        Assert.Equal(new[] { "02", "03" }, Titles(vm.SelectedFolderTracks));

        // A sub-folder of a hidden folder is shown again from the folder that was hidden.
        Bind(artistNode);
        Assert.Equal("Show in Library", menu.ToggleHidden.Header);
        Assert.False(menu.ToggleHidden.IsEnabled);

        Bind(rock);
        Assert.Equal("Show in Library", menu.ToggleHidden.Header);
        Assert.True(menu.ToggleHidden.IsEnabled);
        await Click();
        await RefreshAsync(vm);

        Assert.False(RockNode().IsInHiddenFolder);
        Assert.NotNull(library.GetTrackById(_rockTrack.Id));
        vm.SelectedNode = vm.RootNodes.Single();
        Assert.Equal(new[] { "01", "02", "03" }, Titles(vm.SelectedFolderTracks));
    }

    private sealed class NoOpAudit : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
    }
}
