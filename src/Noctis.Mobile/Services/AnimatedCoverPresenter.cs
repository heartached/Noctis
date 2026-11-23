using System;
using System.Collections.Generic;
using Noctis.Services;

namespace Noctis.Mobile.Services;

/// <summary>
/// The phone's video player for an animated cover (AndroidAnimatedCoverSurface: a muted, looping
/// ExoPlayer of its own drawing into a TextureView over the cover). Separate from the music
/// player: no audio track, no audio focus, no media session. The surface shows nothing until the
/// loop's first frame is drawn, then fades in over the still cover.
/// </summary>
public interface IAnimatedCoverPlayer
{
    /// <summary>Plays <paramref name="source"/> (a file path or content:// URI) on a loop: loads it
    /// when it is not the one loaded, else resumes it.</summary>
    void Play(string source);

    /// <summary>Stops the loop where it is, keeping the decoder for a quick return.</summary>
    void Pause();

    /// <summary>Lets the decoder and its buffers go; the next <see cref="Play"/> starts afresh.</summary>
    void Release();

    /// <summary>The loop cannot play (a codec the phone lacks, a damaged file): raised on the UI thread.</summary>
    event EventHandler? Failed;
}

/// <summary>
/// When Now Playing's animated cover moves. The native video sits OVER the Avalonia drawing, and
/// Avalonia moves it only when a layout bound or visibility changes (NativeControlHost subscribes
/// to Bounds and IsVisible only): it does not follow a sliding sheet, the cover's pause shrink or
/// a swipe, and anything drawn over the cover (a menu, the queue, the lyrics fading in) would be
/// under it. So it shows only while the cover is <c>seen</c> and still: Now Playing landed and on
/// top, the music playing, the app in front, no Battery Saver, nothing over it, no finger on it.
/// It appears <see cref="SettleDelay"/> after that holds (every transition has ended by then), goes
/// the moment it stops (paused at once, the still cover under it showing), and the decoder is let
/// go <see cref="ReleaseDelay"/> later. A loop that fails is not tried again this run: the still
/// cover stays. UI thread only.
/// </summary>
public sealed class AnimatedCoverPresenter
{
    /// <summary>Longer than every motion that moves the cover: the sheet (400 ms), the pause
    /// shrink (450 ms), a swipe's settle.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>A hide this short keeps the decoder (a menu opened and closed, a pause and play).</summary>
    public static readonly TimeSpan ReleaseDelay = TimeSpan.FromSeconds(5);

    private readonly IAnimatedCoverPlayer _player;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private IDisposable? _pendingShow, _pendingRelease;
    private string? _source, _loaded;
    private bool _seen;

    /// <param name="schedule">Runs the action once after the delay on the UI thread; disposing the
    /// result cancels it (DispatcherTimer on the phone, a manual clock in tests).</param>
    public AnimatedCoverPresenter(IAnimatedCoverPlayer player, Func<TimeSpan, Action, IDisposable> schedule)
    {
        _player = player;
        _schedule = schedule;
        _player.Failed += OnFailed;
    }

    /// <summary>The video is up over the cover (the host control visible); it is still see-through
    /// until its first frame is drawn.</summary>
    public bool IsShown { get; private set; }

    /// <summary>Raised when <see cref="IsShown"/> changes.</summary>
    public event EventHandler? IsShownChanged;

    /// <summary>The loop to show (null: none) and whether the cover can be seen and is still.</summary>
    public void Update(string? source, bool seen)
    {
        if (source is not null && _failed.Contains(source)) source = null;
        var changed = !string.Equals(source, _source, StringComparison.Ordinal);
        _source = source;
        _seen = seen;
        if (source is null || !seen || changed)
        {
            CancelShow();
            Hide();
            // Another song's loop, or none: the old decoder has nothing left to show.
            if (_loaded is not null && !string.Equals(_loaded, source, StringComparison.Ordinal)) ReleaseNow();
            else if (_loaded is not null) ScheduleRelease();
        }
        if (source is null || !seen || IsShown || _pendingShow is not null) return;
        CancelRelease();
        _pendingShow = _schedule(SettleDelay, Show);
    }

    /// <summary>Lets everything go (the page left the tree).</summary>
    public void Detach()
    {
        CancelShow();
        Hide();
        ReleaseNow();
    }

    private void Show()
    {
        _pendingShow = null;
        if (_source is not { } source || !_seen) return;
        _player.Play(source);
        if (_loaded != source) DebugLog.Write("AnimatedCover", "playing the loop");
        _loaded = source;
        SetShown(true);
    }

    private void Hide()
    {
        if (!IsShown) return;
        _player.Pause();
        SetShown(false);
    }

    private void OnFailed(object? sender, EventArgs e)
    {
        if (_loaded is { } bad) _failed.Add(bad);
        DebugLog.Write("AnimatedCover", "the loop cannot play here: the still cover stays");
        CancelShow();
        Hide();
        ReleaseNow();
    }

    private void SetShown(bool shown)
    {
        if (IsShown == shown) return;
        IsShown = shown;
        IsShownChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ScheduleRelease()
    {
        if (_pendingRelease is not null) return;
        _pendingRelease = _schedule(ReleaseDelay, () =>
        {
            _pendingRelease = null;
            if (!IsShown) ReleaseNow();
        });
    }

    private void ReleaseNow()
    {
        CancelRelease();
        if (_loaded is null) return;
        _loaded = null;
        _player.Release();
        DebugLog.Write("AnimatedCover", "decoder released");
    }

    private void CancelShow()
    {
        _pendingShow?.Dispose();
        _pendingShow = null;
    }

    private void CancelRelease()
    {
        _pendingRelease?.Dispose();
        _pendingRelease = null;
    }
}
