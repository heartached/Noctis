using System.Runtime.InteropServices;
using Noctis.Services.YouTube;
using Xunit;

namespace Noctis.Tests;

public class YtDlpParsingTests
{
    [Theory]
    [InlineData("[download]  45.3% of 3.21MiB at 1.20MiB/s ETA 00:01", 45.3)]
    [InlineData("[download] 100% of 3.21MiB in 00:02", 100.0)]
    [InlineData("[ExtractAudio] Destination: x.m4a", null)]
    [InlineData("", null)]
    public void ParseProgressPercent_ReadsDownloadLines(string line, double? expected) =>
        Assert.Equal(expected, YtDlpParsing.ParseProgressPercent(line));

    // The audio side (search, info, tags, file names) moved with "Add from YouTube" to the plugin:
    // YouTubeImportParsingTests. What stays is what the lyrics background videos use.

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ&list=RD", "dQw4w9WgXcQ")]
    [InlineData("youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("daft punk digital love", null)]
    public void YouTubeUrls_AreRecognised(string input, string? id)
    {
        Assert.Equal(id is not null, YtDlpParsing.LooksLikeYouTubeUrl(input));
    }

    [Fact]
    public void VideoDownloadArgs_VideoOnly_CappedHeight_RemuxOnlyWithFfmpeg()
    {
        // Lyrics backdrops play muted, so the pick is video-only; 0 = best available.
        var best = YtDlpParsing.VideoDownloadArgs("https://youtu.be/x", "t", null, 0);
        Assert.Equal("https://youtu.be/x", best[^1]);
        Assert.Contains("bestvideo[ext=mp4]/bestvideo/best[ext=mp4]/best", best);
        Assert.DoesNotContain("--remux-video", best);
        Assert.DoesNotContain("-x", best);

        var capped = YtDlpParsing.VideoDownloadArgs("https://youtu.be/x", "t", "C:/ff/ffmpeg.exe", 720);
        Assert.Contains("bestvideo[height<=720][ext=mp4]/bestvideo[height<=720]/best[height<=720][ext=mp4]/best[height<=720]", capped);
        Assert.Contains("--ffmpeg-location", capped);
        Assert.Equal("mp4", capped[capped.ToList().IndexOf("--remux-video") + 1]);
    }

    [Fact]
    public void ReleaseAsset_PerPlatform()
    {
        Assert.Equal("yt-dlp.exe", YtDlpParsing.ReleaseAssetName(OSPlatform.Windows, Architecture.X64));
        Assert.Equal("yt-dlp_macos", YtDlpParsing.ReleaseAssetName(OSPlatform.OSX, Architecture.Arm64));
        Assert.Equal("yt-dlp_linux", YtDlpParsing.ReleaseAssetName(OSPlatform.Linux, Architecture.X64));
        Assert.Equal("yt-dlp_linux_aarch64", YtDlpParsing.ReleaseAssetName(OSPlatform.Linux, Architecture.Arm64));
        Assert.StartsWith(YtDlpParsing.ReleaseBaseUrl, YtDlpParsing.ReleaseDownloadUrl());
    }
}
