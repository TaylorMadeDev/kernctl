using System.Globalization;
using Avalonia.Data.Converters;

namespace Kernctl.App.Converters;

public sealed class BooleanToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 1d : 0.55d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
