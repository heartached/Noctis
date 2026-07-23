using Noctis.ViewModels;
#if WINDOWS
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Noctis.Models;
using Windows.Media;
using Windows.Storage.Streams;
#endif

namespace Noctis.Services;

/// <summary>
/// Windows System Media Transport Controls (SMTC) integration: shows the current
/// track in the Windows media overlay / quick-settings flyout and accepts
/// play/pause/next/previous from it (hardware media keys included), so playback
/// stays controllable while the window sits in the tray. Compiles to a no-op on
/// non-Windows builds (plain net8.0 TFM has no WinRT projections).
/// </summary>
public sealed class SmtcService : IDisposable
{
#if WINDOWS
    /// <summary>
    /// COM interop factory for HWND-scoped SMTC. Desktop (non-UWP) processes must
    /// use this — GetForCurrentView is UWP-only, and a dummy MediaPlayer's SMTC
    /// never registers a system media session for unpackaged apps (verified via
    /// GlobalSystemMediaTransportControlsSessionManager: no session appeared).
    /// </summary>
    [ComImport]
    [Guid("DDB0472D-C911-4A1F-86D9-DC3D71A95F5A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISystemMediaTransportControlsInterop
    {
        // The real interface derives from IInspectable, but built-in COM interop
        // only marshals IUnknown-style vtables — pad the three IInspectable slots
        // so GetForWindow lands on the correct vtable entry.
        void GetIids(out uint iidCount, out IntPtr iids);
        void GetRuntimeClassName(out IntPtr className);
        void GetTrustLevel(out int trustLevel);
        IntPtr GetForWindow(IntPtr appWindow, ref Guid riid);
    }

    /// <summary>IID of Windows.Media.ISystemMediaTransportControls.</summary>
    private static readonly Guid SmtcIid = new("99FA3FF4-1742-42A6-902E-087D41F965EC");

    private readonly PlayerViewModel _player;
    private SystemMediaTransportControls? _smtc;
    private int _thumbnailGeneration;

    public SmtcService(PlayerViewModel player, IntPtr windowHandle)
    {
        _player = player;
        try
        {
            if (windowHandle == IntPtr.Zero)
                throw new InvalidOperationException("No platform window handle available for SMTC.");

            // CsWinRT exposes the runtime class's activation factory via As<T>();
            // GetForWindow hands back an SMTC instance scoped to our window.
            var interop = SystemMediaTransportControls.As<ISystemMediaTransportControlsInterop>();
            var iid = SmtcIid;
            var abi = interop.GetForWindow(windowHandle, ref iid);
            try { _smtc = SystemMediaTransportControls.FromAbi(abi); }
            finally { Marshal.Release(abi); }

            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;
            _smtc.IsEnabled = true;
            _smtc.ButtonPressed += OnButtonPressed;
            _smtc.PlaybackPositionChangeRequested += OnPositionChangeRequested;
            _player.PropertyChanged += OnPlayerPropertyChanged;
            _player.Seeked += OnPlayerSeeked;

            // Reflect whatever is already loaded (e.g. the restored queue).
            UpdatePlaybackStatus();
            UpdateMetadata();
            UpdateTimeline(force: true);
        }
        catch (Exception ex)
        {
            // WinRT media stack unavailable (e.g. Windows N without the Media
            // Feature Pack) — run without the overlay rather than failing launch.
            System.Diagnostics.Debug.WriteLine($"[Smtc] Init failed: {ex.Message}");
            DebugLogger.Error(DebugLogger.Category.Playback, "Smtc.Init", ex.Message);
            Cleanup();
        }
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.State):
                UpdatePlaybackStatus();
                break;
            case nameof(PlayerViewModel.CurrentTrack):
                UpdatePlaybackStatus();
                UpdateMetadata();
                UpdateTimeline(force: true);
                break;
            case nameof(PlayerViewModel.CurrentArtPath):
                UpdateMetadata();
                break;
            case nameof(PlayerViewModel.Position):
            case nameof(PlayerViewModel.Duration):
                UpdateTimeline();
                break;
        }
    }

    private void OnPlayerSeeked(object? sender, TimeSpan newPosition) => UpdateTimeline(force: true);

    /// <summary>SMTC button presses arrive on a WinRT thread — hop to the UI
    /// thread before touching player commands.</summary>
    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var button = args.Button;
        Dispatcher.UIThread.Post(() =>
        {
            switch (button)
            {
                case SystemMediaTransportControlsButton.Play:
                    if (!_player.IsPlaying) _player.PlayPauseCommand.Execute(null);
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    if (_player.IsPlaying) _player.PlayPauseCommand.Execute(null);
                    break;
                case SystemMediaTransportControlsButton.Next:
                    _player.NextCommand.Execute(null);
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    _player.PreviousCommand.Execute(null);
                    break;
            }
        });
    }

    // Feeds the media flyout's progress bar/scrubber. Position updates arrive
    // ~4x/sec; a 1s throttle keeps the WinRT chatter down (seeks and track
    // changes push immediately via force).
    private DateTime _lastTimelinePushUtc;

    private void UpdateTimeline(bool force = false)
    {
        var smtc = _smtc;
        if (smtc == null) return;

        var now = DateTime.UtcNow;
        if (!force && now - _lastTimelinePushUtc < TimeSpan.FromSeconds(1)) return;
        _lastTimelinePushUtc = now;

        try
        {
            var duration = _player.Duration;
            smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                Position = _player.Position,
                MaxSeekTime = duration,
                EndTime = duration
            });
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Playback, "Smtc.Timeline", ex.Message);
        }
    }

    /// <summary>Scrubber drag in the media flyout — hop to the UI thread and
    /// route through the same seek command the in-app slider uses.</summary>
    private void OnPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
    {
        var requested = args.RequestedPlaybackPosition;
        Dispatcher.UIThread.Post(() =>
        {
            var duration = _player.Duration;
            if (duration <= TimeSpan.Zero) return;
            var fraction = Math.Clamp(requested.Ticks / (double)duration.Ticks, 0.0, 1.0);
            _player.SeekToPositionCommand.Execute(fraction);
        });
    }

    private void UpdatePlaybackStatus()
    {
        var smtc = _smtc;
        if (smtc == null) return;
        try
        {
            smtc.PlaybackStatus = _player.CurrentTrack == null
                ? MediaPlaybackStatus.Closed
                : _player.State switch
                {
                    PlaybackState.Playing => MediaPlaybackStatus.Playing,
                    PlaybackState.Paused => MediaPlaybackStatus.Paused,
                    _ => MediaPlaybackStatus.Stopped,
                };
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Playback, "Smtc.Status", ex.Message);
        }
    }

    private void UpdateMetadata()
    {
        var smtc = _smtc;
        if (smtc == null) return;
        try
        {
            var du = smtc.DisplayUpdater;
            var track = _player.CurrentTrack;
            if (track == null)
            {
                du.ClearAll();
                du.Update();
                return;
            }

            du.Type = MediaPlaybackType.Music;
            du.MusicProperties.Title = track.TitleDisplay;
            du.MusicProperties.Artist = track.ArtistDisplay;
            du.MusicProperties.AlbumTitle = track.Album;
            du.Update();

            // Art follows in a second Update: the file copy runs off the UI thread.
            QueueThumbnailUpdate(du);
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Playback, "Smtc.Metadata", ex.Message);
        }
    }

    private void QueueThumbnailUpdate(SystemMediaTransportControlsDisplayUpdater du)
    {
        var path = _player.CurrentArtPath;
        var generation = Interlocked.Increment(ref _thumbnailGeneration);
        _ = Task.Run(() =>
        {
            try
            {
                RandomAccessStreamReference? thumbnail = null;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    // Copy into a WinRT in-memory stream: the overlay clones the
                    // stream when it renders, and the .NET FileStream adapter does
                    // not support cloning (art silently fails to show). The native
                    // reference keeps the memory stream alive after we return.
                    var memory = new InMemoryRandomAccessStream();
                    var writer = memory.AsStreamForWrite(); // do not dispose: closes `memory`
                    using (var file = File.OpenRead(path))
                        file.CopyTo(writer);
                    writer.Flush();
                    memory.Seek(0);
                    thumbnail = RandomAccessStreamReference.CreateFromStream(memory);
                }

                // A newer track/art superseded this load — drop it.
                if (generation != _thumbnailGeneration) return;

                du.Thumbnail = thumbnail;
                du.Update();
            }
            catch (Exception ex)
            {
                DebugLogger.Error(DebugLogger.Category.Playback, "Smtc.Thumbnail", ex.Message);
            }
        });
    }

    public void Dispose() => Cleanup();

    private void Cleanup()
    {
        // Invalidate any in-flight thumbnail load so it can't touch a dead SMTC.
        Interlocked.Increment(ref _thumbnailGeneration);
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        _player.Seeked -= OnPlayerSeeked;
        if (_smtc != null)
        {
            try
            {
                _smtc.ButtonPressed -= OnButtonPressed;
                _smtc.PlaybackPositionChangeRequested -= OnPositionChangeRequested;
                _smtc.IsEnabled = false;
            }
            catch { /* best effort on shutdown */ }
            _smtc = null;
        }
    }
#else
    public SmtcService(PlayerViewModel player, IntPtr windowHandle) { }
    public void Dispose() { }
#endif
}
