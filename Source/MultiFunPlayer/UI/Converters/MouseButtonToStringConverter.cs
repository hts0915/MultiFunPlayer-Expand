using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;

namespace MultiFunPlayer.UI.Converters;

internal sealed class MouseButtonToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not MouseButton button)
            return null;

        return button switch
        {
            MouseButton.Left => "左键",
            MouseButton.Right => "右键",
            MouseButton.Middle => "中键",
            MouseButton.XButton1 => "侧键 1",
            MouseButton.XButton2 => "侧键 2",
            _ => button.ToString()
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
