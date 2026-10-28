using System.Text.Json;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Where "Pick Up on Computer" lands on the desktop: the newest queue a phone sent, kept in
/// phone_handoff.json so it is still there when the desktop opens later, and the moment the user
/// answered the offer. The server writes it (<see cref="Save"/>); the window reads it
/// (<see cref="Pending"/>) when it opens or comes to the front, and when <see cref="Received"/> fires.
/// </summary>
public sealed class PhoneHandoffInbox
{
    private readonly object _lock = new();
    private readonly string _path;
    private PhoneHandoff? _latest;
    private DateTime _handledUtc;
    private DateTime _desktopPlaybackUtc;

    public PhoneHandoffInbox(string? directory = null)
    {
        _path = Path.Combine(directory ?? AppPaths.DataRoot, "phone_handoff.json");
        Load();
    }

    /// <summary>The newest handoff (raised on the server's thread).</summary>
    public event EventHandler<PhoneHandoff>? Received;

    public PhoneHandoff? Latest { get { lock (_lock) return _latest; } }

    /// <summary>Keeps <paramref name="handoff"/> if it is newer than the one held (two phones: the
    /// later one wins), and tells the listeners. False when an older one was ignored.</summary>
    public bool Save(PhoneHandoff handoff)
    {
        lock (_lock)
        {
            if (_latest != null && handoff.SavedUtc < _latest.SavedUtc) return false;
            _latest = handoff;
            Persist();
        }
        Received?.Invoke(this, handoff);
        return true;
    }

    /// <summary>
    /// The handoff to offer, or null: only one newer than <paramref name="desktopLastPlaybackUtc"/>
    /// (when playback here last happened: a phone that played before the desktop did has nothing
    /// to add), and not one the user already answered.
    /// </summary>
    public PhoneHandoff? Pending(DateTime desktopLastPlaybackUtc)
    {
        lock (_lock)
        {
            if (_latest is not { TrackIds.Count: > 0 } h) return null;
            return h.SavedUtc > desktopLastPlaybackUtc && h.SavedUtc > _handledUtc ? h : null;
        }
    }

    /// <summary>When playback here last started, paused or stopped (the desktop window notes it), kept
    /// across restarts: a phone state older than this has nothing to add.</summary>
    public DateTime DesktopPlaybackUtc { get { lock (_lock) return _desktopPlaybackUtc; } }

    public void NoteDesktopPlayback(DateTime utc)
    {
        lock (_lock)
        {
            if (utc <= _desktopPlaybackUtc) return;
            _desktopPlaybackUtc = utc;
            Persist();
        }
    }

    /// <summary>The user took the offer or dismissed it: it is not offered again.</summary>
    public void MarkHandled(PhoneHandoff handoff)
    {
        lock (_lock)
        {
            if (handoff.SavedUtc > _handledUtc) _handledUtc = handoff.SavedUtc;
            Persist();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            using var stream = File.OpenRead(_path);
            var file = JsonSerializer.Deserialize<StoreFile>(stream);
            _latest = file?.Handoff;
            _handledUtc = file?.HandledUtc ?? default;
            _desktopPlaybackUtc = file?.DesktopPlaybackUtc ?? default;
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.State, "PhoneHandoff.Load", ex.Message);
        }
    }

    // Under _lock. Small (at most MaxTracks ids), so written in place of a debounce.
    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, new StoreFile { Handoff = _latest, HandledUtc = _handledUtc, DesktopPlaybackUtc = _desktopPlaybackUtc });
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.State, "PhoneHandoff.Save", ex.Message);
        }
    }

    private sealed class StoreFile
    {
        public PhoneHandoff? Handoff { get; set; }
        public DateTime HandledUtc { get; set; }
        public DateTime DesktopPlaybackUtc { get; set; }
    }
}
