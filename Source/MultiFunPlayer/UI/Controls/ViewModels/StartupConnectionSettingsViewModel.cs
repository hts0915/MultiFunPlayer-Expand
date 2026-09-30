using MultiFunPlayer.Common;
using MultiFunPlayer.Settings;
using MultiFunPlayer.UI;
using MultiFunPlayer.UI.Dialogs.ViewModels;
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
/// 启动时自动连接设备的设置。
/// 流程：先探测数据线（USB 串口）是否空闲 → 空闲就直接连；被占用或没插线就弹窗让用户选蓝牙 / WiFi。
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

    /// <summary>
    /// 打开配网向导。注意：向导和设置共用同一个 DialogHost，所以点这个按钮会先关掉设置窗口。
    /// </summary>
    public void OnOpenWifiConfigWizard() => _ = DialogHelper.ShowAsync(new WifiConfigWizardDialog(this), "RootDialog");

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
