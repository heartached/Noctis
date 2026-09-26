using System.Text.Json;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Organize Files moves tracks, and a track's id is derived from its path, so every
/// playlist reference to a moved track must follow it. The organizer used to remap a
/// separate copy loaded from playlists.json; the sidebar's live playlists kept the old
/// ids, so moved songs showed as missing and the next playlist save wrote the old ids
/// back over the remap — the songs were gone from every playlist for good.
/// </summary>
public class FileOrganizerPlaylistRemapTests
{
    /// <summary>Keeps playlists as JSON, like playlists.json: every load hands out fresh objects.</summary>
    private sealed class JsonPlaylistPersistence : TestPersistenceService
    {
        private string _json = "[]";
        public List<Playlist> Stored => JsonSerializer.Deserialize<List<Playlist>>(_json)!;
        public override Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(Stored);
        public override Task SavePlaylistsAsync(List<Playlist> playlists)
        {
            _json = JsonSerializer.Serialize(playlists);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Organize_ThenAnyPlaylistSave_KeepsTheMovedTrackIds()
    {
        using var persistence = new JsonPlaylistPersistence();
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        await persistence.SavePlaylistsAsync(new List<Playlist> { new() { Name = "Mix", TrackIds = { oldId } } });

        var library = new FakeLibraryService();
        library.RelocateRemap[oldId] = newId;
        var sidebar = new SidebarViewModel(persistence, library);
        await sidebar.LoadPlaylistsAsync();
        var playlist = sidebar.Playlists.Single();

        var source = Path.Combine(persistence.DataDirectory, "song.mp3");
        var target = Path.Combine(persistence.DataDirectory, "Artist", "Album", "01 song.mp3");
        File.WriteAllText(source, "audio");

        var organizer = new FileOrganizerService(library, persistence);
        var result = await organizer.ApplyAsync(
            new[] { new OrganizeMove(oldId, source, target, OrganizeAction.Move) },
            TestContext.Current.CancellationToken);
        // What OrganizeFilesViewModel does with the result once the move finishes.
        await sidebar.ApplyTrackIdRemapAsync(result.TrackIdRemap);

        Assert.Equal(1, result.Moved);
        // The live playlist (what playlist pages read) follows the move, same instance.
        Assert.Same(playlist, sidebar.Playlists.Single());
        Assert.Equal(new[] { newId }, playlist.TrackIds);

        // Any later playlist edit writes the in-memory list back to disk.
        await sidebar.TogglePinAsync(playlist.Id);
        Assert.Equal(new[] { newId }, persistence.Stored.Single().TrackIds);
    }
}
