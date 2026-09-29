using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Animated covers used to run one full software decoder per control: the lyrics page,
/// four mini-player forms and three Cover Flow modes each decoded the same file, hidden
/// or not (a user log showed five Cover.Play lines for one clip in one second). Now one
/// decoder serves every control showing a file, and only controls that could be seen
/// hold a lease on it.
/// </summary>
public class AnimatedCoverSharingTests
{
    // ── Lease bookkeeping ────────────────────────────────────────────────────

    [Fact]
    public void Leases_SameSourceSharesOneDecoder()
    {
        var leases = new AnimatedCoverLeases<object>();
        var consumers = new[] { new object(), new object(), new object(), new object(), new object() };

        Assert.True(leases.Acquire("cover.mp4", consumers[0]));   // first lease starts the decoder
        for (var i = 1; i < consumers.Length; i++)
            Assert.False(leases.Acquire("cover.mp4", consumers[i]));

        Assert.Equal(5, leases.CountOf("cover.mp4"));
        Assert.Equal(1, leases.SourceCount);
    }

    [Fact]
    public void Leases_OnlyTheLastReleaseStopsTheDecoder()
    {
        var leases = new AnimatedCoverLeases<object>();
        object a = new(), b = new();
        leases.Acquire("cover.mp4", a);
        leases.Acquire("cover.mp4", b);

        Assert.Equal(("cover.mp4", false), leases.Release(a));
        Assert.Equal(("cover.mp4", true), leases.Release(b));
        Assert.Equal(0, leases.CountOf("cover.mp4"));
        Assert.Equal(0, leases.SourceCount);
    }

    [Fact]
    public void Leases_AcquiringAnotherSourceMovesTheLease()
    {
        var leases = new AnimatedCoverLeases<object>();
        var consumer = new object();
        leases.Acquire("a.mp4", consumer);

        Assert.True(leases.Acquire("b.mp4", consumer));
        Assert.Equal(0, leases.CountOf("a.mp4"));
        Assert.Equal(1, leases.CountOf("b.mp4"));
        Assert.Equal("b.mp4", leases.SourceOf(consumer));
    }

    [Fact]
    public void Leases_ReacquiringTheSameSourceIsIdempotent()
    {
        var leases = new AnimatedCoverLeases<object>();
        var consumer = new object();
        leases.Acquire("a.mp4", consumer);

        Assert.False(leases.Acquire("a.mp4", consumer));
        Assert.Equal(1, leases.CountOf("a.mp4"));
    }

    [Fact]
    public void Leases_ReleasingWithoutALeaseIsANoOp()
    {
        var leases = new AnimatedCoverLeases<object>();
        Assert.Equal((null, false), leases.Release(new object()));
    }

    // ── Visibility gating ────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, true, true, true, true, false, true)]    // on screen
    [InlineData(false, true, true, true, true, false, false)]  // Animated Artwork off
    [InlineData(true, false, true, true, true, false, false)]  // no cover for this track
    [InlineData(true, true, false, true, true, false, false)]  // page detached
    [InlineData(true, true, true, false, true, false, false)]  // hidden form / costume / mode
    [InlineData(true, true, true, true, false, false, false)]  // main window hidden behind the mini player
    [InlineData(true, true, true, true, true, true, false)]    // window minimized
    public void ShouldDecode_OnlyWhenItCouldBeSeen(bool active, bool hasSource, bool attached,
        bool effectivelyVisible, bool windowShown, bool minimized, bool expected)
        => Assert.Equal(expected, AnimatedCoverPolicy.ShouldDecode(
            active, hasSource, attached, effectivelyVisible, windowShown, minimized));

    // ── Frame-rate ceiling ───────────────────────────────────────────────────

    private const long Freq = 1_000_000; // timestamps in µs

    private static bool Accept(double gapMs) =>
        AnimatedCoverPolicy.AcceptFrame(10 * Freq + (long)(gapMs * 1000), 10 * Freq, Freq);

    [Fact]
    public void AcceptFrame_FirstFrameAlwaysPasses()
        => Assert.True(AnimatedCoverPolicy.AcceptFrame(5, 0, Freq));

    [Theory]
    [InlineData(1000.0 / 24)]
    [InlineData(1000.0 / 25)]
    [InlineData(1000.0 / 30)]
    [InlineData(31)]  // a 30 fps frame arriving 2 ms early still passes
    public void AcceptFrame_CoverFrameRatesPassUntouched(double gapMs) => Assert.True(Accept(gapMs));

    [Fact]
    public void AcceptFrame_SixtyFpsIsHalved()
    {
        Assert.False(Accept(1000.0 / 60));
        Assert.True(Accept(2 * 1000.0 / 60));
    }

    // ── Controls ────────────────────────────────────────────────────────────
    // A real but empty file so File.Exists passes; there is nothing for a decoder to play,
    // so these tests check only who holds a lease. Every test ends with no consumers.

    private static string TempCover()
    {
        var path = Path.Combine(Path.GetTempPath(), $"noctis-cover-test-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    private static AnimatedCoverImage Cover(string source) => new() { Source = source, IsActive = true };

    [AvaloniaFact]
    public void Controls_ShowingOneFileShareOneFeed()
    {
        var source = TempCover();
        var a = Cover(source);
        var b = Cover(source);
        var window = new Window { Content = new StackPanel { Children = { a, b } } };
        try
        {
            window.Show();

            Assert.True(a.IsDecoding);
            Assert.True(b.IsDecoding);
            Assert.Equal(2, AnimatedCoverFeed.ConsumerCount(source));
        }
        finally
        {
            window.Close();
            File.Delete(source);
        }
        Assert.Equal(0, AnimatedCoverFeed.ConsumerCount(source));
    }

    [AvaloniaFact]
    public void Controls_HiddenParentReleasesWhileStillAttached()
    {
        var source = TempCover();
        var cover = Cover(source);
        var form = new Panel { Children = { cover } };
        var window = new Window { Content = form };
        try
        {
            window.Show();
            Assert.True(cover.IsDecoding);

            form.IsVisible = false; // e.g. a mini-player form that isn't the current one
            Assert.True(cover.IsAttachedToVisualTree());
            Assert.False(cover.IsDecoding);
            Assert.Equal(0, AnimatedCoverFeed.ConsumerCount(source));

            form.IsVisible = true;
            Assert.True(cover.IsDecoding);
        }
        finally
        {
            window.Close();
            File.Delete(source);
        }
    }

    [AvaloniaFact]
    public void Controls_HiddenOrMinimizedWindowReleases()
    {
        var source = TempCover();
        var cover = Cover(source);
        var window = new Window { Content = cover };
        try
        {
            Assert.False(cover.IsDecoding); // attached, but the window was never shown

            window.Show();
            Assert.True(cover.IsDecoding);

            window.Hide(); // the main window while the mini player is open
            Assert.False(cover.IsDecoding);

            window.Show();
            Assert.True(cover.IsDecoding);

            window.WindowState = WindowState.Minimized;
            Assert.False(cover.IsDecoding);

            window.WindowState = WindowState.Normal;
            Assert.True(cover.IsDecoding);
        }
        finally
        {
            window.Close();
            File.Delete(source);
        }
    }

    [AvaloniaFact]
    public void Controls_SourceChangeStopsTheOldFeedAtOnce_HidingLingers()
    {
        var first = TempCover();
        var second = TempCover();
        var cover = Cover(first);
        var window = new Window { Content = cover };
        try
        {
            window.Show();
            Assert.True(AnimatedCoverFeed.HasFeed(first));

            // Track change: the old file must be released promptly (replace/remove
            // retries on its lock), so no grace period.
            cover.Source = second;
            Assert.False(AnimatedCoverFeed.HasFeed(first));
            Assert.Equal(1, AnimatedCoverFeed.ConsumerCount(second));

            // Merely going out of sight keeps the feed briefly for a hand-off (the
            // mini player opening as the main window hides).
            window.Hide();
            Assert.Equal(0, AnimatedCoverFeed.ConsumerCount(second));
            Assert.True(AnimatedCoverFeed.HasFeed(second));

            window.Show();
            Assert.Equal(1, AnimatedCoverFeed.ConsumerCount(second));
        }
        finally
        {
            window.Close();
            File.Delete(first);
            File.Delete(second);
        }
    }

    [AvaloniaFact]
    public void Controls_AnimationOffReleases()
    {
        var source = TempCover();
        var cover = Cover(source);
        var window = new Window { Content = cover };
        try
        {
            window.Show();
            Assert.True(cover.IsDecoding);

            cover.IsActive = false;
            Assert.False(cover.IsDecoding);
            Assert.False(AnimatedCoverFeed.HasFeed(source));
        }
        finally
        {
            window.Close();
            File.Delete(source);
        }
    }
}
