using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Log-sink redaction: media-server stream URLs carry auth in their query string
/// (Subsonic t/s, Jellyfin api_key) and LibVLC quotes full MRLs in its own log
/// lines, so anything token-shaped must be gone before a line can reach the
/// session log ("Copy Logs") or the VLC diag file.
/// </summary>
public class LogRedactionTests
{
    [Theory]
    // LibVLC-style quoted MRL: query goes, scheme/host/path stay readable.
    [InlineData(
        "main input error: open of `https://jf.example.com/Audio/t1/stream?static=true&api_key=tok-123' failed",
        "main input error: open of `https://jf.example.com/Audio/t1/stream?[redacted]' failed")]
    // Subsonic salted-token stream URL, with trailing text after the URL.
    [InlineData(
        "http error: https://nas.local/rest/stream.view?u=demo&t=abcdef012345&s=deadbeef&id=9 (410)",
        "http error: https://nas.local/rest/stream.view?[redacted] (410)")]
    // Untouched: plain text, local paths, query-less URLs.
    [InlineData("no urls here at all", "no urls here at all")]
    [InlineData(@"tag save failed for 'C:\Music\track.flac'", @"tag save failed for 'C:\Music\track.flac'")]
    [InlineData("reached https://jf.example.com/System/Info fine", "reached https://jf.example.com/System/Info fine")]
    public void Scrub_RemovesUrlQueryStrings_LeavesEverythingElse(string input, string expected)
        => Assert.Equal(expected, LogRedaction.Scrub(input));

    [Fact]
    public void Scrub_RedactsTokenParameters_OutsideUrls()
    {
        Assert.Equal("header Token=[redacted] rejected",
            LogRedaction.Scrub("header Token=\"tok-123\" rejected"));
        Assert.Equal("retry with api_key=[redacted] later",
            LogRedaction.Scrub("retry with api_key=tok-123 later"));
    }

    [Fact]
    public void DebugLog_Write_ScrubsBeforeStoring()
    {
        // The session log is the exact text "Copy Logs" puts on the clipboard, so
        // the scrub must happen at the sink, not rely on well-behaved callers.
        var marker = Guid.NewGuid().ToString("N");
        DebugLog.Write("Test",
            $"probe-{marker}: open of https://jf.example.com/Audio/t1/stream?static=true&api_key=SECRET{marker} failed");

        var snapshot = DebugLog.Snapshot();
        Assert.Contains($"probe-{marker}", snapshot);
        Assert.DoesNotContain($"SECRET{marker}", snapshot);
    }
}
