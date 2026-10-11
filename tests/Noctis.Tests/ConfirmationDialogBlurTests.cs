using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-10: the shared confirmation (Lyrics Studio's Clear lyrics, Re-sync, and the other
/// confirms) showed the app behind it unblurred under a flat tint. It now sits in the same
/// PillDialogHost as the other pop-ups (blurred backdrop, pill buttons), and its answer still
/// comes back through Confirmed after the animated close.
/// </summary>
public class ConfirmationDialogBlurTests
{
    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static bool PumpUntil(Func<bool> condition, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition()) return true;
            var frame = new DispatcherFrame();
            using (DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromMilliseconds(5), DispatcherPriority.Send))
                Dispatcher.UIThread.PushFrame(frame);
        }
        return condition();
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Confirm_SitsInTheBlurredPillHost_AndAnswers(bool confirm)
    {
        EnsureAppStyles();
        var owner = new Window { Width = 900, Height = 600 };
        owner.Show();
        var dialog = new ConfirmationDialog("Clear the lyrics shown for “G LOCK”?");
        _ = dialog.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();

        var host = Assert.IsType<PillDialogHost>(dialog.Content);
        Assert.True(host.BlurBackdrop);
        var buttons = dialog.GetVisualDescendants().OfType<Button>().ToList();
        var confirmButton = buttons.Single(b => b.Name == "ConfirmButton");
        Assert.Contains("pill-primary", confirmButton.Classes);
        var cancelButton = buttons.Single(b => b.Classes.Contains("pill-secondary"));

        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        (confirm ? confirmButton : cancelButton).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.True(PumpUntil(() => closed), "the dialog closes after its close animation");
        Assert.Equal(confirm, dialog.Confirmed);
        owner.Close();
    }
}
