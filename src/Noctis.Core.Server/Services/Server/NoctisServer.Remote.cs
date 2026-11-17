using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace Noctis.Services.Server;

/// <summary>
/// The phone's remote for the desktop's own player (Discord #mobile-beta 11-17: "remote control
/// the songs playing on pc on the phone like the website thing").
/// <para><c>getNoctisRemote[?queue=N]</c>: what plays now, and with <c>queue</c> (1-<see cref="RemoteMaxQueue"/>)
/// the first N songs of Up Next. Answer: <c>noctisRemote: { playing, positionMs, durationMs, volume,
/// shuffle, repeat ("off"|"all"|"one"), track?: song, queue?: { total, entry: [song] } }</c>, a song
/// being <c>{ id: "tr-…", albumId: "al-…", title, artist, album, durationMs }</c>.</para>
/// <para><c>noctisRemoteControl</c> (form POST only): <c>action</c> = toggle | play | pause | next |
/// previous | seek (<c>positionMs</c>) | volume (<c>volume</c> 0-100) | shuffle (<c>enabled</c>
/// true/false, else toggles) | repeat (<c>mode</c>, else cycles) | jump (<c>index</c> into Up Next and
/// the <c>id</c> the phone saw there). Answers the player after it, as getNoctisRemote (same
/// <c>queue</c>). A jump whose row moved on, or a seek with nothing playing, is error 70.</para>
/// <para>Who may: only a signed-in phone (a device key, not a password or an account key) of the
/// computer's main account (the admin, else the first: the account the QR code signs in), and only
/// on a host that has a player (<see cref="Remote"/>). Everyone else gets error 50. The usual
/// per-request auth, the private-network rule and the device sign-in switch all still apply first.</para>
/// </summary>
public sealed partial class NoctisServer
{
    /// <summary>The desktop's player (read on every call, so it may be set after the start); null
    /// (the headless host) answers the remote calls with error 50.</summary>
    public IServerRemote? Remote { get; set; }

    public const string RemoteStateMethod = "getNoctisRemote";
    public const string RemoteControlMethod = "noctisRemoteControl";

    /// <summary>Most Up Next songs one answer lists.</summary>
    public const int RemoteMaxQueue = 100;

    private async Task<JsonObject> RemoteStateAsync(Params p, HttpContext ctx)
    {
        var remote = RequireRemote(ctx);
        var limit = RemoteQueueLimit(p);
        var snapshot = await remote.SnapshotAsync(limit).ConfigureAwait(false);
        return new JsonObject { ["noctisRemote"] = RemoteObject(snapshot, limit > 0) };
    }

    private async Task<JsonObject> RemoteControlAsync(Params p, HttpContext ctx)
    {
        var remote = RequireRemote(ctx);
        // An action changes what plays in the owner's room: never from a GET (a link, a prefetch).
        if (!HttpMethods.IsPost(ctx.Request.Method))
            throw new SubsonicException(SubsonicResponse.ErrGeneric, "Send remote commands as a POST.");
        var command = ParseRemoteCommand(p);
        if (!await remote.ControlAsync(command).ConfigureAwait(false)) throw NotFound();
        var limit = RemoteQueueLimit(p);
        var snapshot = await remote.SnapshotAsync(limit).ConfigureAwait(false);
        return new JsonObject { ["noctisRemote"] = RemoteObject(snapshot, limit > 0) };
    }

    /// <summary>The remote, for a caller allowed to drive it (see the class notes); error 50 otherwise.</summary>
    private IServerRemote RequireRemote(HttpContext ctx)
    {
        var remote = Remote ?? throw new SubsonicException(SubsonicResponse.ErrNotAuthorized, "This computer has no player to control.");
        if (ctx.Items["device"] is not ServerDevice device)
            throw new SubsonicException(SubsonicResponse.ErrNotAuthorized, "Only a signed-in phone can control this computer's player.");
        var users = _users.List();
        var main = users.FirstOrDefault(u => u.IsAdmin) ?? users.FirstOrDefault();
        if (main is null || !string.Equals(main.Name, device.User, StringComparison.OrdinalIgnoreCase))
            throw new SubsonicException(SubsonicResponse.ErrNotAuthorized, "Only the computer's main account can control its player.");
        return remote;
    }

    private static int RemoteQueueLimit(Params p)
        => int.TryParse(p.Get("queue"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 0, RemoteMaxQueue) : 0;

    internal static RemoteCommand ParseRemoteCommand(string? action, Func<string, string?> get)
    {
        static SubsonicException Bad(string message) => new(SubsonicResponse.ErrMissingParameter, message);
        long Number(string name, long min, long max)
            => long.TryParse(get(name), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
                ? v : throw Bad($"Parameter '{name}' must be a number from {min} to {max}");

        switch (action?.ToLowerInvariant())
        {
            case "toggle": return new RemoteCommand(RemoteAction.Toggle);
            case "play": return new RemoteCommand(RemoteAction.Play);
            case "pause": return new RemoteCommand(RemoteAction.Pause);
            case "next": return new RemoteCommand(RemoteAction.Next);
            case "previous": return new RemoteCommand(RemoteAction.Previous);
            case "seek": return new RemoteCommand(RemoteAction.Seek, PositionMs: Number("positionMs", 0, 24L * 60 * 60 * 1000));
            case "volume": return new RemoteCommand(RemoteAction.Volume, Volume: (int)Number("volume", 0, 100));
            case "shuffle":
            {
                var enabled = get("enabled");
                if (enabled is null) return new RemoteCommand(RemoteAction.Shuffle);
                return bool.TryParse(enabled, out var on) ? new RemoteCommand(RemoteAction.Shuffle, Shuffle: on)
                    : throw Bad("Parameter 'enabled' must be true or false");
            }
            case "repeat":
            {
                var mode = get("mode");
                if (mode is null) return new RemoteCommand(RemoteAction.Repeat);
                return mode.ToLowerInvariant() switch
                {
                    "off" => new RemoteCommand(RemoteAction.Repeat, Repeat: RemoteRepeat.Off),
                    "all" => new RemoteCommand(RemoteAction.Repeat, Repeat: RemoteRepeat.All),
                    "one" => new RemoteCommand(RemoteAction.Repeat, Repeat: RemoteRepeat.One),
                    _ => throw Bad("Parameter 'mode' must be off, all or one"),
                };
            }
            case "jump":
            {
                var index = (int)Number("index", 0, int.MaxValue);
                var id = Ids.TrackGuid(get("id")) ?? throw Bad("Parameter 'id' must be the song's id");
                return new RemoteCommand(RemoteAction.Jump, Index: index, TrackId: id);
            }
            case null: throw Missing("action");
            default: throw Bad($"Unknown action '{action}'");
        }
    }

    private static RemoteCommand ParseRemoteCommand(Params p) => ParseRemoteCommand(p.Get("action"), p.Get);

    internal static string RepeatName(RemoteRepeat repeat) => repeat switch
    {
        RemoteRepeat.All => "all",
        RemoteRepeat.One => "one",
        _ => "off",
    };

    /// <param name="withQueue">The caller asked for Up Next: <c>queue</c> is always there then, empty or not.</param>
    private static JsonObject RemoteObject(RemoteSnapshot s, bool withQueue)
    {
        var obj = new JsonObject
        {
            ["playing"] = s.Playing,
            ["positionMs"] = Math.Max(0, s.PositionMs),
            ["durationMs"] = Math.Max(0, s.DurationMs),
            ["volume"] = Math.Clamp(s.Volume, 0, 100),
            ["shuffle"] = s.Shuffle,
            ["repeat"] = RepeatName(s.Repeat),
        };
        if (s.Track is { } track) obj["track"] = RemoteTrackObject(track);
        if (withQueue)
            obj["queue"] = new JsonObject
            {
                ["total"] = Math.Max(s.UpNextTotal, s.UpNext.Count),
                ["entry"] = new JsonArray(s.UpNext.Select(t => (JsonNode)RemoteTrackObject(t)).ToArray()),
            };
        return obj;
    }

    private static JsonObject RemoteTrackObject(RemoteTrack t)
    {
        var obj = new JsonObject
        {
            ["id"] = Ids.TrackId(t.Id),
            ["title"] = t.Title,
            ["artist"] = t.Artist,
            ["album"] = t.Album,
            ["durationMs"] = Math.Max(0, t.DurationMs),
        };
        if (t.AlbumId != Guid.Empty) obj["albumId"] = Ids.AlbumId(t.AlbumId);
        return obj;
    }
}
