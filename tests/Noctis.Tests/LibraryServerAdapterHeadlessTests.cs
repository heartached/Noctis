using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The OpenSubsonic server's library adapter used to hop onto the Avalonia dispatcher
/// for every mutation, which made it unusable in a process with no UI thread (every
/// request hung). It now takes the marshalling step from its host: the desktop passes
/// the dispatcher, noctis-server passes nothing and gets a serialising lock. These
/// pin the headless default.
/// </summary>
public class LibraryServerAdapterHeadlessTests
{
    private static Track MakeTrack(string title) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = "A", Album = "B", AlbumArtist = "A",
        FilePath = @"C:\music\" + title + ".flac", AlbumId = Guid.NewGuid(),
    };

    [Fact]
    public async Task Scrobbles_FromManyThreads_AreSerialised_AndAllLand()
    {
        var library = new FakeLibraryService();
        var track = MakeTrack("one");
        library.TrackList.Add(track);
        using var persistence = new TestPersistenceService();
        var history = new CountingPlayHistory();
        var adapter = new LibraryServerAdapter(library, persistence, history);

        // Kestrel would call this from many threads at once.
        var calls = Enumerable.Range(0, 50).Select(_ => Task.Run(() => adapter.ScrobbleAsync(track.Id)));
        await Task.WhenAll(calls);

        Assert.Equal(50, track.PlayCount);
        Assert.Equal(50, history.Plays);
        Assert.Equal(1, history.MaxConcurrent);
    }

    [Fact]
    public async Task Scrobble_WithATime_NeverMovesLastPlayedBack_AndPlayCountNeverWraps()
    {
        var library = new FakeLibraryService();
        var track = MakeTrack("late");
        var recent = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);
        track.LastPlayed = recent;
        track.PlayCount = int.MaxValue;
        library.TrackList.Add(track);
        using var persistence = new TestPersistenceService();
        var adapter = new LibraryServerAdapter(library, persistence, new CountingPlayHistory());

        await adapter.ScrobbleAsync(track.Id, recent.AddHours(-5)); // an offline play reported late
        Assert.Equal(recent, track.LastPlayed);
        Assert.Equal(int.MaxValue, track.PlayCount);

        await adapter.ScrobbleAsync(track.Id, recent.AddHours(1));
        Assert.Equal(recent.AddHours(1), track.LastPlayed);
    }

    [Fact]
    public async Task PhonePlays_AreForwardedOnlyWhenAsked_OnceEach_AtThePhonesTime_AndLoggedOnTheirDay()
    {
        var library = new FakeLibraryService();
        var track = MakeTrack("phone");
        library.TrackList.Add(track);
        using var persistence = new TestPersistenceService();
        var history = new PlayHistoryService(System.IO.Path.Combine(persistence.DataDirectory, "play_history.json"));
        var forwarded = new List<(Track Track, DateTime At)>();
        var adapter = new LibraryServerAdapter(library, persistence, history,
            forwardScrobble: (t, at) => { lock (forwarded) forwarded.Add((t, at)); return Task.CompletedTask; },
            scrobbleServices: () => (true, false));

        var live = DateTime.UtcNow.AddMinutes(-1);
        history.RecordPlay(MakeTrack("desktop"));                        // the desktop played since
        var offline = DateTime.UtcNow.AddDays(-2);                          // the phone, offline two days ago
        await adapter.ScrobbleAsync(track.Id, offline, forward: true);
        await adapter.ScrobbleAsync(track.Id, live, forward: false);       // skipped, or sent by the phone itself
        await adapter.ScrobbleAsync(track.Id);                             // a plain Subsonic client

        var sent = Assert.Single(forwarded);
        Assert.Same(track, sent.Track);
        Assert.Equal(offline, sent.At);
        Assert.Equal(3, track.PlayCount);
        Assert.Equal((true, false), adapter.ScrobbleServices);
        // The log keeps its order, the offline play on the day it was played (it was stamped now).
        var events = history.Events;
        Assert.Equal(4, events.Count);
        Assert.Equal(offline, events[0].PlayedAtUtc);
        Assert.Equal(events.OrderBy(e => e.PlayedAtUtc).Select(e => e.PlayedAtUtc), events.Select(e => e.PlayedAtUtc));
        Assert.Null(new LibraryServerAdapter(library, persistence, history).ScrobbleServices);   // the headless host
    }

    [Fact]
    public async Task Star_ThenSnapshot_ReflectsTheChange_WithoutADispatcher()
    {
        var library = new FakeLibraryService();
        var track = MakeTrack("two");
        library.TrackList.Add(track);
        using var persistence = new TestPersistenceService();
        var adapter = new LibraryServerAdapter(library, persistence, new CountingPlayHistory());

        await adapter.SetStarredAsync(new[] { track.Id }, Array.Empty<Guid>(), Array.Empty<Guid>(), starred: true);
        var snapshot = await adapter.SnapshotAsync();

        Assert.True(track.IsFavorite);
        Assert.Single(snapshot.Tracks);
        Assert.True(snapshot.Tracks[0].IsFavorite);
    }

    [Fact]
    public void AvatarPath_IsTheCurrentProfilePicture_WhileItExists()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "noctis-avatar-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var picture = System.IO.Path.Combine(dir, "avatar-1.png");
            System.IO.File.WriteAllBytes(picture, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            string? setting = picture;
            using var persistence = new TestPersistenceService();
            var adapter = new LibraryServerAdapter(new FakeLibraryService(), persistence, new CountingPlayHistory(), avatarPath: () => setting);

            Assert.Equal(picture, adapter.AvatarPath("alice"));
            setting = System.IO.Path.Combine(dir, "gone.png");   // read per call: a new pick shows at once
            Assert.Null(adapter.AvatarPath("alice"));
            setting = string.Empty;                              // the desktop's "no picture"
            Assert.Null(adapter.AvatarPath("alice"));
            // The headless host passes nothing: no avatar.
            Assert.Null(new LibraryServerAdapter(new FakeLibraryService(), persistence, new CountingPlayHistory()).AvatarPath("alice"));
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Playlists_RoundTrip_ThroughThePersistenceStore()
    {
        var library = new FakeLibraryService();
        var track = MakeTrack("three");
        library.TrackList.Add(track);
        using var persistence = new PlaylistKeepingPersistence();
        var adapter = new LibraryServerAdapter(library, persistence, new CountingPlayHistory());

        var created = await adapter.CreatePlaylistAsync("Road", new[] { track.Id });
        Assert.True(await adapter.UpdatePlaylistAsync(created.Id, "Road trip", Array.Empty<Guid>(), Array.Empty<int>()));
        var snapshot = await adapter.SnapshotAsync();
        Assert.Equal("Road trip", Assert.Single(snapshot.Playlists).Name);
        Assert.True(await adapter.DeletePlaylistAsync(created.Id));
        Assert.Empty((await adapter.SnapshotAsync()).Playlists);
    }

    [Fact]
    public async Task SongsFromAPhone_AskTheHostForTheirFolder_AndImport_OnTheLibrarysThread()
    {
        using var persistence = new TestPersistenceService();
        // The headless host passes no folder: phones cannot send songs to it.
        Assert.Null(await new LibraryServerAdapter(new FakeLibraryService(), persistence, new CountingPlayHistory())
            .UploadRootAsync(TestContext.Current.CancellationToken));

        var marshalled = 0;
        var adapter = new LibraryServerAdapter(new FakeLibraryService(), persistence, new CountingPlayHistory(),
            marshal: async work => { Interlocked.Increment(ref marshalled); await work(); },
            uploadRoot: _ => Task.FromResult<string?>(@"C:\Music\Noctis Imports"));

        Assert.Equal(@"C:\Music\Noctis Imports", await adapter.UploadRootAsync(TestContext.Current.CancellationToken));
        await adapter.ImportUploadedAsync(@"C:\Music\Noctis Imports\Phone\a.mp3", TestContext.Current.CancellationToken);
        Assert.Equal(2, marshalled);   // the desktop's marshal is the UI thread: settings and library writes live there
    }

    /// <summary>The shared fake forgets playlists; this one keeps them like the real store.</summary>
    private sealed class PlaylistKeepingPersistence : TestPersistenceService
    {
        private List<Playlist> _playlists = new();
        public override Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(_playlists.Select(p => p).ToList());
        public override Task SavePlaylistsAsync(List<Playlist> playlists) { _playlists = playlists.ToList(); return Task.CompletedTask; }
    }

    private sealed class CountingPlayHistory : IPlayHistoryService
    {
        private int _inFlight;
        public int Plays;
        public int MaxConcurrent;
        public IReadOnlyList<PlayHistoryEvent> Events { get; } = new List<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track)
        {
            var now = Interlocked.Increment(ref _inFlight);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            Thread.Sleep(2); // widen the window so a missing lock would show as overlap
            Interlocked.Increment(ref Plays);
            Interlocked.Decrement(ref _inFlight);
        }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
}
