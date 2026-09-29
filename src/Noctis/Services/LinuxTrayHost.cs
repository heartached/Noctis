using System;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Noctis.Services;

/// <summary>
/// Linux only: follows whether the session has a tray at all, i.e. whether anything owns
/// org.kde.StatusNotifierWatcher on the session bus.
///
/// Audit P29: Avalonia's Linux TrayIcon is a StatusNotifierItem, and creating one never fails
/// when nobody hosts those — it logs that the watcher is unavailable and waits. So on stock
/// GNOME (no AppIndicator extension) the TrayIcon object exists while nothing is on screen,
/// and start-minimized-at-login, minimize-to-tray and close-to-tray hid Noctis with no window,
/// no taskbar entry and no icon while the music kept playing. The watcher can appear late
/// (panel still starting at login) or come and go (extension toggled), so its owner is
/// followed live, the same way Avalonia's tray registers itself once one shows up.
/// </summary>
public sealed class LinuxTrayHost : IDisposable
{
    private const string WatcherName = "org.kde.StatusNotifierWatcher";
    private const string BusName = "org.freedesktop.DBus";
    private const string BusPath = "/org/freedesktop/DBus";

    private readonly TaskCompletionSource<bool> _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DBusConnection? _connection;
    private IDisposable? _match;
    private volatile bool _isAvailable;
    private volatile bool _ownerChangeSeen;
    private volatile bool _disposed;

    private LinuxTrayHost()
    {
        _ = Task.Run(InitializeAsync);
    }

    /// <summary>True while something owns the StatusNotifierWatcher name.</summary>
    public bool IsAvailable => _isAvailable;

    /// <summary>
    /// Starts following the watcher on Linux; null everywhere else, where a created TrayIcon
    /// is a visible one. Never throws.
    /// </summary>
    public static LinuxTrayHost? TryStart()
    {
        try
        {
            if (!OperatingSystem.IsLinux())
                return null;
            return new LinuxTrayHost();
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.UI, "TrayHost.Start", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Whether hiding the window into the tray leaves the user a way back: the TrayIcon was
    /// created and, on Linux, something hosts it. Pure; internal for tests.
    /// </summary>
    internal static bool IsTrayUsable(bool trayIconCreated, bool isLinux, bool statusNotifierHostPresent)
        => trayIconCreated && (!isLinux || statusNotifierHostPresent);

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for a watcher to be seen — at login the panel
    /// may register it a moment after Noctis starts — and returns <see cref="IsAvailable"/>.
    /// Returns at once when the session bus can't be reached.
    /// </summary>
    public async Task<bool> WaitForHostAsync(TimeSpan timeout)
    {
        await Task.WhenAny(_settled.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return _isAvailable;
    }

    private async Task InitializeAsync()
    {
        try
        {
            var address = DBusAddress.Session;
            if (string.IsNullOrEmpty(address))
            {
                DebugLogger.Warn(DebugLogger.Category.UI, "TrayHost.NoBus",
                    "no session bus address; no tray, hide-to-tray keeps the window on the taskbar");
                _settled.TrySetResult(false);
                return;
            }

            var connection = new DBusConnection(address);
            await connection.ConnectAsync();

            // Subscribe before asking, so an owner arriving in between is not missed.
            var rule = new MatchRule
            {
                Type = MessageType.Signal,
                Sender = BusName,
                Path = BusPath,
                Interface = BusName,
                Member = "NameOwnerChanged",
                Arg0 = WatcherName,
            };
            // NameOwnerChanged(s name, s old_owner, s new_owner): an empty new owner = gone.
            var match = await connection.AddMatchAsync(
                rule,
                static (Message message, object? _) =>
                {
                    var reader = message.GetBodyReader();
                    reader.ReadString();
                    reader.ReadString();
                    return reader.ReadString();
                },
                (Notification<string> signal) =>
                {
                    if (signal.Exception != null) return;
                    _ownerChangeSeen = true;
                    SetAvailable(!string.IsNullOrEmpty(signal.Value), "TrayHost.Changed");
                },
                emitOnCapturedContext: false);

            if (_disposed)
            {
                match.Dispose();
                connection.Dispose();
                return;
            }

            _connection = connection;
            _match = match;

            var hasOwner = await connection.CallMethodAsync(
                CreateNameHasOwnerMessage(connection),
                static (Message msg, object? _) => msg.GetBodyReader().ReadBool(),
                null);

            // A change signalled while the call was in flight is newer than its answer.
            if (!_ownerChangeSeen)
                SetAvailable(hasOwner, hasOwner ? "TrayHost.Present" : "TrayHost.Missing");
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.UI, "TrayHost.Init", ex.Message);
            _settled.TrySetResult(false);
        }
    }

    private void SetAvailable(bool available, string action)
    {
        _isAvailable = available;
        if (available)
        {
            _settled.TrySetResult(true);
            DebugLogger.Info(DebugLogger.Category.UI, action, $"{WatcherName} owned; hide-to-tray enabled");
        }
        else
            DebugLogger.Warn(DebugLogger.Category.UI, action,
                $"no {WatcherName} on the session bus; hide-to-tray keeps the window on the taskbar");
    }

    private static MessageBuffer CreateNameHasOwnerMessage(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: BusName,
            path: BusPath,
            @interface: BusName,
            member: "NameHasOwner",
            signature: "s");
        writer.WriteString(WatcherName);
        return writer.CreateMessage();
    }

    public void Dispose()
    {
        _disposed = true;
        try { _match?.Dispose(); } catch { /* best effort on shutdown */ }
        try { _connection?.Dispose(); } catch { /* best effort on shutdown */ }
        _match = null;
        _connection = null;
    }
}
