using System.Globalization;
using TurntableSpeed.App.Presentation;

namespace TurntableSpeed.App.Converters;

/// <summary>
/// Colors a notice by how much it should worry the user. Information is deliberately quiet:
/// the two mandatory caveats — the phone's weight and the record's tuning — are always present,
/// and if they shouted the user would learn to ignore everything.
/// </summary>
public sealed class NoticeLevelToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is NoticeLevel level
            ? level switch
            {
                NoticeLevel.Problem => Color.FromArgb("#EF5350"),
                NoticeLevel.Caution => Color.FromArgb("#FFB74D"),
                _ => Color.FromArgb("#5C6B7A"),
            }
            : Color.FromArgb("#5C6B7A");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
