namespace Noctis.Plugins;

/// <summary>
/// API 1.5: a lyrics motion effect, registered with <see cref="IPluginHost.RegisterLyricsEffect"/>.
/// Noctis calls <see cref="GetPose"/> while it draws a word of a word-synced line, on the UI
/// thread, every frame that word changes: it must be quick and must
/// not allocate (no strings, no LINQ, no boxing, no closures). Everything a pose needs is in
/// <see cref="LyricsWord"/>. An exception switches the effect off and Noctis's own motion comes back.
/// </summary>
public interface ILyricsEffect
{
    /// <summary>Shown in Settings → Plugins under the owning plugin.</summary>
    string Name { get; }

    /// <summary>
    /// Called once each time a song's lyrics load (UI thread), with the text of every line in
    /// order. Set <paramref name="rightToLeft"/>[i] (same length as <paramref name="lines"/>) to true
    /// for each line Noctis should lay out from the right: its words run from the right, the line
    /// sits against the right edge and the sweep fills each word from the right. Lines left false
    /// are laid out as always.
    /// </summary>
    void GetLineDirections(IReadOnlyList<string> lines, Span<bool> rightToLeft);

    /// <summary>How Noctis draws this word: its own motion, the word in one piece, or letter by
    /// letter. Asked when the word is laid out (and again after the effect changes), not per frame.
    /// Words of a right-to-left line are always drawn in one piece (their letters join).</summary>
    LyricsWordMode GetMode(in LyricsWord word);

    /// <summary>The pose of one letter: <paramref name="letter"/> runs from 0 to
    /// <see cref="LyricsWord.LetterCount"/> - 1 in reading order, and is 0 for a word in one piece.</summary>
    LyricsLetterPose GetPose(in LyricsWord word, int letter);
}

/// <summary>API 1.5. How a word is drawn under an <see cref="ILyricsEffect"/>.</summary>
public enum LyricsWordMode
{
    /// <summary>Noctis's own motion (its float and held-note letters).</summary>
    Native,
    /// <summary>The word in one piece, posed by <see cref="ILyricsEffect.GetPose"/> with letter 0.</summary>
    Whole,
    /// <summary>Letter by letter, each posed on its own.</summary>
    Letters,
}

/// <summary>API 1.5. Where lyrics are shown.</summary>
public enum LyricsSurface
{
    /// <summary>The lyrics page.</summary>
    Page,
    /// <summary>The lyrics side panel.</summary>
    SidePanel,
    /// <summary>The mini player.</summary>
    MiniPlayer,
}

/// <summary>API 1.5. One word being drawn, as Noctis sees it this frame.</summary>
/// <param name="Progress">Where the sweep is through the word: 0 at its start, 1 at its end, a
/// little below 0 or above 1 while it is on a neighbour. Far ahead it is -2; long sung it is 3.</param>
/// <param name="DurationMs">How long the word is sung, in milliseconds.</param>
/// <param name="Lift">Noctis's float of the word, 0 at rest to 1 fully up: it rises as the word is
/// sung and falls back over about 0.6 s once its line is over. An effect that keeps its words up
/// until the line ends can scale its float by it.</param>
/// <param name="IsHeld">Noctis treats the word as a held note (it shows its glow layer).</param>
/// <param name="IsLastInLine">The line's last word.</param>
/// <param name="IsRightToLeft">The word's line reads right to left.</param>
/// <param name="LetterCount">Letters (graphemes) in the word, spaces excluded.</param>
/// <param name="FontSize">The word's font size in pixels (offsets are in em of it).</param>
/// <param name="Surface">Where the word is shown.</param>
public readonly record struct LyricsWord(
    double Progress,
    double DurationMs,
    double Lift,
    bool IsHeld,
    bool IsLastInLine,
    bool IsRightToLeft,
    int LetterCount,
    double FontSize,
    LyricsSurface Surface);

/// <summary>
/// API 1.5. One letter's pose relative to where Noctis lays it out. Offsets are in em of the
/// font size (0.05 is a twentieth of the font size); a negative <paramref name="OffsetYEm"/> is up.
/// </summary>
/// <param name="Scale">Size about the letter's centre: 1 = as laid out.</param>
/// <param name="OffsetXEm">Sideways shift.</param>
/// <param name="OffsetYEm">Vertical shift (negative = up).</param>
/// <param name="Glow">Strength of the glow behind a held word (0..1): 1 is Noctis's own glow, 0 none.</param>
public readonly record struct LyricsLetterPose(double Scale, double OffsetXEm, double OffsetYEm, double Glow)
{
    /// <summary>As laid out, with Noctis's glow.</summary>
    public static LyricsLetterPose Rest => new(1, 0, 0, 1);
}
