using MultiFunPlayer.Common;
using MultiFunPlayer.Settings;
using Newtonsoft.Json.Linq;
using Stylet;
using System.ComponentModel;

namespace MultiFunPlayer.UI.Controls.ViewModels;

internal enum WifiProtocol
{
    [Description("UDP")]
    Udp,
    [Description("TCP")]
    Tcp
}

/// <summary>
/// 启动连接设置。
/// 启动时只自动连接数据线；连不上不弹窗，而是把蓝牙和 WiFi 目标准备好（只填参数），由用户手动点连接。
/// </summary>
internal sealed class StartupConnectionSettingsViewModel : Screen, IHandle<SettingsMessage>
{
    public bool AutoConnectOnStartup { get; set; } = true;
    public bool ShowConnectNotification { get; set; } = true;
    public string UsbSerialMatch { get; set; } = "VID_1A86&PID_7523";
    public string BluetoothDeviceMatch { get; set; } = "OSR6";
    public bool WifiProbeEnabled { get; set; } = true;
    public WifiProtocol DefaultWifiProtocol { get; set; } = WifiProtocol.Udp;
    public string DefaultWifiEndpoint { get; set; } = "192.168.0.101:8000";

    public IReadOnlyCollection<WifiProtocol> WifiProtocols { get; } = [WifiProtocol.Udp, WifiProtocol.Tcp];

    public StartupConnectionSettingsViewModel(IEventAggregator eventAggregator)
    {
        DisplayName = "启动连接";
        eventAggregator.Subscribe(this);
    }

    public void Handle(SettingsMessage message)
    {
        var settings = message.Settings;

        if (message.Action == SettingsAction.Saving)
        {
            settings[nameof(AutoConnectOnStartup)] = AutoConnectOnStartup;
            settings[nameof(ShowConnectNotification)] = ShowConnectNotification;
            settings[nameof(UsbSerialMatch)] = UsbSerialMatch;
            settings[nameof(BluetoothDeviceMatch)] = BluetoothDeviceMatch;
            settings[nameof(WifiProbeEnabled)] = WifiProbeEnabled;
            settings[nameof(DefaultWifiProtocol)] = JToken.FromObject(DefaultWifiProtocol);
            settings[nameof(DefaultWifiEndpoint)] = DefaultWifiEndpoint;
        }
        else if (message.Action == SettingsAction.Loading)
        {
            if (settings.TryGetValue<bool>(nameof(AutoConnectOnStartup), out var autoConnectOnStartup))
                AutoConnectOnStartup = autoConnectOnStartup;
            if (settings.TryGetValue<bool>(nameof(ShowConnectNotification), out var showConnectNotification))
                ShowConnectNotification = showConnectNotification;
            if (settings.TryGetValue<string>(nameof(UsbSerialMatch), out var usbSerialMatch))
                UsbSerialMatch = usbSerialMatch;
            if (settings.TryGetValue<string>(nameof(BluetoothDeviceMatch), out var bluetoothDeviceMatch))
                BluetoothDeviceMatch = bluetoothDeviceMatch;
            if (settings.TryGetValue<bool>(nameof(WifiProbeEnabled), out var wifiProbeEnabled))
                WifiProbeEnabled = wifiProbeEnabled;
            if (settings.TryGetValue<WifiProtocol>(nameof(DefaultWifiProtocol), out var defaultWifiProtocol))
                DefaultWifiProtocol = defaultWifiProtocol;
            if (settings.TryGetValue<string>(nameof(DefaultWifiEndpoint), out var defaultWifiEndpoint))
                DefaultWifiEndpoint = defaultWifiEndpoint;
        }
    }
}
