using System.Diagnostics;
using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.Plugins.LyricMotion;
using Noctis.Plugins.LyricMotion.Motion;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Lyric Motion (by Alfamin, plugin API 1.5): right-to-left detection, the motion's rules, its
/// frame budget (no allocation per frame), the effect through the real host and the lyrics
/// controls, and the Release zip.
/// </summary>
[Collection("LyricsWordEffect")] // the effect slot is app-wide
public class LyricMotionPluginTests : IDisposable
{
    private readonly PluginSandbox _box = new();

    public void Dispose()
    {
        LyricsWordEffect.Set(null);
        _box.Dispose();
    }

    private static LyricsWord W(double progress, double ms = 400, int letters = 5, double lift = 0, bool last = false,
        LyricsSurface surface = LyricsSurface.Page, bool rtl = false, bool held = false)
        => new(progress, ms, lift, held, last, rtl, letters, 46, surface);

    private static Tuning Tune(Handover a = Handover.Smooth, double intensity = 1, bool letters = true, bool glow = true, bool other = true)
        => new() { Animation = a, Intensity = intensity, Letters = letters, Glow = glow, OtherViews = other };

    // ── Right to left ──

    [Fact]
    public void Rtl_APersianSong_IsLaidOutFromTheRight_EvenALineWithAnEnglishWordInIt()
    {
        var lines = new[] { "دلم برات تنگ شده", "baby تو رو می‌خوام", "۱۲۳ ...", "" };
        var rtl = new bool[lines.Length];
        Script.Directions(lines, rtl);
        Assert.Equal(new[] { true, true, false, false }, rtl); // digits and punctuation alone stay as they are
    }

    [Fact]
    public void Rtl_InAnEnglishSong_OnlyMostlyRightToLeftLinesTurn()
    {
        var lines = new[] { "I said salaam سلام to you", "שלום עולם", "Hello again", "Hello there", "and goodbye now" };
        var rtl = new bool[lines.Length];
        Script.Directions(lines, rtl);
        Assert.Equal(new[] { false, true, false, false, false }, rtl);
    }

    [Fact]
    public void Rtl_ArabicHebrewSyriacThaanaAndNko_AreRightToLeft_LatinCyrillicCjkAreNot()
    {
        foreach (var c in new[] { 'ع', 'پ', 'א', 'ܐ', 'ދ', 'ߊ' }) Assert.True(Script.IsRightToLeft(c), c.ToString());
        foreach (var c in new[] { 'a', 'Ж', '日', 'ä', 'ñ' }) Assert.False(Script.IsRightToLeft(c), c.ToString());
    }

    [Fact]
    public void Rtl_Off_LeavesEveryLineAlone()
    {
        var effect = new MotionEffect(new Tuning { RightToLeft = false });
        var rtl = new bool[2];
        effect.GetLineDirections(new[] { "سلام", "שלום" }, rtl);
        Assert.Equal(new[] { false, false }, rtl);
    }

    // ── The motion (ported from Lyric Motion as built into Noctis Plus) ──

    [Fact]
    public void Letters_ComeUpOneAfterAnother_WithTheHighlight_ThenHoldStillUntilTheLineEnds()
    {
        var t = Tune(Handover.Crisp);
        var w = W(0.5, ms: 500, letters: 4);
        Assert.Equal(LyricsWordMode.Letters, LetterMotion.Mode(t, w));
        var mid = Enumerable.Range(0, 4).Select(i => LetterMotion.Pose(t, w, i).Scale).ToArray();
        Assert.True(mid[0] > mid[3], "the first letters are up before the last");
        // Crisp: every letter is up exactly when the word is over.
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(1, LetterMotion.Rise(t, 1.0, 500, i, 4), 6));
        // Then nothing moves: not while the next word is sung, not when Noctis stops redrawing the
        // word (progress 3, or at once after a pause). A word relaxing then dropped in one frame.
        for (var i = 0; i < 4; i++)
        {
            var done = LetterMotion.Pose(t, W(1.0, ms: 500, letters: 4, lift: 1), i);
            Assert.Equal(done, LetterMotion.Pose(t, W(1.7, ms: 500, letters: 4, lift: 1), i));
            Assert.Equal(done, LetterMotion.Pose(t, W(2.49, ms: 500, letters: 4, lift: 1), i));
            Assert.Equal(done, LetterMotion.Pose(t, W(3, ms: 500, letters: 4, lift: 1), i));
            // Once the line is over the word comes down with Noctis's float, to rest.
            Assert.Equal(1, LetterMotion.Pose(t, W(3, ms: 500, letters: 4, lift: 0), i).Scale, 6);
            Assert.Equal(1, LetterMotion.Pose(t, W(-2, ms: 500, letters: 4), i).Scale, 6);
        }
    }

    [Fact]
    public void Letters_FloatUpOneAfterAnother_AsTheHighlightReachesThem_ThenSitLevel()
    {
        var t = Tune(Handover.Flowing);
        var reach = 2.0 / 46;
        var w = W(0.5, ms: 500, letters: 6, lift: 1);
        Assert.Equal(LyricsWordMode.Letters, LetterMotion.Mode(t, w));
        var first = LetterMotion.Pose(t, w, 0).OffsetYEm;
        var last = LetterMotion.Pose(t, w, 5).OffsetYEm;
        Assert.True(first < last - 0.02, $"the first letter is higher ({first}) than the last ({last})");
        Assert.All(Enumerable.Range(0, 6), i => Assert.Equal(-reach, LetterMotion.Pose(t, W(1 + LetterMotion.RunOn, ms: 120, letters: 6, lift: 1), i).OffsetYEm, 6));
        // A word Noctis gives no length is sung over 320 ms, as in Noctis Plus.
        Assert.Equal(320, LetterMotion.Duration(0));
    }

    [Fact]
    public void Smooth_LetsTheLastLettersFinishAMomentIntoTheNextWord_Crisp_DoesNot_ALinesLastWordFinishesOnItsNote()
    {
        var crisp = LetterMotion.Rise(Tune(Handover.Crisp), 1.0, 500, 4, 5);
        var smooth = LetterMotion.Rise(Tune(Handover.Smooth), 1.0, 500, 4, 5);
        Assert.Equal(1, crisp, 6);
        Assert.InRange(smooth, 0.05, 0.99);
        // Noctis stops redrawing a word two words on, or a line's last word once the line is over:
        // every letter is up by then, quick words included, so nothing jumps.
        foreach (var a in new[] { Handover.Crisp, Handover.Smooth, Handover.Flowing })
        foreach (var ms in new[] { 80.0, 150, 500, 2500 })
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(1, LetterMotion.Rise(Tune(a), 1 + LetterMotion.RunOn, ms, i, 6), 6);
            Assert.Equal(1, LetterMotion.WholeRise(Tune(a), 1 + LetterMotion.RunOn, ms), 6);
            Assert.Equal(1, LetterMotion.Rise(Tune(a), 1.0, ms, i, 6, lastInLine: true), 6);
            Assert.Equal(1, LetterMotion.WholeRise(Tune(a), 1.0, ms, lastInLine: true), 6);
            var done = LetterMotion.Pose(Tune(a), W(1 + LetterMotion.RunOn, ms, 6, lift: 1), i);
            Assert.Equal(done, LetterMotion.Pose(Tune(a), W(3, ms, 6, lift: 1), i));
            var lastDone = LetterMotion.Pose(Tune(a), W(1.0, ms, 6, lift: 1, last: true), i);
            Assert.Equal(lastDone, LetterMotion.Pose(Tune(a), W(3, ms, 6, lift: 1, last: true), i));
        }
    }

    [Fact]
    public void ALetter_NeverComesUpFasterThanTheEyeCanFollow_InSmoothAndFlowing()
    {
        // A 300 ms word: its letters still take at least 0.12 s (Smooth) / 0.2 s (Flowing) to come up.
        foreach (var (a, floor) in new[] { (Handover.Smooth, Tuning.SmoothRiseSec), (Handover.Flowing, Tuning.LetterRiseSec) })
        {
            var t = Tune(a);
            // Half its minimum rise after the first letter starts (progress 0): at most half way.
            var p = floor / 2 / 0.3;
            Assert.InRange(LetterMotion.Rise(t, p, 300, 0, 4), 0, 0.5 + 1e-9);
        }
        Assert.Equal(1, LetterMotion.Rise(Tune(Handover.Crisp), 1.0, 100, 0, 4), 6); // crisp: exactly with the fill
    }

    [Fact]
    public void Motion_IsContinuous_NothingPopsBetweenFrames()
    {
        foreach (var a in new[] { Handover.Crisp, Handover.Smooth, Handover.Flowing })
        foreach (var ms in new[] { 100.0, 200, 500, 1200, 3000 })
        foreach (var last in new[] { false, true })
        {
            var t = Tune(a, 1.4);
            for (var i = 0; i < 6; i++)
            {
                var before = LetterMotion.Pose(t, W(-1.5, ms, 6, lift: 1, last: last), i);
                for (var p = -1.5; p <= 2.5; p += 1 / 240.0)
                {
                    var now = LetterMotion.Pose(t, W(p, ms, 6, lift: 1, last: last), i);
                    Assert.True(Math.Abs(now.Scale - before.Scale) < 0.01, $"{a} {ms} ms letter {i} at {p:0.000}: {before.Scale} -> {now.Scale}");
                    Assert.True(Math.Abs(now.OffsetYEm - before.OffsetYEm) < 0.01, $"{a} {ms} ms letter {i} at {p:0.000}: y {before.OffsetYEm} -> {now.OffsetYEm}");
                    Assert.True(Math.Abs(now.OffsetXEm - before.OffsetXEm) < 0.01, $"{a} {ms} ms letter {i} at {p:0.000}: x {before.OffsetXEm} -> {now.OffsetXEm}");
                    Assert.True(Math.Abs(now.Glow - before.Glow) < 0.1, $"{a} {ms} ms letter {i} at {p:0.000}: glow {before.Glow} -> {now.Glow}");
                    before = now;
                }
            }
        }
    }

    [Fact]
    public void LongNotes_GrowMore_AndGlow_OrdinaryWordsDoNotGlow()
    {
        var t = Tune();
        var quick = LetterMotion.Pose(t, W(0.6, ms: 400), 1);
        var held = LetterMotion.Pose(t, W(0.6, ms: 2500), 1);
        Assert.True(held.Scale > quick.Scale + 0.01);
        Assert.Equal(0, quick.Glow);
        Assert.True(held.Glow > 0.5);
        Assert.Equal(0, LetterMotion.Pose(Tune(glow: false), W(0.6, ms: 2500), 1).Glow);
        // Expressive grows more than Subtle.
        Assert.True(LetterMotion.Pose(Tune(intensity: 1.4), W(0.6, 2500), 1).Scale > LetterMotion.Pose(Tune(intensity: 0.7), W(0.6, 2500), 1).Scale);
    }

    [Fact]
    public void GrownLetters_MakeRoomForThemselves_TheWordStayingCentred()
    {
        var t = Tune(Handover.Crisp);
        var w = W(1.0, ms: 2500, letters: 5);
        var poses = Enumerable.Range(0, 5).Select(i => LetterMotion.Pose(t, w, i)).ToArray();
        Assert.True(poses[0].OffsetXEm < 0 && poses[4].OffsetXEm > 0);
        Assert.Equal(0, poses[2].OffsetXEm, 6);
        Assert.Equal(0, poses.Sum(p => p.OffsetXEm), 6);
        // Each pushed by what the letters before it grew: neighbours a grown letter's width apart.
        var g = poses[0].Scale - 1;
        Assert.Equal(g * LetterMotion.LetterWidthEm, poses[1].OffsetXEm - poses[0].OffsetXEm, 6);
    }

    [Fact]
    public void QuickWords_DoNotGrow_SoARapVerseDoesNotFlicker()
    {
        var t = Tune();
        Assert.Equal(LyricsWordMode.Whole, LetterMotion.Mode(t, W(0.5, ms: 120)));
        Assert.Equal(1, LetterMotion.Pose(t, W(0.5, ms: 120), 0).Scale, 6);
    }

    [Fact]
    public void ASungWord_FloatsByWholePixels_StaysUpUntilItsLineEnds_AndComesDownWithNoctissFloat()
    {
        var t = Tune();
        var reach = 2.0 / 46; // 0.05 em of 46 px, rounded to whole pixels
        Assert.Equal(-reach, LetterMotion.Pose(t, W(3, ms: 120, lift: 1), 0).OffsetYEm, 6);
        Assert.Equal(-reach / 2, LetterMotion.Pose(t, W(3, ms: 120, lift: 0.5), 0).OffsetYEm, 6); // the line is over
        Assert.Equal(-reach, LetterMotion.Pose(t, W(1.5, ms: 120, lift: 1), 0).OffsetYEm, 6);
        Assert.Equal(0, LetterMotion.Pose(t, W(-2, lift: 0), 0).OffsetYEm, 6);
        // Noctis's own word float switched off (its lift stays 0): no float from the plugin either.
        Assert.Equal(0, LetterMotion.Pose(t, W(1.5, ms: 120, lift: 0), 0).OffsetYEm, 6);
    }

    [Fact]
    public void ALinesLastWord_KeepsItsLongNoteUntilTheLineEnds_ThenSettlesWithTheFloat()
    {
        var t = Tune();
        var held = LetterMotion.Pose(t, W(1.0, 2500, lift: 1, last: true), 2).Scale;
        Assert.True(held > 1.05);
        Assert.Equal(held, LetterMotion.Pose(t, W(3, 2500, lift: 1, last: true), 2).Scale, 6);
        Assert.Equal(1 + (held - 1) / 2, LetterMotion.Pose(t, W(3, 2500, lift: 0.5, last: true), 2).Scale, 6);
        Assert.Equal(1, LetterMotion.Pose(t, W(3, 2500, lift: 0, last: true), 2).Scale, 6);
    }

    [Fact]
    public void Off_GivesNoctissOwnMotion_AndTheSidePanelCanBeLeftAlone()
    {
        Assert.Equal(LyricsWordMode.Native, LetterMotion.Mode(Tune(Handover.Off), W(0.5)));
        Assert.Equal(LyricsWordMode.Native, LetterMotion.Mode(Tune(other: false), W(0.5, surface: LyricsSurface.SidePanel)));
        Assert.Equal(LyricsWordMode.Native, LetterMotion.Mode(Tune(other: false), W(0.5, surface: LyricsSurface.MiniPlayer)));
        Assert.Equal(LyricsWordMode.Letters, LetterMotion.Mode(Tune(other: false), W(0.5)));
        Assert.Equal(LyricsWordMode.Whole, LetterMotion.Mode(Tune(letters: false), W(0.5)));
        Assert.Equal(LyricsWordMode.Whole, LetterMotion.Mode(Tune(), W(0.5, rtl: true))); // joined letters
        Assert.Equal(LyricsWordMode.Whole, LetterMotion.Mode(Tune(), W(0.5, letters: 20)));
        Assert.Equal(LyricsWordMode.Letters, LetterMotion.Mode(Tune(Handover.Flowing), W(0.5, ms: 100))); // flowing: every word in letters
    }

    [Fact]
    public void Settings_AreReadWithTheirDefaults()
    {
        var t = Tuning.From(null);
        Assert.Equal((Handover.Smooth, 1.0, 600.0), (t.Animation, t.Intensity, t.HeldMs));
    }

    // ── Frame budget ──

    /// <summary>
    /// What one frame of a busy line costs the plugin: 12 words of 8 letters (more than any line has
    /// moving at once) asked for every letter's pose, for 2,000 frames. No allocation at all, and well
    /// under a tenth of a millisecond per frame.
    /// </summary>
    [Fact]
    public void AFrame_AllocatesNothing_AndStaysWellWithinBudget()
    {
        ILyricsEffect effect = new MotionEffect(Tune(Handover.Flowing, 1.4));
        var words = new LyricsWord[12];
        double sum = 0;
        void Frame(int f)
        {
            for (var w = 0; w < words.Length; w++)
            {
                var word = W(f / 60.0 - w * 0.4, 300 + w * 200, 8, Math.Min(1, f / 90.0));
                if (effect.GetMode(word) == LyricsWordMode.Native) continue;
                for (var i = 0; i < 8; i++)
                {
                    var p = effect.GetPose(word, i);
                    sum += p.Scale + p.OffsetXEm + p.OffsetYEm + p.Glow;
                }
            }
        }
        for (var f = 0; f < 300; f++) Frame(f); // JIT
        var sw = new Stopwatch();
        var before = GC.GetAllocatedBytesForCurrentThread();
        sw.Start();
        const int frames = 2000;
        for (var f = 0; f < frames; f++) Frame(f % 600);
        sw.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(sw.Elapsed.TotalMilliseconds / frames < 0.1, $"{sw.Elapsed.TotalMilliseconds / frames:0.0000} ms per frame");
        Assert.True(double.IsFinite(sum));
    }

    /// <summary>
    /// The same through Noctis's word control: 40 words of a sung line moving under the effect for
    /// 240 frames, each frame a sweep step and a draw. Text layouts are made once per word (cached),
    /// never per frame.
    /// </summary>
    [AvaloniaFact]
    public void ThroughTheWordControl_ALineOfFortyWords_StaysInTheFrameBudget()
    {
        LyricsWordEffect.Set(new FakeHostEffect(new MotionEffect(Tune(Handover.Smooth, 1.4))));
        var panel = new WrapPanel { Width = 900 };
        var words = Enumerable.Range(0, 40).Select(i => new KaraokeWordText
        {
            Text = "word" + (i % 7) + " ", FontSize = 46, HeldDurationMs = 300 + i % 5 * 400, Progress = -2,
        }).ToList();
        foreach (var w in words) panel.Children.Add(w);
        var window = new Window { Content = panel, Width = 900, Height = 600 };
        window.Show();
        try
        {
            using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(900, 600));
            bmp.Render(panel);
            Assert.All(words, w => Assert.Equal(WordDrawMode.Letters, w.EffectMode));
            var sw = Stopwatch.StartNew();
            const int frames = 240;
            for (var f = 0; f < frames; f++)
            {
                for (var i = 0; i < words.Count; i++) words[i].Progress = f / 24.0 - i * 0.25;
                bmp.Render(panel);
            }
            var perFrame = sw.Elapsed.TotalMilliseconds / frames;
            Assert.True(perFrame < 16, $"{perFrame:0.00} ms per frame for 40 moving words (software raster included)");
        }
        finally
        {
            window.Close();
            LyricsWordEffect.Set(null);
        }
    }

    /// <summary>The host's wrapper (PluginHost.PluginWordEffect) without a host: the same mapping.</summary>
    internal sealed class FakeHostEffect(ILyricsEffect effect) : IWordEffect
    {
        private static LyricsWord Word(in WordPoseInput w) => new(w.Progress, w.DurationMs, w.Lift, w.IsHeld, w.IsLastInLine,
            w.IsRightToLeft, w.LetterCount, w.FontSize, (LyricsSurface)(int)w.Surface);
        public WordDrawMode Mode(in WordPoseInput word) => (WordDrawMode)(int)effect.GetMode(Word(word));
        public LetterPose Pose(in WordPoseInput word, int letter)
        {
            var p = effect.GetPose(Word(word), letter);
            return new LetterPose(p.Scale, p.OffsetXEm, p.OffsetYEm, p.Glow);
        }
        public void LineDirections(IReadOnlyList<string> lines, Span<bool> rightToLeft) => effect.GetLineDirections(lines, rightToLeft);
    }

    // ── Through the host ──

    [AvaloniaFact]
    public void ThePlugin_RegistersItsEffect_ReRegistersOnASettingChange_AndWithdrawsItOnDisable()
    {
        var manifest = PluginManifest.Parse(File.ReadAllText(PluginSandbox.RepoFile("plugins/Noctis.Plugins.LyricMotion/plugin.json")));
        _box.Approve((manifest.Id, Array.Empty<string>()));
        var host = _box.NewHost(appVersion: "1.6.6");
        var plugin = new LyricMotionPlugin();
        var dir = Path.Combine(_box.PluginsDir, manifest.Id);
        Directory.CreateDirectory(dir);
        var loaded = host.AddInProcess(dir, manifest, () => plugin);
        Assert.Equal(PluginStatus.Running, loaded.Status);
        Assert.Contains("Lyrics motion: Lyric Motion", loaded.Extensions);
        var first = LyricsWordEffect.Current;
        Assert.NotNull(first);

        var setting = loaded.SettingItems.Single(s => s.Key == "animation");
        setting.SelectedChoice = "Crisp";
        Assert.Equal(Handover.Crisp, plugin.Effect!.Tuning.Animation);
        Assert.NotSame(first, LyricsWordEffect.Current);
        Assert.Single(host.WordEffects);

        host.SetEnabled(loaded, false);
        Assert.Null(LyricsWordEffect.Current); // Noctis's own motion is back
        Assert.Empty(host.WordEffects);
    }

    [AvaloniaFact]
    public void AnEffectThatThrows_IsWithdrawn_AndItsPluginStopped()
    {
        _box.Approve(("dev.test.plugin", Array.Empty<string>()));
        var host = _box.NewHost(appVersion: "1.6.6");
        var script = new ScriptedPlugin { OnInit = h => h.RegisterLyricsEffect(new Throwing()) };
        var loaded = _box.AddInProcess(host, script, PluginSandbox.Manifest(apiVersion: "1.5"));
        Assert.NotNull(LyricsWordEffect.Current);
        var word = new KaraokeWordText { Text = "hello", FontSize = 40, Progress = 0.5 };
        var window = new Window { Content = word };
        window.Show();
        try
        {
            using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(200, 100));
            bmp.Render(word); // the effect throws while the word is drawn
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Null(LyricsWordEffect.Current);
            Assert.Equal(PluginStatus.Failed, loaded.Status);
            bmp.Render(word); // drawn by Noctis again, no exception
        }
        finally { window.Close(); }
    }

    private sealed class Throwing : ILyricsEffect
    {
        public string Name => "Throws";
        public void GetLineDirections(IReadOnlyList<string> lines, Span<bool> rightToLeft) { }
        public LyricsWordMode GetMode(in LyricsWord word) => LyricsWordMode.Letters;
        public LyricsLetterPose GetPose(in LyricsWord word, int letter) => throw new InvalidOperationException("boom");
    }

    // ── The lyrics controls ──

    [AvaloniaFact]
    public void AnArabicHeldWord_IsDrawnInOnePiece_SoItsLettersStayJoined()
    {
        // Noctis drew a held word letter by letter (one text layout per letter): an Arabic word then
        // came out as isolated letter forms. Joining scripts are drawn whole, with or without an effect.
        Assert.True(KaraokeWordText.HasJoiningScript("سلام"));
        Assert.False(KaraokeWordText.HasJoiningScript("hello"));
        Assert.False(KaraokeWordText.HasJoiningScript("שלום")); // Hebrew letters do not join
        var arabic = new KaraokeWordText { Text = "سلام ", FontSize = 40, IsEmphasis = true };
        var latin = new KaraokeWordText { Text = "hello ", FontSize = 40, IsEmphasis = true };
        var window = new Window { Content = new StackPanel { Children = { arabic, latin } } };
        window.Show();
        try
        {
            Assert.Equal(1, arabic.PieceCount);
            Assert.Equal(5, latin.PieceCount);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ARightToLeftRow_StartsAtTheRight_AndWrapsLeftwards_WithTheRestingCopyInTheSamePlaces()
    {
        var words = Enumerable.Range(0, 5).Select(i => new WordTiming { Text = "w" + i + " " }).ToList();
        var live = new LyricWrapPanel { RightToLeft = true, Width = 300 };
        foreach (var w in words) live.Children.Add(new Border { Width = 100, Height = 20 });
        var resting = new KaraokeRestingWords { Words = words, RightToLeft = true, Width = 300 };
        var window = new Window { Content = new StackPanel { Children = { live, resting } }, Width = 400 };
        window.Show();
        try
        {
            window.UpdateLayout();
            var xs = live.Children.Select(c => (c.Bounds.X, c.Bounds.Y)).ToList();
            Assert.Equal((200d, 0d), xs[0]); // the first word at the right edge
            Assert.Equal((100d, 0d), xs[1]);
            Assert.Equal((0d, 0d), xs[2]);
            Assert.Equal((200d, 20d), xs[3]); // the next row starts at the right again
            live.RightToLeft = false;
            window.UpdateLayout();
            Assert.Equal(0, live.Children[0].Bounds.X);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void ALine_PassesItsDirectionToEveryWordLayer()
    {
        var line = new LyricLine
        {
            Text = "سلام دنیا",
            Words = new[] { new WordTiming { Text = "سلام " }, new WordTiming { Text = "دنیا" } },
            BackgroundWords = new[] { new WordTiming { Text = "آه" } },
        };
        line.SetRightToLeft(true);
        Assert.True(line.IsRightToLeft);
        Assert.All(line.Words!.Concat(line.BackgroundWords!), w => Assert.True(w.IsRightToLeft));
        line.SetRightToLeft(false);
        Assert.All(line.Words!, w => Assert.False(w.IsRightToLeft));
    }

    [Fact]
    public void ALineAlignment_FollowsItsDirection()
    {
        var conv = new Noctis.Converters.LyricLineAlignmentConverter();
        object? Align(LyricVoice v, bool rtl) => conv.Convert(new object?[] { v, rtl }, typeof(HorizontalAlignment), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(HorizontalAlignment.Left, Align(LyricVoice.Default, false));
        Assert.Equal(HorizontalAlignment.Right, Align(LyricVoice.Default, true));
        Assert.Equal(HorizontalAlignment.Right, Align(LyricVoice.Voice2, false));
        Assert.Equal(HorizontalAlignment.Left, Align(LyricVoice.Voice2, true));
        Assert.Equal(HorizontalAlignment.Center, Align(LyricVoice.Group, true));
    }

    // ── Package ──

    [AvaloniaFact]
    public void ReleaseZip_InstallsFromFile_AndRuns()
    {
        var zip = PluginSandbox.RepoFile("plugins/Noctis.Plugins.LyricMotion/bin/Release/Noctis.Plugins.LyricMotion-1.1.0.zip");
        if (!File.Exists(zip)) Assert.Skip("build it first: dotnet build plugins/Noctis.Plugins.LyricMotion -c Release");
        using (var archive = ZipFile.OpenRead(zip))
        {
            var names = archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { "LICENSE", "Noctis.Plugins.LyricMotion.deps.json", "Noctis.Plugins.LyricMotion.dll", "icon.png", "plugin.json" }, names);
            using var license = new StreamReader(archive.GetEntry("LICENSE")!.Open());
            Assert.Contains("Copyright (c) 2026 Alfamin", license.ReadToEnd());
        }

        using var box = new PluginSandbox();
        box.Settings.CommunityPluginsEnabled = true;
        var host = box.NewHost(appVersion: "1.6.6");
        host.LoadAll();
        var result = host.InstallPackage(zip, allowUpdate: false);
        Assert.Equal(PluginInstallOutcome.Installed, result.Outcome);
        var plugin = result.Plugin!;
        Assert.Equal(("Lyric Motion", "1.1.0", "Alfamin"), (plugin.Name, plugin.Version, plugin.Manifest?.Author));
        Assert.Equal(LyricMotionPlugin.About, plugin.Manifest!.Description); // no "By Alfamin." (the card says by X)
        Assert.DoesNotContain("Alfamin", plugin.Manifest.Description);
        Assert.Empty(plugin.Manifest.Permissions);
        Assert.Equal(7, plugin.SettingItems.Count);

        host.SetEnabled(plugin, true);
        try
        {
            Assert.Equal(PluginStatus.Running, plugin.Status);
            Assert.NotNull(LyricsWordEffect.Current);
        }
        finally
        {
            host.SetEnabled(plugin, false);
            Assert.Null(LyricsWordEffect.Current);
            Assert.True(host.Remove(plugin, deleteData: true));
        }
    }

    [Fact]
    public void UserFacingText_HasNoEmDashes()
    {
        foreach (var f in Directory.GetFiles(PluginSandbox.RepoFile("plugins/Noctis.Plugins.LyricMotion"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".json")) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                                 && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
            Assert.DoesNotContain("—", File.ReadAllText(f));
    }
}
