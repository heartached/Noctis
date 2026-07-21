using System;
using System.Runtime.InteropServices;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Regression tests for the MMDeviceEnumerator CLSID collision that crashed the
/// silent keep-alive (<c>InvalidCastException</c>) on every activation, left the
/// audio engine cold, and reintroduced the first-play "playback too late →
/// flushing buffers" stutter (Discord report, v1.1.16 diagnostic log).
///
/// The enumerator COM object is creatable on any Windows machine without an audio
/// endpoint (only GetDefaultAudioEndpoint needs a device), so these assertions are
/// deterministic on headless CI Windows agents. They no-op on other platforms.
/// </summary>
public class CoreAudioComInteropTests
{
    [Fact]
    public void RepeatedActivations_DoNotCollide()
    {
        if (!OperatingSystem.IsWindows())
            return; // Core Audio COM is Windows-only.

        // Activating the same CLSID multiple times in-process must not throw the
        // InvalidCastException the duplicate [ComImport] coclasses produced.
        var a = CoreAudioComInterop.CreateMMDeviceEnumerator();
        var b = CoreAudioComInterop.CreateMMDeviceEnumerator();

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(Marshal.IsComObject(a));
        Assert.True(Marshal.IsComObject(b));

        Marshal.ReleaseComObject(a);
        Marshal.ReleaseComObject(b);
    }

    [Fact]
    public void ActivationAfterSessionVolume_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows())
            return; // Core Audio COM is Windows-only.

        // WindowsSessionVolume activates the enumerator at construction (the first
        // consumer). The keep-alive then activates it again — the exact ordering
        // that previously threw because the two declared distinct coclasses for
        // one CLSID. Both must now succeed.
        var sessionVolume = WindowsSessionVolume.TryCreate();
        try
        {
            var keepAlivePath = CoreAudioComInterop.CreateMMDeviceEnumerator();
            Assert.NotNull(keepAlivePath);
            Assert.True(Marshal.IsComObject(keepAlivePath));
            Marshal.ReleaseComObject(keepAlivePath);
        }
        finally
        {
            sessionVolume?.Dispose();
        }
    }

    [Fact]
    public void NAudioEnumerator_StillCastsAfterInterop()
    {
        if (!OperatingSystem.IsWindows())
            return; // Core Audio COM is Windows-only.

        // Regression test for the dead-silence exclusive/WASAPI output: NAudio's
        // WasapiOut activates its OWN [ComImport] coclass for this CLSID and casts
        // it internally. If our GetTypeFromCLSID activation bound the CLSID to
        // System.__ComObject first, NAudio's cast threw, both its sinks failed to
        // initialise, AudioSetup returned -1, and LibVLC emitted no audio at all.
        // CoreAudioComInterop must warm NAudio's coclass first so it keeps working
        // alongside our own activations.
        CoreAudioComInterop.EnsureInitialized();

        // Our (collision-proof) path works.
        var ours = CoreAudioComInterop.CreateMMDeviceEnumerator();
        Assert.True(Marshal.IsComObject(ours));
        Marshal.ReleaseComObject(ours);

        // And NAudio's own wrapper — which internally does
        // (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject() — must NOT throw
        // InvalidCastException, the failure captured in noctis_wasapi.log.
        using var naudio = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        Assert.NotNull(naudio);
    }

    [Fact]
    public void NAudioActivation_WhileInteropEnumeratorHeld_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows())
            return; // Core Audio COM is Windows-only.

        // The enumerator CLSID is a per-process COM singleton: every activation
        // returns the same underlying object, so one shared RCW wraps it and that
        // RCW's managed type is set by whichever wrapper is alive. In the app,
        // WindowsSessionVolume and WasapiSilenceKeepAlive HOLD their interop-created
        // wrap for the whole session — NAudio activation must still succeed while
        // such a wrap is alive. This is the exact in-app sequence behind the silent
        // Exclusive Mode report (noctis_wasapi.log: every TryCreateExclusive AND
        // shared TryCreate threw InvalidCastException, AudioSetup returned -1,
        // LibVLC looped "failed to create audio output").
        CoreAudioComInterop.EnsureInitialized();
        var held = CoreAudioComInterop.CreateMMDeviceEnumerator();
        try
        {
            using var naudio = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            Assert.NotNull(naudio);
        }
        finally
        {
            Marshal.ReleaseComObject(held);
        }
    }
}
