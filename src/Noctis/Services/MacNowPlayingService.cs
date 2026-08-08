using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Services;

/// <summary>
/// macOS counterpart to <see cref="SmtcService"/> (Windows) and
/// <see cref="MprisService"/> (Linux): publishes the current track to
/// MPNowPlayingInfoCenter (the Control Center "Now Playing" widget) and
/// registers MPRemoteCommandCenter handlers so hardware media keys, the
/// Touch Bar and AirPods controls drive playback. macOS delivers media keys
/// only to the app registered with MPRemoteCommandCenter — without this they
/// are dead (issue #38). Uses raw objc_msgSend interop; no new dependency.
/// Fail-soft: any interop error logs and leaves the app without media-key
/// integration rather than affecting launch. Inert on non-macOS platforms.
/// All AppKit/MediaPlayer calls happen on the UI thread, which on macOS is
/// the AppKit main thread.
/// </summary>
public sealed class MacNowPlayingService : IDisposable
{
    // MPNowPlayingPlaybackState (NSInteger — pointer-sized, so passed as IntPtr)
    private const int PlaybackStatePlaying = 1;
    private const int PlaybackStatePaused = 2;
    private const int PlaybackStateStopped = 3;

    private const int CommandHandlerSuccess = 0;

    private static MacNowPlayingService? s_service;

    private readonly PlayerViewModel _player;
    private volatile bool _disposed;

    private IntPtr _infoCenter;
    private IntPtr _commandCenter;
    private IntPtr _handler;
    private readonly List<IntPtr> _targetedCommands = new();

    // dlsym'd NSString* dictionary keys from MediaPlayer.framework.
    private IntPtr _keyTitle, _keyArtist, _keyAlbum, _keyDuration, _keyArtwork, _keyElapsed, _keyRate;

    /// <summary>Artwork object cached per art path — rebuilding an NSImage on every
    /// pause/resume would re-read the file each time (see MprisService.SnapshotState
    /// for the same concern with the stat call).</summary>
    private IntPtr _cachedArtwork;
    private string? _cachedArtworkPath = string.Empty;

    public static MacNowPlayingService? TryStart(PlayerViewModel player)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        if (s_service != null) return s_service;
        try
        {
            s_service = new MacNowPlayingService(player);
            DebugLogger.Info(DebugLogger.Category.Playback, "MacNowPlaying.Started", "MPRemoteCommandCenter registered");
            return s_service;
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Playback, "MacNowPlaying.Init", ex.Message);
            return null;
        }
    }

    private MacNowPlayingService(PlayerViewModel player)
    {
        _player = player;

        // AppKit is already loaded (Avalonia runs on it); MediaPlayer is not.
        if (Dlopen("/System/Library/Frameworks/MediaPlayer.framework/MediaPlayer", RtldNow) == IntPtr.Zero)
            throw new InvalidOperationException("dlopen(MediaPlayer.framework) failed");

        _keyTitle = ReadConstant("MPMediaItemPropertyTitle");
        _keyArtist = ReadConstant("MPMediaItemPropertyArtist");
        _keyAlbum = ReadConstant("MPMediaItemPropertyAlbumTitle");
        _keyDuration = ReadConstant("MPMediaItemPropertyPlaybackDuration");
        _keyArtwork = ReadConstant("MPMediaItemPropertyArtwork");
        _keyElapsed = ReadConstant("MPNowPlayingInfoPropertyElapsedPlaybackTime");
        _keyRate = ReadConstant("MPNowPlayingInfoPropertyPlaybackRate");

        _infoCenter = MsgSend(GetClass("MPNowPlayingInfoCenter"), Sel("defaultCenter"));
        _commandCenter = MsgSend(GetClass("MPRemoteCommandCenter"), Sel("sharedCommandCenter"));
        if (_infoCenter == IntPtr.Zero || _commandCenter == IntPtr.Zero)
            throw new InvalidOperationException("MPNowPlayingInfoCenter/MPRemoteCommandCenter unavailable");

        _handler = MsgSend(RegisterHandlerClass(), Sel("new"));

        WireCommand("playCommand", "noctisHandlePlay:");
        WireCommand("pauseCommand", "noctisHandlePause:");
        WireCommand("togglePlayPauseCommand", "noctisHandleToggle:");
        WireCommand("nextTrackCommand", "noctisHandleNext:");
        WireCommand("previousTrackCommand", "noctisHandlePrevious:");
        WireCommand("changePlaybackPositionCommand", "noctisHandleChangePosition:");

        _player.PropertyChanged += OnPlayerPropertyChanged;
        _player.Seeked += OnPlayerSeeked;
        UpdateNowPlaying();
    }

    // ── Player → Now Playing ──

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.State)
            or nameof(PlayerViewModel.CurrentTrack)
            or nameof(PlayerViewModel.CurrentArtPath))
        {
            OnUiThread(UpdateNowPlaying);
        }
    }

    private void OnPlayerSeeked(object? sender, TimeSpan position) => OnUiThread(UpdateNowPlaying);

    /// <summary>Pushes title/artist/album/art, duration, elapsed and rate. Runs only on
    /// track/state/art/seek changes — macOS extrapolates the live position from
    /// elapsed + rate, so no per-second timer is needed.</summary>
    private void UpdateNowPlaying()
    {
        if (_disposed) return;
        try
        {
            var track = _player.CurrentTrack;
            if (track == null)
            {
                MsgSendVoid(_infoCenter, Sel("setNowPlayingInfo:"), IntPtr.Zero);
                MsgSendVoid(_infoCenter, Sel("setPlaybackState:"), new IntPtr(PlaybackStateStopped));
                return;
            }

            var dict = MsgSend(GetClass("NSMutableDictionary"), Sel("new"));
            SetString(dict, _keyTitle, track.TitleDisplay);
            SetString(dict, _keyArtist, track.ArtistDisplay);
            SetString(dict, _keyAlbum, track.Album);
            SetDouble(dict, _keyDuration, track.Duration.TotalSeconds);
            SetDouble(dict, _keyElapsed, _player.Position.TotalSeconds);
            SetDouble(dict, _keyRate, _player.IsPlaying ? 1.0 : 0.0);

            var artwork = GetArtwork(_player.CurrentArtPath);
            if (artwork != IntPtr.Zero)
                MsgSendVoid(dict, Sel("setObject:forKey:"), artwork, _keyArtwork);

            MsgSendVoid(_infoCenter, Sel("setNowPlayingInfo:"), dict);
            MsgSendVoid(dict, Sel("release"));

            MsgSendVoid(_infoCenter, Sel("setPlaybackState:"),
                new IntPtr(_player.IsPlaying ? PlaybackStatePlaying : PlaybackStatePaused));
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Playback, "MacNowPlaying.Update", ex.Message);
        }
    }

    private IntPtr GetArtwork(string? artPath)
    {
        if (string.Equals(artPath, _cachedArtworkPath, StringComparison.Ordinal))
            return _cachedArtwork;

        _cachedArtworkPath = artPath;
        if (_cachedArtwork != IntPtr.Zero)
        {
            MsgSendVoid(_cachedArtwork, Sel("release"));
            _cachedArtwork = IntPtr.Zero;
        }
        if (string.IsNullOrEmpty(artPath)) return IntPtr.Zero;

        // initWithImage: is the block-free initializer; it is deprecated in favor of
        // initWithBoundsSize:requestHandler:, so probe for it rather than risking an
        // unrecognized-selector NSException (which would abort the process).
        var artworkClass = GetClass("MPMediaItemArtwork");
        if (!MsgSendBool(artworkClass, Sel("instancesRespondToSelector:"), Sel("initWithImage:")))
            return IntPtr.Zero;

        var image = MsgSend(MsgSend(GetClass("NSImage"), Sel("alloc")),
            Sel("initWithContentsOfFile:"), NsString(artPath));
        if (image == IntPtr.Zero) return IntPtr.Zero;

        _cachedArtwork = MsgSend(MsgSend(artworkClass, Sel("alloc")), Sel("initWithImage:"), image);
        MsgSendVoid(image, Sel("release"));
        return _cachedArtwork;
    }

    // ── Remote commands → player ──

    private void WireCommand(string commandProperty, string handlerSelector)
    {
        var command = MsgSend(_commandCenter, Sel(commandProperty));
        if (command == IntPtr.Zero) return;
        MsgSendVoid(command, Sel("setEnabled:"), true);
        MsgSendVoid(command, Sel("addTarget:action:"), _handler, Sel(handlerSelector));
        _targetedCommands.Add(command);
    }

    /// <summary>One ObjC class hosts all command callbacks; addTarget:action: needs a
    /// real target object, and a runtime-registered class avoids the block-literal ABI
    /// entirely. Registered once per process (the class cannot be unregistered).</summary>
    private static IntPtr RegisterHandlerClass()
    {
        const string className = "NoctisRemoteCommandHandler";
        var existing = GetClass(className);
        if (existing != IntPtr.Zero) return existing;

        var cls = objc_allocateClassPair(GetClass("NSObject"), className, 0);
        if (cls == IntPtr.Zero) throw new InvalidOperationException("objc_allocateClassPair failed");

        // "q@:@" = NSInteger (id self, SEL _cmd, MPRemoteCommandEvent* event).
        AddMethod(cls, "noctisHandlePlay:", s_handlePlay);
        AddMethod(cls, "noctisHandlePause:", s_handlePause);
        AddMethod(cls, "noctisHandleToggle:", s_handleToggle);
        AddMethod(cls, "noctisHandleNext:", s_handleNext);
        AddMethod(cls, "noctisHandlePrevious:", s_handlePrevious);
        AddMethod(cls, "noctisHandleChangePosition:", s_handleChangePosition);
        objc_registerClassPair(cls);
        return cls;
    }

    private static void AddMethod(IntPtr cls, string selector, RemoteCommandCallback callback)
    {
        if (!class_addMethod(cls, Sel(selector), Marshal.GetFunctionPointerForDelegate(callback), "q@:@"))
            throw new InvalidOperationException($"class_addMethod({selector}) failed");
    }

    private delegate nint RemoteCommandCallback(IntPtr self, IntPtr sel, IntPtr evt);

    // Rooted in static fields so the reverse-P/Invoke thunks outlive any GC.
    private static readonly RemoteCommandCallback s_handlePlay = (_, _, _) =>
        DispatchToPlayer(p => { if (!p.IsPlaying) p.PlayPauseCommand.Execute(null); });
    private static readonly RemoteCommandCallback s_handlePause = (_, _, _) =>
        DispatchToPlayer(p => { if (p.IsPlaying) p.PlayPauseCommand.Execute(null); });
    private static readonly RemoteCommandCallback s_handleToggle = (_, _, _) =>
        DispatchToPlayer(p => p.PlayPauseCommand.Execute(null));
    private static readonly RemoteCommandCallback s_handleNext = (_, _, _) =>
        DispatchToPlayer(p => p.NextCommand.Execute(null));
    private static readonly RemoteCommandCallback s_handlePrevious = (_, _, _) =>
        DispatchToPlayer(p => p.PreviousCommand.Execute(null));
    private static readonly RemoteCommandCallback s_handleChangePosition = (_, _, evt) =>
    {
        // The event object is only valid for the duration of the callback, so the
        // position must be read before dispatching.
        var seconds = MsgSendDouble(evt, Sel("positionTime"));
        return DispatchToPlayer(p =>
        {
            var duration = p.Duration;
            if (duration <= TimeSpan.Zero) return;
            var fraction = Math.Clamp(seconds / duration.TotalSeconds, 0.0, 1.0);
            p.SeekToPositionCommand.Execute(fraction);
        });
    };

    private static nint DispatchToPlayer(Action<PlayerViewModel> action)
    {
        var service = s_service;
        if (service is { _disposed: false })
            Dispatcher.UIThread.Post(() => action(service._player));
        return CommandHandlerSuccess;
    }

    private static void OnUiThread(Action action) => Dispatcher.UIThread.Post(action);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        _player.Seeked -= OnPlayerSeeked;
        try
        {
            foreach (var command in _targetedCommands)
                MsgSendVoid(command, Sel("removeTarget:"), _handler);
            _targetedCommands.Clear();
            MsgSendVoid(_infoCenter, Sel("setNowPlayingInfo:"), IntPtr.Zero);
            MsgSendVoid(_infoCenter, Sel("setPlaybackState:"), new IntPtr(PlaybackStateStopped));
        }
        catch { /* best effort on shutdown */ }
        if (ReferenceEquals(s_service, this)) s_service = null;
    }

    // ── objc interop ──

    private const int RtldNow = 2;

    private static IntPtr ReadConstant(string symbol)
    {
        // Framework constants are exported as NSString* variables; dlsym hands back
        // the variable's address, so one dereference yields the id.
        var address = Dlsym(DlopenSelf, symbol);
        if (address == IntPtr.Zero) throw new InvalidOperationException($"dlsym({symbol}) failed");
        return Marshal.ReadIntPtr(address);
    }

    /// <summary>RTLD_DEFAULT — search every loaded image, so the MediaPlayer handle
    /// needn't be threaded through.</summary>
    private static readonly IntPtr DlopenSelf = new(-2);

    private static void SetString(IntPtr dict, IntPtr key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        MsgSendVoid(dict, Sel("setObject:forKey:"), NsString(value), key);
    }

    private static void SetDouble(IntPtr dict, IntPtr key, double value)
    {
        var number = MsgSendIntPtrDouble(GetClass("NSNumber"), Sel("numberWithDouble:"), value);
        MsgSendVoid(dict, Sel("setObject:forKey:"), number, key);
    }

    private static IntPtr NsString(string value) =>
        MsgSendUtf8(GetClass("NSString"), Sel("stringWithUTF8String:"), value);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr Sel([MarshalAs(UnmanagedType.LPUTF8Str)] string selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoid(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoid(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoid(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoid(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MsgSendBool(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern double MsgSendDouble(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendIntPtrDouble(IntPtr receiver, IntPtr selector, double arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendUtf8(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint extraBytes);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern void objc_registerClassPair(IntPtr cls);

    [DllImport("/usr/lib/libobjc.dylib")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(IntPtr cls, IntPtr selector, IntPtr imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "dlopen")]
    private static extern IntPtr Dlopen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "dlsym")]
    private static extern IntPtr Dlsym(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string symbol);
}
