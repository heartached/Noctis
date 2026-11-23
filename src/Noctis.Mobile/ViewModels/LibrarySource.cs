using Noctis.Mobile.Services.Account;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Which songs the Library's lists show (Songs, Albums, Artists, Recently Added). The names are
/// the persisted values (AppSettings.PhoneLibrarySource), so they must not be renamed.
/// </summary>
public enum LibrarySource { All, Phone, Computer }

public static class LibrarySources
{
    /// <summary>
    /// Whether <paramref name="track"/> is in <paramref name="source"/>:
    /// <list type="bullet">
    /// <item>Phone: every song that is not one of the computer's rows, i.e. the phone's own files,
    /// including a file the sync paired with a computer song (it plays from the phone), and a download
    /// the computer no longer has (<see cref="INoctisAccountService.IsNotOnComputer"/>).</item>
    /// <item>Computer: every song the computer has: its rows, streamed or downloaded, and the phone's
    /// files the sync paired with one of its songs (the pair is one row, so it shows in both).</item>
    /// </list>
    /// Without an account service there is only the phone.
    /// </summary>
    public static bool Includes(INoctisAccountService? account, Track track, LibrarySource source) => source switch
    {
        LibrarySource.Phone => account is null || !account.IsRemote(track) || account.IsNotOnComputer(track),
        LibrarySource.Computer => account?.DesktopIdOf(track) is not null && !account.IsNotOnComputer(track),
        _ => true,
    };

    /// <summary>The persisted value read back; empty or unknown is <see cref="LibrarySource.All"/>.</summary>
    public static LibrarySource Parse(string? key) =>
        Enum.TryParse<LibrarySource>(key, ignoreCase: true, out var source) && Enum.IsDefined(source)
            && !int.TryParse(key, out _) ? source : LibrarySource.All;

    /// <summary>What is saved: empty for All, so a settings file without a choice means All.</summary>
    public static string Key(LibrarySource source) => source == LibrarySource.All ? "" : source.ToString();
}
