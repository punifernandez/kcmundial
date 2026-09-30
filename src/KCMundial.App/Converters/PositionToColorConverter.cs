using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace KCMundial.App.Converters;

public sealed class PositionToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var ok = value is true;
        return new SolidColorBrush(ok ? Colors.Lime : Colors.Yellow);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
