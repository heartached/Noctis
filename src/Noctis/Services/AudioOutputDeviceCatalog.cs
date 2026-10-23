using System.Runtime.Versioning;
using NAudio.CoreAudioApi;

namespace Noctis.Services;

/// <summary>One selectable render endpoint: the MMDevice id string plus the name shown
/// in Settings. <see cref="IsDefault"/> marks the current Windows default at read time.</summary>
internal sealed record AudioOutputDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Enumeration of Windows render endpoints for the Settings → Output → Output Device
/// picker (NAudio's MMDeviceEnumerator; CLSID activation is safe — NAudio's coclass is
/// pinned by CoreAudioComInterop from player startup). The ids are the same WASAPI
/// endpoint id strings VLC's mmdevice module uses as its device values, so one id feeds
/// every output path: the exclusive sink, the gapless engine, VLC's own output, the
/// keep-warm stream and the session-volume resolver.
///
/// All enumeration is best-effort: any COM failure yields an empty list and the picker
/// keeps whatever it had. <see cref="Build"/> is pure (no COM) so the ordering/dedup
/// rules are unit-testable off Windows.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AudioOutputDeviceCatalog
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Active render endpoints, the Windows default first, the rest by name.
    /// Empty when the platform is unsupported or enumeration fails.</summary>
    public static List<AudioOutputDevice> Enumerate()
    {
        if (!IsSupported) return new List<AudioOutputDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = def.ID;
            }
            catch { /* no default endpoint right now — list without the marker */ }

            var raw = new List<(string Id, string Name)>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    string name;
                    try
                    {
                        // FriendlyName is the endpoint name Windows shows ("Speakers
                        // (Realtek Audio)"); fall back to the device/adapter name.
                        name = string.IsNullOrWhiteSpace(device.FriendlyName)
                            ? device.DeviceFriendlyName
                            : device.FriendlyName;
                    }
                    catch { name = device.ID; }
                    raw.Add((device.ID, string.IsNullOrWhiteSpace(name) ? device.ID : name));
                }
            }
            return Build(raw, defaultId);
        }
        catch
        {
            return new List<AudioOutputDevice>();
        }
    }

    /// <summary>True when <paramref name="deviceId"/> names a currently-active render
    /// endpoint. False for null/empty (that's the default-device sentinel) and on
    /// failure — callers treat "not found" as "fall back to the default device".</summary>
    public static bool Exists(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || !IsSupported) return false;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(deviceId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Friendly name of one endpoint, or null when it cannot be resolved
    /// (missing device, non-Windows). Used for the signal-path description.</summary>
    public static string? NameOf(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || !IsSupported) return null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(deviceId);
            return string.IsNullOrWhiteSpace(device.FriendlyName)
                ? device.DeviceFriendlyName
                : device.FriendlyName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Pure ordering step: default endpoint first, the rest by name
    /// (ordinal, case-insensitive), duplicate display names disambiguated with a
    /// " (2)"/" (3)" suffix (two identical USB DACs otherwise render as one choice).
    /// </summary>
    internal static List<AudioOutputDevice> Build(
        IEnumerable<(string Id, string Name)> devices, string? defaultId)
    {
        var result = new List<AudioOutputDevice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in devices)
        {
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
            var display = name;
            if (names.TryGetValue(display, out var n))
            {
                n++;
                names[display] = n;
                display = $"{name} ({n})";
            }
            else
            {
                names[display] = 1;
            }
            result.Add(new AudioOutputDevice(id, display, id == defaultId));
        }
        result.Sort((a, b) =>
        {
            if (a.IsDefault != b.IsDefault) return a.IsDefault ? -1 : 1;
            var byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.Compare(a.Id, b.Id, StringComparison.Ordinal);
        });
        return result;
    }
}
