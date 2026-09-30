using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget;
using MultiFunPlayer.UI.Controls.ViewModels;
using NLog;
using PropertyChanged;
using Stylet;
using SerialPortInfo = MultiFunPlayer.OutputTarget.ViewModels.SerialOutputTarget.SerialPortInfo;

namespace MultiFunPlayer.UI.Dialogs.ViewModels;

/// <summary>
/// WiFi 配网向导：通过数据线把路由器的名称和密码写进设备。
/// <br/>
/// 设备的固件只支持 2.4GHz 网络，而且必须先把网络信息写进去、重启之后才会连上无线；
/// 没配过网的设备，在输出目标里填任何地址都连不上。
/// <br/>
/// 流程：选端口 → ①检测设备（#isOSR）→ ②写名称（#wifi-ssid:）→ ③写密码（#wifi-pass:）
/// → ④保存（$save）→ ⑤重启（#restart）→ ⑥等设备重启后读回 IP。
/// </summary>
internal sealed class WifiConfigWizardDialog : Screen
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly StartupConnectionSettingsViewModel _settings;

    public WifiConfigWizardDialog(StartupConnectionSettingsViewModel settings)
    {
        _settings = settings;
        Hint = "设备的无线只支持 2.4GHz；如果路由器是 2.4G/5G 合一的，建议先把两个频段分开，否则设备可能连不上。"
             + "写入并重启时设备会重启，舵机可能不受控地动一下，建议先关掉舵机电源（开关向左）。";

        _ = RefreshPortsAsync();
    }

    public string Hint { get; }

    public BindableCollection<SerialPortInfo> Ports { get; } = [];
    public SerialPortInfo SelectedPort { get; set; }

    public string WifiSsid { get; set; }
    public string WifiPassword { get; set; }

    public string StatusText { get; set; } = "正在枚举串口…";
    public string DeviceInfoText { get; set; }

    public bool IsBusy { get; set; }
    public bool ShowConfirm { get; set; }

    [DependsOn(nameof(IsBusy), nameof(SelectedPort))]
    public bool CanOperate => !IsBusy && SelectedPort != null;

    [DependsOn(nameof(ShowConfirm))]
    public bool CanEditInputs => !ShowConfirm && !IsBusy;

    public async Task OnRefreshPortsAsync() => await RefreshPortsAsync();

    private async Task RefreshPortsAsync()
    {
        IsBusy = true;
        StatusText = "正在枚举串口…";

        var ports = await Task.Run(SerialPortUtils.EnumeratePorts);
        var selected = SerialPortUtils.FindUsbDevicePort(ports, _settings.UsbSerialMatch) ?? ports.FirstOrDefault();

        Ports.Clear();
        Ports.AddRange(ports);
        SelectedPort = selected;

        StatusText = ports.Count == 0
            ? "没有找到任何串口。请插好数据线、确认设备已上电，并确认 CH340 驱动已安装。"
            : $"找到 {ports.Count} 个串口，已选中 {(selected?.Name ?? "无")}。点「① 检测设备」继续。";

        IsBusy = false;
    }

    public async Task OnCheckDeviceAsync()
    {
        if (SelectedPort == null || IsBusy)
            return;

        var portName = SelectedPort.PortName;
        IsBusy = true;
        StatusText = $"正在通过 {portName} 检测设备…";
        DeviceInfoText = null;

        try
        {
            var (ok, message, info) = await Task.Run(() => CheckDevice(portName));
            StatusText = message;
            if (info != null)
            {
                DeviceInfoText = info.Format();
                TryApplyEndpoint(info);
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, "配网向导检测设备时出错");
            StatusText = $"检测失败：{e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static (bool Ok, string Message, TcodeDeviceInfo Info) CheckDevice(string portName)
    {
        if (SerialPortUtils.Probe(portName) == SerialPortAvailability.Busy)
            return (false, $"端口 {portName} 被占用。如果本软件已经连上了设备，请先在输出目标面板断开串口连接；如果是别的软件占用，先把它关掉。", null);

        using var session = TcodeDeviceSession.Open(portName);

        var identify = session.Send(TcodeDeviceSession.IdentifyCommand);
        if (!identify.Contains("OK", StringComparison.OrdinalIgnoreCase))
            return (false, $"设备没有按预期回应「#isOSR」（收到「{identify}」）。确认端口选对了吗？", null);

        var infoResponse = session.Send(TcodeDeviceSession.SystemInfoCommand, 2500);
        var info = TcodeDeviceInfo.Parse(infoResponse);

        return (true, "设备验证通过。填好 WiFi 名称和密码后点「② 开始配网」。", info.HasAny ? info : null);
    }

    public void OnRequestProvision()
    {
        if (!CanOperate)
            return;

        if (string.IsNullOrWhiteSpace(WifiSsid))
        {
            StatusText = "请先填写 WiFi 名称（SSID）。";
            return;
        }

        if (string.IsNullOrWhiteSpace(WifiPassword) || WifiPassword.Length < 8)
        {
            StatusText = "WiFi 密码至少要 8 位。";
            return;
        }

        ShowConfirm = true;
        StatusText = "确认后会把 WiFi 信息写入设备并重启设备 —— 舵机可能不受控地动一下，确认舵机电源已关闭再继续。";
    }

    public void OnCancelProvision()
    {
        ShowConfirm = false;
        StatusText = "已取消。";
    }

    public async Task OnProvisionAsync()
    {
        if (SelectedPort == null || IsBusy)
            return;

        ShowConfirm = false;
        var portName = SelectedPort.PortName;
        var ssid = WifiSsid.Trim();

        IsBusy = true;
        DeviceInfoText = null;

        try
        {
            var (ok, message, info) = await Task.Run(() => Provision(portName, ssid, WifiPassword, Report));
            StatusText = message;
            if (info != null)
            {
                DeviceInfoText = info.Format();
                TryApplyEndpoint(info);
            }

            if (ok)
                Logger.Info("配网向导完成 [Port: {0}, SSID: {1}]", portName, ssid);
        }
        catch (Exception e)
        {
            Logger.Error(e, "配网向导执行时出错");
            StatusText = $"配网失败：{e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Report(string text) => Execute.OnUIThread(() => StatusText = text);

    private static (bool Ok, string Message, TcodeDeviceInfo Info) Provision(string portName, string ssid, string password, Action<string> progress)
    {
        if (SerialPortUtils.Probe(portName) == SerialPortAvailability.Busy)
            return (false, $"端口 {portName} 被占用，请先在输出目标面板断开串口连接，或关掉占用它的软件。", null);

        using (var session = TcodeDeviceSession.Open(portName))
        {
            progress("① 验证设备…");
            var identify = session.Send(TcodeDeviceSession.IdentifyCommand);
            if (!identify.Contains("OK", StringComparison.OrdinalIgnoreCase))
                return (false, $"设备验证失败（收到「{identify}」）。", null);

            progress($"② 写入 WiFi 名称「{ssid}」…");
            var ssidResponse = session.Send(TcodeDeviceSession.WifiSsidCommand + ssid);
            if (IsError(ssidResponse))
                return (false, $"写入 WiFi 名称失败：{ssidResponse}", null);

            progress("③ 写入 WiFi 密码…");
            var passwordResponse = session.Send(TcodeDeviceSession.WifiPasswordCommand + password);
            if (IsError(passwordResponse))
                return (false, $"写入 WiFi 密码失败：{passwordResponse}", null);

            progress("④ 保存配置…");
            var saveResponse = session.Send(TcodeDeviceSession.SaveCommand, 2500);
            if (IsError(saveResponse))
                return (false, $"保存配置失败：{saveResponse}", null);

            progress("⑤ 重启设备，让无线配置生效…");
            // 设备收到重启命令会立刻断开，收不到回应是正常的
            session.Send(TcodeDeviceSession.RestartCommand, 1000);
        }

        // 设备重启后 USB 会重新枚举，端口可能消失几秒，这里轮询等它回来再读一次网络信息
        var lastInfo = default(TcodeDeviceInfo);
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            progress($"⑥ 等待设备重启并读取网络信息…（第 {attempt}/8 次，约 {attempt * 3} 秒）");
            Thread.Sleep(3000);

            try
            {
                if (SerialPortUtils.Probe(portName) != SerialPortAvailability.Free)
                    continue;

                using var session = TcodeDeviceSession.Open(portName);
                var infoResponse = session.Send(TcodeDeviceSession.SystemInfoCommand, 2500);

                var info = TcodeDeviceInfo.Parse(infoResponse);
                if (!info.HasAny)
                    continue;

                lastInfo = info;

                var endpoint = info.Endpoint;
                if (string.IsNullOrWhiteSpace(endpoint))
                    continue;

                // 设备自己开热点时也会报一个 IP（一般是 192.168.4.1），那不是"配网成功"
                if (!info.IsStationMode)
                {
                    progress($"⑥ 设备还在热点(AP)模式，说明还没连上路由器，继续等…（第 {attempt}/8 次）");
                    continue;
                }

                return (true, $"配网成功！设备已经连上路由器，地址是 {endpoint}。这个地址已经填进「设置 → 启动连接」的 WiFi 默认地址里，下次启动弹窗会直接用它。{DescribeSubnetMismatch(endpoint)}", info);
            }
            catch (Exception)
            {
                // 端口还没回来，继续等
            }
        }

        var tail = lastInfo != null
            ? $"没等到设备进入 Station 模式。设备最后的状态：{lastInfo.Format().Replace(Environment.NewLine, "；")}。如果是 AP 模式，说明路由器名称或密码没写对（设备只支持 2.4GHz）。"
            : "没等到设备重新上线。等设备起来后可以点「① 检测设备」再看一次状态。";

        return (true, $"配网命令已全部发送完成，但{tail}", lastInfo);
    }

    private static bool IsError(string response)
        => !string.IsNullOrEmpty(response) && response.Contains("ERR", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 设备连上路由器、但电脑在另一个网段是无线连不上的常见原因（设备 192.168.0.x、电脑 192.168.1.x），
    /// 这里提前说清楚，免得用户对着"连接超时"发呆。
    /// </summary>
    private static string DescribeSubnetMismatch(string endpoint)
    {
        if (!NetUtils.TryParseEndpoint(endpoint, out var parsed) || NetUtils.IsOnLocalSubnet(parsed))
            return string.Empty;

        return $" ⚠ 不过设备在 {endpoint}，而电脑在 {NetUtils.DescribeLocalAddresses()}，两者不在同一个网络 —— 需要把电脑也连到同一个路由器（例如把电脑的 WiFi 连到设备所在的网络），否则无线连不上。";
    }

    private void TryApplyEndpoint(TcodeDeviceInfo info)
    {
        var endpoint = info?.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
            return;

        _settings.DefaultWifiEndpoint = endpoint;
        Logger.Info("配网向导已把 WiFi 默认地址更新为 {0}", endpoint);
    }

    // 注意：不能叫 OnClose，那会隐藏 Stylet Screen 自带的 OnClose
    public void OnCloseClick() => DialogHelper.CloseByModel(this);
}
