using System.Runtime.InteropServices;
using Noctis.Services.YouTube;
using Xunit;

namespace Noctis.Tests;

public class YtDlpParsingTests
{
    [Fact]
    public void ParseInfo_ReadsMusicFields()
    {
        var json = """
        {"id":"abc123def45","title":"Daft Punk - Digital Love (Official Video)","webpage_url":"https://www.youtube.com/watch?v=abc123def45",
         "channel":"Daft Punk - Topic","duration":301.2,"track":"Digital Love","artist":"Daft Punk","album":"Discovery",
         "release_year":2001,"ext":"m4a",
         "thumbnails":[{"url":"https://i.ytimg.com/vi/abc123def45/default.webp","height":90},
                       {"url":"https://i.ytimg.com/vi/abc123def45/hqdefault.jpg","height":360},
                       {"url":"https://i.ytimg.com/vi/abc123def45/maxresdefault.jpg","height":1080},
                       {"url":"https://i.ytimg.com/vi/abc123def45/huge.jpg","height":2160}]}
        """;
        var info = YtDlpParsing.ParseInfo(json)!;
        Assert.Equal("abc123def45", info.Id);
        Assert.Equal("Digital Love", info.Track);
        Assert.Equal("Daft Punk", info.Artist);
        Assert.Equal("Discovery", info.Album);
        Assert.Equal(2001, info.Year);
        Assert.Equal(TimeSpan.FromSeconds(301.2), info.Duration);
        Assert.Equal("https://i.ytimg.com/vi/abc123def45/maxresdefault.jpg", info.ThumbnailUrl);
        Assert.Equal("5:01", info.DurationText);
    }

    [Fact]
    public void ParseInfo_FallsBackToUploadDateYear_AndHqDefaultThumb()
    {
        var info = YtDlpParsing.ParseInfo("""{"id":"zzz","title":"x","upload_date":"20190812","uploader":"Someone"}""")!;
        Assert.Equal(2019, info.Year);
        Assert.Equal("https://i.ytimg.com/vi/zzz/hqdefault.jpg", info.ThumbnailUrl);
        Assert.Equal("Someone", info.Channel);
        Assert.Equal("https://www.youtube.com/watch?v=zzz", info.Url);
    }

    [Fact]
    public void ParseSearch_TakesOneEntryPerLine_SkipsGarbage_AndDedupes()
    {
        var ndjson = "WARNING: something\n{\"id\":\"a\",\"title\":\"A\"}\n\n{\"id\":\"b\",\"title\":\"B\"}\n{\"id\":\"a\",\"title\":\"A again\"}\nnot json\n";
        var list = YtDlpParsing.ParseSearch(ndjson);
        Assert.Equal(new[] { "a", "b" }, list.Select(x => x.Id));
    }

    [Theory]
    [InlineData("Artist - Song (Official Video)", "Artist - Song")]
    [InlineData("Song [Official Audio] | Label", "Song")]
    [InlineData("Song (Lyrics)", "Song")]
    [InlineData("Song (Live at Wembley)", "Song")]
    [InlineData("Song (Remastered 2011)", "Song")]
    [InlineData("Plain Song", "Plain Song")]
    [InlineData("Song - Official Music Video", "Song")]
    public void CleanTitle_StripsNoise(string input, string expected) =>
        Assert.Equal(expected, YtDlpParsing.CleanTitle(input));

    [Theory]
    [InlineData("Daft Punk - Topic", "Daft Punk")]
    [InlineData("RihannaVEVO", "Rihanna")]
    [InlineData("Some Band Official", "Some Band")]
    [InlineData("Channel", "Channel")]
    public void CleanChannel_StripsTopicAndVevo(string input, string expected) =>
        Assert.Equal(expected, YtDlpParsing.CleanChannel(input));

    [Fact]
    public void InferTags_PrefersMusicFields_ThenTitleSplit_ThenChannel()
    {
        var withTags = new YouTubeTrackInfo("1", "u", "Whatever (Official)", "Chan", null, null, "Track T", "Artist A", null, null, null);
        Assert.Equal(("Artist A", "Track T"), YtDlpParsing.InferTags(withTags));

        var split = new YouTubeTrackInfo("2", "u", "Radiohead - Karma Police (Official Video)", "RadioheadVEVO", null, null, null, null, null, null, null);
        Assert.Equal(("Radiohead", "Karma Police"), YtDlpParsing.InferTags(split));

        var channelOnly = new YouTubeTrackInfo("3", "u", "Karma Police [Lyrics]", "Radiohead - Topic", null, null, null, null, null, null, null);
        Assert.Equal(("Radiohead", "Karma Police"), YtDlpParsing.InferTags(channelOnly));
    }

    [Fact]
    public void FileNames_AreSafe_AndKeepArtistTitleShape()
    {
        Assert.Equal("AC_DC - Back In Black.m4a", YtDlpParsing.BuildFileName("AC/DC", "Back In Black", "m4a"));
        Assert.Equal("Song.m4a", YtDlpParsing.BuildFileName("", "Song", ".M4A"));
        Assert.Equal("untitled", YtDlpParsing.SanitizeFileName("  ...  "));
        Assert.Equal("a b", YtDlpParsing.SanitizeFileName("a    b"));
        Assert.True(YtDlpParsing.SanitizeFileName(new string('x', 300)).Length <= 120);
    }

    [Theory]
    [InlineData("[download]  45.3% of 3.21MiB at 1.20MiB/s ETA 00:01", 45.3)]
    [InlineData("[download] 100% of 3.21MiB in 00:02", 100.0)]
    [InlineData("[ExtractAudio] Destination: x.m4a", null)]
    [InlineData("", null)]
    public void ParseProgressPercent_ReadsDownloadLines(string line, double? expected) =>
        Assert.Equal(expected, YtDlpParsing.ParseProgressPercent(line));

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ&list=RD", "dQw4w9WgXcQ")]
    [InlineData("youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("daft punk digital love", null)]
    public void YouTubeUrls_AreRecognised(string input, string? id)
    {
        Assert.Equal(id is not null, YtDlpParsing.LooksLikeYouTubeUrl(input));
        Assert.Equal(id, YtDlpParsing.ExtractVideoId(input));
    }

    [Fact]
    public void Args_UseTheExpectedShapes()
    {
        var search = YtDlpParsing.SearchArgs("  daft punk ", 12);
        Assert.Contains("--dump-json", search);
        Assert.Contains("--flat-playlist", search);
        Assert.Equal("ytsearch12:daft punk", search[^1]);

        var dl = YtDlpParsing.DownloadArgs("https://youtu.be/x", "C:/out/%(id)s.%(ext)s", null);
        Assert.Equal("https://youtu.be/x", dl[^1]);
        Assert.Contains("bestaudio[ext=m4a]/bestaudio/best", dl);
        Assert.DoesNotContain("-x", dl);

        var dlFf = YtDlpParsing.DownloadArgs("https://youtu.be/x", "t", "C:/ff/ffmpeg.exe");
        Assert.Contains("-x", dlFf);
        Assert.Contains("--ffmpeg-location", dlFf);
        Assert.Equal("m4a", dlFf[dlFf.ToList().IndexOf("--audio-format") + 1]);
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
