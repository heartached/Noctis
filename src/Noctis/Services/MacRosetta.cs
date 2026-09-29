// src/Noctis/Services/MacRosetta.cs
using System.Runtime.InteropServices;

namespace Noctis.Services;

/// <summary>
/// Detects an Intel (osx-x64) build running under Rosetta 2 on an Apple Silicon Mac,
/// using Apple's documented check: sysctlbyname("sysctl.proc_translated") is 1 for a
/// translated process, 0 for a native one, and fails with ENOENT where Rosetta does
/// not exist (Intel Macs, older macOS).
/// </summary>
internal static class MacRosetta
{
    private const int ENOENT = 2;

    private static bool? _translated;
    private static bool _probed;

    /// <summary>True when this process is translated by Rosetta, false when it runs
    /// natively (always false off macOS or in an arm64 build), null when the check
    /// itself failed. Computed once per process.</summary>
    public static bool? IsTranslated
    {
        get
        {
            if (!_probed)
            {
                _translated = Probe();
                _probed = true;
            }
            return _translated;
        }
    }

    private static bool? Probe()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return false;

        try
        {
            int value = 0;
            nuint size = sizeof(int);
            if (sysctlbyname("sysctl.proc_translated", ref value, ref size, IntPtr.Zero, 0) == 0)
                return value == 1;
            return Marshal.GetLastPInvokeError() == ENOENT ? false : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int sysctlbyname(string name, ref int oldp, ref nuint oldlenp, IntPtr newp, nuint newlen);
}
