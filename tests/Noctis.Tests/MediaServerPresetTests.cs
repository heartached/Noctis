using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.MediaServer;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Music Server picker offers named presets (Jellyfin, Navidrome, Airsonic,
/// Gonic, Subsonic (other)) instead of a bare protocol pair. Everything except
/// Jellyfin speaks the Subsonic protocol underneath, and the picked flavor is
/// stamped onto the persisted connection so the connected summary and a later
/// relaunch echo the user's choice.
/// </summary>
public class MediaServerPresetTests
{
    [Theory]
    [InlineData("Jellyfin", SourceType.Jellyfin)]
    [InlineData("Navidrome", SourceType.Navidrome)]
    [InlineData("Airsonic", SourceType.Navidrome)]
    [InlineData("Gonic", SourceType.Navidrome)]
    [InlineData("Subsonic (other)", SourceType.Navidrome)]
    [InlineData(null, SourceType.Jellyfin)] // mirrors the field's default selection
    public void PresetMapsToProtocol(string? option, SourceType expected)
        => Assert.Equal(expected, SettingsViewModel.MediaServerOptionToSourceType(option));

    [AvaloniaFact]
    public async Task Connect_PassesProtocolAndStampsFlavor()
    {
        var server = new FakeMediaServerService();
        var vm = new SettingsViewModel(
            new TestPersistenceService(), new FakeLibraryService(), new NoOpPlayHistoryService(), server);
        await vm.LoadAsync();

        vm.MediaServerType = "Gonic";
        vm.MediaServerUrl = "https://demo.example";
        vm.MediaServerUsername = "u";
        vm.MediaServerPassword = "pw";
        await vm.ConnectMediaServerCommand.ExecuteAsync(null);

        Assert.Equal(SourceType.Navidrome, server.ConnectedType);
        Assert.True(vm.IsMediaServerConnected);
        Assert.False(vm.HasMediaServerError);
        Assert.NotNull(server.Active);
        Assert.Equal("Gonic", server.Active!.Name);
    }

    [AvaloniaFact]
    public async Task Connect_MissingAddress_FlagsErrorWithoutCallingServer()
    {
        var server = new FakeMediaServerService();
        var vm = new SettingsViewModel(
            new TestPersistenceService(), new FakeLibraryService(), new NoOpPlayHistoryService(), server);
        await vm.LoadAsync();

        vm.MediaServerType = "Navidrome";
        vm.MediaServerUrl = "";
        vm.MediaServerUsername = "u";
        vm.MediaServerPassword = "pw";
        await vm.ConnectMediaServerCommand.ExecuteAsync(null);

        Assert.True(vm.HasMediaServerError);
        Assert.False(vm.IsMediaServerConnected);
        Assert.Null(server.ConnectedType);
    }

    [AvaloniaFact]
    public async Task StoredFlavor_RestoredOnLoad()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        try
        {
            var seed = new PersistenceService(root);
            var settings = await seed.LoadSettingsAsync();
            settings.SourceConnections.Add(new SourceConnection
            {
                Type = SourceType.Navidrome,
                Name = "Gonic",
                BaseUriOrPath = "https://demo.example",
                Username = "u",
                TokenOrPassword = "pw",
            });
            await seed.SaveSettingsAsync(settings);

            var vm = new SettingsViewModel(
                new PersistenceService(root), new FakeLibraryService(), new NoOpPlayHistoryService());
            await vm.LoadAsync();

            Assert.True(vm.IsMediaServerConnected);
            Assert.Equal("Gonic", vm.MediaServerType);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task LegacyProtocolName_FallsBackToGenericSubsonicPreset()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        try
        {
            var seed = new PersistenceService(root);
            var settings = await seed.LoadSettingsAsync();
            // Connections saved before flavors existed carry the client's generic
            // protocol name ("Subsonic"), which is not a picker option.
            settings.SourceConnections.Add(new SourceConnection
            {
                Type = SourceType.Navidrome,
                Name = "Subsonic",
                BaseUriOrPath = "https://demo.example",
                Username = "u",
                TokenOrPassword = "pw",
            });
            await seed.SaveSettingsAsync(settings);

            var vm = new SettingsViewModel(
                new PersistenceService(root), new FakeLibraryService(), new NoOpPlayHistoryService());
            await vm.LoadAsync();

            Assert.Equal("Subsonic (other)", vm.MediaServerType);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed class FakeMediaServerService : IMediaServerService
    {
        public SourceType? ConnectedType;
        public SourceConnection? Active;

        public SourceConnection? ActiveConnection => Active;
        public bool IsConfigured => Active != null;
        public event EventHandler? ActiveConnectionChanged { add { } remove { } }

        public Task<(MediaServerConnectResult result, SourceConnection connection)> ConnectAsync(
            SourceType type, string url, string username, string password, Guid? existingId, CancellationToken ct = default)
        {
            ConnectedType = type;
            // Mirrors the real service: a fresh connection stamped with the generic
            // protocol name — the VM is expected to overwrite it with the preset.
            var connection = new SourceConnection
            {
                Id = existingId ?? Guid.NewGuid(),
                Name = type == SourceType.Jellyfin ? "Jellyfin" : "Subsonic",
                Type = type,
                BaseUriOrPath = url,
                Username = username,
                TokenOrPassword = "token",
                Enabled = true,
            };
            return Task.FromResult((new MediaServerConnectResult { Success = true, Message = "Connected" }, connection));
        }

        public void SetActiveConnection(SourceConnection? connection) => Active = connection;

        // Browse surface is never touched by these tests.
        public Task<IReadOnlyList<ServerAlbum>> GetAlbumsAsync(int offset, int limit, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<Track>> GetAlbumTracksAsync(ServerAlbum album, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ServerSearchResult> SearchAsync(string query, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string?> EnsureAlbumArtworkAsync(ServerAlbum album, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
