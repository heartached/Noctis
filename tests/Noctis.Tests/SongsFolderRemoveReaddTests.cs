using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 09-23 (owner): after removing the only media folder and re-adding it, Albums showed the
/// music but Songs stayed at "0 songs"; on removal Songs kept the old rows for ~10 s.
/// Drives the real LibraryService scan + the Songs view model through that sequence.
/// </summary>
[Collection("MetadataServiceStatics")]
public class SongsFolderRemoveReaddTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private readonly string _music;

    public SongsFolderRemoveReaddTests()
    {
        _music = Path.Combine(_root, "music");
        var album = Path.Combine(_music, "Album");
        Directory.CreateDirectory(album);
        WriteMp3(Path.Combine(album, "01 - First.mp3"), "First");
        WriteMp3(Path.Combine(album, "02 - Second.mp3"), "Second");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static void WriteMp3(string path, string title)
    {
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        using var f = TagLib.File.Create(path);
        f.Tag.Title = title; f.Tag.Performers = new[] { "Tester" }; f.Tag.Album = "Fixture Album";
        f.Save();
    }

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Songs_FollowsRemoveAndReaddOfTheOnlyFolder()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoOpAudit());
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, persistence, new FakeAnimatedCoverService());
        var songs = new LibrarySongsViewModel(library, player, new SidebarViewModel(persistence, library), persistence);

        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { _music } });
        await Task.Run(() => library.ScanAsync(new[] { _music }));
        songs.Refresh();
        await PumpUntil(() => songs.FilteredTracks.Count == 2);
        Assert.Equal(2, songs.FilteredTracks.Count);

        // Remove the folder → rescan → Songs must empty.
        await persistence.SaveSettingsAsync(new AppSettings());
        await Task.Run(() => library.ScanAsync(Array.Empty<string>()));
        Assert.Empty(library.Tracks);
        songs.Refresh();
        await PumpUntil(() => songs.FilteredTracks.Count == 0);
        Assert.Empty(songs.FilteredTracks);

        // Re-add → rescan → Songs must list the files again.
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { _music } });
        await Task.Run(() => library.ScanAsync(new[] { _music }));
        Assert.Equal(2, library.Tracks.Count);
        songs.Refresh();
        await PumpUntil(() => songs.FilteredTracks.Count == 2);
        Assert.Equal(2, songs.FilteredTracks.Count);
    }

    private sealed class NoOpAudit : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
    }
}
