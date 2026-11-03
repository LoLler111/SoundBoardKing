using System.Globalization;
using System.Windows.Data;

namespace SoundBoardKing.App.Converters;

/// <summary>Flips a number's sign both ways, e.g. to turn a pad axis around.</summary>
public sealed class NegateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double number ? -number : Binding.DoNothing;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double number ? -number : Binding.DoNothing;
}
