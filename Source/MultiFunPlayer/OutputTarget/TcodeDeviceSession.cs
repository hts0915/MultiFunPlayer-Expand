using Newtonsoft.Json.Linq;
using NLog;
using System.Text;

namespace MultiFunPlayer.OutputTarget;

/// <summary>
/// 与设备的一条串口命令会话：可以"发一条命令、等一条回应"。
/// <br/>
/// 用于 WiFi 配网向导。命令集来自设备固件（与成熟实现一致）：
/// <list type="bullet">
/// <item><c>#isOSR</c>　验证是不是 OSR 设备，回应 <c>OK</c></item>
/// <item><c>#systemInfo</c>　查询系统信息，回应一段 JSON（含 ipAddress / ssid / wifiMode / udpPort 等）</item>
/// <item><c>#wifi-ssid:名称</c>　写入 WiFi 名称</item>
/// <item><c>#wifi-pass:密码</c>　写入 WiFi 密码</item>
/// <item><c>$save</c>　保存配置，回应 <c>Settings saved</c></item>
/// <item><c>#restart</c>　重启设备（无线配置生效）</item>
/// </list>
/// 注意：写入并重启后设备会重启，舵机可能不受控动一下，配网前建议先关掉舵机电源。
/// </summary>
internal sealed class TcodeDeviceSession : IDisposable
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public const string IdentifyCommand = "#isOSR";
    public const string SystemInfoCommand = "#systemInfo";
    public const string WifiSsidCommand = "#wifi-ssid:";
    public const string WifiPasswordCommand = "#wifi-pass:";
    public const string SaveCommand = "$save";
    public const string RestartCommand = "#restart";

    private const char ResponseTerminator = '\n';

    private readonly ISerialTransport _transport;

    public string PortName { get; }

    private TcodeDeviceSession(ISerialTransport transport, string portName)
    {
        _transport = transport;
        PortName = portName;
    }

    /// <summary>占用串口并建立会话。用一个已经打开的端口是调用方的事，这里会自己打开（独占）。</summary>
    public static TcodeDeviceSession Open(string portName, int baudRate = 115200)
    {
        var transport = SerialTransport.Open(new SerialPortOptions(
            portName, baudRate, System.IO.Ports.Parity.None, System.IO.Ports.StopBits.One, 8,
            System.IO.Ports.Handshake.None,
            DtrEnable: false, RtsEnable: false,
            ReadTimeout: 250, WriteTimeout: 250, ReadBufferSize: 4096, WriteBufferSize: 2048));

        Logger.Info("配网向导已打开串口 {0} [Transport: {1}]", portName, transport.Description);
        return new TcodeDeviceSession(transport, portName);
    }

    /// <summary>发一条命令并等回应；超时返回已经收到的内容（可能为空字符串）。</summary>
    public string Send(string command, int timeoutMilliseconds = 1500)
    {
        Logger.Info("配网向导发送命令 [Command: \"{0}\", Timeout: {1}ms]", command, timeoutMilliseconds);

        // 先把残留的旧数据读掉，避免把上一条的回应当成这一条的
        Drain();

        _transport.Write($"{command}\n");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var buffer = new StringBuilder();
        while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            if (_transport.BytesToRead > 0)
            {
                buffer.Append(_transport.ReadExisting());
                if (buffer.ToString().Contains(ResponseTerminator))
                    break;
            }

            Thread.Sleep(20);
        }

        var response = buffer.ToString().Trim();
        if (response.Length == 0)
            Logger.Warn("配网向导命令没有收到回应 [Command: \"{0}\"]", command);
        else
            Logger.Info("配网向导收到回应 [Command: \"{0}\", Response: \"{1}\"]", command, response);

        return response;
    }

    private void Drain()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 100)
        {
            if (_transport.BytesToRead <= 0)
            {
                Thread.Sleep(10);
                continue;
            }

            _transport.ReadExisting();
        }
    }

    public void Dispose()
    {
        try { _transport.Dispose(); }
        catch (Exception e) { Logger.Warn(e, "关闭配网向导串口时出错"); }
    }
}

/// <summary>设备 <c>#systemInfo</c> 回应的 JSON 解析。</summary>
internal static class TcodeDeviceInfo
{
    /// <summary>从回应里抠出 JSON 对象（设备可能前后带别的内容）。</summary>
    public static bool TryParse(string response, out JObject info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start < 0 || end <= start)
            return false;

        try
        {
            info = JObject.Parse(response[start..(end + 1)]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string GetString(JObject info, params string[] names)
    {
        if (info == null)
            return null;

        foreach (var name in names)
        {
            var token = info.GetValue(name, StringComparison.OrdinalIgnoreCase);
            if (token != null && token.Type != JTokenType.Null && !string.IsNullOrWhiteSpace(token.ToString()))
                return token.ToString();
        }

        return null;
    }

    public static string GetEndpoint(JObject info)
    {
        var ip = GetString(info, "ipAddress", "ip");
        if (string.IsNullOrWhiteSpace(ip))
            return null;

        var port = GetString(info, "udpPort");
        return string.IsNullOrWhiteSpace(port) ? ip : $"{ip}:{port}";
    }

    /// <summary>拼成界面上好读的几行。</summary>
    public static string Format(JObject info)
    {
        if (info == null)
            return "（没有解析出设备信息）";

        var lines = new List<string>();
        void Add(string label, params string[] names)
        {
            var value = GetString(info, names);
            if (!string.IsNullOrWhiteSpace(value))
                lines.Add($"{label}：{value}");
        }

        Add("设备类型", "devType", "deviceType", "device");
        Add("芯片", "chipId");
        Add("MAC", "macAddress", "mac");
        Add("固件", "firmwareVersion");
        Add("TCode", "tcodeVersion", "tcode");
        Add("工作模式", "workMode");
        Add("WiFi 模式", "wifiMode", "mode", "wifi");
        Add("已连 WiFi", "wifiConnectedName", "ssid");
        Add("IP 地址", "ipAddress", "ip");
        Add("UDP 端口", "udpPort");
        Add("网页地址", "webAddress");
        Add("mDNS", "mdns");

        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : info.ToString();
    }
}
