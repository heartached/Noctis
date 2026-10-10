using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Localization;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: "add the blur to the Remove from library". The dialog now sits in
/// PillDialogHost like the other pop-ups. Pinned on the way: Esc answers Cancel (it did
/// nothing), the first answer sticks (a second click during the close used to overwrite
/// Choice, because each handler set it before the closing guard), and the texts come from
/// the translations (the constructor used to overwrite them with English literals).
/// </summary>
public class RemoveFromLibraryDialogTests : IDisposable
{
    public void Dispose() => Loc.Instance.SetCulture("en");

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

    private static (RemoveFromLibraryDialog Dialog, PillDialogHost Host) Open(int count)
    {
        var dialog = new RemoveFromLibraryDialog(count) { Width = 900, Height = 600 };
        dialog.Show();
        var host = dialog.GetVisualDescendants().OfType<PillDialogHost>().Single();
        host.BlurBackdrop = false;
        Assert.True(PumpUntil(() => host.IsOpenStarted && host.Card!.Opacity > 0.99), "never opened");
        return (dialog, host);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void Dialog_IsHostedInThePillPopUp()
    {
        var (dialog, host) = Open(1);
        Assert.Same(host, dialog.Content);
        dialog.Close();
        PumpUntil(() => !dialog.IsVisible);
    }

    [AvaloniaFact]
    public void Escape_AnswersCancel_AndCloses()
    {
        var (dialog, _) = Open(2);
        var closed = false;
        dialog.Closed += (_, _) => closed = true;

        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.True(PumpUntil(() => closed), "Esc did not close the dialog");
        Assert.Equal(RemoveFromLibraryChoice.Cancel, dialog.Choice);
    }

    [AvaloniaFact]
    public void FirstAnswer_Sticks_WhenAnotherButtonIsClickedDuringTheClose()
    {
        var (dialog, host) = Open(1);
        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        var buttons = dialog.GetVisualDescendants().OfType<Button>().ToList();
        var trash = buttons.Single(b => b.Name == "TrashButton");
        var keep = buttons.Single(b => b.Content as string == Loc.T("RemoveFromLibrary.KeepFiles"));

        Click(trash);
        Assert.True(host.IsClosing);
        Click(keep); // mid-close

        Assert.True(PumpUntil(() => closed), "never closed");
        Assert.Equal(RemoveFromLibraryChoice.Trash, dialog.Choice);
    }

    [AvaloniaFact]
    public void Texts_FollowTheUiLanguage()
    {
        Loc.Instance.SetCulture("es");
        var (dialog, _) = Open(3);
        var trash = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TrashButton");

        var expected = Loc.T(OperatingSystem.IsWindows() ? "RemoveFromLibrary.MoveRecycleBin" : "RemoveFromLibrary.MoveTrash");
        Assert.Equal(expected, trash.Content);
        if (OperatingSystem.IsWindows())
            Assert.NotEqual("Move to Recycle Bin", expected); // Spanish ships this key translated

        dialog.Close();
        PumpUntil(() => !dialog.IsVisible);
    }
}
