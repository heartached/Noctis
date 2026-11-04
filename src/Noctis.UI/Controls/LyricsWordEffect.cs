using System;
using System.Collections.Generic;
using Avalonia;

namespace Noctis.Controls;

/// <summary>How a karaoke word is drawn under a lyrics effect.</summary>
public enum WordDrawMode
{
    /// <summary>Noctis's own motion (float, held-note letters).</summary>
    Native,
    /// <summary>In one piece, posed by the effect (letter 0).</summary>
    Whole,
    /// <summary>Letter by letter, each posed by the effect.</summary>
    Letters,
}

/// <summary>Where a karaoke word is shown (<see cref="LyricsWordEffect.SurfaceProperty"/>).</summary>
public enum WordSurface
{
    Page,
    SidePanel,
    MiniPlayer,
}

/// <summary>One word as the effect sees it this frame (plugin API 1.5's LyricsWord).</summary>
public readonly record struct WordPoseInput(
    double Progress, double DurationMs, double Lift, bool IsHeld, bool IsLastInLine,
    bool IsRightToLeft, int LetterCount, double FontSize, WordSurface Surface)
{
    /// <summary>Time since the held word started; NaN when no held-note animation is active.</summary>
    public double EmphasisMs { get; init; }

    /// <summary>True for scripts that must remain a single shaped text run.</summary>
    public bool IsJoinedScript { get; init; }
}

/// <summary>One letter's pose: scale about its centre, offsets in em, glow strength (1 = Noctis's own).</summary>
public readonly record struct LetterPose(double Scale, double OffsetXEm, double OffsetYEm, double Glow)
{
    public static LetterPose Rest => new(1, 0, 0, 1);
}

/// <summary>What <see cref="KaraokeWordText"/> asks of a lyrics effect. Calls run on the UI thread
/// while a word is laid out or drawn, and must not allocate.</summary>
public interface IWordEffect
{
    WordDrawMode Mode(in WordPoseInput word);
    LetterPose Pose(in WordPoseInput word, int letter);
    void LineDirections(IReadOnlyList<string> lines, Span<bool> rightToLeft);
}

/// <summary>
/// The lyrics effect in use (plugin API 1.5, IPluginHost.RegisterLyricsEffect), or none: every
/// <see cref="KaraokeWordText"/> reads it while it draws. <see cref="Changed"/> tells the words
/// and the lyrics view model to lay out again (and to ask the line directions again).
/// </summary>
public sealed class LyricsWordEffect
{
    private LyricsWordEffect() { }

    /// <summary>Where the words below are shown; set once on each lyrics view, inherited.</summary>
    public static readonly AttachedProperty<WordSurface> SurfaceProperty =
        AvaloniaProperty.RegisterAttached<LyricsWordEffect, AvaloniaObject, WordSurface>("Surface", WordSurface.Page, inherits: true);

    public static WordSurface GetSurface(AvaloniaObject o) => o.GetValue(SurfaceProperty);
    public static void SetSurface(AvaloniaObject o, WordSurface value) => o.SetValue(SurfaceProperty, value);

    private static IWordEffect? _current;

    public static IWordEffect? Current => _current;

    /// <summary>Raised on the UI thread after <see cref="Current"/> changed.</summary>
    public static event EventHandler? Changed;

    /// <summary>Call on the UI thread.</summary>
    public static void Set(IWordEffect? effect)
    {
        if (ReferenceEquals(_current, effect)) return;
        _current = effect;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
