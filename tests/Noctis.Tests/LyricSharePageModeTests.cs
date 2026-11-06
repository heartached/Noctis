using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The "Lyrics Page" share style in the share dialog's view-model: what each line brings
/// from the lyrics page, the layout choice and its switching rules, and range picking.
/// </summary>
public class LyricSharePageModeTests
{
    internal static bool PumpUntil(Func<bool> condition, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    private static Track Song(string title = "TUTU", string artist = "6ix9ine", string? filePath = null) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = artist, FilePath = filePath ?? string.Empty,
    };

    [AvaloniaFact]
    public void Source_lines_bring_voice_adlibs_and_original_text()
    {
        var src = new[] { new LyricLine { Text = "a b", Timestamp = TimeSpan.FromSeconds(1), Voice = LyricVoice.Voice2,
            BackgroundEndTimestamp = TimeSpan.FromSeconds(3),
            BackgroundWords = new[] { new WordTiming { Text = "ooh", Start = TimeSpan.FromSeconds(2) } } } };
        var vm = new LyricShareViewModel(Song(), new[] { "a b" }, new TimeSpan?[] { TimeSpan.FromSeconds(1) }, null, sources: src);
        try
        {
            vm.Lines[0].Text = "edited";
            Assert.Equal("a b", vm.Lines[0].OriginalText);
            Assert.Equal(LyricVoice.Voice2, vm.Lines[0].Voice);
            Assert.Equal("ooh", vm.Lines[0].BackgroundWords![0].Text);
        }
        finally { vm.Detach(); }
    }
}
