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
    private const string DefaultIconKey = "noctis_icon";

    /// <summary>
    /// Grace period between clearing the presence and tearing down the pipe.
    /// DiscordRPC.NET flushes presence frames on an internal worker thread; disposing
    /// immediately after <c>ClearPresence()</c> drops the queued clear frame, leaving a
    /// stale presence in Discord. The library exposes no synchronous flush, so we wait
    /// a bounded period to let the worker send it.
    /// </summary>
    private const int ClearFlushDelayMs = 250;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DiscordRpcClient? _client;

    // Monotonic stamp for presence mutations (updates and clears). SemaphoreSlim
    // wakes waiters in no particular order, so when tracks are skipped rapidly an
    // older update could acquire the gate AFTER a newer one and overwrite Discord
    // with a stale song. Each call takes a stamp on entry and bails inside the
    // gate if a newer call has arrived since — last call wins.
    private long _presenceSequence;

    // Last successfully-published artwork key and the track it belonged to.
    // Used to avoid flipping good art to the app icon when the artwork relay
    // transiently drops mid-track (relay outage -> null URL -> would otherwise
    // overwrite the cached cover with the logo).
    private string? _lastArtworkKey;
    private string? _lastTrackIdentity;

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
            // Flush a clear frame and give the RPC worker time to send it before we
            // close the pipe — otherwise the presence lingers in Discord after toggle-off.
            if (_client is { IsDisposed: false })
            {
                try { _client.ClearPresence(); } catch { /* best effort */ }
                await Task.Delay(ClearFlushDelayMs);
            }

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
        var seq = Interlocked.Increment(ref _presenceSequence);
        await _gate.WaitAsync();
        try
        {
            if (seq != Interlocked.Read(ref _presenceSequence)) return; // superseded by a newer update/clear
            if (!IsConnected) return;

            var title = string.IsNullOrWhiteSpace(track.Title) ? "Unknown" : track.Title;
            var artist = string.IsNullOrWhiteSpace(track.Artist) ? "Unknown Artist" : track.Artist;
            var album = track.Album;

            var identity = TrackIdentity(title, artist, album);
            var artworkKey = ResolveArtworkKey(track.ArtworkUrl, identity, _lastArtworkKey, _lastTrackIdentity);
            if (!string.Equals(artworkKey, DefaultIconKey, StringComparison.Ordinal))
            {
                _lastArtworkKey = artworkKey;
                _lastTrackIdentity = identity;
            }

            var presence = new RichPresence
            {
                Type = ActivityType.Listening,
                StatusDisplay = StatusDisplayType.State,
                Details = Truncate(title, 128),
                State = Truncate(artist, 128),
                Assets = new Assets
                {
                    LargeImageKey = artworkKey,
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
        var seq = Interlocked.Increment(ref _presenceSequence);
        await _gate.WaitAsync();
        try
        {
            if (seq != Interlocked.Read(ref _presenceSequence)) return; // superseded by a newer update
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
        // Forget cached art so a later reconnect can't reuse a key from a prior session.
        _lastArtworkKey = null;
        _lastTrackIdentity = null;

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

    /// <summary>Stable identity for a track, used to scope cached artwork keys.</summary>
    private static string TrackIdentity(string title, string artist, string? album)
        => $"{title}{artist}{album}";

    /// <summary>
    /// Chooses the Discord <c>LargeImageKey</c>. Prefers a fresh artwork URL; if none is
    /// available (relay transiently down) it reuses the last good key for the SAME track so
    /// Discord keeps showing the already-cached cover instead of flipping to the app icon.
    /// Falls back to the app icon only for a track we have no prior art for.
    /// </summary>
    public static string ResolveArtworkKey(string? incomingUrl, string identity, string? lastKey, string? lastIdentity)
    {
        if (!string.IsNullOrWhiteSpace(incomingUrl)) return incomingUrl;
        if (lastKey != null && string.Equals(identity, lastIdentity, StringComparison.Ordinal)) return lastKey;
        return DefaultIconKey;
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
