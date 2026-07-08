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

    [Fact]
    public void BuildFfmpegFrameArgs_UsesFrameSequenceInsteadOfLoop()
    {
        var timing = new ShareClipTiming(12.5, 20);
        var args = ShareClipRenderer.BuildFfmpegFrameArgs("frames/frame-%05d.png", 24, "song.flac", "out.mp4", timing);

        Assert.DoesNotContain("-loop", args);                     // not the still-image path
        Assert.DoesNotContain("stillimage", args);
        Assert.Equal("24", args[args.IndexOf("-framerate") + 1]); // sequence framerate
        Assert.Contains("frames/frame-%05d.png", args);
        Assert.Contains("song.flac", args);
        Assert.Contains("12.5", args);                            // invariant-culture start (-ss)
        Assert.Contains("libx264", args);
        Assert.Contains("aac", args);
        Assert.Equal("out.mp4", args[^1]);                        // output path is last
    }

    [Fact]
    public void BuildFfmpegFrameArgs_KeepsOutputClampSemantics()
    {
        var timing = new ShareClipTiming(0, 14);
        var args = ShareClipRenderer.BuildFfmpegFrameArgs("f-%05d.png", 24, "a.mp3", "o.mp4", timing);

        // Same clamp contract as the still path: input -t (audio trim) + output -t
        // (stream clamp) + -shortest, so the clip ends exactly with the audio.
        Assert.NotEqual(args.IndexOf("-t"), args.LastIndexOf("-t"));
        Assert.Equal("14", args[args.LastIndexOf("-t") + 1]);
        Assert.Contains("-shortest", args);
        Assert.DoesNotContain("-af", args);
    }
}
