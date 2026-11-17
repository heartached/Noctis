using System.Globalization;
using System.Text.Json;

namespace Noctis.Mobile.Services.Account;

/// <summary>A song on the computer's player: the playing one, or one of its Up Next.</summary>
public sealed record ComputerTrack(Guid Id, Guid? AlbumId, string Title, string Artist, string Album, long DurationMs);

public enum ComputerRepeat { Off, All, One }

/// <summary>
/// What the computer's own player is doing (getNoctisRemote). <see cref="UpNext"/> is null when
/// the call did not ask for it; <see cref="UpNextTotal"/> then is 0.
/// </summary>
public sealed record ComputerPlayback(
    ComputerTrack? Track,
    bool Playing,
    long PositionMs,
    long DurationMs,
    int Volume,
    bool Shuffle,
    ComputerRepeat Repeat,
    IReadOnlyList<ComputerTrack>? UpNext,
    int UpNextTotal);

/// <summary>One press on the remote, as noctisRemoteControl's form takes it.</summary>
public sealed record ComputerCommand(string Action, string? Name = null, string? Value = null, string? Id = null)
{
    public static ComputerCommand Toggle { get; } = new("toggle");
    public static ComputerCommand Next { get; } = new("next");
    public static ComputerCommand Previous { get; } = new("previous");
    public static ComputerCommand ToggleShuffle { get; } = new("shuffle");
    public static ComputerCommand CycleRepeat { get; } = new("repeat");

    public static ComputerCommand Seek(long positionMs) =>
        new("seek", "positionMs", Math.Max(0, positionMs).ToString(CultureInfo.InvariantCulture));

    public static ComputerCommand Volume(int volume) =>
        new("volume", "volume", Math.Clamp(volume, 0, 100).ToString(CultureInfo.InvariantCulture));

    /// <summary>Plays Up Next's row <paramref name="index"/>, if it still holds <paramref name="trackId"/>.</summary>
    public static ComputerCommand Jump(int index, Guid trackId) =>
        new("jump", "index", Math.Max(0, index).ToString(CultureInfo.InvariantCulture), NoctisRemoteIds.ToServerTrackId(trackId));
}

/// <summary>The computer's player as the phone's remote drives it (the desktop's NoctisServer.Remote.cs).</summary>
internal sealed partial class NoctisServerClient
{
    public const string RemoteStateMethod = "getNoctisRemote";
    public const string RemoteControlMethod = "noctisRemoteControl";

    /// <summary>Most Up Next songs the remote asks for (the desktop's own cap is 100).</summary>
    public const int RemoteQueueLimit = 50;

    /// <summary>getNoctisRemote: the computer's player now; with <paramref name="withQueue"/>, its
    /// Up Next too (the first <see cref="RemoteQueueLimit"/>).</summary>
    public async Task<ComputerPlayback> GetComputerPlaybackAsync(bool withQueue, CancellationToken ct)
    {
        var query = withQueue
            ? new KeyValuePair<string, string>[] { new("queue", RemoteQueueLimit.ToString(CultureInfo.InvariantCulture)) }
            : Array.Empty<KeyValuePair<string, string>>();
        var r = await GetJsonAsync(RemoteStateMethod, query, ct).ConfigureAwait(false);
        return ParseComputerPlayback(r);
    }

    /// <summary>noctisRemoteControl (form POST): runs <paramref name="command"/> on the computer and
    /// returns its player after it.</summary>
    public async Task<ComputerPlayback> ControlComputerAsync(ComputerCommand command, bool withQueue, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>> { new("action", command.Action) };
        if (command.Name is not null && command.Value is not null) form.Add(new(command.Name, command.Value));
        if (command.Id is not null) form.Add(new("id", command.Id));
        if (withQueue) form.Add(new("queue", RemoteQueueLimit.ToString(CultureInfo.InvariantCulture)));
        var r = await PostFormAsync(RemoteControlMethod, form, signingIn: false, withKey: true, ct).ConfigureAwait(false);
        return ParseComputerPlayback(r);
    }

    internal static ComputerPlayback ParseComputerPlayback(JsonElement r)
    {
        if (!r.TryGetProperty("noctisRemote", out var s) || s.ValueKind != JsonValueKind.Object)
            throw new NoctisServerException(NoctisErrorKind.Server, "The computer sent an unreadable player state.");
        List<ComputerTrack>? upNext = null;
        var total = 0;
        if (s.TryGetProperty("queue", out var q) && q.ValueKind == JsonValueKind.Object)
        {
            upNext = new List<ComputerTrack>();
            if (q.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array)
                foreach (var e in entries.EnumerateArray())
                {
                    if (upNext.Count >= RemoteQueueLimit * 2) break;   // a misbehaving computer's list stays bounded
                    if (ParseComputerTrack(e) is { } t) upNext.Add(t);
                }
            total = Math.Max(upNext.Count, Math.Clamp(Int(q, "total"), 0, 1_000_000));
        }
        return new ComputerPlayback(
            s.TryGetProperty("track", out var tr) ? ParseComputerTrack(tr) : null,
            Bool(s, "playing"),
            Math.Max(0, Long(s, "positionMs")),
            Math.Max(0, Long(s, "durationMs")),
            Math.Clamp(Int(s, "volume"), 0, 100),
            Bool(s, "shuffle"),
            Str(s, "repeat") switch { "all" => ComputerRepeat.All, "one" => ComputerRepeat.One, _ => ComputerRepeat.Off },
            upNext,
            total);
    }

    private static ComputerTrack? ParseComputerTrack(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!NoctisRemoteIds.TryParseServerId(Str(e, "id"), "tr-", out var id)) return null;
        Guid? albumId = NoctisRemoteIds.TryParseServerId(Str(e, "albumId"), "al-", out var al) ? al : null;
        return new ComputerTrack(id, albumId,
            Cap(Str(e, "title"), MaxStringLength) ?? string.Empty,
            Cap(Str(e, "artist"), MaxStringLength) ?? string.Empty,
            Cap(Str(e, "album"), MaxStringLength) ?? string.Empty,
            Math.Clamp(Long(e, "durationMs"), 0, 24L * 60 * 60 * 1000));
    }
}
