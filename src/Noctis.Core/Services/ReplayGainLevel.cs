using Noctis.Models;

namespace Noctis.Services;

/// <summary>Which ReplayGain value the phone's player applies (settings.json's ReplayGainMode).</summary>
public enum ReplayGainMode { Off, Track, Album }

/// <summary>
/// The phone's ReplayGain arithmetic, on the desktop's model (AppSettings.ReplayGainMode and
/// ReplayGainPreampDb; VlcAudioPlayer.ReplayGainScalarFor): the tag the mode picks plus the pre-amp,
/// kept within the desktop's -30..+12 dB window, as an amplitude factor. Two additions the phone
/// needs: Track falls back to the album gain as Album falls back to the track gain, and Prevent
/// Clipping caps a boost at the song's peak headroom. A song with no ReplayGain plays as it is
/// (no guessed gain, no pre-amp), as on the desktop. Kept out of the player so it can be tested.
/// </summary>
public static class ReplayGainLevel
{
    public const double MinPreampDb = -12, MaxPreampDb = 12;
    // The desktop's window (VlcAudioPlayer.ReplayGainScalarFor): a corrupt tag can't blow speakers.
    public const double MinTotalDb = -30, MaxTotalDb = 12;

    /// <summary>A settings.json value: "Off", "Track", "Album"; the desktop's "Auto" (album, else
    /// track) is Album here. Anything else is Off.</summary>
    public static ReplayGainMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "track" => ReplayGainMode.Track,
        "album" or "auto" => ReplayGainMode.Album,
        _ => ReplayGainMode.Off,
    };

    /// <summary>The settings.json value of <paramref name="mode"/>, the desktop's spelling.</summary>
    public static string ModeName(ReplayGainMode mode) => mode switch
    {
        ReplayGainMode.Track => "Track",
        ReplayGainMode.Album => "Album",
        _ => "Off",
    };

    /// <summary>The gain in dB the mode reads, falling back to the other tag; null when off or untagged.</summary>
    public static double? SelectGainDb(ReplayGainMode mode, double? trackDb, double? albumDb) => mode switch
    {
        ReplayGainMode.Track => Finite(trackDb) ?? Finite(albumDb),
        ReplayGainMode.Album => Finite(albumDb) ?? Finite(trackDb),
        _ => null,
    };

    /// <summary>The amplitude factor for a song's tags; 1 = as it is (off, or no ReplayGain).</summary>
    public static double Linear(ReplayGainMode mode, double? trackDb, double? albumDb, double? trackPeak, double preampDb, bool preventClipping)
    {
        if (SelectGainDb(mode, trackDb, albumDb) is not { } gainDb) return 1.0;
        var preamp = double.IsFinite(preampDb) ? Math.Clamp(preampDb, MinPreampDb, MaxPreampDb) : 0;
        var linear = Math.Pow(10.0, Math.Clamp(gainDb + preamp, MinTotalDb, MaxTotalDb) / 20.0);
        // The song's own peak bounds the gain whichever tag it came from: its samples are what clip.
        if (preventClipping && Finite(trackPeak) is { } peak && peak > 0)
            linear = Math.Min(linear, 1.0 / peak);
        return linear;
    }

    /// <summary><see cref="Linear(ReplayGainMode, double?, double?, double?, double, bool)"/> from a
    /// song's fields (<see cref="Track.ReplayGainTrackDb"/> and the others); 1 for no song.</summary>
    public static double Linear(ReplayGainMode mode, Track? track, double preampDb, bool preventClipping) =>
        track == null
            ? 1.0
            : Linear(mode, track.ReplayGainTrackDb, track.ReplayGainAlbumDb, track.ReplayGainTrackPeak, preampDb, preventClipping);

    private static double? Finite(double? value) => value is { } v && double.IsFinite(v) ? v : null;
}
