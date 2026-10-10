using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 10-08 (found while redoing the playlist pop-ups): while a pill pop-up plays its close
/// animation, a text field's Enter KeyBinding still ran — Create fired a second time. The
/// host's tunnel handler marks KeyDown handled, but key gestures run regardless; nothing in
/// the closing card may act on keys any more.
/// </summary>
public class PillDialogCloseKeysTests
{
    private static bool PumpUntil(Func<bool> condition, int budgetMs = 3000)
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

    [AvaloniaFact]
    public void EnterBinding_InTheClosingCard_DoesNotRunAgain()
    {
        var runs = 0;
        var box = new TextBox { Width = 200 };
        box.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Enter), Command = new RelayCommand(() => runs++) });
        var host = new PillDialogHost { BlurBackdrop = false, Content = new Border { Width = 300, Height = 120, Child = box } };
        var win = new Window { Width = 600, Height = 400, Content = host };
        var closed = 0;
        win.Closed += (_, _) => closed++;
        win.Show();
        try
        {
            Assert.True(PumpUntil(() => host.IsOpenStarted && host.Card!.Opacity > 0.99), "never opened");
            box.Focus();
            PumpUntil(() => false, 30);

            win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.Equal(1, runs); // open: Enter works

            win.Close();           // the close animation starts; the window is still up
            Assert.True(host.IsClosing);
            win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.Equal(1, runs);  // closing: nothing more

            Assert.True(PumpUntil(() => closed > 0, 2000), "never closed");
        }
        finally { if (closed == 0) win.Close(); }
    }
}
