using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class ShareClipRendererTests
{
    [Fact]
    public void BuildFfmpegArgs_ContainsInputsCodecsAndOutputLast()
    {
        var timing = new ShareClipTiming(12.5, 20);
        var args = ShareClipRenderer.BuildFfmpegArgs("frame.png", "song.flac", "out.mp4", timing);

        Assert.Contains("-loop", args);
        Assert.Contains("frame.png", args);
        Assert.Contains("song.flac", args);
        Assert.Contains("12.5", args);            // start seconds (-ss)
        Assert.Contains("20", args);              // duration seconds (-t)
        Assert.Contains("libx264", args);
        Assert.Contains("aac", args);
        Assert.Contains("-shortest", args);
        Assert.Equal("out.mp4", args[^1]);        // output path is last
    }

    [Fact]
    public void BuildFfmpegArgs_ClampsOutputToAudioWindow_WithNoFade()
    {
        var timing = new ShareClipTiming(0, 14);
        var args = ShareClipRenderer.BuildFfmpegArgs("f.png", "a.mp3", "o.mp4", timing);

        // An output-side -t pins the looped still-image video to the audio window so the
        // clip ends exactly with the audio (no trailing silence). Both an input -t (audio
        // trim) and an output -t (stream clamp) are present, with -shortest kept.
        Assert.NotEqual(args.IndexOf("-t"), args.LastIndexOf("-t"));
        Assert.Equal("14", args[args.LastIndexOf("-t") + 1]);
        Assert.Contains("-shortest", args);

        // No audio fade — the clip should cut cleanly, not fade out.
        Assert.DoesNotContain("-af", args);
    }
}
