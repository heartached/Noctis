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

    /// <summary>Track we have already reported as having no cover, so seeks don't repeat it.</summary>
    private string? _noArtLoggedForIdentity;

    public bool IsConnected => _client is { IsInitialized: true, IsDisposed: false };

    // Background reconnect. Bounded so a permanently-absent Discord doesn't retry
    // forever, and cancelled by Disconnect/Dispose.
    private const int ReconnectDelaySeconds = 30;
    private const int MaxReconnectAttempts = 20;   // ~10 minutes
    private int _reconnectAttempts;
    private CancellationTokenSource? _reconnectCts;

    /// <summary>Set by the owner; reconnect stops when this returns false.</summary>
    public Func<bool>? IsEnabled { get; set; }

    private void ScheduleReconnect()
    {
        if (_reconnectAttempts >= MaxReconnectAttempts) return;
        if (_reconnectCts != null) return;   // one in flight is enough

        var cts = new CancellationTokenSource();
        _reconnectCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested && _reconnectAttempts < MaxReconnectAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(ReconnectDelaySeconds), cts.Token);
                    if (cts.IsCancellationRequested) return;
                    if (IsEnabled?.Invoke() == false) return;
                    if (IsConnected) return;

                    _reconnectAttempts++;
                    if (await ConnectAsync(cts.Token)) return;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.WriteLine($"[Discord] Reconnect loop: {ex.Message}"); }
            finally
            {
                if (ReferenceEquals(_reconnectCts, cts))
                {
                    _reconnectCts = null;
                    cts.Dispose();
                }
            }
        });
    }

    private void CancelReconnect()
    {
        var cts = _reconnectCts;
        _reconnectCts = null;
        try { cts?.Cancel(); cts?.Dispose(); } catch { }
    }

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
                DebugLog.Write("Discord",
                    "Could not open the Discord RPC pipe — Discord may not be running. Will retry.");
                client.Dispose();

                // Retry in the background. ConnectAsync is called exactly once,
                // fire-and-forget, from SettingsViewModel.LoadAsync — so if Discord
                // wasn't running at that moment the user got no presence at all for the
                // rest of the session (every publish site is gated on IsConnected) until
                // they manually toggled the setting off and on.
                ScheduleReconnect();
                return false;
            }

            _client = client;
            // Reset the retry budget on success. Without this the count only ever grew, so
            // once a session had spent all 20 attempts (Discord closed for a while) no later
            // drop could ever be recovered from — ScheduleReconnect became a permanent no-op.
            _reconnectAttempts = 0;
            Debug.WriteLine("[Discord] Connected.");
            DebugLog.Write("Discord", "Rich Presence connected.");
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
        CancelReconnect();
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
            if (artworkKey != null)
            {
                _lastArtworkKey = artworkKey;
                _lastTrackIdentity = identity;
            }
            else if (!string.Equals(_noArtLoggedForIdentity, identity, StringComparison.Ordinal))
            {
                // With no key the image is omitted and Discord falls back to the application
                // icon — which is exactly what "I can't see album covers" looks like. Say so
                // once per track; the Loon log line alongside it names the reason.
                _noArtLoggedForIdentity = identity;
                DebugLog.Write("Discord",
                    $"Published \"{title}\" without a cover; Discord shows the app icon instead.");
            }

            var presence = new RichPresence
            {
                Type = ActivityType.Listening,
                StatusDisplay = StatusDisplayType.State,
                Details = Truncate(title, 128),
                State = Truncate(artist, 128),
                // Only the artwork URL goes in here. Every asset *name* this used to send
                // ("noctis_icon" large, "play"/"pause" small) has to exist in the Discord
                // application's Art Assets, and it has none — GET /oauth2/applications/
                // {id}/assets returns []. Discord answers SET_ACTIVITY by echoing the
                // activity back with unknown asset names stripped, so those keys never
                // rendered; they only replaced the cover with a broken-image placeholder
                // whenever the relay URL was missing. A null key is omitted from the
                // payload, which is what Discord did with them anyway.
                Assets = new Assets
                {
                    LargeImageKey = artworkKey,
                    LargeImageText = artworkKey == null
                        ? null
                        : Truncate(!string.IsNullOrWhiteSpace(album) ? album : artist, 128),
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

    /// <summary>
    /// Forgets the cached artwork key. Called when the artwork relay drops: a reconnect
    /// rotates its clientId, so every URL handed out under the old one is dead. Without
    /// this, position updates kept re-publishing that dead URL and Discord replaced the
    /// cover with a broken-image placeholder instead of simply keeping the last good art.
    /// </summary>
    public void InvalidateArtworkCache()
    {
        _lastArtworkKey = null;
        _lastTrackIdentity = null;
    }

    public void Dispose()
    {
        // Best-effort synchronous teardown (called from DI container or shutdown path)
        CancelReconnect();
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
        InvalidateArtworkCache();

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
    /// Discord keeps showing the already-cached cover instead of losing it. Returns null
    /// for a track we have no art for at all, which omits the image from the payload —
    /// the application has no uploaded art assets to fall back on, so naming one only
    /// produced a broken-image placeholder.
    /// </summary>
    public static string? ResolveArtworkKey(string? incomingUrl, string identity, string? lastKey, string? lastIdentity)
    {
        if (!string.IsNullOrWhiteSpace(incomingUrl)) return incomingUrl;
        if (lastKey != null && string.Equals(identity, lastIdentity, StringComparison.Ordinal)) return lastKey;
        return null;
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
