using Newtonsoft.Json.Linq;
using NLog;
using System.Text;
using System.Text.RegularExpressions;

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

/// <summary>
/// 设备 <c>#systemInfo</c> 回应的解析。
/// <br/>
/// 注意：设备固件回的 JSON **最后一个字段经常缺一个引号**，例如
/// <c>..."ip":"192.168.0.101",...,"uptime":"6}</c>，
/// 严格 JSON 解析会直接失败（这就是配网向导一开始判不出"已经拿到 IP"的原因）。
/// 所以这里先试标准解析，失败就退化成宽松的键值提取。
/// </summary>
internal sealed class TcodeDeviceInfo
{
    private static readonly Regex FieldRegex = new(@"""(?<key>[^""]+)""\s*:\s*""?(?<value>[^"",}]*)", RegexOptions.Compiled);

    private readonly Dictionary<string, string> _fields = new(StringComparer.OrdinalIgnoreCase);

    public bool HasAny => _fields.Count > 0;

    public static TcodeDeviceInfo Parse(string response)
    {
        var info = new TcodeDeviceInfo();
        if (string.IsNullOrWhiteSpace(response))
            return info;

        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        var json = start >= 0 && end > start ? response[start..(end + 1)] : response;

        if (!info.TryParseJson(json))
            info.ParseLenient(json);

        return info;
    }

    private bool TryParseJson(string json)
    {
        try
        {
            var parsed = JObject.Parse(json);
            foreach (var property in parsed.Properties())
                _fields[property.Name] = property.Value.Type == JTokenType.Null ? string.Empty : property.Value.ToString();

            return _fields.Count > 0;
        }
        catch
        {
            _fields.Clear();
            return false;
        }
    }

    private void ParseLenient(string text)
    {
        foreach (Match match in FieldRegex.Matches(text))
        {
            var key = match.Groups["key"].Value.Trim();
            var value = match.Groups["value"].Value.Trim().TrimEnd('"').Trim();
            if (key.Length > 0)
                _fields[key] = value;
        }
    }

    public string Get(params string[] names)
    {
        foreach (var name in names)
            if (_fields.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;

        return null;
    }

    /// <summary>设备的 UDP 地址（ip:port）。</summary>
    public string Endpoint
    {
        get
        {
            var ip = Get("ipAddress", "ip", "address");
            if (string.IsNullOrWhiteSpace(ip))
                return null;

            var port = Get("udpPort", "port");
            return string.IsNullOrWhiteSpace(port) ? ip : $"{ip}:{port}";
        }
    }

    /// <summary>
    /// 是否已经连上路由器。设备自己开热点时 <c>wifiMode</c> 是 AP 模式（IP 一般是 192.168.4.1），
    /// 那不是"配网成功"，只有 Station 模式才算真的接进了路由器。
    /// </summary>
    public bool IsStationMode
    {
        get
        {
            var mode = Get("wifiMode", "wifi", "mode", "workMode");
            if (string.IsNullOrWhiteSpace(mode))
                return !string.Equals(Get("ip"), "192.168.4.1", StringComparison.Ordinal);

            return mode.Contains("Station", StringComparison.OrdinalIgnoreCase)
                || mode.Contains("STA", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>拼成界面上好读的几行。</summary>
    public string Format()
    {
        if (!HasAny)
            return "（没有解析出设备信息）";

        var lines = new List<string>();
        void Add(string label, params string[] names)
        {
            var value = Get(names);
            if (!string.IsNullOrWhiteSpace(value))
                lines.Add($"{label}：{value}");
        }

        Add("设备类型", "devType", "deviceType", "device", "workMode");
        Add("芯片", "chipId");
        Add("MAC", "macAddress", "mac");
        Add("固件", "firmwareVersion");
        Add("TCode", "tcodeVersion", "tcode");
        Add("WiFi 模式", "wifiMode", "mode", "wifi");
        Add("已连 WiFi", "wifiConnectedName", "ssid");
        Add("IP 地址", "ipAddress", "ip");
        Add("UDP 端口", "udpPort");
        Add("蓝牙", "btName");
        Add("网页地址", "webAddress");
        Add("mDNS", "mdns");
        Add("运行时间", "uptime");

        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : string.Join(Environment.NewLine, _fields.Select(x => $"{x.Key}：{x.Value}"));
    }
}
