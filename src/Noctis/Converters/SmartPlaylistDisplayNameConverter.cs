using System.Globalization;
using Avalonia.Data.Converters;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Converters;

/// <summary>
/// Maps smart playlist enum values (RuleField, RuleOperator, SmartPlaylistSortBy)
/// to their friendly display names for ComboBox items.
/// </summary>
public sealed class SmartPlaylistDisplayNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RuleField field => SmartPlaylistEvaluator.GetFieldDisplayName(field),
        RuleOperator op => SmartPlaylistEvaluator.GetOperatorDisplayName(op),
        SmartPlaylistSortBy sort => SmartPlaylistEvaluator.GetSortDisplayName(sort),
        _ => value?.ToString()
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
