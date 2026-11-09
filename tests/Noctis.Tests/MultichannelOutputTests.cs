using System.Diagnostics;
using Avalonia.Headless.XUnit;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings → Audio → Multichannel output (GitHub #200): 4–8 channel output on a surround device.
/// LibVLC's amem hands over VLC's internal channel order (measured: 5.1 = L R RL RR C LFE,
/// 7.1 = L R ML MR RL RR C LFE), which the sinks reorder to Windows' WAVEFORMATEXTENSIBLE order.
/// </summary>
[Collection("Tag save replace")] // the real-player test below must not share the process with another LibVLC player
public class MultichannelOutputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "multich-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _o;

    public MultichannelOutputTests(ITestOutputHelper o)
    {
        _o = o;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // ── Channel order ──

    // amem slot contents per channel count (VLC order, from the LibVLC 3.0.23 measurement),
    // and the Windows order they must end up in.
    [Theory]
    [InlineData(4, "FL FR BL BR", "FL FR BL BR")]
    [InlineData(5, "FL FR BL BR FC", "FL FR FC BL BR")]
    [InlineData(6, "FL FR BL BR FC LFE", "FL FR FC LFE BL BR")]
    [InlineData(7, "FL FR SL SR BC FC LFE", "FL FR FC LFE BC SL SR")]
    [InlineData(8, "FL FR SL SR BL BR FC LFE", "FL FR FC LFE BL BR SL SR")]
    public void ToWaveOrder_MapsVlcOrderToWindowsOrder(int channels, string amem, string wave)
    {
        var amemRoles = amem.Split(' ');
        var waveRoles = wave.Split(' ');
        // Windows' order is also what the upmix layout (speaker roles per slot) uses.
        Assert.Equal(waveRoles, UpmixSampleProvider.LayoutFor(channels).Select(r => r.ToString()));

        const int frames = 3;
        var block = new short[frames * channels];
        for (var f = 0; f < frames; f++)
            for (var c = 0; c < channels; c++)
                block[f * channels + c] = (short)((f + 1) * 100 + RoleId(amemRoles[c]));

        MultichannelLayout.ToWaveOrder(block, channels);

        for (var f = 0; f < frames; f++)
            for (var c = 0; c < channels; c++)
                Assert.Equal((short)((f + 1) * 100 + RoleId(waveRoles[c])), block[f * channels + c]);
    }

    private static int RoleId(string role) => Array.IndexOf(new[] { "FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR", "BC" }, role);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void ToWaveOrder_LeavesStereoMonoAndQuadAlone(int channels)
    {
        var block = Enumerable.Range(0, channels * 4).Select(i => (short)i).ToArray();
        var copy = block.ToArray();
        MultichannelLayout.ToWaveOrder(block, channels);
        Assert.Equal(copy, block);
    }

    [Theory]
    [InlineData(4, 0x33)]
    [InlineData(5, 0x37)]
    [InlineData(6, 0x3F)]
    [InlineData(7, 0x70F)]
    [InlineData(8, 0x63F)]
    public void StandardMask_IsWindowsDefaultLayout(int channels, int mask)
    {
        Assert.Equal(mask, MultichannelLayout.StandardMask(channels));
        Assert.Equal(channels, System.Numerics.BitOperations.PopCount((uint)mask));
    }

    [Fact]
    public void MaskFor_KeepsASideSurround51Device_AndOtherwiseDescribesOurLayout()
    {
        Assert.Equal(0x60F, MultichannelLayout.MaskFor(6, 0x60F));
        Assert.Equal(0x3F, MultichannelLayout.MaskFor(6, 0x3F));
        Assert.Equal(0x3F, MultichannelLayout.MaskFor(6, 0));
        Assert.Equal(0x63F, MultichannelLayout.MaskFor(8, 0xFF));
    }

    // ── Which channel count a sink opens at ──

    [Theory]
    [InlineData(2, true, false, 2)]
    [InlineData(6, false, false, 2)]  // setting off: stereo, LibVLC downmixes (as before)
    [InlineData(6, true, false, 6)]
    [InlineData(8, true, false, 8)]
    [InlineData(6, true, true, 2)]    // Headphone surround wins: it needs a stereo output
    [InlineData(3, true, false, 2)]   // 2.1 is not rendered discretely
    [InlineData(1, true, false, 1)]
    [InlineData(12, true, false, 2)]
    public void SharedChannels_FollowsSettingDeviceAndHeadphoneSurround(int device, bool multichannel, bool headphones, int expected) =>
        Assert.Equal(expected, MultichannelLayout.SharedChannels(device, multichannel, headphones));

    [Theory]
    [InlineData(2, 2)]
    [InlineData(6, 6)]
    [InlineData(8, 8)]
    [InlineData(3, 2)]
    [InlineData(1, 2)]
    public void EngineFormat_KeepsASurroundDeviceLayout_AndIsStereoOtherwise(int deviceChannels, int expected)
    {
        var (rate, channels) = GaplessSink.EngineFormat(48000, deviceChannels);
        Assert.Equal(48000, rate);
        Assert.Equal(expected, channels);
        Assert.Equal(2, GaplessSink.EngineFormat(48000).Channels);
    }

    // ── The masked output format ──

    private sealed class ConstantFrames : ISampleProvider
    {
        private readonly float[] _frame;
        public ConstantFrames(params float[] frame) { _frame = frame; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, frame.Length); }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++) buffer[offset + i] = _frame[i % _frame.Length];
            return count;
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void MaskedWaveProvider_IsExtensibleWithTheMask_AndMatchesNAudiosConversion(int bits)
    {
        var frame = new[] { 0.5f, -0.25f, 1.5f, -1.5f, 12345f / 32768f, -1f };
        var masked = new MultichannelLayout.MaskedWaveProvider(new ConstantFrames(frame), bits, 0x60F);
        var ext = Assert.IsType<WaveFormatExtensible>(masked.WaveFormat);
        Assert.Equal(6, ext.Channels);
        Assert.Equal(bits, ext.BitsPerSample);
        Assert.Equal(0x60F, MultichannelLayout.MaskOf(ext));

        IWaveProvider reference = bits switch
        {
            16 => new SampleToWaveProvider16(new ConstantFrames(frame)),
            24 => new SampleToWaveProvider24(new ConstantFrames(frame)),
            _ => new SampleToWaveProvider(new ConstantFrames(frame)),
        };
        var bytes = 6 * bits / 8 * 4;
        var a = new byte[bytes];
        var b = new byte[bytes];
        Assert.Equal(bytes, masked.Read(a, 0, bytes));
        Assert.Equal(bytes, reference.Read(b, 0, bytes));
        Assert.Equal(b, a);
    }

    // ── Upmix of a stereo input on a multichannel engine ──

    [Theory]
    [InlineData(UpmixMode.Duplicate)]
    [InlineData(UpmixMode.Surround)]
    public void SpreadFrontsInPlace_KeepsFrontsBitExact_AndMatchesTheStereoUpmix(UpmixMode mode)
    {
        const short l = 20000, r = -8000;
        var block = new short[6 * 64];
        for (var f = 0; f < 64; f++) { block[f * 6] = l; block[f * 6 + 1] = r; }

        new UpmixSampleProvider.Spreader(48000, 6, mode).SpreadFrontsInPlace(block);

        var reference = new UpmixSampleProvider(new ConstantFrames(l / 32768f, r / 32768f), 6, mode);
        var expected = new float[6 * 64];
        reference.Read(expected, 0, expected.Length);
        for (var f = 0; f < 64; f++)
        {
            Assert.Equal(l, block[f * 6]);
            Assert.Equal(r, block[f * 6 + 1]);
            for (var c = 2; c < 6; c++)
                Assert.InRange(block[f * 6 + c] / 32768f - expected[f * 6 + c], -1.5f / 32768f, 1.5f / 32768f);
        }
        Assert.NotEqual(0, block[6 * 63 + UpmixSampleProvider.SlotOf(6, "FC")]);
    }

    // ── The engine at 6 channels ──

    [Fact]
    public void SpliceEngine_SixChannels_KeepsEveryChannel_ThroughUnderrunAndSpeed()
    {
        // The declick frames were sized for stereo: a 5.1 underrun ramp indexed past them.
        var provider = new GaplessSpliceProvider(48000, 6, startThresholdMs: 200, startFadeMs: 5);
        Assert.Equal(6, provider.WaveFormat.Channels);
        var seg = new GaplessTrackSegment(48000, 6, source: null, capacitySeconds: 20);
        Assert.Equal(6, seg.Channels);
        provider.Enqueue(seg);
        short[] Block(int frames)
        {
            var b = new short[frames * 6];
            for (var i = 0; i < b.Length; i++) b[i] = (short)(1000 * (i % 6 + 1));
            return b;
        }
        seg.Write(Block(48000));
        var buf = new float[960 * 6];
        void CheckChannels()
        {
            for (var f = 0; f < 960; f++)
                for (var c = 0; c < 6; c++)
                    Assert.Equal(1000f * (c + 1) / 32768f, buf[f * 6 + c], 4);
        }

        for (var r = 0; r < 5; r++) Assert.Equal(buf.Length, provider.Read(buf, 0, buf.Length));
        CheckChannels();
        for (var r = 0; r < 60; r++) provider.Read(buf, 0, buf.Length); // run dry: underrun declick
        seg.Write(Block(48000));
        for (var r = 0; r < 20; r++) Assert.Equal(buf.Length, provider.Read(buf, 0, buf.Length));
        CheckChannels();

        // Playback speed (WSOLA) used to treat the stream as stereo frames.
        provider.PlaybackRate = 1.5;
        seg.Write(Block(48000 * 3));
        for (var r = 0; r < 30; r++) Assert.Equal(buf.Length, provider.Read(buf, 0, buf.Length));
        CheckChannels();
    }

    // ── Settings ──

    private SettingsViewModel NewVm() => new(new PersistenceService(_root), new FakeLibraryService(), new FakeHistoryLog());

    [Fact]
    public async Task FreshInstall_IsOff_AndTheLaunchReadSeesTheSave()
    {
        Assert.False(new AppSettings().MultichannelOutput);
        Assert.False(PersistenceService.ReadBoolSettingAtLaunch(_root, nameof(AppSettings.MultichannelOutput)));
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings { MultichannelOutput = true });
        Assert.True(PersistenceService.ReadBoolSettingAtLaunch(_root, nameof(AppSettings.MultichannelOutput)));
    }

    [AvaloniaFact]
    public async Task Toggle_IsSaved_AndSaysARestartIsNeeded_UnlessHeadphoneSurroundOverridesIt()
    {
        var vm = NewVm();
        vm.SetAudioPlayer(new FakeAudioPlayer { MultichannelOutputActive = false });
        await vm.LoadAsync();
        Assert.False(vm.MultichannelOutput);
        Assert.False(vm.MultichannelOutputNeedsRestart);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.MultichannelOutput = true;
        Assert.Contains(nameof(SettingsViewModel.MultichannelOutputNeedsRestart), raised);
        Assert.True(vm.MultichannelOutputNeedsRestart);
        // Headphone surround on as well: the next launch still runs stereo, nothing to restart for.
        vm.HeadphoneSurround = true;
        Assert.False(vm.MultichannelOutputNeedsRestart);
        vm.HeadphoneSurround = false;
        await vm.SaveAsync();
        Assert.True(PersistenceService.ReadBoolSettingAtLaunch(_root, nameof(AppSettings.MultichannelOutput)));

        var reloaded = NewVm();
        reloaded.SetAudioPlayer(new FakeAudioPlayer { MultichannelOutputActive = true });
        await reloaded.LoadAsync();
        Assert.True(reloaded.MultichannelOutput);
        Assert.False(reloaded.MultichannelOutputNeedsRestart);
        Assert.True(reloaded.GetSettings().MultichannelOutput);
    }

    // ── The real player (LibVLC + gapless engine, silent mode: no audio device) ──

    private static string? FindFfmpeg()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var c = Path.Combine(dir.Trim(), exe); if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    // One sine per channel: FL 300, FR 500, FC 700, LFE 100, BL 1100, BR 1300 Hz.
    private static readonly int[] s_51Freqs = { 300, 500, 700, 100, 1100, 1300 };

    private static string MakeTones(string ffmpeg, string dir, string name, string layout, string[] channels, int[] freqs)
    {
        var path = Path.Combine(dir, name);
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        var args = new List<string> { "-nostdin", "-loglevel", "error", "-y" };
        foreach (var f in freqs) args.AddRange(new[] { "-f", "lavfi", "-i", $"sine=f={f}:d=6:sample_rate=48000" });
        var map = string.Join("|", channels.Select((ch, i) => $"{i}.0-{ch}"));
        args.AddRange(new[] { "-filter_complex", $"join=inputs={channels.Length}:channel_layout={layout}:map={map}",
            "-c:a", "flac", "-sample_fmt", "s16", path });
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        Assert.True(File.Exists(path), "ffmpeg made no test track");
        return path;
    }

    /// <summary>Plays the file through a fresh multichannel player on a (pretend) 5.1 device and
    /// returns the per-channel tone amplitudes the engine rendered (NOCTIS_ENGINE_TAP capture).</summary>
    private double[,] PlayAndMeasure(string path, UpmixMode upmix, int[] freqs)
    {
        var tap = Path.Combine(_root, "engine-tap.wav");
        var previousTap = Environment.GetEnvironmentVariable("NOCTIS_ENGINE_TAP");
        var previousFlag = MultichannelLayout.EnabledAtLaunch;
        var previousUpmix = GaplessSink.UpmixMode;
        Environment.SetEnvironmentVariable("NOCTIS_ENGINE_TAP", tap);
        MultichannelLayout.EnabledAtLaunch = true;
        GaplessSink.MixChannelsOverrideForTests = 6;
        GaplessSink.UpmixMode = upmix;
        try
        {
            using (var player = new VlcAudioPlayer())
            {
                Assert.True(player.MultichannelOutputActive);
                long heard = 0;
                string? error = null;
                player.PositionChanged += (_, p) => Interlocked.Exchange(ref heard, (long)p.TotalMilliseconds);
                player.PlaybackError += (_, e) => error = e;
                player.Play(path);
                var sw = Stopwatch.StartNew();
                while (Interlocked.Read(ref heard) < 3000 && sw.ElapsedMilliseconds < 15000)
                    Thread.Sleep(20);
                Assert.True(Interlocked.Read(ref heard) >= 3000, $"never played (error={error})");
                player.Stop();
                Thread.Sleep(300);
            }

            using var reader = new WaveFileReader(tap);
            Assert.Equal(6, reader.WaveFormat.Channels);
            var rate = reader.WaveFormat.SampleRate;
            var samples = reader.ToSampleProvider();
            var all = new List<float>();
            var chunk = new float[rate * 6];
            int n;
            while ((n = samples.Read(chunk, 0, chunk.Length)) > 0) all.AddRange(chunk.Take(n));
            var frames = all.Count / 6;
            // The tap starts with idle silence: measure the 2 s after the first sound.
            var start = Enumerable.Range(0, frames).FirstOrDefault(f => Math.Abs(all[f * 6]) > 0.01) + rate / 2;
            var end = Math.Min(frames, start + rate * 2);
            Assert.True(end - start > rate, $"tap holds only {frames} frames");
            var result = new double[6, freqs.Length];
            for (var c = 0; c < 6; c++)
                for (var k = 0; k < freqs.Length; k++)
                {
                    double w = 2 * Math.PI * freqs[k] / rate, coeff = 2 * Math.Cos(w), s1 = 0, s2 = 0;
                    for (var f = start; f < end; f++)
                    {
                        var s0 = all[f * 6 + c] + coeff * s1 - s2;
                        s2 = s1;
                        s1 = s0;
                    }
                    result[c, k] = 2 * Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - coeff * s1 * s2)) / (end - start);
                }
            for (var c = 0; c < 6; c++)
                _o.WriteLine($"ch{c}: " + string.Join(" ", freqs.Select((f, k) => $"{f}Hz={result[c, k]:0.000}")));
            return result;
        }
        finally
        {
            GaplessSink.MixChannelsOverrideForTests = null;
            GaplessSink.UpmixMode = previousUpmix;
            MultichannelLayout.EnabledAtLaunch = previousFlag;
            Environment.SetEnvironmentVariable("NOCTIS_ENGINE_TAP", previousTap);
        }
    }

    [Fact]
    public void RealPlayer_51Flac_ReachesEachSpeakerDiscretely_StereoStaysInFront_UpmixFillsIt()
    {
        Assert.SkipUnless(NullWavePlayer.SilentMode, "needs NOCTIS_AOUT=dummy for the test run");
        Assert.SkipUnless(OperatingSystem.IsWindows(), "multichannel output is Windows-only");
        var ffmpeg = FindFfmpeg();
        Assert.SkipWhen(ffmpeg == null, "no ffmpeg on PATH");

        // 5.1: every tone on its own speaker, in Windows' order (FL FR FC LFE BL BR).
        var surround = MakeTones(ffmpeg!, _root, "tones51.flac", "5.1",
            new[] { "FL", "FR", "FC", "LFE", "BL", "BR" }, s_51Freqs);
        var m = PlayAndMeasure(surround, UpmixMode.Off, s_51Freqs);
        for (var c = 0; c < 6; c++)
            for (var k = 0; k < 6; k++)
                if (c == k) Assert.True(m[c, k] > 0.05, $"ch{c} lacks its {s_51Freqs[k]} Hz tone ({m[c, k]:0.000})");
                else Assert.True(m[c, k] < 0.005, $"ch{c} carries {s_51Freqs[k]} Hz ({m[c, k]:0.000})");

        // Stereo, upmix off: fronts only, no fake surround.
        var freqs = new[] { 300, 500 };
        var stereo = MakeTones(ffmpeg!, _root, "tones20.flac", "stereo", new[] { "FL", "FR" }, freqs);
        var s = PlayAndMeasure(stereo, UpmixMode.Off, freqs);
        Assert.True(s[0, 0] > 0.05 && s[1, 1] > 0.05, "stereo fronts missing");
        for (var c = 2; c < 6; c++)
            Assert.True(s[c, 0] < 0.002 && s[c, 1] < 0.002, $"ch{c} not silent with upmix off");

        // Stereo, upmix Duplicate: centre and rears filled from the fronts.
        var d = PlayAndMeasure(stereo, UpmixMode.Duplicate, freqs);
        Assert.True(d[0, 0] > 0.05 && d[1, 1] > 0.05, "fronts changed by the upmix");
        Assert.True(d[2, 0] > 0.01 && d[2, 1] > 0.01, "no centre from the upmix");
        Assert.True(d[4, 0] > 0.03 && d[5, 1] > 0.03, "no rears from the upmix");
    }
}
