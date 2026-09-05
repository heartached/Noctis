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
}
