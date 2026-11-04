using Noctis.Models;
using Noctis.Localization;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// What the playing file is, for Now Playing's quality badge and the card it opens: Core's
/// classification (<see cref="Track.IsHiResLossless"/>, <see cref="Track.IsLossless"/>,
/// <see cref="Track.AudioQualityBadge"/>, <see cref="Track.QualityDetailLine"/>), Apple Music's
/// tiers - Lossless up to 24-bit/48 kHz, Hi-Res Lossless beyond it - and a compressed file's
/// codec, the desktop's label word for word (OPUS, OGG as the PC shows them). Null for a file
/// with no codec to name.
/// </summary>
public sealed record TrackQuality(string Label, string Detail, string Meaning, bool IsLossless)
{
    /// <summary>The Hi-Res line: Core's lossless sentence says nothing about the resolution.</summary>
    public static string HiResMeaning => Loc.T("Mobile.Quality.HiResMeaning");

    public static TrackQuality? Of(Track? track)
    {
        if (track == null) return null;
        var label = track.AudioQualityBadge;
        if (label.Length == 0) return null;
        var meaning = track.IsHiResLossless ? HiResMeaning : track.AudioQualityDescription;
        return new TrackQuality(label, track.QualityDetailLine, meaning, track.IsLossless);
    }
}
