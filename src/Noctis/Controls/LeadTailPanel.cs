using System;
using Avalonia;
using Avalonia.Controls;

namespace Noctis.Controls;

/// <summary>
/// One text line made of a lead field and what follows it (" · 4:12", " · Album"), GitHub #247.
/// The first child is the lead, the others follow it left to right.
/// A short tail is never cut: the lead trims to make room for it. When both are long the tail
/// may take at most half of the line, so a long album name cannot push the lead out.
/// </summary>
public sealed class LeadTailPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children;
        if (children.Count == 0) return default;

        var infinite = new Size(double.PositiveInfinity, availableSize.Height);
        var lead = children[0];
        lead.Measure(infinite);
        var leadWidth = lead.DesiredSize.Width;

        double tailWidth = 0;
        Control? widest = null;
        for (var i = 1; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(infinite);
            tailWidth += child.DesiredSize.Width;
            if (widest is null || child.DesiredSize.Width > widest.DesiredSize.Width) widest = child;
        }

        var width = availableSize.Width;
        if (!double.IsInfinity(width) && leadWidth + tailWidth > width)
        {
            // The tail keeps its size up to half the line, and more when the lead is short.
            var tailBudget = Math.Min(tailWidth, Math.Max(width / 2, width - leadWidth));
            if (tailWidth > tailBudget && widest is not null)
            {
                var cut = tailWidth - tailBudget;
                widest.Measure(new Size(Math.Max(0, widest.DesiredSize.Width - cut), availableSize.Height));
                tailWidth = 0;
                for (var i = 1; i < children.Count; i++) tailWidth += children[i].DesiredSize.Width;
            }
            lead.Measure(new Size(Math.Max(0, width - tailWidth), availableSize.Height));
            leadWidth = lead.DesiredSize.Width;
        }

        double height = 0;
        foreach (var child in children) height = Math.Max(height, child.DesiredSize.Height);
        return new Size(leadWidth + tailWidth, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (var child in Children)
        {
            var w = child.DesiredSize.Width;
            child.Arrange(new Rect(x, 0, w, finalSize.Height));
            x += w;
        }
        return finalSize;
    }
}
