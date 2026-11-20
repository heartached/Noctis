using System.Runtime.InteropServices;

namespace Noctis.Services;

/// <summary>
/// Sets a LibVLC 3 object variable on one media player, through libvlccore (the libvlc API has
/// no call for it). A <c>libvlc_media_player_t</c> is a VLC object and the parent of its audio
/// output, so a variable created on it is what that player's audio filters inherit, ahead of
/// the instance's command-line value. Used for "audio-resampler": the splice engine's players
/// get VLC's zero-delay "ugly" resampler (a pass-through when the rates match) while every other
/// player keeps the instance's speex.
/// </summary>
internal static class VlcObjectVars
{
    private const int VarString = 0x0040; // VLC_VAR_STRING

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VarCreateFn(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int type);

    // vlc_value_t is an 8-byte union passed by value: a long carries psz_string on every ABI
    // LibVLC ships for (x64/arm64 in a register, x86 as 8 bytes on the stack).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VarSetCheckedFn(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int type, long value);

    private static readonly Lazy<(VarCreateFn Create, VarSetCheckedFn Set)?> Api = new(Load);

    /// <summary>True when libvlccore's variable calls could be bound.</summary>
    public static bool Available => Api.Value != null;

    private static (VarCreateFn, VarSetCheckedFn)? Load()
    {
        string[] names = OperatingSystem.IsWindows()
            ? ["libvlccore.dll", "libvlccore"]
            : OperatingSystem.IsMacOS()
                ? ["libvlccore.dylib", "libvlccore.9.dylib"]
                : ["libvlccore.so.9", "libvlccore.so"];
        foreach (var name in names)
        {
            try
            {
                // Already loaded with libvlc: the loader hands back that module.
                if (!NativeLibrary.TryLoad(name, out var lib)) continue;
                if (NativeLibrary.TryGetExport(lib, "var_Create", out var create) &&
                    NativeLibrary.TryGetExport(lib, "var_SetChecked", out var set))
                    return (Marshal.GetDelegateForFunctionPointer<VarCreateFn>(create),
                        Marshal.GetDelegateForFunctionPointer<VarSetCheckedFn>(set));
            }
            catch { /* next name */ }
        }
        return null;
    }

    /// <summary>
    /// Creates (if needed) and sets a string variable on the VLC object behind
    /// <paramref name="vlcObject"/> (a media player's NativeReference). False when libvlccore
    /// could not be bound or VLC refused.
    /// </summary>
    public static bool TrySetString(IntPtr vlcObject, string name, string value)
    {
        if (vlcObject == IntPtr.Zero || Api.Value is not { } api) return false;
        var str = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            // var_Create on an existing variable only adds a reference: harmless when repeated.
            if (api.Create(vlcObject, name, VarString) != 0) return false;
            // var_SetChecked copies the string.
            return api.Set(vlcObject, name, VarString, str.ToInt64()) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeCoTaskMem(str);
        }
    }
}
