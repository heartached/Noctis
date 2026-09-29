using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Noctis.Services.LocalApi;

/// <summary>
/// The Local API's discovery file, <c>&lt;data&gt;/local-api.json</c>: the access token
/// plus the port the API is listening on, so scripts on this PC (Stream Deck, OBS,
/// Rainmeter) can find it without the user copying anything.
///
/// The token lives here and nowhere else: not in settings.json, which users attach to
/// bug reports. It survives restarts so an OBS browser-source URL keeps working, and
/// only changes when the user presses "Regenerate token". The file sits in the per-user
/// data folder (ACL'd to the user on Windows; chmod 600 on Unix).
/// </summary>
public sealed class LocalApiTokenStore
{
    public const string FileName = "local-api.json";

    public LocalApiTokenStore(string dataDirectory) =>
        FilePath = Path.Combine(dataDirectory, FileName);

    public string FilePath { get; }

    /// <summary>A fresh 256-bit token, lowercase hex (URL-safe, no escaping needed).</summary>
    public static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>The stored token, or a new one written to the file when there is none
    /// (first enable, or a missing / unreadable / hand-mangled file).</summary>
    public string LoadOrCreateToken()
    {
        var existing = ReadToken();
        if (existing != null) return existing;
        var token = NewToken();
        Write(token, port: null, running: false);
        return token;
    }

    /// <summary>Replaces the token, keeping the recorded port/running state.</summary>
    public string Regenerate()
    {
        var (port, running) = ReadState();
        var token = NewToken();
        Write(token, port, running);
        return token;
    }

    /// <summary>Records where the API is listening (or that it stopped) next to the token.</summary>
    public void WriteState(string token, int? port, bool running) => Write(token, port, running);

    /// <summary>Stored token when it looks like one of ours (64 hex chars), else null.</summary>
    public string? ReadToken()
    {
        var node = ReadNode();
        var token = node?["token"]?.GetValue<string>();
        return IsWellFormed(token) ? token : null;
    }

    private (int? Port, bool Running) ReadState()
    {
        var node = ReadNode();
        int? port = null;
        var running = false;
        try { port = node?["port"]?.GetValue<int?>(); } catch { }
        try { running = node?["running"]?.GetValue<bool>() ?? false; } catch { }
        return (port, running);
    }

    private JsonNode? ReadNode()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonNode.Parse(File.ReadAllText(FilePath));
        }
        catch
        {
            return null; // corrupt → treated as absent; the next write repairs it
        }
    }

    internal static bool IsWellFormed(string? token) =>
        token is { Length: 64 } && token.All(Uri.IsHexDigit);

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private void Write(string token, int? port, bool running)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var doc = new JsonObject
        {
            ["apiVersion"] = LocalApiDto.ApiVersion,
            ["running"] = running,
            ["port"] = port,
            ["baseUrl"] = port is > 0 ? $"http://127.0.0.1:{port}/api/v1" : null,
            ["token"] = token,
            ["pid"] = running ? Environment.ProcessId : null,
            ["updatedUtc"] = DateTime.UtcNow.ToString("O"),
        };

        // Temp + move: a tool polling the file never reads half of it.
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, doc.ToJsonString(WriteOptions));
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* best effort on filesystems without POSIX modes */ }
        }
        File.Move(tmp, FilePath, overwrite: true);
    }
}
