using System.Globalization;
using System.Windows.Data;
using CodexUsageWidget.Core;

namespace CodexUsageWidget.Converters;

public sealed class TokenCountConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (!TryGetNumber(value, out var number))
        {
            return "--";
        }

        return LocalizedTokenFormatter.Format(number, culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static bool TryGetNumber(object? value, out decimal number)
    {
        try
        {
            number = value is null
                ? 0
                : System.Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            return value is not null;
        }
        catch (Exception) when (value is not null)
        {
            number = 0;
            return false;
        }
    }
}

public sealed class RatioToPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double ratio || double.IsNaN(ratio))
        {
            return "--";
        }

        return (Math.Clamp(ratio, 0, 1) * 100)
            .ToString("0.0", culture) + "%";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
