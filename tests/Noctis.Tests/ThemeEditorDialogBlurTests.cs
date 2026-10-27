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
using Noctis.Controls;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-27: Settings › Appearance › New Custom Theme showed the app behind it unblurred
/// under a flat tint, unlike every other pop-up. It now sits in the same PillDialogHost
/// (blurred backdrop, animated card), and the saved theme still comes back through Result
/// after the animated close.
/// </summary>
public class ThemeEditorDialogBlurTests
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
    public void Editor_SitsInTheBlurredPillHost_AndReturnsTheTheme(bool save)
    {
        EnsureAppStyles();
        var owner = new Window { Width = 1100, Height = 760 };
        owner.Show();
        var vm = new ThemeEditorViewModel(null, Array.Empty<string>()) { Name = "Night Drive" };
        var dialog = new ThemeEditorDialog(vm);
        _ = dialog.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();

        var host = Assert.IsType<PillDialogHost>(dialog.Content);
        Assert.True(host.BlurBackdrop);
        // The name box is the pop-ups' filled pill field, like the Metadata editor's (owner 10-27).
        Assert.Contains("pill-field", dialog.FindControl<TextBox>("NameTextBox")!.Classes);

        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        if (save) vm.SaveCommand.Execute(null);
        else vm.CancelCommand.Execute(null);

        Assert.True(PumpUntil(() => closed), "the editor closes after its close animation");
        if (save) Assert.Equal("Night Drive", dialog.Result?.Name);
        else Assert.Null(dialog.Result);
        owner.Close();
    }
}
