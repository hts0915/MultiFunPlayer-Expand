using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;

namespace MultiFunPlayer.UI.Converters;

internal sealed class KeyToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Key key)
            return null;

        return key switch
        {
            Key.Add => "小键盘 +",
            Key.Apps => "右键菜单",
            Key.Back => "退格",
            Key.BrowserBack => "浏览器后退",
            Key.BrowserFavorites => "浏览器收藏夹",
            Key.BrowserForward => "浏览器前进",
            Key.BrowserHome => "浏览器主页",
            Key.BrowserRefresh => "浏览器刷新",
            Key.BrowserSearch => "浏览器搜索",
            Key.BrowserStop => "浏览器停止",
            Key.Cancel => "Break",
            Key.CapsLock => "大写锁定",
            Key.D0 => "0",
            Key.D1 => "1",
            Key.D2 => "2",
            Key.D3 => "3",
            Key.D4 => "4",
            Key.D5 => "5",
            Key.D6 => "6",
            Key.D7 => "7",
            Key.D8 => "8",
            Key.D9 => "9",
            Key.Decimal => "小键盘 .",
            Key.Divide => "小键盘 /",
            Key.Down => "向下键",
            Key.JunjaMode => "Junja",
            Key.KanaMode => "Kana",
            Key.KanjiMode => "Kanji",
            Key.LaunchApplication1 => "应用程序 1",
            Key.LaunchApplication2 => "应用程序 2",
            Key.LaunchMail => "邮件",
            Key.Left => "向左键",
            Key.LeftAlt => "左 Alt",
            Key.LeftCtrl => "左 Ctrl",
            Key.LeftShift => "左 Shift",
            Key.LineFeed => "换行",
            Key.LWin => "左 Win",
            Key.MediaNextTrack => "媒体下一曲",
            Key.MediaPlayPause => "媒体播放/暂停",
            Key.MediaPreviousTrack => "媒体上一曲",
            Key.MediaStop => "媒体停止",
            Key.Multiply => "小键盘 *",
            Key.NumLock => "数字锁定",
            Key.NumPad0 => "小键盘 0",
            Key.NumPad1 => "小键盘 1",
            Key.NumPad2 => "小键盘 2",
            Key.NumPad3 => "小键盘 3",
            Key.NumPad4 => "小键盘 4",
            Key.NumPad5 => "小键盘 5",
            Key.NumPad6 => "小键盘 6",
            Key.NumPad7 => "小键盘 7",
            Key.NumPad8 => "小键盘 8",
            Key.NumPad9 => "小键盘 9",
            Key.OemBackslash => "/",
            Key.OemBackTab => "反向 Tab",
            Key.OemClear => "清除",
            Key.OemCloseBrackets => "]",
            Key.OemComma => ",",
            Key.OemCopy => "复制",
            Key.OemFinish => "完成",
            Key.OemMinus => "-",
            Key.OemOpenBrackets => "[",
            Key.OemPeriod => ".",
            Key.OemPipe => "|",
            Key.OemPlus => "+",
            Key.OemQuestion => "?",
            Key.OemQuotes => "\"",
            Key.OemSemicolon => ";",
            Key.OemTilde => "~",
            Key.PageDown => "下一页",
            Key.PageUp => "上一页",
            Key.Pause => "Pause",
            Key.Play => "播放",
            Key.Print => "打印",
            Key.PrintScreen => "打印屏幕",
            Key.Right => "向右键",
            Key.RightAlt => "右 Alt",
            Key.RightCtrl => "右 Ctrl",
            Key.RightShift => "右 Shift",
            Key.RWin => "右 Win",
            Key.Scroll => "滚动锁定",
            Key.SelectMedia => "选择媒体",
            Key.Subtract => "小键盘 -",
            Key.Tab => "Tab",
            Key.Up => "向上键",
            Key.VolumeDown => "音量减",
            Key.VolumeMute => "静音",
            Key.VolumeUp => "音量加",
            _ => key.ToString()
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
