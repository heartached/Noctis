using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Tests;

/// <summary>Shared builders for the Phase 4 phone page tests: a shell over the fakes, and
/// tracks/albums carrying just the fields the pages read.</summary>
internal static class MobileFixtures
{
    internal sealed class NoPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Artist photos without a network: <see cref="Cached"/> answers at once (a photo already on
    /// the phone); <see cref="Online"/> is what a lookup finds, handed out when the test calls
    /// <see cref="Complete"/> (or at once with <see cref="AnswerAtOnce"/>). Records every ask and
    /// every cancelled one.
    /// </summary>
    internal sealed class FakeArtistPhotos : IArtistPhotoSource
    {
        public Dictionary<string, string> Cached { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Online { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Asked { get; } = new();
        public List<string> Cancelled { get; } = new();
        public bool AnswerAtOnce { get; set; }
        private readonly List<(string Name, TaskCompletionSource<string?> Answer)> _waiting = new();

        public string? CachedPhoto(string artistName) => Cached.GetValueOrDefault(artistName);

        public Task<string?> GetPhotoAsync(string artistName, System.Threading.CancellationToken ct)
        {
            Asked.Add(artistName);
            if (Cached.TryGetValue(artistName, out var cached)) return Task.FromResult<string?>(cached);
            if (AnswerAtOnce) return Task.FromResult(Online.GetValueOrDefault(artistName));
            var answer = new TaskCompletionSource<string?>();
            ct.Register(() =>
            {
                Cancelled.Add(artistName);
                answer.TrySetCanceled(ct);
            });
            _waiting.Add((artistName, answer));
            return answer.Task;
        }

        /// <summary>Answers every lookup still waiting with what <see cref="Online"/> has (cached from then on).</summary>
        public void Complete()
        {
            foreach (var (name, answer) in _waiting.ToList())
            {
                var path = Online.GetValueOrDefault(name);
                if (path != null) Cached[name] = path;
                answer.TrySetResult(path);
            }
            _waiting.Clear();
        }
    }

    internal sealed class Rig : IDisposable
    {
        public required ShellViewModel Shell { get; init; }
        public required FakeLibraryService Library { get; init; }
        public required FakeAudioPlayer Player { get; init; }
        public required PersistenceService Persistence { get; init; }
        public required FakeHistoryLog History { get; init; }
        public required string Root { get; init; }
        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch { } }
    }

    /// <summary>A shell over the fakes, with the library already initialised. <paramref name="seed"/>
    /// runs against the persistence root first (settings, playlists).</summary>
    internal static Rig MakeRig(Track[]? tracks = null, Album[]? albums = null, Func<PersistenceService, Task>? seed = null, Action<FakeHistoryLog>? log = null, Func<PageTint>? tint = null, IThemeHost? theme = null, IArtistPhotoSource? photos = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var library = new FakeLibraryService();
        library.TrackList.AddRange(tracks ?? Array.Empty<Track>());
        ((List<Album>)library.Albums).AddRange(albums ?? Array.Empty<Album>());
        var persistence = new PersistenceService(root);
        if (seed != null) RunBlocking(() => seed(persistence));
        var history = new FakeHistoryLog();
        log?.Invoke(history);
        var player = new FakeAudioPlayer();
        var nowPlaying = new NowPlayingViewModel(player, library, persistence, history, marshal: a => a());
        var shell = new ShellViewModel(
            new LibraryViewModel(library, persistence, new NoPicker(), history, marshal: a => a()),
            nowPlaying,
            new LyricsPageViewModel(player, nowPlaying, new FakeTrackFiles(), persistence, work => Task.FromResult(work())))
        {
            // Tests never decode covers: no tint, extracted synchronously.
            TintFactory = tint ?? (() => new PageTint(_ => null, work => Task.FromResult(work()))),
            Theme = theme,
            ArtistPhotos = photos,
        };
        RunBlocking(shell.Library.InitializeAsync);
        return new Rig { Shell = shell, Library = library, Player = player, Persistence = persistence, History = history, Root = root };
    }

    /// <summary>
    /// Block on async setup without deadlocking an [AvaloniaFact]: PersistenceService awaits
    /// real file reads without ConfigureAwait(false), so once a seeded settings/playlists file
    /// exists its continuation is posted to the headless UI thread this call is blocking.
    /// Running the work on the pool keeps the continuations off that thread.
    /// </summary>
    private static void RunBlocking(Func<Task> work) => Task.Run(work).GetAwaiter().GetResult();

    internal static Window Mount(ShellViewModel shell, out ShellView view, double width = 412, double height = 915)
    {
        view = new ShellView { DataContext = shell };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    internal static T Find<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>().First();

    /// <summary>A named control anywhere below <paramref name="root"/>: pages are UserControls
    /// with their own name scopes, so ShellView.FindControl cannot see into them.</summary>
    internal static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    internal static Track Song(string title, string artist = "Artist", int daysAgo = 0, bool favourite = false, int plays = 0) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = artist, AlbumArtist = artist, Album = "Album",
        FilePath = "content://x/" + Uri.EscapeDataString(title), Duration = TimeSpan.FromSeconds(90),
        DateAdded = DateTime.UtcNow.AddDays(-daysAgo), IsFavorite = favourite, PlayCount = plays,
    };

    /// <summary>An album over <paramref name="tracks"/>, re-pointing each track at it the way the library index does.</summary>
    internal static Album MakeAlbum(string name, string artist, params Track[] tracks)
    {
        var album = new Album
        {
            Id = Guid.NewGuid(), Name = name, Artist = artist, Tracks = tracks.ToList(), TrackCount = tracks.Length,
            TotalDuration = TimeSpan.FromSeconds(tracks.Sum(t => t.Duration.TotalSeconds)),
        };
        foreach (var t in tracks)
        {
            t.AlbumId = album.Id;
            t.Album = name;
            t.AlbumArtist = artist;
        }
        return album;
    }
}
