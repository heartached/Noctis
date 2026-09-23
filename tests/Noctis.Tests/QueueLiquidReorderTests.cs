using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Queue drag-to-reorder "liquid" gap: while a row is dragged from <c>source</c> to
/// <c>target</c>, the rows in between slide one slot toward the source so the gap opens
/// where the card will land (and the list already looks like the committed move).
/// </summary>
public class QueueLiquidReorderTests
{
    private const double Pitch = 58;

    [Theory]
    // Dragging down 1 → 4: rows 2..4 move up one slot, everything else stays.
    [InlineData(0, 1, 4, 0)]
    [InlineData(1, 1, 4, 0)]       // the dragged row's own (hidden) slot never moves
    [InlineData(2, 1, 4, -Pitch)]
    [InlineData(4, 1, 4, -Pitch)]
    [InlineData(5, 1, 4, 0)]
    // Dragging up 5 → 2: rows 2..4 move down one slot.
    [InlineData(1, 5, 2, 0)]
    [InlineData(2, 5, 2, Pitch)]
    [InlineData(4, 5, 2, Pitch)]
    [InlineData(5, 5, 2, 0)]
    [InlineData(6, 5, 2, 0)]
    // Hovering its own slot: nothing moves.
    [InlineData(3, 3, 3, 0)]
    [InlineData(4, 3, 3, 0)]
    public void GapOffset_ShiftsOnlyTheRowsBetweenSourceAndTarget(int index, int source, int target, double expected)
        => Assert.Equal(expected, LiquidReorder.GapOffset(index, source, target, Pitch));

    [Fact]
    public void GapOffset_NoDrag_IsZero()
    {
        Assert.Equal(0, LiquidReorder.GapOffset(2, -1, 4, Pitch));
        Assert.Equal(0, LiquidReorder.GapOffset(2, 1, -1, Pitch));
    }
}
