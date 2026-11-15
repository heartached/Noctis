using System.Reflection;
using Avalonia.Controls;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// RetainingVirtualizingStackPanel and RetainedStackElements are copies of Avalonia files (their
/// recycle path is private). An Avalonia upgrade must re-sync them, or the app keeps a panel
/// written against the old version's layout code without anyone noticing.
/// </summary>
public class RetainingPanelVersionTests
{
    [Fact]
    public void TheCopiedPanel_MatchesTheAvaloniaVersionInUse()
    {
        var info = typeof(VirtualizingStackPanel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var inUse = info.Split('+')[0];
        Assert.True(inUse == RetainingVirtualizingStackPanel.AvaloniaVersionCopiedFrom,
            $"Avalonia is now {inUse}, but src/Noctis/Controls/RetainingVirtualizingStackPanel.cs and " +
            $"RetainedStackElements.cs were copied from {RetainingVirtualizingStackPanel.AvaloniaVersionCopiedFrom}. " +
            "Diff Avalonia's VirtualizingStackPanel.cs and Utils/RealizedStackElements.cs at the new tag " +
            "against the copies, carry the upstream changes over (keep the // NOCTIS: regions), update the " +
            "header's tag/commit and AvaloniaVersionCopiedFrom.");
    }
}
