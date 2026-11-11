using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Noctis.Services;

/// <summary>
/// Who is reading the data folder, and how this computer protects its secrets (GitHub #230).
/// A data folder on a music drive can be opened from several computers or Windows users;
/// Windows DPAPI only lets the computer and user that protected a secret read it back, so
/// everything kept per computer is filed under <see cref="ComputerId"/>.
/// Tests subclass it to act as two computers sharing one folder.
/// </summary>
internal class SecretProtector
{
    public static SecretProtector Current { get; } = new();

    private static readonly Lazy<string> s_computerId = new(ComputeComputerId);

    /// <summary>Stable id of this computer and user: a hash, so the file never names either.</summary>
    public virtual string ComputerId => s_computerId.Value;

    public virtual string Protect(string value) => PersistenceService.ProtectSecret(value);

    public virtual string Unprotect(string value) => PersistenceService.UnprotectSecret(value);

    private static string ComputeComputerId()
    {
        string machine = "", user = "";
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                machine = key?.GetValue("MachineGuid") as string ?? "";
            }
            catch { /* falls back to the machine name */ }
            try { user = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? ""; }
            catch { /* falls back to the user name */ }
        }
        if (machine.Length == 0) machine = Environment.MachineName;
        if (user.Length == 0) user = Environment.UserName;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("noctis-computer|" + machine + "|" + user));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}

/// <summary>
/// <c>computers.json</c> in the data folder (GitHub #230): one slot per computer and Windows user
/// that opens the folder, under <see cref="SecretProtector.ComputerId"/>. Each slot holds that
/// computer's protected sign-ins (<c>"secrets"</c>) and the settings that only make sense on that
/// computer (<c>"settings"</c>: window places, output device, interface size).
///
/// A file of its own, not a part of settings.json: Noctis 1.6.7–1.6.9 rewrite settings.json
/// without the keys they do not know, but never touch this file, so a computer still running
/// one of them cannot sign the other computer out. A computer only ever changes its own slot;
/// the other slots and anything else in the file are written back as they were.
/// </summary>
internal static class ComputerStore
{
    public const string FileName = "computers.json";
    private const string ComputersKey = "computers";
    public const string SecretsKey = "secrets";
    public const string SettingsKey = "settings";

    private static readonly object s_lock = new();
    private static readonly JsonSerializerOptions s_write = new() { WriteIndented = true };

    /// <summary>A copy of this computer's slot, or null when it has none (or the file is unreadable).</summary>
    public static JsonObject? ReadSlot(string dataDirectory, string computerId)
    {
        lock (s_lock)
            return ReadFile(Path.Combine(dataDirectory, FileName))?[ComputersKey]?[computerId] is JsonObject slot
                ? (JsonObject)slot.DeepClone()
                : null;
    }

    /// <summary>
    /// Changes this computer's slot in place and writes the file back (atomically, and only when
    /// something changed). An emptied slot is removed. A damaged file is left alone.
    /// </summary>
    public static void UpdateSlot(string dataDirectory, string computerId, Action<JsonObject> change)
    {
        var path = Path.Combine(dataDirectory, FileName);
        lock (s_lock)
        {
            JsonObject root;
            if (File.Exists(path))
            {
                if (ReadFile(path) is not { } existing)
                {
                    DebugLog.Write("Persistence", $"{FileName} could not be read; left as it is.");
                    return;
                }
                root = existing;
            }
            else root = new JsonObject();

            var before = root.ToJsonString();
            if (root[ComputersKey] is not JsonObject computers)
                root[ComputersKey] = computers = new JsonObject();
            if (computers[computerId] is not JsonObject slot)
                slot = new JsonObject();
            else
                computers.Remove(computerId);

            change(slot);
            foreach (var key in slot.Where(p => p.Value is JsonObject { Count: 0 }).Select(p => p.Key).ToList())
                slot.Remove(key);
            if (slot.Count > 0) computers[computerId] = slot;

            if (root.ToJsonString() == before) return;
            if (computers.Count == 0 && root.Count == 1 && !File.Exists(path)) return;

            Directory.CreateDirectory(dataDirectory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(s_write));
            PersistenceService.TryRestrictToOwner(temp, isDirectory: false);
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>Plain value of <paramref name="field"/> among this computer's secrets, or "".</summary>
    public static string ReadSecret(JsonObject? slot, string field, SecretProtector protector) =>
        slot?[SecretsKey]?[field] is JsonValue v && v.TryGetValue<string>(out var stored) && stored.Length > 0
            ? protector.Unprotect(stored)
            : string.Empty;

    /// <summary>
    /// Sets (or with an empty <paramref name="plain"/>, removes) one secret in <paramref name="slot"/>.
    /// The stored copy is kept when it still holds the same value: DPAPI output differs on every
    /// call, so re-protecting would rewrite the file on every save. Returns the stored copy.
    /// </summary>
    public static string WriteSecret(JsonObject slot, string field, string plain, SecretProtector protector)
    {
        if (slot[SecretsKey] is not JsonObject secrets)
            slot[SecretsKey] = secrets = new JsonObject();
        if (string.IsNullOrEmpty(plain))
        {
            secrets.Remove(field);
            return string.Empty;
        }
        if (secrets[field] is JsonValue v && v.TryGetValue<string>(out var stored) && stored.Length > 0
            && protector.Unprotect(stored) == plain)
            return stored;
        var protectedValue = protector.Protect(plain);
        secrets[field] = protectedValue;
        return protectedValue;
    }

    private static JsonObject? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
