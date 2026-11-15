using System.ComponentModel;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Localization;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Every <c>{loc:T}</c> binding subscribes weakly to <see cref="Loc.Instance"/>'s PropertyChanged.
/// Avalonia drops a dead subscriber (its control gone) only while raising that event, and Loc
/// raises it only on a language change, so every discarded localized control left its slot
/// (a WeakReference, its GC handle and a dictionary entry) for the rest of the session: 38 per
/// song change on the Home page, 12,160 after 320 songs (memory harness gcdump, 11-14).
/// </summary>
public class LocWeakSubscriptionTests
{
    private readonly ITestOutputHelper _o;
    public LocWeakSubscriptionTests(ITestOutputHelper o) => _o = o;

    /// <summary>Slots in Avalonia's weak PropertyChanged list for <see cref="Loc.Instance"/> (alive or dead).</summary>
    internal static int LocSubscriberSlots()
    {
        var weakEvents = typeof(Avalonia.AvaloniaObject).Assembly.GetType("Avalonia.Utilities.WeakEvents")!;
        foreach (var field in weakEvents.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            var args = field.FieldType.IsGenericType ? field.FieldType.GetGenericArguments() : Type.EmptyTypes;
            if (args.Length != 2 || args[0] != typeof(INotifyPropertyChanged) || args[1] != typeof(PropertyChangedEventArgs)) continue;
            var weakEvent = field.GetValue(null)!;
            var table = FieldOfType(weakEvent, "ConditionalWeakTable")!;
            var tryGet = table.GetType().GetMethod("TryGetValue")!;
            var callArgs = new object?[] { Loc.Instance, null };
            if (!(bool)tryGet.Invoke(table, callArgs)!) return 0;
            var list = FieldOfType(callArgs[1]!, "WeakHashList")!;
            var dic = FieldOfType(list, "Dictionary");
            if (dic != null) return ((System.Collections.ICollection)dic).Count;
            var count = list.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .First(f => f.FieldType == typeof(int)).GetValue(list);
            return (int)count!;
        }
        throw new InvalidOperationException("Avalonia's WeakEvents.ThreadSafePropertyChanged not found");
    }

    private static object? FieldOfType(object owner, string typeNamePrefix) =>
        owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(f => f.FieldType.Name.StartsWith(typeNamePrefix, StringComparison.Ordinal))?.GetValue(owner);

    private static void Collect()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void DiscardedLocalizedControls_DoNotKeepTheirSubscriptionSlots()
    {
        var panel = new StackPanel();
        var window = new Window { Content = panel, Width = 400, Height = 300 };
        window.Show();
        try
        {
            Collect();
            var before = LocSubscriberSlots();
            const int perRound = 500, rounds = 20;
            for (var r = 0; r < rounds; r++)
            {
                for (var i = 0; i < perRound; i++)
                {
                    var text = new TextBlock();
                    text.Bind(TextBlock.TextProperty, (BindingBase)new TExtension("Nav.Home").ProvideValue(null!));
                    panel.Children.Add(text);
                }
                Dispatcher.UIThread.RunJobs();
                panel.Children.Clear();
                Collect();
            }
            var after = LocSubscriberSlots();
            _o.WriteLine($"Loc subscriber slots {before} -> {after} after {perRound * rounds} discarded bindings");
            // Without the sweep: grew by every binding ever made (10,000). With it, at most about two
            // sweep intervals of dead slots sit in the list (one waits for the GC, one for the sweep).
            Assert.True(after - before < 2 * Loc.SweepEveryReads + perRound,
                $"Loc subscriber slots {before} -> {after} after {perRound * rounds} discarded bindings");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Sweep_DoesNotMakeLiveBindingsReRead()
    {
        var panel = new StackPanel();
        var window = new Window { Content = panel, Width = 400, Height = 300 };
        window.Show();
        try
        {
            var texts = new List<TextBlock>();
            for (var i = 0; i < 300; i++)
            {
                var text = new TextBlock();
                text.Bind(TextBlock.TextProperty, (BindingBase)new TExtension("Nav.Home").ProvideValue(null!));
                panel.Children.Add(text);
                texts.Add(text);
            }
            Dispatcher.UIThread.RunJobs();
            var reads = typeof(Loc).GetField("_indexerReads", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var before = (int)reads.GetValue(Loc.Instance)!;
            typeof(Loc).GetMethod("QueueSweep", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Loc.Instance, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(before, (int)reads.GetValue(Loc.Instance)!);
            Assert.All(texts, t => Assert.Equal(Loc.T("Nav.Home"), t.Text));
        }
        finally
        {
            window.Close();
        }
    }
}
