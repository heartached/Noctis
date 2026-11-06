namespace Noctis.Mobile.Views;

/// <summary>Which way a press has turned out to move, once it has travelled past the slop.</summary>
internal enum SwipeAxis { Undecided, Horizontal, Vertical }

/// <summary>What a released track swipe does.</summary>
internal enum SwipeOutcome { None, Next, Previous }

/// <summary>
/// The mini player's and Now Playing cover's sideways swipe (Apple Music / Spotify): left skips to
/// the next song, right goes back to the one before. Pure maths, kept apart for tests; the
/// pointer plumbing is <see cref="TrackSwipeGesture"/>.
/// </summary>
internal static class TrackSwipe
{
    /// <summary>Travel before the press is a gesture at all: the shell's swipe-down to close uses
    /// the same 10 dp, so the two decide on the same move and never both take the finger.</summary>
    internal const double SlopDip = 10;

    /// <summary>A release past this share of the width changes the song.</summary>
    internal const double CommitFraction = 0.35;

    /// <summary>A flick this fast (dp/ms) in the drag's direction changes the song from a short drag…</summary>
    internal const double FlingSpeed = 0.5;

    /// <summary>…provided it went at least this far, so a jittery tap never skips.</summary>
    internal const double MinFlingDip = 24;

    /// <summary>How much of the finger's travel shows where there is no song to go to.</summary>
    internal const double RubberBand = 0.25;

    /// <summary>Horizontal once the sideways travel passes the slop; vertical once the up or down
    /// travel passes it with the sideways still inside (the sheet's drag, a list's scroll), and
    /// the swipe then stays out. Sideways is judged first, exactly as ShellView's swipe-down to
    /// close judges it, so on any one move exactly one of the two takes the finger.</summary>
    internal static SwipeAxis Lock(double dx, double dy)
    {
        if (Math.Abs(dx) > SlopDip) return SwipeAxis.Horizontal;
        if (Math.Abs(dy) > SlopDip) return SwipeAxis.Vertical;
        return SwipeAxis.Undecided;
    }

    /// <summary>Left (negative dx) is the next song, right the previous one.</summary>
    internal static SwipeOutcome Direction(double dx) =>
        dx < 0 ? SwipeOutcome.Next : dx > 0 ? SwipeOutcome.Previous : SwipeOutcome.None;

    /// <summary>Whether the song in <paramref name="dx"/>'s direction exists.</summary>
    internal static bool CanGo(double dx, bool canNext, bool canPrevious) => Direction(dx) switch
    {
        SwipeOutcome.Next => canNext,
        SwipeOutcome.Previous => canPrevious,
        _ => false,
    };

    /// <summary>Where the content sits for a finger <paramref name="dx"/> along: with it, or a
    /// quarter of it (the rubber band) towards a song that is not there.</summary>
    internal static double Offset(double dx, bool canNext, bool canPrevious) =>
        CanGo(dx, canNext, canPrevious) ? dx : dx * RubberBand;

    /// <summary>
    /// The release: past <see cref="CommitFraction"/> of <paramref name="width"/>, or flicked at
    /// <see cref="FlingSpeed"/> the same way after <see cref="MinFlingDip"/>, it changes the song;
    /// a flick back the other way, or a song that is not there, settles back.
    /// <paramref name="velocity"/> is dp/ms, negative leftwards.
    /// </summary>
    internal static SwipeOutcome Decide(double dx, double velocity, double width, bool canNext, bool canPrevious)
    {
        var direction = Direction(dx);
        if (direction == SwipeOutcome.None || !CanGo(dx, canNext, canPrevious)) return SwipeOutcome.None;
        if (width > 0 && Math.Abs(dx) >= width * CommitFraction && !(Math.Sign(velocity) == -Math.Sign(dx) && Math.Abs(velocity) >= FlingSpeed))
            return direction;
        if (Math.Abs(dx) >= MinFlingDip && Math.Sign(velocity) == Math.Sign(dx) && Math.Abs(velocity) >= FlingSpeed)
            return direction;
        return SwipeOutcome.None;
    }
}
