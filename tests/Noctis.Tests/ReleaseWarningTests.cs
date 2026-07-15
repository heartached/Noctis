using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Developer Mode version manager: warnings are parsed from a release body's
/// "[!WARNING]" admonition so flagged releases (e.g. v1.2.0's startup crash)
/// surface a warning without hardcoding version numbers.
/// </summary>
public class ReleaseWarningTests
{
    [Fact]
    public void ExtractReleaseWarning_readsAdmonitionBlockquote_keepsFirstSentenceOnly()
    {
        var body = "## What's Changed\n" +
                   "> [!WARNING]\n" +
                   "> This build has a startup crash on some systems. Please use the latest release — **[v1.2.1](https://github.com/heartached/Noctis/releases/tag/v1.2.1)** — instead.\n" +
                   "\n" +
                   "## Download\n";

        Assert.Equal(
            "This build has a startup crash on some systems.",
            UpdateService.ExtractReleaseWarning(body));
    }

    [Fact]
    public void ExtractReleaseWarning_stripsMarkdownInFirstSentence()
    {
        var body = "> [!WARNING]\n> Use **[v1.2.1](https://example.com)** or `--safe-mode` instead!";
        Assert.Equal("Use v1.2.1 or --safe-mode instead!", UpdateService.ExtractReleaseWarning(body));
    }

    [Fact]
    public void ExtractReleaseWarning_versionDotsDoNotEndTheSentence()
    {
        var body = "> [!WARNING]\r\n> Rolling back to v1.2.1 loses playlists\r\nNot part of the quote.";
        Assert.Equal("Rolling back to v1.2.1 loses playlists", UpdateService.ExtractReleaseWarning(body));
    }

    [Fact]
    public void ExtractReleaseWarning_markerWithoutTextGetsGenericMessage()
    {
        Assert.Equal(
            "This release has a known issue — see the release notes.",
            UpdateService.ExtractReleaseWarning("> [!WARNING]\nplain text, not a blockquote"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("## What's Changed\n- fixed a bug\n")]
    [InlineData("> [!NOTE]\n> Just a note, not a warning.")]
    public void ExtractReleaseWarning_returnsNullWithoutWarning(string? body)
        => Assert.Null(UpdateService.ExtractReleaseWarning(body));
}
