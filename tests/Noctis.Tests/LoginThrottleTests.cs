using System;
using Noctis.Services.Server;
using Xunit;

namespace Noctis.Tests;

public class LoginThrottleTests
{
    [Fact]
    public void LocksAfterMaxFailures_ThenReleases()
    {
        var now = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
        var t = new LoginThrottle(() => now);

        for (var i = 1; i < LoginThrottle.MaxFailures; i++)
            Assert.False(t.RecordFailure("1.2.3.4"));
        Assert.False(t.IsLocked("1.2.3.4", out _));

        Assert.True(t.RecordFailure("1.2.3.4"));
        Assert.True(t.IsLocked("1.2.3.4", out var retry));
        Assert.Equal(LoginThrottle.Lockout, retry);

        // Another client is unaffected.
        Assert.False(t.IsLocked("5.6.7.8", out _));

        now += LoginThrottle.Lockout + TimeSpan.FromSeconds(1);
        Assert.False(t.IsLocked("1.2.3.4", out _));
    }

    [Fact]
    public void OldFailures_FallOutOfTheWindow()
    {
        var now = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
        var t = new LoginThrottle(() => now);
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++) t.RecordFailure("c");
        now += LoginThrottle.Window + TimeSpan.FromSeconds(1);
        Assert.False(t.RecordFailure("c")); // the earlier ones expired: this is failure #1 again
        Assert.False(t.IsLocked("c", out _));
    }

    [Fact]
    public void Success_ClearsTheRecord_AndPruneDropsStaleClients()
    {
        var now = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
        var t = new LoginThrottle(() => now);
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++) t.RecordFailure("c");
        t.RecordSuccess("c");
        Assert.False(t.RecordFailure("c"));

        t.RecordFailure("stale");
        now += LoginThrottle.Window + TimeSpan.FromMinutes(1);
        t.Prune();
        Assert.False(t.IsLocked("stale", out _));
    }

    [Fact]
    public void RecordFailure_SweepsStaleEntries_SoJunkNamesDoNotPileUp()
    {
        var now = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
        var t = new LoginThrottle(() => now);
        for (var i = 0; i < 100; i++) t.RecordFailure("1.2.3.4\njunk" + i);
        Assert.Equal(100, t.Count);

        now += LoginThrottle.Window + TimeSpan.FromMinutes(1);
        t.RecordFailure("1.2.3.4\nfresh");
        Assert.Equal(1, t.Count);
    }

    [Fact]
    public void Key_NamesNoAccountCanHave_ShareOneBucketPerAddress()
    {
        var tooLong = LoginThrottle.Key("1.2.3.4", new string('a', LoginThrottle.MaxNameLength + 1));
        Assert.Equal(tooLong, LoginThrottle.Key("1.2.3.4", new string('b', 1_000_000)));
        Assert.True(tooLong.Length < 16);
        Assert.NotEqual(tooLong, LoginThrottle.Key("5.6.7.8", new string('b', 1_000_000)));
        Assert.NotEqual(tooLong, LoginThrottle.Key("1.2.3.4", new string('a', LoginThrottle.MaxNameLength)));
        Assert.Equal(LoginThrottle.Key("1.2.3.4", "alice"), LoginThrottle.Key("1.2.3.4", " Alice "));
    }

    [Fact]
    public void FullTable_CountsNewNamesAgainstTheAddress()
    {
        var now = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
        var t = new LoginThrottle(() => now);
        var alice = LoginThrottle.Key("9.9.9.9", "alice");
        t.RecordFailure(alice);
        for (var i = 0; t.Count < LoginThrottle.MaxEntries; i++)
            t.RecordFailure(LoginThrottle.Key("1.2.3.4", "junk" + i));

        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
            t.RecordFailure(LoginThrottle.Key("1.2.3.4", "more" + i));

        // One address bucket instead of an entry per name, and it trips the lockout.
        Assert.Equal(LoginThrottle.MaxEntries + 1, t.Count);
        Assert.True(t.IsLocked(LoginThrottle.Key("1.2.3.4", "anyone"), out _));
        Assert.False(t.IsLocked(LoginThrottle.Key("5.6.7.8", "anyone"), out _));
        // A name already in the table still counts on its own key.
        Assert.False(t.RecordFailure(alice));
        Assert.False(t.IsLocked(alice, out _));
        Assert.Equal(LoginThrottle.MaxEntries + 1, t.Count);
    }

    [Fact]
    public void Prune_KeepsFreshFailures_WhenTheOldestIsStale()
    {
        var now = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
        var t = new LoginThrottle(() => now);
        t.RecordFailure("c");
        now += LoginThrottle.Window - TimeSpan.FromMinutes(1);
        for (var i = 0; i < LoginThrottle.MaxFailures - 2; i++) t.RecordFailure("c");
        now += TimeSpan.FromMinutes(2); // only the first failure has left the window

        t.Prune();

        Assert.False(t.RecordFailure("c"));
        Assert.True(t.RecordFailure("c"));
    }
}
