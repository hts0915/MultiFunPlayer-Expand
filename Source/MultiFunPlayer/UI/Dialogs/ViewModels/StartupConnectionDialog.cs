using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget.ViewModels;
using MultiFunPlayer.UI.Controls.ViewModels;
using PropertyChanged;
using Stylet;
using System.Net;
using SerialPortInfo = MultiFunPlayer.OutputTarget.ViewModels.SerialOutputTarget.SerialPortInfo;

namespace MultiFunPlayer.UI.Dialogs.ViewModels;

internal enum StartupConnectionKind
{
    Bluetooth,
    Wifi,
    WifiProvision,
    RetryUsb,
    Skip
}

/// <summary>启动连接弹窗的选择结果。</summary>
internal sealed class StartupConnectionChoice
{
    public StartupConnectionKind Kind { get; init; }
    public string SerialPortDeviceId { get; init; }
    public EndPoint Endpoint { get; init; }
    public WifiProtocol Protocol { get; init; }
}

/// <summary>
/// 数据线端口被占用（或者没插线）时，让用户选择改用蓝牙或 WiFi 连接。
/// </summary>
internal sealed class StartupConnectionDialog : Screen
{
    public string Reason { get; }
    public string Hint { get; }

    public IReadOnlyList<SerialPortInfo> BluetoothPorts { get; }
    public SerialPortInfo SelectedSerialPort { get; set; }
    public bool CanUseBluetooth => BluetoothPorts.Count > 0;

    public IReadOnlyCollection<WifiProtocol> WifiProtocols { get; }
    public WifiProtocol WifiProtocol { get; set; }
    public string WifiEndpoint { get; set; }

    [DependsOn(nameof(WifiEndpoint))]
    public bool CanUseWifi => NetUtils.TryParseEndpoint(WifiEndpoint, out _);

    public StartupConnectionDialog(string reason, IReadOnlyList<SerialPortInfo> bluetoothPorts, SerialPortInfo selectedPort, StartupConnectionSettingsViewModel settings)
    {
        Reason = reason;
        Hint = "数据线端口被占用时可以先用蓝牙或 WiFi；等数据线空闲了再点「重试 USB」。"
             + "WiFi 需要设备已经用 USB 配好 2.4GHz 网络，没配过网的话填什么地址都连不上。";

        BluetoothPorts = bluetoothPorts ?? [];
        SelectedSerialPort = selectedPort;

        WifiProtocols = settings.WifiProtocols;
        WifiProtocol = settings.DefaultWifiProtocol;
        WifiEndpoint = settings.DefaultWifiEndpoint;
    }

    public void OnConnectBluetooth()
    {
        if (SelectedSerialPort == null)
            return;

        Close(new StartupConnectionChoice()
        {
            Kind = StartupConnectionKind.Bluetooth,
            SerialPortDeviceId = SelectedSerialPort.DeviceID
        });
    }

    public void OnConnectWifi()
    {
        if (!NetUtils.TryParseEndpoint(WifiEndpoint, out var endpoint))
            return;

        Close(new StartupConnectionChoice()
        {
            Kind = StartupConnectionKind.Wifi,
            Endpoint = endpoint,
            Protocol = WifiProtocol
        });
    }

    public void OnRetryUsb() => Close(new StartupConnectionChoice() { Kind = StartupConnectionKind.RetryUsb });

    /// <summary>打开配网向导：设备从没配过网时，填什么地址都连不上，必须先走这一步。</summary>
    public void OnOpenWifiWizard() => Close(new StartupConnectionChoice() { Kind = StartupConnectionKind.WifiProvision });

    public void OnSkip() => Close(new StartupConnectionChoice() { Kind = StartupConnectionKind.Skip });

    private void Close(StartupConnectionChoice choice) => DialogHelper.CloseByModel(this, choice);
}
