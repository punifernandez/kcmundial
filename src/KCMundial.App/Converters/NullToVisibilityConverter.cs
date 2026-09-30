using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KCMundial.App.Converters;

/// <summary>Visible si hay valor (null o texto vacío = oculto). Con ConverterParameter=Invert, al revés.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hasValue = value != null && !string.IsNullOrEmpty(value.ToString());
        if ("Invert".Equals(parameter)) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
