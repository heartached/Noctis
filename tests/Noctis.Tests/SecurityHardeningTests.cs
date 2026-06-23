using System.IO;
using Noctis.Services;
using Noctis.Services.Loon;
using Xunit;

namespace Noctis.Tests;

public class SecurityHardeningTests
{
    // ── Self-updater: download URLs are pinned to GitHub over HTTPS ──

    [Theory]
    [InlineData("https://api.github.com/repos/heartached/Noctis/releases/assets/123", true)]
    [InlineData("https://github.com/heartached/Noctis/releases/download/v1/Setup.exe", true)]
    [InlineData("https://objects.githubusercontent.com/abc", true)]
    [InlineData("https://release-assets.githubusercontent.com/abc", true)]
    [InlineData("http://api.github.com/x", false)]            // not HTTPS
    [InlineData("https://evil.com/Setup.exe", false)]
    [InlineData("https://api.github.com.evil.com/x", false)]  // suffix-spoof
    [InlineData("https://notgithub.com/x", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTrustedGitHubUrl_pinsToGitHubHttps(string? url, bool expected)
        => Assert.Equal(expected, UpdateService.IsTrustedGitHubUrl(url));

    // ── Self-updater: SHA-256 manifest parsing ──

    [Fact]
    public void ParseSha256_findsMatchingEntry_acrossFormats()
    {
        const string expected = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        var manifest =
            $"{new string('a', 64)}  Other-File.dmg\n" +   // double-space (text mode)
            "# a comment line\n" +
            $"{expected} *Noctis-v1.2.3-Setup.exe\n";       // ' *' binary marker

        Assert.Equal(expected, UpdateService.ParseSha256FromChecksums(manifest, "Noctis-v1.2.3-Setup.exe"));
        Assert.Equal(expected, UpdateService.ParseSha256FromChecksums(manifest, "noctis-v1.2.3-setup.exe")); // case-insensitive name
    }

    [Theory]
    [InlineData("Missing.exe")]   // no line for this file
    [InlineData("")]
    [InlineData(null)]
    public void ParseSha256_returnsNullWhenNoMatch(string? fileName)
    {
        var manifest = $"{new string('a', 64)}  Present.exe\n";
        Assert.Null(UpdateService.ParseSha256FromChecksums(manifest, fileName));
    }

    [Fact]
    public void ParseSha256_rejectsNonHexAndWrongLength()
    {
        Assert.Null(UpdateService.ParseSha256FromChecksums("zzzz  File.exe", "File.exe"));          // non-hex
        Assert.Null(UpdateService.ParseSha256FromChecksums("abcd  File.exe", "File.exe"));          // too short
        Assert.Null(UpdateService.ParseSha256FromChecksums(null, "File.exe"));
    }

    // ── Loon artwork relay: path-traversal containment ──

    [Fact]
    public void ResolveArtworkPath_allowsContainedFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "noctis-art-test");
        var resolved = LoonClient.ResolveArtworkPath(root, "artwork/abc123.jpg");
        Assert.NotNull(resolved);
        Assert.EndsWith("abc123.jpg", resolved);
    }

    [Theory]
    [InlineData("artwork/../../escape.txt")]
    [InlineData("../escape.txt")]
    [InlineData("artwork/../../../../../../etc/passwd")]
    [InlineData("artwork/")]   // strips to empty
    [InlineData("")]
    public void ResolveArtworkPath_rejectsTraversalAndEmpty(string requestPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "noctis-art-test");
        Assert.Null(LoonClient.ResolveArtworkPath(root, requestPath));
    }
}
