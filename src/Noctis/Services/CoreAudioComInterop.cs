using System.Runtime.Versioning;
using NAudio.CoreAudioApi;

namespace Noctis.Services;

/// <summary>
/// Shared, collision-proof activation of the Windows Core Audio
/// <c>MMDeviceEnumerator</c> COM object.
///
/// Why this exists (root-cause fix for the first-play stutter AND the silent
/// WASAPI/exclusive output):
/// three places need the enumerator for CLSID <c>BCDE0395-…</c> —
/// <see cref="WindowsSessionVolume"/> (per audio-session volume),
/// <see cref="WasapiSilenceKeepAlive"/> (the silent keep-warm stream), and
/// NAudio (used by <see cref="WasapiGainOutput"/> for exclusive / per-sample
/// output). The CLR binds a CLSID to a SINGLE managed coclass type per process:
/// the first <c>[ComImport]</c> coclass activated for a CLSID wins, and every
/// later <c>new</c> of a DIFFERENT coclass type for that same CLSID returns the
/// winner's RCW and throws
/// <c>InvalidCastException: Unable to cast object of type 'A' to type 'B'</c>.
///
/// Two distinct symptoms came from this one collision:
///   • Our own two services each declared a private coclass → whichever lost
///     threw on activation. The keep-alive lost, never started, and left the
///     audio engine cold → the "playback too late → flushing buffers" first-play
///     stutter it was meant to prevent.
///   • NAudio activates its OWN internal coclass. Whenever anything else bound
///     the CLSID first, NAudio's cast threw → its exclusive and shared sinks both
///     failed to initialise → <c>AudioSetup</c> returned -1 → LibVLC skipped audio
///     entirely → dead silence with the clock still running (lyrics advance, no
///     sound).
///
/// Fix, two parts:
///   1. Our services no longer declare coclasses; they activate via
///      <see cref="System.Type.GetTypeFromCLSID(System.Guid)"/>, which yields the
///      runtime's generic <c>System.__ComObject</c> and QueryInterfaces to the
///      caller's own <c>IMMDeviceEnumerator</c> — robust no matter which coclass
///      is registered.
///   2. NAudio CANNOT be changed and REQUIRES its own coclass cast to succeed, so
///      its coclass must be the one that wins. <see cref="EnsureInitialized"/>
///      activates NAudio's enumerator once, before anything else binds the CLSID,
///      and KEEPS IT ALIVE — the binding lives only as long as a wrapper does, so
///      the winner must stay referenced for the process lifetime. Our
///      GetTypeFromCLSID path keeps working regardless, so all three consumers
///      coexist.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CoreAudioComInterop
{
    // CLSID_MMDeviceEnumerator.
    private static readonly Guid MMDeviceEnumeratorClsid =
        new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private static readonly object _initGate = new();
    private static bool _initialized;

    // Pins NAudio's coclass as the singleton's live wrapper for the process
    // lifetime. The enumerator CLSID is a per-process COM singleton: every
    // activation returns the SAME underlying object, so one shared RCW wraps it
    // and that RCW's managed type is set by whichever wrapper is alive. The type
    // binding does NOT survive disposal — if this instance were disposed, the next
    // GetTypeFromCLSID activation (held for the whole session by
    // WindowsSessionVolume / WasapiSilenceKeepAlive) would re-wrap the singleton
    // as System.__ComObject and every later NAudio activation would throw
    // InvalidCastException again (the silent Exclusive Mode failure).
    private static MMDeviceEnumerator? _naudioPin;

    /// <summary>
    /// Activate NAudio's <c>MMDeviceEnumerator</c> once, before any other code
    /// binds this CLSID, and keep it alive so NAudio's internal <c>[ComImport]</c>
    /// coclass stays the singleton's wrapper and NAudio's own casts keep working.
    /// Idempotent, thread-safe, and best-effort — a failure here leaves the next
    /// call to retry. Call as early as possible in audio startup (e.g. the player
    /// ctor).
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initGate)
        {
            if (_initialized) return;
            try
            {
                // Constructing NAudio's wrapper activates NAudio's coclass; this is
                // a pure COM-object creation (no audio endpoint required), so it
                // works even with no playback device present.
                _naudioPin = new MMDeviceEnumerator();
                _initialized = true;
            }
            catch
            {
                // Transient activation failure: leave _initialized false so the
                // next audio-path call retries before anything else can bind the
                // CLSID to the generic __ComObject wrapper.
            }
        }
    }

    /// <summary>
    /// Create a fresh <c>MMDeviceEnumerator</c> COM object. The caller casts the
    /// returned object to its own <c>IMMDeviceEnumerator</c> interface, which
    /// performs a QueryInterface by IID. Throws if the CLSID is not registered or
    /// activation fails (caught by the callers' existing try/catch fallbacks).
    /// </summary>
    public static object CreateMMDeviceEnumerator()
    {
        // Make sure NAudio's coclass has won the CLSID binding before we register
        // the generic __ComObject for it, so NAudio's own activations still work.
        EnsureInitialized();

        var type = Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid)
            ?? throw new InvalidOperationException("CLSID_MMDeviceEnumerator is not registered.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Failed to activate MMDeviceEnumerator.");
    }
}
