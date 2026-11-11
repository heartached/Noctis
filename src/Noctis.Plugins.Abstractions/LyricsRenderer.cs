using Avalonia;
using Avalonia.Media;

namespace Noctis.Plugins;

/// <summary>
/// API 1.8: draws the lyric lines in place of Noctis's own renderer, registered with
/// <see cref="IPluginHost.RegisterLyricsRenderer"/>. The user picks it in Settings → Lyrics →
/// Lyrics Renderer; registering one never switches to it. Noctis keeps everything around the
/// line: the list, its layout and scrolling, which line is active, click-to-seek, the screen
/// reader name, and the line's dimming, depth blur and growth while it is sung. The renderer
/// only measures and draws the inside of each line (<see cref="ILyricsLineView"/>).
/// Everything runs on the UI thread. An exception from any member switches the renderer
/// off (Noctis's own lines come back) and stops the plugin.
/// </summary>
public interface ILyricsRenderer
{
    /// <summary>Shown in the Settings → Lyrics → Lyrics Renderer picker.</summary>
    string Name { get; }

    /// <summary>Whether the renderer draws the lines on <paramref name="surface"/>; where it
    /// says no, Noctis draws them. Asked when the renderer is switched on, not per line.</summary>
    bool SupportsSurface(LyricsSurface surface) => true;

    /// <summary>
    /// Called on the UI thread each time Noctis builds a line of the list (when lyrics load, when
    /// the renderer is switched on, when a lyrics view opens). Return the object that measures
    /// and draws this line, or null to leave this one line to Noctis (an interlude's dots, say).
    /// </summary>
    ILyricsLineView? CreateLine(LyricsLineInfo line);
}

/// <summary>
/// API 1.8: one line drawn by an <see cref="ILyricsRenderer"/>. Noctis calls it on the UI
/// thread from inside its own control, so a frame never runs twice at once. Keep both calls
/// quick, and do not allocate in <see cref="Render"/>: build text layouts in
/// <see cref="Measure"/> (it runs only when the width, the font or a colour changes) and
/// reuse them. Noctis cannot catch an exception thrown later from code that is not called
/// from here (a timer, a control of your own): catch those yourself.
/// </summary>
public interface ILyricsLineView
{
    /// <summary>The size the line needs at <paramref name="availableWidth"/> (the lyrics column,
    /// in device-independent pixels). Noctis lays the list out with it: the height must hold
    /// every row you draw (background vocals, romanization, translation), and a width over
    /// <paramref name="availableWidth"/> is cut to it.</summary>
    Size Measure(double availableWidth, in LyricsLineStyle style);

    /// <summary>
    /// Draws the line into <paramref name="context"/>, at (0, 0) to <paramref name="size"/>
    /// (what <see cref="Measure"/> asked for, or the column's width). Return true to be drawn
    /// again next frame (while words sweep or an animation settles), false when the line is at
    /// rest: Noctis then draws it again only when its state, size or style changes. Returning
    /// true for every line forever costs a frame of work for each of them all the time.
    /// </summary>
    bool Render(DrawingContext context, Size size, in LyricsLineFrame frame);
}

/// <summary>API 1.8. A copy of one lyric line for <see cref="ILyricsRenderer.CreateLine"/>.
/// Times are the lyrics' own, in milliseconds; <see cref="LyricsLineFrame.PositionMs"/> already
/// has the track's lyrics offset applied, so compare the two directly.</summary>
/// <param name="Text">The line's text (the words joined, for a word-timed line).</param>
/// <param name="StartMs">When the line starts, or null for unsynced lyrics.</param>
/// <param name="EndMs">When the line ends, or null when the file does not say.</param>
/// <param name="Words">The line's timed words in order (each keeps its trailing space), or
/// empty for a line timed only as a whole.</param>
public sealed record LyricsLineInfo(string Text, double? StartMs, double? EndMs, IReadOnlyList<LyricsLineWord> Words)
{
    /// <summary>Who sings it in a duet: the main voice, the second voice (Noctis sets it against
    /// the right edge), or both.</summary>
    public LyricsLineVoice Voice { get; init; }

    /// <summary>The "…" placeholder of an intro or an instrumental gap, not a lyric. Its
    /// <see cref="Text"/> is empty; <see cref="LyricsLineFrame.InterludeProgress"/> runs
    /// 0 to 1 through the gap while it is active.</summary>
    public bool IsInterlude { get; init; }

    /// <summary>Timed background vocals (adlibs) sung under the line, or empty.</summary>
    public IReadOnlyList<LyricsLineWord> BackgroundWords { get; init; } = Array.Empty<LyricsLineWord>();

    /// <summary>The line's romanization (TTML), or null.</summary>
    public string? Romanization { get; init; }

    /// <summary>Timed romanization words, or empty (then <see cref="Romanization"/> is untimed).</summary>
    public IReadOnlyList<LyricsLineWord> RomanizationWords { get; init; } = Array.Empty<LyricsLineWord>();

    /// <summary>The line's translation (TTML), or null.</summary>
    public string? Translation { get; init; }
}

/// <summary>API 1.8. One timed word of a <see cref="LyricsLineInfo"/>.</summary>
/// <param name="Text">The word, with its trailing space (none on a line's last word).</param>
/// <param name="StartMs">When it starts to be sung.</param>
/// <param name="EndMs">When it ends, or null when the file does not say (it then lasts until
/// the next word starts).</param>
public readonly record struct LyricsLineWord(string Text, double StartMs, double? EndMs);

/// <summary>API 1.8. Who sings a line in a duet.</summary>
public enum LyricsLineVoice
{
    /// <summary>The main voice (or not a duet).</summary>
    Main,
    /// <summary>The second voice: Noctis sets the line against the right edge.</summary>
    Second,
    /// <summary>Both voices together.</summary>
    Group,
}

/// <summary>API 1.8. Where a line stands this frame.</summary>
public enum LyricsLineState
{
    /// <summary>Unsynced lyrics: no line is ever active.</summary>
    Unsynced,
    /// <summary>Not sung yet.</summary>
    Upcoming,
    /// <summary>Being sung (two lines can be active at once in a duet).</summary>
    Active,
    /// <summary>Sung.</summary>
    Past,
}

/// <summary>API 1.8. How Noctis would draw the lines on this surface right now.</summary>
/// <param name="FontFamily">The lyrics font.</param>
/// <param name="FontSize">The lyric line's font size in pixels: it follows the size of the
/// window. Noctis draws background vocals, romanization and translation at
/// 0.6 of it.</param>
/// <param name="Foreground">The text colour of the lyrics on this surface.</param>
/// <param name="Accent">The accent colour of the lyrics view.</param>
/// <param name="Surface">Where the line is shown.</param>
/// <param name="ShowBackgroundVocals">Settings → Lyrics shows background vocals.</param>
/// <param name="ShowRomanization">Settings → Lyrics shows romanization.</param>
/// <param name="ShowTranslation">Settings → Lyrics shows translations.</param>
public readonly record struct LyricsLineStyle(
    FontFamily FontFamily,
    double FontSize,
    Color Foreground,
    Color Accent,
    LyricsSurface Surface,
    bool ShowBackgroundVocals,
    bool ShowRomanization,
    bool ShowTranslation);

/// <summary>API 1.8. What a line draws from, this frame.</summary>
/// <param name="PositionMs">Where the song is, as the lyrics see it: the audible position
/// (output latency taken off, the track's lyrics offset applied), smooth from frame to frame
/// while playing, still while paused. Compare it with <see cref="LyricsLineWord.StartMs"/>.</param>
/// <param name="State">Upcoming, active or past (or unsynced).</param>
/// <param name="IsLeading">The list is already gliding to this line ahead of its start: Noctis
/// shows its words unsung but no longer dimmed.</param>
/// <param name="InterludeProgress">0 to 1 through the gap while an interlude line is active,
/// NaN otherwise.</param>
/// <param name="IsRightToLeft">The line reads right to left (a lyrics effect decided it).</param>
/// <param name="IsPlaying">The song is playing (not paused).</param>
/// <param name="Style">Font, size and colours to draw with.</param>
public readonly record struct LyricsLineFrame(
    double PositionMs,
    LyricsLineState State,
    bool IsLeading,
    double InterludeProgress,
    bool IsRightToLeft,
    bool IsPlaying,
    LyricsLineStyle Style);
