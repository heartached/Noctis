using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Noctis.ViewModels;

namespace Noctis.Services.LocalApi;

/// <summary>
/// Server-Sent Events for the Local API (<c>GET /api/v1/events</c>).
///
/// Threading: the player's PropertyChanged / collection events are raised on the UI
/// thread; the handlers here only build a small JSON string and TryWrite it into each
/// subscriber's bounded channel (never blocking). Each stream's socket writes happen on
/// its own worker task. The hub hooks the player only while at least one stream is open.
///
/// Events: track-changed, state-changed, position (≤ 1/s while playing, and right after a
/// seek), queue-changed, lyrics-line (only for streams opened with ?lyrics=1). A
/// ": ping" comment goes out on every idle heartbeat interval so proxies and OBS keep the
/// connection, and so a dead peer is noticed.
/// </summary>
public sealed class LocalApiEventHub
{
    private readonly PlayerViewModel _player;
    private readonly ILocalApiLyricsSource? _lyrics;
    private readonly Func<Action, Task> _marshal;
    private readonly object _gate = new();
    private readonly List<Subscriber> _subscribers = new();

    // UI-thread state.
    private bool _hooked;
    private long _lastPositionTicks = -1; // -1: nothing sent yet
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Guid? _lyricsTrackId;
    private long[] _lyricStarts = Array.Empty<long>();
    private string[] _lyricTexts = Array.Empty<string>();
    private string? _lastLyricKey; // track:index:text of the last lyrics-line sent
    private Guid? _lastTrackId;

    public LocalApiEventHub(PlayerViewModel player, ILocalApiLyricsSource? lyrics, Func<Action, Task> marshal,
        TimeSpan heartbeat, int maxStreams)
    {
        _player = player;
        _lyrics = lyrics;
        _marshal = marshal;
        Heartbeat = heartbeat;
        MaxStreams = maxStreams;
    }

    public TimeSpan Heartbeat { get; }
    public int MaxStreams { get; }

    public int ActiveStreams { get { lock (_gate) return _subscribers.Count; } }

    private sealed class Subscriber
    {
        public required bool WantsLyrics { get; init; }
        public Channel<string> Queue { get; } = Channel.CreateBounded<string>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        public CancellationTokenSource Cts { get; } = new();
    }

    /// <summary>Reserves a stream slot; false when <see cref="MaxStreams"/> are open.</summary>
    private Subscriber? TryAdd(bool wantsLyrics)
    {
        lock (_gate)
        {
            if (_subscribers.Count >= MaxStreams) return null;
            var sub = new Subscriber { WantsLyrics = wantsLyrics };
            _subscribers.Add(sub);
            return sub;
        }
    }

    /// <summary>Ends every open stream (token regenerated, server stopping).</summary>
    public void DisconnectAll()
    {
        Subscriber[] all;
        lock (_gate) all = _subscribers.ToArray();
        foreach (var s in all)
        {
            try { s.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Runs one SSE stream until the client disconnects, <paramref name="ct"/> fires or
    /// <see cref="DisconnectAll"/> is called. Returns false (writing nothing) when the
    /// stream cap is reached, so the caller can answer 503.
    /// </summary>
    public async Task<bool> RunAsync(Stream stream, string responseHeader, bool wantsLyrics, CancellationToken ct)
    {
        var sub = TryAdd(wantsLyrics);
        if (sub == null) return false;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, sub.Cts.Token);
        var token = linked.Token;
        try
        {
            await WriteAsync(stream, responseHeader + "retry: 3000\n\n", token);

            // Initial snapshot so an overlay can paint before anything changes.
            string? initial = null;
            await _marshal(() =>
            {
                EnsureHooked();
                var sb = new StringBuilder();
                sb.Append(Format("track-changed", LocalApiDto.NowPlaying(_player)));
                sb.Append(Format("state-changed", LocalApiDto.Playback(_player)));
                if (wantsLyrics && CurrentLyricEvent() is { } line)
                    sb.Append(line);
                initial = sb.ToString();
            });
            await WriteAsync(stream, initial!, token);

            // A read that returns 0 means the peer closed its end: stop right away
            // instead of waiting for the next write to fail.
            _ = WatchForDisconnectAsync(stream, sub, token);

            var reader = sub.Queue.Reader;
            while (!token.IsCancellationRequested)
            {
                bool hasData;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    idle.CancelAfter(Heartbeat);
                    try
                    {
                        hasData = await reader.WaitToReadAsync(idle.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        await WriteAsync(stream, ": ping\n\n", token);
                        continue;
                    }
                }
                if (!hasData) break;

                var batch = new StringBuilder();
                while (reader.TryRead(out var msg)) batch.Append(msg);
                if (batch.Length > 0)
                    await WriteAsync(stream, batch.ToString(), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            bool last;
            lock (_gate)
            {
                _subscribers.Remove(sub);
                last = _subscribers.Count == 0;
            }
            sub.Cts.Dispose();
            if (last)
                _ = _marshal(UnhookIfIdle);
        }
        return true;
    }

    private static async Task WatchForDisconnectAsync(Stream stream, Subscriber sub, CancellationToken ct)
    {
        var buf = new byte[256];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (await stream.ReadAsync(buf, ct) == 0) break;
            }
        }
        catch { /* reset / disposed / cancelled: all mean "gone" */ }
        try { sub.Cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private static async Task WriteAsync(Stream stream, string text, CancellationToken ct)
    {
        // A client that stops reading fills the socket buffer and would park this
        // write forever; bound it so the slot is reclaimed.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), timeout.Token);
        await stream.FlushAsync(timeout.Token);
    }

    public static string Format(string eventName, object payload) =>
        $"event: {eventName}\ndata: {LocalApiDto.Serialize(payload)}\n\n";

    // ── UI-thread side ──

    private void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        _player.PropertyChanged += OnPlayerPropertyChanged;
        _player.Seeked += OnSeeked;
        _player.UpNext.CollectionChanged += OnQueueChanged;
        if (_lyrics != null) _lyrics.Changed += OnLyricsChanged;
        _lastPositionTicks = -1;
        _lastTrackId = _player.CurrentTrack?.Id;
        _lyricsTrackId = null;
        _lastLyricKey = null;
    }

    private void UnhookIfIdle()
    {
        if (!_hooked || ActiveStreams > 0) return;
        _hooked = false;
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        _player.Seeked -= OnSeeked;
        _player.UpNext.CollectionChanged -= OnQueueChanged;
        if (_lyrics != null) _lyrics.Changed -= OnLyricsChanged;
    }

    private void Broadcast(string message, bool lyricsOnly = false)
    {
        lock (_gate)
        {
            foreach (var s in _subscribers)
                if (!lyricsOnly || s.WantsLyrics)
                    s.Queue.Writer.TryWrite(message);
        }
    }

    private bool AnyLyricsSubscriber()
    {
        lock (_gate) return _subscribers.Any(s => s.WantsLyrics);
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.CurrentTrack):
                // PlayTrack re-assigns CurrentTrack (and re-raises it) for the same track on
                // play-from-stopped; only a different track is a change.
                if (_player.CurrentTrack?.Id == _lastTrackId) break;
                _lastTrackId = _player.CurrentTrack?.Id;
                Broadcast(Format("track-changed", LocalApiDto.NowPlaying(_player)));
                _lyricsTrackId = null; // re-read on the next position tick
                break;
            case nameof(PlayerViewModel.State):
            case nameof(PlayerViewModel.IsShuffleEnabled):
            case nameof(PlayerViewModel.RepeatMode):
            case nameof(PlayerViewModel.Volume):
            case nameof(PlayerViewModel.IsMuted):
                Broadcast(Format("state-changed", LocalApiDto.Playback(_player)));
                break;
            case nameof(PlayerViewModel.Position):
                OnPosition(force: false);
                break;
        }
    }

    private void OnSeeked(object? sender, TimeSpan e) => OnPosition(force: true);

    private void OnPosition(bool force)
    {
        var now = _clock.Elapsed.Ticks;
        if (force || _lastPositionTicks < 0 || now - _lastPositionTicks >= TimeSpan.TicksPerSecond)
        {
            _lastPositionTicks = now;
            Broadcast(Format("position", new
            {
                positionMs = (long)_player.Position.TotalMilliseconds,
                durationMs = (long)_player.Duration.TotalMilliseconds,
                state = LocalApiDto.StateName(_player.State),
            }));
        }

        if (AnyLyricsSubscriber() && CurrentLyricEvent(onlyIfChanged: true) is { } line)
            Broadcast(line, lyricsOnly: true);
    }

    private void OnQueueChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        Broadcast(Format("queue-changed", new { upNextCount = _player.UpNext.Count }));

    private void OnLyricsChanged(object? sender, EventArgs e)
    {
        _lyricsTrackId = null; // reload; the key check below drops an unchanged line
        if (AnyLyricsSubscriber() && CurrentLyricEvent(onlyIfChanged: true) is { } line)
            Broadcast(line, lyricsOnly: true);
    }

    /// <summary>lyrics-line event for the line under the playhead (null when the track has
    /// no synced lyrics, or when <paramref name="onlyIfChanged"/> and it's the same line).</summary>
    private string? CurrentLyricEvent(bool onlyIfChanged = false)
    {
        var track = _player.CurrentTrack;
        if (track == null || _lyrics == null) return null;

        if (_lyricsTrackId != track.Id)
        {
            var snap = _lyrics.Snapshot();
            if (snap is { Synced: true } && snap.TrackId == track.Id)
            {
                _lyricStarts = snap.Lines.Select(l => l.StartMs ?? 0).ToArray();
                _lyricTexts = snap.Lines.Select(l => l.Text).ToArray();
                _lyricsTrackId = track.Id;
            }
            else
            {
                _lyricStarts = Array.Empty<long>();
                _lyricTexts = Array.Empty<string>();
                // Leave _lyricsTrackId unset while nothing is loaded so a later
                // Changed/position tick retries once the lyrics arrive.
                if (snap != null) _lyricsTrackId = track.Id;
            }
        }
        if (_lyricStarts.Length == 0) return null;

        var pos = (long)_player.Position.TotalMilliseconds;
        // Last line that started at or before pos (lines are in time order; ~100 of them).
        var index = -1;
        for (var i = 0; i < _lyricStarts.Length && _lyricStarts[i] <= pos; i++) index = i;
        var text = index >= 0 ? _lyricTexts[index] : string.Empty;
        var key = $"{track.Id:N}:{index}:{text}";
        if (onlyIfChanged && key == _lastLyricKey) return null;
        _lastLyricKey = key;

        return Format("lyrics-line", new
        {
            trackId = track.Id,
            index,
            text,
            startMs = index >= 0 ? _lyricStarts[index] : 0,
            nextStartMs = index + 1 < _lyricStarts.Length ? _lyricStarts[index + 1] : (long?)null,
            positionMs = pos,
        });
    }
}
