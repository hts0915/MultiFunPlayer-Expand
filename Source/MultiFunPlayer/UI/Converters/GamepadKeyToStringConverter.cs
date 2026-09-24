using System.Globalization;
using System.Windows.Data;
using Vortice.XInput;

namespace MultiFunPlayer.UI.Converters;

internal sealed class GamepadKeyToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not GamepadVirtualKey key)
            return null;

        return key switch
        {
            GamepadVirtualKey.RightShoulder => "右肩键",
            GamepadVirtualKey.LeftShoulder => "左肩键",
            GamepadVirtualKey.LeftTrigger => "左扳机",
            GamepadVirtualKey.RightTrigger => "右扳机",
            GamepadVirtualKey.DirectionalPadUp    => "十字键 上",
            GamepadVirtualKey.DirectionalPadDown  => "十字键 下",
            GamepadVirtualKey.DirectionalPadLeft  => "十字键 左",
            GamepadVirtualKey.DirectionalPadRight => "十字键 右",
            GamepadVirtualKey.LeftThumbPress => "左摇杆按下",
            GamepadVirtualKey.RightThumbPress => "右摇杆按下",
            GamepadVirtualKey.LeftThumbUp => "左摇杆 上",
            GamepadVirtualKey.LeftThumbDown => "左摇杆 下",
            GamepadVirtualKey.LeftThumbRight => "左摇杆 右",
            GamepadVirtualKey.LeftThumbLeft  => "左摇杆 左",
            GamepadVirtualKey.LeftThumbUpLeft    => "左摇杆 左上",
            GamepadVirtualKey.LeftThumbUpRight   => "左摇杆 右上",
            GamepadVirtualKey.LeftThumbDownRight => "左摇杆 右下",
            GamepadVirtualKey.LeftThumbDownLeft  => "左摇杆 左下",
            GamepadVirtualKey.RightThumbUp => "右摇杆 上",
            GamepadVirtualKey.RightThumbDown => "右摇杆 下",
            GamepadVirtualKey.RightThumbRight => "右摇杆 右",
            GamepadVirtualKey.RightThumbLeft => "右摇杆 左",
            GamepadVirtualKey.RightThumbUpLeft => "右摇杆 左上",
            GamepadVirtualKey.RightThumbUpRight => "右摇杆 右上",
            GamepadVirtualKey.RightThumbDownRight => "右摇杆 右下",
            GamepadVirtualKey.RightThumbDownLeft => "右摇杆 左下",
            GamepadVirtualKey.None => throw new NotImplementedException(),
            _ => key.ToString()
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
