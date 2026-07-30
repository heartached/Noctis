using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Noctis.Services;

namespace Noctis.Converters;

/// <summary>
/// Held-note glow opacity from <c>[0]=Progress</c> and <c>[1]=HeldDurationMs</c>:
/// the AMLL bell — rises over the first half of the hold, releases over the second,
/// with a peak that scales with how long the note is held. Replaces the fixed
/// snap-to-0.5 class transition, which lit every emphasis word equally and then
/// froze for the rest of the hold.
/// </summary>
public class EmphasisGlowConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var progress = values.Count > 0 && values[0] is double p ? p : 0.0;
        var heldMs = values.Count > 1 && values[1] is double d ? d : 0.0;
        return EmphasisBell.Evaluate(progress, heldMs);
    }
}
