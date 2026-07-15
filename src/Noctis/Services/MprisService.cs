using System.ComponentModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Tmds.DBus.Protocol;

namespace Noctis.Services;

/// <summary>
/// Linux counterpart to <see cref="SmtcService"/>: exposes playback over the
/// MPRIS D-Bus interface (org.mpris.MediaPlayer2.noctis) so hardware media
/// keys, the GNOME/KDE media widgets, and tools like playerctl can see and
/// control Noctis. Desktop environments deliver media keys to MPRIS players
/// only — without this, media keys are dead on Linux. Uses Tmds.DBus.Protocol,
/// which already ships with Avalonia's FreeDesktop backend (no new vendor).
/// Fail-soft: any D-Bus error logs and leaves the app without MPRIS rather
/// than affecting launch. Inert on non-Linux platforms.
/// </summary>
public sealed class MprisService : IDisposable
{
    private const string BusName = "org.mpris.MediaPlayer2.noctis";
    private const string MprisPath = "/org/mpris/MediaPlayer2";
    private const string IfaceRoot = "org.mpris.MediaPlayer2";
    private const string IfacePlayer = "org.mpris.MediaPlayer2.Player";
    private const string IfaceProps = "org.freedesktop.DBus.Properties";

    private readonly PlayerViewModel _player;
    private Connection? _connection;
    private volatile bool _disposed;

    // Snapshot of the now-playing state, written on the UI thread by
    // OnPlayerPropertyChanged and read from the D-Bus handler thread.
    private readonly object _stateLock = new();
    private string _status = "Stopped";
    private string? _title;
    private string? _artist;
    private string? _album;
    private string? _artUrl;
    private string _trackId = "/org/mpris/MediaPlayer2/TrackList/NoTrack";
    private long _lengthUs;

    public static MprisService? TryStart(PlayerViewModel player)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            return new MprisService(player);
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Playback, "Mpris.Init", ex.Message);
            return null;
        }
    }

    private MprisService(PlayerViewModel player)
    {
        _player = player;
        _player.PropertyChanged += OnPlayerPropertyChanged;
        SnapshotState();
        _ = Task.Run(InitializeAsync);
    }

    private async Task InitializeAsync()
    {
        try
        {
            var address = Address.Session;
            if (string.IsNullOrEmpty(address))
            {
                DebugLogger.Warn(DebugLogger.Category.Playback, "Mpris.NoBus",
                    "no session bus address; media keys unavailable");
                return;
            }

            var connection = new Connection(address);
            await connection.ConnectAsync();
            connection.AddMethodHandler(new Handler(this));

            // MessageWriter is a ref struct, so the message is built in a
            // separate non-async method.
            await connection.CallMethodAsync(CreateRequestNameMessage(connection));

            if (_disposed)
            {
                connection.Dispose();
                return;
            }

            _connection = connection;
            DebugLogger.Info(DebugLogger.Category.Playback, "Mpris.Started", BusName);

            // The desktop may have missed state set before the name was acquired.
            EmitPropertiesChanged(statusChanged: true, metadataChanged: true, volumeChanged: true);
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Playback, "Mpris.Init", ex.Message);
        }
    }

    private static MessageBuffer CreateRequestNameMessage(Connection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: "org.freedesktop.DBus",
            path: "/org/freedesktop/DBus",
            @interface: "org.freedesktop.DBus",
            member: "RequestName",
            signature: "su");
        writer.WriteString(BusName);
        writer.WriteUInt32(0);
        return writer.CreateMessage();
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.State):
                SnapshotState();
                EmitPropertiesChanged(statusChanged: true, metadataChanged: false, volumeChanged: false);
                break;
            case nameof(PlayerViewModel.CurrentTrack):
                SnapshotState();
                EmitPropertiesChanged(statusChanged: true, metadataChanged: true, volumeChanged: false);
                break;
            case nameof(PlayerViewModel.CurrentArtPath):
                SnapshotState();
                EmitPropertiesChanged(statusChanged: false, metadataChanged: true, volumeChanged: false);
                break;
            case nameof(PlayerViewModel.Volume):
                EmitPropertiesChanged(statusChanged: false, metadataChanged: false, volumeChanged: true);
                break;
        }
    }

    private void SnapshotState()
    {
        var track = _player.CurrentTrack;
        var artPath = _player.CurrentArtPath;
        var state = _player.State;
        lock (_stateLock)
        {
            _status = track == null
                ? "Stopped"
                : state switch
                {
                    PlaybackState.Playing => "Playing",
                    PlaybackState.Paused => "Paused",
                    _ => "Stopped",
                };
            _title = track?.TitleDisplay;
            _artist = track?.ArtistDisplay;
            _album = track?.Album;
            _lengthUs = track == null ? 0 : track.Duration.Ticks / 10;
            _trackId = track == null
                ? "/org/mpris/MediaPlayer2/TrackList/NoTrack"
                : "/com/heartached/noctis/track/" + track.Id.ToString("N");
            _artUrl = null;
            if (!string.IsNullOrEmpty(artPath) && File.Exists(artPath))
            {
                try { _artUrl = new Uri(artPath).AbsoluteUri; }
                catch { /* unmappable path — art just won't show */ }
            }
        }
    }

    // Must be called under _stateLock.
    private VariantValue BuildMetadataVariant()
    {
        var dict = new Dictionary<string, VariantValue>
        {
            ["mpris:trackid"] = new ObjectPath(_trackId),
        };
        if (_lengthUs > 0) dict["mpris:length"] = _lengthUs;
        if (!string.IsNullOrEmpty(_title)) dict["xesam:title"] = _title;
        if (!string.IsNullOrEmpty(_album)) dict["xesam:album"] = _album;
        if (!string.IsNullOrEmpty(_artist))
            dict["xesam:artist"] = VariantValue.Array(new[] { _artist });
        if (!string.IsNullOrEmpty(_artUrl)) dict["mpris:artUrl"] = _artUrl;
        return new Dict<string, VariantValue>(dict).AsVariantValue();
    }

    private void EmitPropertiesChanged(bool statusChanged, bool metadataChanged, bool volumeChanged)
    {
        var conn = _connection;
        if (conn == null || _disposed) return;
        try
        {
            using var writer = conn.GetMessageWriter();
            writer.WriteSignalHeader(
                path: MprisPath,
                @interface: IfaceProps,
                member: "PropertiesChanged",
                signature: "sa{sv}as");
            writer.WriteString(IfacePlayer);
            var dict = writer.WriteDictionaryStart();
            lock (_stateLock)
            {
                if (statusChanged)
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString("PlaybackStatus");
                    writer.WriteVariantString(_status);
                }
                if (metadataChanged)
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString("Metadata");
                    writer.WriteVariant(BuildMetadataVariant());
                }
            }
            if (volumeChanged)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("Volume");
                writer.WriteVariantDouble(Math.Clamp(_player.Volume, 0, 100) / 100.0);
            }
            writer.WriteDictionaryEnd(dict);
            writer.WriteArray(System.Array.Empty<string>()); // no invalidated properties
            conn.TrySendMessage(writer.CreateMessage());
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Playback, "Mpris.Signal", ex.Message);
        }
    }

    private void OnUiThread(Action action) => Dispatcher.UIThread.Post(action);

    private void ExecutePlayerCommand(string member)
    {
        OnUiThread(() =>
        {
            switch (member)
            {
                case "PlayPause":
                    _player.PlayPauseCommand.Execute(null);
                    break;
                case "Play":
                    if (!_player.IsPlaying) _player.PlayPauseCommand.Execute(null);
                    break;
                case "Pause":
                case "Stop":
                    if (_player.IsPlaying) _player.PlayPauseCommand.Execute(null);
                    break;
                case "Next":
                    _player.NextCommand.Execute(null);
                    break;
                case "Previous":
                    _player.PreviousCommand.Execute(null);
                    break;
            }
        });
    }

    private void SeekBy(long offsetUs)
    {
        OnUiThread(() =>
        {
            var duration = _player.Duration;
            if (duration <= TimeSpan.Zero) return;
            var target = _player.Position + TimeSpan.FromTicks(offsetUs * 10);
            var fraction = Math.Clamp(target.Ticks / (double)duration.Ticks, 0.0, 1.0);
            _player.SeekToPositionCommand.Execute(fraction);
        });
    }

    private void SeekTo(long positionUs)
    {
        OnUiThread(() =>
        {
            var duration = _player.Duration;
            if (duration <= TimeSpan.Zero) return;
            var fraction = Math.Clamp(positionUs * 10 / (double)duration.Ticks, 0.0, 1.0);
            _player.SeekToPositionCommand.Execute(fraction);
        });
    }

    private void RaiseWindow()
    {
        OnUiThread(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow is { } window)
            {
                window.Show();
                window.Activate();
            }
        });
    }

    private void QuitApp()
    {
        OnUiThread(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        try { _connection?.Dispose(); } catch { /* best effort on shutdown */ }
        _connection = null;
    }

    /// <summary>
    /// Serves the MPRIS object at /org/mpris/MediaPlayer2. Requests arrive on
    /// the D-Bus read loop; anything touching the ViewModel hops to the UI
    /// thread (commands) or reads the lock-protected snapshot (properties).
    /// </summary>
    private sealed class Handler : IMethodHandler
    {
        private readonly MprisService _s;

        public Handler(MprisService s) => _s = s;

        public string Path => MprisPath;

        public bool RunMethodHandlerSynchronously(Message message) => true;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var iface = context.Request.InterfaceAsString ?? string.Empty;
            var member = context.Request.MemberAsString ?? string.Empty;

            if (context.IsDBusIntrospectRequest)
            {
                context.ReplyIntrospectXml(new[]
                {
                    IntrospectionXml.DBusProperties,
                    IntrospectionXml.DBusPeer,
                    RootXml,
                    PlayerXml,
                });
                return default;
            }

            switch (iface)
            {
                case IfaceProps:
                    HandleProperties(context, member);
                    break;
                case IfaceRoot:
                    HandleRoot(context, member);
                    break;
                case IfacePlayer:
                    HandlePlayerMethod(context, member);
                    break;
                case "org.freedesktop.DBus.Peer" when member == "Ping":
                    ReplyEmpty(context);
                    break;
                default:
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownInterface",
                        $"Unknown interface: {iface}");
                    break;
            }
            return default;
        }

        private void HandleRoot(MethodContext context, string member)
        {
            switch (member)
            {
                case "Raise":
                    _s.RaiseWindow();
                    ReplyEmpty(context);
                    break;
                case "Quit":
                    ReplyEmpty(context);
                    _s.QuitApp();
                    break;
                default:
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod",
                        $"Unknown method: {member}");
                    break;
            }
        }

        private void HandlePlayerMethod(MethodContext context, string member)
        {
            switch (member)
            {
                case "Next":
                case "Previous":
                case "Pause":
                case "PlayPause":
                case "Stop":
                case "Play":
                    _s.ExecutePlayerCommand(member);
                    ReplyEmpty(context);
                    break;
                case "Seek":
                {
                    var reader = context.Request.GetBodyReader();
                    _s.SeekBy(reader.ReadInt64());
                    ReplyEmpty(context);
                    break;
                }
                case "SetPosition":
                {
                    var reader = context.Request.GetBodyReader();
                    reader.ReadObjectPath(); // trackid — single-track player, ignored
                    _s.SeekTo(reader.ReadInt64());
                    ReplyEmpty(context);
                    break;
                }
                case "OpenUri":
                    ReplyEmpty(context); // not supported; reply so callers don't hang
                    break;
                default:
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod",
                        $"Unknown method: {member}");
                    break;
            }
        }

        private void HandleProperties(MethodContext context, string member)
        {
            switch (member)
            {
                case "Get":
                {
                    var reader = context.Request.GetBodyReader();
                    var iface = reader.ReadString();
                    var prop = reader.ReadString();
                    var writer = context.CreateReplyWriter("v");
                    if (WriteProperty(ref writer, iface, prop))
                    {
                        context.Reply(writer.CreateMessage());
                    }
                    else
                    {
                        writer.Dispose();
                        context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty",
                            $"Unknown property: {iface}.{prop}");
                    }
                    break;
                }
                case "GetAll":
                {
                    var reader = context.Request.GetBodyReader();
                    var iface = reader.ReadString();
                    var writer = context.CreateReplyWriter("a{sv}");
                    var dict = writer.WriteDictionaryStart();
                    foreach (var prop in iface == IfaceRoot ? RootProps : PlayerProps)
                    {
                        writer.WriteDictionaryEntryStart();
                        writer.WriteString(prop);
                        WriteProperty(ref writer, iface, prop);
                    }
                    writer.WriteDictionaryEnd(dict);
                    context.Reply(writer.CreateMessage());
                    break;
                }
                case "Set":
                {
                    var reader = context.Request.GetBodyReader();
                    var iface = reader.ReadString();
                    var prop = reader.ReadString();
                    if (iface == IfacePlayer)
                        ApplyPropertySet(prop, reader.ReadVariantValue());
                    ReplyEmpty(context); // unknown/read-only props are accepted and ignored
                    break;
                }
                default:
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod",
                        $"Unknown method: {member}");
                    break;
            }
        }

        private void ApplyPropertySet(string prop, VariantValue value)
        {
            try
            {
                switch (prop)
                {
                    case "Volume":
                    {
                        var volume = (int)Math.Round(Math.Clamp(value.GetDouble(), 0.0, 1.0) * 100);
                        _s.OnUiThread(() => _s._player.Volume = volume);
                        break;
                    }
                    case "Shuffle":
                    {
                        var shuffle = value.GetBool();
                        _s.OnUiThread(() => _s._player.IsShuffleEnabled = shuffle);
                        break;
                    }
                    case "LoopStatus":
                    {
                        var mode = value.GetString() switch
                        {
                            "Track" => RepeatMode.One,
                            "Playlist" => RepeatMode.All,
                            _ => RepeatMode.Off,
                        };
                        _s.OnUiThread(() => _s._player.RepeatMode = mode);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error(DebugLogger.Category.Playback, "Mpris.Set", ex.Message);
            }
        }

        private static readonly string[] RootProps =
        {
            "CanQuit", "CanRaise", "HasTrackList", "Identity", "DesktopEntry",
            "SupportedUriSchemes", "SupportedMimeTypes",
        };

        private static readonly string[] PlayerProps =
        {
            "PlaybackStatus", "LoopStatus", "Rate", "Shuffle", "Metadata", "Volume",
            "Position", "MinimumRate", "MaximumRate", "CanGoNext", "CanGoPrevious",
            "CanPlay", "CanPause", "CanSeek", "CanControl",
        };

        // MessageWriter is a ref struct with value-copy semantics: passed by value,
        // the property data lands in a copy and the finalized reply goes out with an
        // empty body — the bus daemon drops the connection for the malformed message
        // (the "MPRIS name silently vanishes on first widget query" bug). Keep `ref`.
        private bool WriteProperty(ref MessageWriter writer, string iface, string prop)
        {
            if (iface == IfaceRoot)
            {
                switch (prop)
                {
                    case "CanQuit": writer.WriteVariantBool(true); return true;
                    case "CanRaise": writer.WriteVariantBool(true); return true;
                    case "HasTrackList": writer.WriteVariantBool(false); return true;
                    case "Identity": writer.WriteVariantString("Noctis"); return true;
                    case "DesktopEntry": writer.WriteVariantString("noctis"); return true;
                    case "SupportedUriSchemes":
                        writer.WriteVariant(VariantValue.Array(new[] { "file" }));
                        return true;
                    case "SupportedMimeTypes":
                        writer.WriteVariant(VariantValue.Array(System.Array.Empty<string>()));
                        return true;
                }
                return false;
            }

            switch (prop)
            {
                case "PlaybackStatus":
                    lock (_s._stateLock) writer.WriteVariantString(_s._status);
                    return true;
                case "LoopStatus":
                    writer.WriteVariantString(_s._player.RepeatMode switch
                    {
                        RepeatMode.One => "Track",
                        RepeatMode.All => "Playlist",
                        _ => "None",
                    });
                    return true;
                case "Rate":
                case "MinimumRate":
                case "MaximumRate":
                    writer.WriteVariantDouble(1.0);
                    return true;
                case "Shuffle":
                    writer.WriteVariantBool(_s._player.IsShuffleEnabled);
                    return true;
                case "Metadata":
                    lock (_s._stateLock) writer.WriteVariant(_s.BuildMetadataVariant());
                    return true;
                case "Volume":
                    writer.WriteVariantDouble(Math.Clamp(_s._player.Volume, 0, 100) / 100.0);
                    return true;
                case "Position":
                    writer.WriteVariantInt64(_s._player.Position.Ticks / 10);
                    return true;
                case "CanGoNext":
                case "CanGoPrevious":
                case "CanPlay":
                case "CanPause":
                case "CanSeek":
                case "CanControl":
                    writer.WriteVariantBool(true);
                    return true;
            }
            return false;
        }

        private static void ReplyEmpty(MethodContext context)
        {
            if (context.NoReplyExpected) return;
            using var writer = context.CreateReplyWriter(null!);
            context.Reply(writer.CreateMessage());
        }

        private static readonly ReadOnlyMemory<byte> RootXml =
            """
            <interface name="org.mpris.MediaPlayer2">
              <method name="Raise"/>
              <method name="Quit"/>
              <property name="CanQuit" type="b" access="read"/>
              <property name="CanRaise" type="b" access="read"/>
              <property name="HasTrackList" type="b" access="read"/>
              <property name="Identity" type="s" access="read"/>
              <property name="DesktopEntry" type="s" access="read"/>
              <property name="SupportedUriSchemes" type="as" access="read"/>
              <property name="SupportedMimeTypes" type="as" access="read"/>
            </interface>
            """u8.ToArray();

        private static readonly ReadOnlyMemory<byte> PlayerXml =
            """
            <interface name="org.mpris.MediaPlayer2.Player">
              <method name="Next"/>
              <method name="Previous"/>
              <method name="Pause"/>
              <method name="PlayPause"/>
              <method name="Stop"/>
              <method name="Play"/>
              <method name="Seek">
                <arg direction="in" type="x" name="Offset"/>
              </method>
              <method name="SetPosition">
                <arg direction="in" type="o" name="TrackId"/>
                <arg direction="in" type="x" name="Position"/>
              </method>
              <method name="OpenUri">
                <arg direction="in" type="s" name="Uri"/>
              </method>
              <signal name="Seeked">
                <arg type="x" name="Position"/>
              </signal>
              <property name="PlaybackStatus" type="s" access="read"/>
              <property name="LoopStatus" type="s" access="readwrite"/>
              <property name="Rate" type="d" access="readwrite"/>
              <property name="Shuffle" type="b" access="readwrite"/>
              <property name="Metadata" type="a{sv}" access="read"/>
              <property name="Volume" type="d" access="readwrite"/>
              <property name="Position" type="x" access="read"/>
              <property name="MinimumRate" type="d" access="read"/>
              <property name="MaximumRate" type="d" access="read"/>
              <property name="CanGoNext" type="b" access="read"/>
              <property name="CanGoPrevious" type="b" access="read"/>
              <property name="CanPlay" type="b" access="read"/>
              <property name="CanPause" type="b" access="read"/>
              <property name="CanSeek" type="b" access="read"/>
              <property name="CanControl" type="b" access="read"/>
            </interface>
            """u8.ToArray();
    }
}
