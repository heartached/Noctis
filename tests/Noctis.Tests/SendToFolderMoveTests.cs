using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #121 (LuigiGame3): Send to Folder for whole albums and artists, and a Move option.
/// A move must keep the library whole: the moved songs keep their favorites, plays, ratings
/// and lyrics; a song that can't move stays exactly as it was; nothing is lost or doubled.
/// Real library + real files in temp folders only (never the owner's music or %APPDATA%).
/// </summary>
[Collection("MetadataServiceStatics")]
public sealed class SendToFolderMoveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "stfmove-" + Guid.NewGuid().ToString("N"));
    private readonly string _music;
    private readonly string _dst;

    public SendToFolderMoveTests()
    {
        _music = Path.Combine(_root, "music");
        _dst = Path.Combine(_root, "USB");
        Directory.CreateDirectory(Path.Combine(_music, "Album"));
        Directory.CreateDirectory(_dst);
        WriteMp3(Song("01 - First.mp3"), "First", 1);
        WriteMp3(Song("02 - Second.mp3"), "Second", 2);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string Song(string name) => Path.Combine(_music, "Album", name);

    private static void WriteMp3(string path, string title, uint no)
    {
        // One MPEG-1 Layer III frame repeated (see AddedFilesScanTests).
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        using var f = TagLib.File.Create(path);
        f.Tag.Title = title; f.Tag.Performers = new[] { "Tester" }; f.Tag.AlbumArtists = new[] { "Tester" };
        f.Tag.Album = "Fixture Album"; f.Tag.Track = no;
        f.Save();
    }

    private async Task<LibraryService> ScannedLibraryAsync()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var settings = new AppSettings();
        settings.MusicFolders.Add(_music);
        await persistence.SaveSettingsAsync(settings);
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new FolderMetadataBackfillTests.FakeAuditTrail());
        await library.ScanAsync(new[] { _music });
        Assert.Equal(2, library.Tracks.Count);
        return library;
    }

    private static Track ByTitle(ILibraryService library, string title) => library.Tracks.Single(t => t.Title == title);

    // ── Move: files and library ──

    [Fact]
    public async Task Move_MovesTheFiles_AndTheLibraryFollows_KeepingUserState()
    {
        var library = await ScannedLibraryAsync();
        var first = ByTitle(library, "First");
        var second = ByTitle(library, "Second");
        first.IsFavorite = true;
        first.PlayCount = 7;
        first.Rating = 4;
        var oldId = first.Id;
        var oldPath = first.FilePath;
        File.WriteAllText(Path.ChangeExtension(oldPath, ".lrc"), "[00:01.00]hi");

        var service = new SendToFolderService(library);
        var plan = service.Plan(new[] { first, second }, _dst, null, includeLyrics: true, move: true);
        var result = await service.MoveAsync(plan, null, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Copied);
        Assert.Equal(0, result.Failed);
        var newPath = Path.Combine(_dst, "01 - First.mp3");
        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(newPath));
        // The lyrics went along (left behind they'd detach from the song).
        Assert.False(File.Exists(Path.ChangeExtension(oldPath, ".lrc")));
        Assert.True(File.Exists(Path.ChangeExtension(newPath, ".lrc")));

        // Same library track, now at the new path, with its user state.
        Assert.Equal(2, library.Tracks.Count);
        Assert.Same(first, ByTitle(library, "First"));
        Assert.Equal(newPath, first.FilePath);
        Assert.True(first.IsFavorite);
        Assert.Equal(7, first.PlayCount);
        Assert.Equal(4, first.Rating);
        // Ids derive from the path (LibraryService.ComputeFileId), so the id changes and the
        // remap carries it to the playlists and the play log.
        Assert.Equal(first.Id, result.TrackIdRemap[oldId]);
        Assert.Same(first, library.GetTrackById(first.Id));
    }

    /// <summary>Bug: a song moved (or organized) to a folder outside every music folder was
    /// dropped by the next scan, with its plays, favorite and playlist places. The scan keeps
    /// only what its folder walk finds plus tracks marked as added on their own (GitHub #108),
    /// and a relocated track was never marked.</summary>
    [Fact]
    public async Task Move_OutsideTheMusicFolders_SurvivesTheNextScan()
    {
        var library = await ScannedLibraryAsync();
        var first = ByTitle(library, "First");
        first.PlayCount = 3;
        await library.SaveAsync();

        var service = new SendToFolderService(library);
        var plan = service.Plan(new[] { first }, _dst, null, includeLyrics: false, move: true);
        await service.MoveAsync(plan, null, TestContext.Current.CancellationToken);
        await library.ScanAsync(new[] { _music });

        var kept = library.Tracks.SingleOrDefault(t => t.Title == "First");
        Assert.NotNull(kept);
        Assert.Equal(Path.Combine(_dst, "01 - First.mp3"), kept!.FilePath);
        Assert.Equal(3, kept.PlayCount);
    }

    /// <summary>A library track whose stored id isn't the hash of its path (no current code
    /// writes one; a hand-edited or foreign library.json can): RelocateTracksAsync looked it
    /// up by the path's id only, missed, and the moved file was left with the library still
    /// pointing at the old path.</summary>
    [Fact]
    public async Task Move_TrackWhoseIdIsNotItsPathHash_LibraryStillFollows()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var path = Song("01 - First.mp3");
        var oddId = Guid.NewGuid();
        await persistence.SaveLibraryAsync(new List<Track>
        {
            new() { Id = oddId, FilePath = path, Title = "First", Artist = "Tester", Album = "Fixture Album", PlayCount = 5 },
        });
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new FolderMetadataBackfillTests.FakeAuditTrail());
        await library.LoadAsync();
        var track = ByTitle(library, "First");
        Assert.Equal(oddId, track.Id);

        var service = new SendToFolderService(library);
        var plan = service.Plan(new[] { track }, _dst, null, includeLyrics: false, move: true);
        var result = await service.MoveAsync(plan, null, TestContext.Current.CancellationToken);

        var moved = Path.Combine(_dst, "01 - First.mp3");
        Assert.Equal(1, result.Copied);
        Assert.True(File.Exists(moved));
        Assert.Same(track, ByTitle(library, "First"));
        Assert.Equal(moved, track.FilePath);   // the library follows the file
        Assert.Equal(5, track.PlayCount);
        Assert.Equal(track.Id, result.TrackIdRemap[oddId]); // remapped from its real id
    }

    [Fact]
    public async Task Move_ASongThatCantMove_IsLeftUntouched_TheOthersMove()
    {
        var library = await ScannedLibraryAsync();
        var first = ByTitle(library, "First");
        var second = ByTitle(library, "Second");
        second.IsFavorite = true;
        var secondId = second.Id;
        var secondPath = second.FilePath;

        var service = new SendToFolderService(library);
        var plan = service.Plan(new[] { first, second }, _dst, null, includeLyrics: false, move: true);
        // The second file goes missing between plan and run (drive hiccup, renamed outside).
        File.Move(secondPath, secondPath + ".bak");
        var result = await service.MoveAsync(plan, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Copied);
        Assert.Equal(1, result.Failed);
        Assert.Single(result.Errors);
        Assert.Equal(Path.Combine(_dst, "01 - First.mp3"), first.FilePath);
        // The failed song: same path, same id, same state, no stray file at the target.
        Assert.Equal(secondPath, second.FilePath);
        Assert.Equal(secondId, second.Id);
        Assert.True(second.IsFavorite);
        Assert.False(File.Exists(Path.Combine(_dst, "02 - Second.mp3")));
        Assert.DoesNotContain(secondId, result.TrackIdRemap.Keys);
    }

    /// <summary>A song in use (playing) can't be renamed away on Windows: it stays, untouched.</summary>
    [Fact]
    public async Task Move_ASongInUse_IsLeftUntouched()
    {
        if (!OperatingSystem.IsWindows()) return; // Unix doesn't lock open files
        var library = await ScannedLibraryAsync();
        var first = ByTitle(library, "First");
        var path = first.FilePath;
        var id = first.Id;

        var service = new SendToFolderService(library);
        var plan = service.Plan(new[] { first }, _dst, null, includeLyrics: false, move: true);
        SendToFolderResult result;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = await service.MoveAsync(plan, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Failed);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(Path.Combine(_dst, "01 - First.mp3")));
        Assert.Equal(path, first.FilePath);
        Assert.Equal(id, first.Id);
    }

    // ── One file across drives (copy, check, delete) ──

    [Fact]
    public async Task MoveFile_AcrossDrives_CopiesWholeThenRemovesTheSource()
    {
        var source = Song("01 - First.mp3");
        var bytes = File.ReadAllBytes(source);
        var target = Path.Combine(_dst, "a.mp3");

        await SendToFolderService.MoveFileAsync(source, target, TestContext.Current.CancellationToken, sameVolume: false);

        Assert.False(File.Exists(source));
        Assert.Equal(bytes, File.ReadAllBytes(target));
    }

    /// <summary>Across drives the source is deleted only after the copy; when it can't be
    /// (in use), the copy is taken back out so the song isn't in two places.</summary>
    [Fact]
    public async Task MoveFile_AcrossDrives_SourceInUse_TakesTheCopyBack()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Song("01 - First.mp3");
        var target = Path.Combine(_dst, "a.mp3");

        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() =>
                SendToFolderService.MoveFileAsync(source, target, TestContext.Current.CancellationToken, sameVolume: false));

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task MoveFile_NeverOverwrites()
    {
        var source = Song("01 - First.mp3");
        var target = Path.Combine(_dst, "a.mp3");
        File.WriteAllText(target, "someone else's file");

        foreach (var same in new[] { true, false })
            await Assert.ThrowsAnyAsync<IOException>(() =>
                SendToFolderService.MoveFileAsync(source, target, TestContext.Current.CancellationToken, sameVolume: same));

        Assert.True(File.Exists(source));
        Assert.Equal("someone else's file", File.ReadAllText(target));
    }

    // ── Planner ──

    /// <summary>A copy skips a same-name, same-size file as "already there"; a move must not
    /// (it would delete the song on a size match alone): it gets a numbered name instead.</summary>
    [Fact]
    public void MovePlan_SameSizeFileThere_IsRenamedNotSkipped()
    {
        var song = Song("01 - First.mp3");
        File.Copy(song, Path.Combine(_dst, "01 - First.mp3"));
        var track = new Track { Id = Guid.NewGuid(), FilePath = song, Title = "First" };

        var copy = SendToFolderPlanner.Plan(new[] { track }, _dst, null, false, SendToFolderPlanner.DiskProbe);
        var move = SendToFolderPlanner.Plan(new[] { track }, _dst, null, false, SendToFolderPlanner.DiskProbe, move: true);

        Assert.Equal(SendToFolderAction.SkipIdentical, copy.Single().Action);
        Assert.Equal(SendToFolderAction.Renamed, move.Single().Action);
        Assert.Equal(Path.Combine(_dst, "01 - First (2).mp3"), move.Single().TargetPath);
    }

    [Fact]
    public async Task MovePlan_SongAlreadyWhereItGoes_IsLeftAlone()
    {
        var song = Song("01 - First.mp3");
        File.WriteAllText(Path.ChangeExtension(song, ".lrc"), "[00:01.00]hi");
        var track = new Track { Id = Guid.NewGuid(), FilePath = song, Title = "First" };

        var plan = SendToFolderPlanner.Plan(new[] { track }, Path.GetDirectoryName(song)!, null, true,
            SendToFolderPlanner.DiskProbe, move: true);
        var result = await new SendToFolderService().MoveAsync(plan, null, TestContext.Current.CancellationToken);

        Assert.Equal(SendToFolderAction.SkipIdentical, plan.Single().Action);
        Assert.Empty(plan.Single().Sidecars);
        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(song));
        Assert.True(File.Exists(Path.ChangeExtension(song, ".lrc")));
    }

    // ── Dialog ──

    /// <summary>An album's songs through the dialog: every one is copied, organized by the pattern.</summary>
    [AvaloniaFact]
    public async Task AlbumSend_CopiesEveryAlbumSong()
    {
        var album = new Album
        {
            Id = Guid.NewGuid(), Name = "Fixture Album", Artist = "Tester",
            Tracks =
            {
                new Track { Id = Guid.NewGuid(), FilePath = Song("01 - First.mp3"), Title = "First", Artist = "Tester", AlbumArtist = "Tester", Album = "Fixture Album", TrackNumber = 1 },
                new Track { Id = Guid.NewGuid(), FilePath = Song("02 - Second.mp3"), Title = "Second", Artist = "Tester", AlbumArtist = "Tester", Album = "Fixture Album", TrackNumber = 2 },
            },
        };
        var vm = new SendToFolderViewModel(album.Tracks.ToList(), new SendToFolderService(), FileOrganizePlanner.DefaultPattern, _dst)
        {
            OrganizeIntoFolders = true,
        };
        await vm.PlanRebuild;
        Assert.Equal(Loc.T("SendTo.Copy"), vm.StartLabel);
        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(File.Exists(Path.Combine(_dst, "Tester", "Fixture Album", "01 First.mp3")));
        Assert.True(File.Exists(Path.Combine(_dst, "Tester", "Fixture Album", "02 Second.mp3")));
        // A copy leaves the songs where they were.
        Assert.True(File.Exists(Song("01 - First.mp3")));
        Assert.True(File.Exists(Song("02 - Second.mp3")));
    }

    [AvaloniaFact]
    public async Task MoveOption_MovesTakesLyricsAndHandsTheRemapOn()
    {
        var library = await ScannedLibraryAsync();
        var tracks = library.Tracks.OrderBy(t => t.TrackNumber).ToList();
        var oldIds = tracks.Select(t => t.Id).ToList();
        var vm = new SendToFolderViewModel(tracks, new SendToFolderService(library), FileOrganizePlanner.DefaultPattern, _dst)
        {
            IncludeLyrics = false,
        };
        IReadOnlyDictionary<Guid, Guid>? handed = null;
        vm.ApplyTrackIdRemap = remap => { handed = remap; return Task.CompletedTask; };

        vm.MoveFiles = true;
        await vm.PlanRebuild;
        Assert.True(vm.IncludeLyrics);            // a move always takes the lyrics
        Assert.False(vm.CanChooseLyrics);
        Assert.Equal(Loc.T("SendTo.Move"), vm.StartLabel);
        Assert.Equal(Loc.T("SendToFolder.StateMove"), vm.Rows[0].ChipText);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(Loc.T("SendToFolder.DoneMove", 2), vm.StatusMessage);
        Assert.Equal(Loc.T("SendToFolder.StateMoved"), vm.Rows[0].ChipText);
        Assert.All(tracks, t => Assert.StartsWith(_dst, t.FilePath));
        Assert.False(File.Exists(Song("01 - First.mp3")));
        Assert.NotNull(handed);
        Assert.Equal(oldIds.ToHashSet(), handed!.Keys.ToHashSet());
    }

    // ── Menus ──

    [AvaloniaFact]
    public void AlbumMenu_SendToFolder_SitsInTools_BoundToTheAlbum()
    {
        var b = new AlbumContextMenuBuilder();
        b.Build("Remove from Library", new Border(), v2: true);
        var none = new RelayCommand<object?>(_ => { });
        var album = new Album { Id = Guid.NewGuid(), Name = "X", Artist = "Y", TrackCount = 1 };
        b.Bind(album, none, none, none, none, none, none, none, none, none);
        Assert.False(b.SendToFolder.IsVisible); // a view that doesn't opt in shows no row

        var send = new RelayCommand<Album>(_ => { });
        b.BindSendToFolder(album, send);

        Assert.Contains(b.SendToFolder, b.Tools.Items.OfType<MenuItem>());
        Assert.True(b.SendToFolder.IsVisible);
        Assert.True(b.Tools.IsVisible);
        Assert.Same(send, b.SendToFolder.Command);
        Assert.Same(album, b.SendToFolder.CommandParameter);
        Assert.Equal(Loc.T("SendTo.Title"), b.SendToFolder.Header);

        b.BindSendToFolder(album, null);
        Assert.False(b.SendToFolder.IsVisible);
    }

    [AvaloniaFact]
    public void ArtistTileMenu_SendToFolder_BoundToTheArtist()
    {
        var none = new RelayCommand<Artist>(_ => { });
        var menu = new LibraryArtistsView.ArtistTileMenu(new Border(), none, none, none, none);
        var artist = new Artist { Name = "Tester" };
        menu.Bind(artist);
        Assert.False(menu.SendToFolder.IsVisible);

        var send = new RelayCommand<Artist>(_ => { });
        menu.BindSendToFolder(artist, send);

        Assert.True(menu.SendToFolder.IsVisible);
        Assert.Same(send, menu.SendToFolder.Command);
        Assert.Same(artist, menu.SendToFolder.CommandParameter);
    }

    private sealed class NoLastFm : ILastFmService
    {
        public bool IsAuthenticated => false;
        public string? Username => null;
        public void Configure(string? sessionKey) { }
        public Task<string> GetAuthUrlAsync() => Task.FromResult(string.Empty);
        public Task<bool> CompleteAuthAsync() => Task.FromResult(false);
        public string? GetSessionKey() => null;
        public void Logout() { }
        public Task ScrobbleAsync(Track track, DateTime startedAt) => Task.CompletedTask;
        public Task UpdateNowPlayingAsync(Track track) => Task.CompletedTask;
        public Task<string?> GetAlbumDescriptionAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<string?> GetAlbumDescriptionFullAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task SetAlbumDescriptionOverrideAsync(string artistName, string albumName, string? description, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task ClearAlbumDescriptionOverrideAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>Opens every header "…" flyout of the page and collects its items' commands.</summary>
    private static List<System.Windows.Input.ICommand?> HeaderFlyoutCommands(Control view)
    {
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var commands = new List<System.Windows.Input.ICommand?>();
        try
        {
            foreach (var button in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Button>()
                         .Where(b => b.Flyout is MenuFlyout).ToList())
            {
                var flyout = (MenuFlyout)button.Flyout!;
                flyout.ShowAt(button);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                commands.AddRange(flyout.Items.OfType<MenuItem>().Select(i => i.Command));
                flyout.Hide();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
        }
        finally { window.Close(); }
        return commands;
    }

    /// <summary>Bug: AlbumDetailViewModel.SendAlbumToFolderCommand ("header menu") existed since
    /// 2a616afb but no menu bound it, so a whole album could not be sent.</summary>
    [AvaloniaFact]
    public void AlbumPage_HeaderMenu_HasSendToFolder()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var tracks = new List<Track> { new() { Id = Guid.NewGuid(), Title = "A", Artist = "X", FilePath = Song("01 - First.mp3") } };
        var album = new Album { Id = Guid.NewGuid(), Name = "Al", Artist = "X", TrackCount = 1, Tracks = tracks };
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new NoLastFm());

        Assert.Contains(vm.SendAlbumToFolderCommand, HeaderFlyoutCommands(new AlbumDetailView { DataContext = vm }));
    }

    [AvaloniaFact]
    public void ArtistPage_HeaderMenu_HasSendToFolder()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("X", lib, player);

        Assert.Contains(vm.SendArtistToFolderCommand, HeaderFlyoutCommands(new ArtistDetailView { DataContext = vm }));
    }

    [Fact]
    public async Task ArtistsGrid_SendsTheArtistsSongs()
    {
        var library = await ScannedLibraryAsync();
        var vm = new LibraryArtistsViewModel(library);

        var tracks = vm.TracksOf(new Artist { Name = "Tester" });

        Assert.Equal(new[] { "First", "Second" }, tracks.Select(t => t.Title));
    }
}
