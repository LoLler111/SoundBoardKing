using System.Globalization;
using System.Windows.Data;

namespace SoundBoardKing.App.Converters;

/// <summary>Binds a RadioButton to an int: checked when the value equals the parameter.</summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int number && number == ParseParameter(parameter);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? ParseParameter(parameter) : Binding.DoNothing;

    private static int ParseParameter(object parameter) =>
        int.Parse(System.Convert.ToString(parameter, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
}
