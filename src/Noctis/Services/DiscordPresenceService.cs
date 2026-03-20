using System.Diagnostics;
using DiscordRPC;

using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Manages Discord Rich Presence via the DiscordRPC library.
/// Thread-safe: all public methods are guarded by a <see cref="SemaphoreSlim"/>.
/// Idempotent: repeated connect/disconnect calls are safe no-ops.
/// Never throws to callers — failures are logged via <see cref="Debug.WriteLine"/>.
/// </summary>
public sealed class DiscordPresenceService : IDiscordPresenceService
{
    private const string ApplicationId = "1470224696976085096";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DiscordRpcClient? _client;

    public bool IsConnected => _client is { IsInitialized: true, IsDisposed: false };

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (IsConnected) return true;

            // Tear down stale client if it exists
            DisposeClient();

            var client = new DiscordRpcClient(ApplicationId)
            {
                SkipIdenticalPresence = true,
            };

            client.OnError += (_, e) =>
                Debug.WriteLine($"[Discord] RPC error: {e.Message}");

            client.OnConnectionFailed += (_, _) =>
                Debug.WriteLine("[Discord] Connection failed — Discord may not be running.");

            var ok = await Task.Run(() => client.Initialize(), ct);
            if (!ok)
            {
                Debug.WriteLine("[Discord] Initialize returned false.");
                client.Dispose();
                return false;
            }

            _client = client;
            Debug.WriteLine("[Discord] Connected.");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Discord] ConnectAsync failed: {ex.Message}");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            DisposeClient();
            Debug.WriteLine("[Discord] Disconnected.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Discord] DisconnectAsync failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(DiscordPresenceTrack track, TimeSpan position, TimeSpan? duration, bool isPlaying)
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsConnected) return;

            var title = string.IsNullOrWhiteSpace(track.Title) ? "Unknown" : track.Title;
            var artist = string.IsNullOrWhiteSpace(track.Artist) ? "Unknown Artist" : track.Artist;
            var album = track.Album;

            var presence = new RichPresence
            {
                Type = ActivityType.Listening,
                StatusDisplay = StatusDisplayType.State,
                Details = Truncate(title, 128),
                State = Truncate(artist, 128),
                Assets = new Assets
                {
                    LargeImageKey = !string.IsNullOrWhiteSpace(track.ArtworkUrl) ? track.ArtworkUrl : "noctis_icon",
                    LargeImageText = Truncate(!string.IsNullOrWhiteSpace(album) ? album : artist, 128),
                    SmallImageKey = isPlaying ? "play" : "pause",
                    SmallImageText = isPlaying ? "Playing" : "Paused",
                },
            };

            if (isPlaying && duration.HasValue && duration.Value.TotalSeconds > 0)
            {
                var now = DateTime.UtcNow;
                presence.Timestamps = new Timestamps
                {
                    Start = now - position,
                    End = now + (duration.Value - position),
                };
            }

            _client!.SetPresence(presence);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Discord] UpdateAsync failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _client?.ClearPresence();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Discord] ClearAsync failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        // Best-effort synchronous teardown (called from DI container or shutdown path)
        try
        {
            DisposeClient();
        }
        catch
        {
            // Ignore disposal errors
        }
    }

    // ── Helpers ──

    private void DisposeClient()
    {
        if (_client == null) return;
        try
        {
            _client.ClearPresence();
            _client.Dispose();
        }
        catch
        {
            // Ignore errors during teardown
        }
        _client = null;
    }

    /// <summary>
    /// Truncates a string to fit Discord's field limits (max 128 chars)
    /// and pads to meet the minimum 2-character requirement.
    /// Discord silently drops the entire presence update when any text field is 1 char.
    /// </summary>
    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var result = value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
        // Discord requires all text fields to be at least 2 characters.
        // Single-char names (e.g. album "?") cause the entire SetPresence call to fail silently.
        if (result.Length == 1)
            result = result + "\u200B"; // zero-width space pad
        return result;
    }
}
