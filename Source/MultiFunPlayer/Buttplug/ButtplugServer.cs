using MultiFunPlayer.UI.Controls.ViewModels;
using NLog;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace MultiFunPlayer.Buttplug;

/// <summary>
/// 一个「Intiface 兼容」的 Buttplug WebSocket 服务器：把设备当成一台 Buttplug 设备暴露出去，
/// 让只支持 Buttplug 的软件（游戏、Virt-A-Mate 等）通过本程序驱动设备。
/// <br/>
/// 实现方式：<see cref="TcpListener"/>（只监听环回）+ 自己完成 WebSocket 握手 +
/// <see cref="WebSocket.CreateFromStream"/> 处理帧。不依赖 HttpListener，
/// 因此不需要管理员权限、也不受 URL ACL 限制。
/// <br/>
/// 与真正 Intiface Central 对齐的几个细节（客户端兼容性很依赖它们）：
/// 同时监听 127.0.0.1 与 ::1（客户端连 localhost 时可能解析到 IPv6）、
/// 每条消息单独一帧、扫描后补发 ScanningFinished、按客户端请求的协议版本回话（v1/v2/v3）。
/// </summary>
internal sealed class ButtplugServer : IDisposable
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly IButtplugAxisSink _bridge;
    private readonly List<TcpListener> _listeners = [];
    private readonly List<Task> _acceptTasks = [];

    private CancellationTokenSource _cancellationSource;
    private Timer _idleTimer;
    private ButtplugServerOptions _options;
    private long _lastCommandTick;
    private int _clientCount;

    public ButtplugServer(IButtplugAxisSink bridge) => _bridge = bridge;

    public event Action StatusChanged;

    public bool IsRunning { get; private set; }
    public int Port => _options?.Port ?? ButtplugProtocol.DefaultPort;
    public string ListenAddress => $"ws://127.0.0.1:{Port}";
    public int ClientCount => Volatile.Read(ref _clientCount);
    public string LastCommand { get; private set; }
    public string LastError { get; private set; }
    public string DeviceName => _bridge.DeviceName;
    public IReadOnlyList<ButtplugActuator> Actuators => _bridge.Actuators;
    public IReadOnlyList<string> SkippedAxes => _bridge.SkippedAxes;

    public void Start(ButtplugServerOptions options)
    {
        if (IsRunning)
            return;

        _options = options;
        _bridge.Configure(options);

        try
        {
            _cancellationSource = new CancellationTokenSource();

            // 同时监听 IPv4 与 IPv6 环回：老游戏连 "localhost" 时可能解析到 ::1，
            // 只监听 127.0.0.1 的话会直接连不上
            var started = 0;
            var lastError = default(Exception);
            foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            {
                try
                {
                    var listener = new TcpListener(address, options.Port);
                    listener.Start();
                    _listeners.Add(listener);
                    started++;
                }
                catch (Exception e)
                {
                    lastError = e;
                    Logger.Warn(e, "Buttplug 服务器无法监听 {0}:{1}", address, options.Port);
                }
            }

            if (started == 0)
                throw lastError ?? new IOException("无法监听环回地址");

            _lastCommandTick = Environment.TickCount64;
            _idleTimer = new Timer(OnIdleTimer, null, 1000, 1000);

            IsRunning = true;
            LastError = null;

            foreach (var listener in _listeners)
                _acceptTasks.Add(Task.Run(() => AcceptLoopAsync(listener, _cancellationSource.Token)));

            Logger.Info("Buttplug 服务器已启动 [{0}]（监听 127.0.0.1 与 ::1）[设备名: {1}, 暴露轴: {2}]",
                ListenAddress, _bridge.DeviceName, string.Join("、", _bridge.Actuators.Select(a => a.Axis.Name)));
        }
        catch (Exception e)
        {
            // 最常见的失败原因：端口被 Intiface Central 占用
            LastError = e.Message;
            Logger.Error(e, "Buttplug 服务器启动失败 [端口: {0}]", options.Port);
            Stop();
        }

        RaiseStatus();
    }

    public void Stop()
    {
        if (!IsRunning && _listeners.Count == 0)
            return;

        IsRunning = false;

        try { _cancellationSource?.Cancel(); } catch { }

        foreach (var listener in _listeners)
        {
            try { listener.Stop(); } catch { }
        }

        _listeners.Clear();

        _idleTimer?.Dispose();
        _idleTimer = null;

        _bridge.ReleaseAll();

        _cancellationSource?.Dispose();
        _cancellationSource = null;

        Logger.Info("Buttplug 服务器已停止");
        RaiseStatus();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                if (token.IsCancellationRequested)
                    break;

                Logger.Warn(e, "Buttplug 服务器接受连接失败");
                break;
            }

            _ = HandleClientAsync(client, token);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        var connection = new ClientConnection { Remote = remote };
        WebSocket socket = null;

        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();

            var subProtocol = await TryHandshakeAsync(stream, token);
            if (subProtocol == HandshakeResult.Invalid)
            {
                Logger.Warn("Buttplug 服务器：来自 {0} 的连接不是合法的 WebSocket 握手", remote);
                return;
            }

            socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
            connection.Socket = socket;

            Interlocked.Increment(ref _clientCount);
            Logger.Info("Buttplug 客户端已连接 [{0}]，当前 {1} 个", remote, ClientCount);
            RaiseStatus();

            var buffer = new byte[16 * 1024];
            var pending = new StringBuilder();

            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Logger.Info("Buttplug 客户端主动关闭连接 [{0}] [状态: {1}]", DescribeClient(connection), socket.CloseStatus);
                    break;
                }

                pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage)
                    continue;

                var payload = pending.ToString();
                pending.Clear();

                foreach (var message in Dispatch(payload, connection))
                    await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException e)
        {
            // 客户端（游戏）自己崩了的话通常走到这里：连接被异常断开
            Logger.Warn("Buttplug 连接异常中断 [{0}]：{1}", DescribeClient(connection), e.Message);
        }
        catch (Exception e)
        {
            Logger.Warn(e, "Buttplug 客户端处理出错 [{0}]", DescribeClient(connection));
        }
        finally
        {
            try { if (socket != null) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch { }

            socket?.Dispose();
            client.Dispose();

            Interlocked.Decrement(ref _clientCount);
            Logger.Info("Buttplug 客户端已断开 [{0}]，当前 {1} 个", DescribeClient(connection), ClientCount);

            // 最后一个客户端走了就把控制权交还给脚本
            if (ClientCount <= 0)
                _bridge.ReleaseAll();

            RaiseStatus();
        }
    }

    private static string DescribeClient(ClientConnection connection)
        => connection.Name != null ? $"{connection.Name} @ {connection.Remote} v{connection.MessageVersion}" : connection.Remote;

    private enum HandshakeResult { Invalid, Done }

    /// <summary>完成 WebSocket 握手（只做这一次 HTTP 交互，之后全是 WebSocket 帧）。</summary>
    private static async Task<HandshakeResult> TryHandshakeAsync(NetworkStream stream, CancellationToken token)
    {
        var request = new StringBuilder();
        var buffer = new byte[1024];
        while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read <= 0)
                return HandshakeResult.Invalid;

            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (request.Length > 16 * 1024)
                return HandshakeResult.Invalid;
        }

        var key = default(string);
        var protocol = default(string);
        foreach (var line in request.ToString().Split("\r\n"))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                key = value;
            else if (name.Equals("Sec-WebSocket-Protocol", StringComparison.OrdinalIgnoreCase))
                protocol = value.Split(',')[0].Trim();
        }

        if (string.IsNullOrEmpty(key))
            return HandshakeResult.Invalid;

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + ButtplugProtocol.WebSocketGuid)));
        var response = new StringBuilder()
            .Append("HTTP/1.1 101 Switching Protocols\r\n")
            .Append("Connection: Upgrade\r\n")
            .Append("Upgrade: websocket\r\n")
            .Append($"Sec-WebSocket-Accept: {accept}\r\n");

        // 客户端如果协商了子协议，必须回一个它提供的，否则有些库会直接判定连接失败
        if (!string.IsNullOrEmpty(protocol))
            response.Append($"Sec-WebSocket-Protocol: {protocol}\r\n");

        response.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(response.ToString()), token);
        await stream.FlushAsync(token);
        return HandshakeResult.Done;
    }

    private IReadOnlyList<string> Dispatch(string payload, ClientConnection client)
    {
        var responses = new List<JObject>();

        JArray messages;
        try
        {
            messages = JArray.Parse(payload);
        }
        catch
        {
            Logger.Warn("Buttplug 服务器：无法解析消息 \"{0}\"", payload);
            responses.Add(ButtplugProtocol.Error(0, ButtplugProtocol.ErrorMessage, "消息不是合法的 JSON 数组"));
            return Serialize(responses);
        }

        foreach (var item in messages.OfType<JObject>())
        {
            var property = item.Properties().FirstOrDefault();
            if (property == null)
                continue;

            var type = property.Name;
            var body = property.Value as JObject ?? [];
            var id = body.Value<uint?>("Id") ?? 0;

            if (IsHighFrequency(type))
            {
                // 控制指令可能每秒几十条，只记第一条，避免刷爆日志
                if (!client.LoggedFirstCommand)
                {
                    client.LoggedFirstCommand = true;
                    Logger.Info("Buttplug 收到第一条控制指令 {0} [{1}]：{2}", type, DescribeClient(client), body.ToString(Newtonsoft.Json.Formatting.None));
                }
            }
            else
            {
                Logger.Info("Buttplug 收到 {0} [{1}]：{2}", type, DescribeClient(client), body.ToString(Newtonsoft.Json.Formatting.None));
            }

            try
            {
                DispatchMessage(type, body, id, responses, client);
            }
            catch (Exception e)
            {
                Logger.Warn(e, "Buttplug 服务器：处理 {0} 时出错", type);
                responses.Add(ButtplugProtocol.Error(id, ButtplugProtocol.ErrorUnknown, e.Message));
            }
        }

        if (responses.Count == 0)
            return [];

        RaiseStatus();
        return Serialize(responses);
    }

    private static bool IsHighFrequency(string type) => type switch
    {
        "LinearCmd" or "ScalarCmd" or "VibrateCmd" or "SingleMotorVibrateCmd" or "FleshlightLaunchFW12Cmd" or "RotateCmd" or "Ping" => true,
        _ => false,
    };

    /// <summary>一条消息一帧（和真正的 Intiface 行为一致，兼容只读第一帧的客户端）。</summary>
    private static IReadOnlyList<string> Serialize(List<JObject> messages)
        => [.. messages.Select(m => new JArray(m).ToString(Newtonsoft.Json.Formatting.None))];

    private void DispatchMessage(string type, JObject body, uint id, List<JObject> responses, ClientConnection client)
    {
        switch (type)
        {
            case "RequestServerInfo":
                {
                    client.Name = body.Value<string>("ClientName");
                    var requested = body.Value<uint?>("MessageVersion") ?? ButtplugProtocol.MessageVersion;
                    client.MessageVersion = (int)Math.Clamp(requested, 1, ButtplugProtocol.MessageVersion);

                    // 自写客户端（例如 Beat Banger 的 bbfh-client）只认"兼容形态"的能力描述，
                    // 用标准形态它会崩（它会无脑遍历 LinearCmd / RotateCmd / ScalarCmd）
                    client.Compat = _options.ForceCompat || ButtplugProtocol.IsCompatClient(client.Name);

                    Logger.Info("Buttplug 客户端握手 [名称: {0}, 地址: {1}, 请求协议版本: v{2} → 使用 v{3}, 能力描述: {4}]",
                        client.Name ?? "(未提供)", client.Remote, requested, client.MessageVersion,
                        client.Compat ? "兼容形态" : "标准形态");

                    responses.Add(ButtplugProtocol.ServerInfo(id, _options.ServerName, client.MessageVersion));
                    break;
                }

            case "Ping":
                responses.Add(ButtplugProtocol.Ok(id));
                break;

            case "StartScanning":
                responses.Add(ButtplugProtocol.Ok(id));
                responses.Add(ButtplugProtocol.DeviceAdded(_bridge.DeviceName, _bridge.Actuators, client.MessageVersion, client.Compat));
                responses.Add(ButtplugProtocol.ScanningFinished());
                break;

            case "StopScanning":
                responses.Add(ButtplugProtocol.Ok(id));
                break;

            case "RequestDeviceList":
                responses.Add(ButtplugProtocol.DeviceList(id, _bridge.DeviceName, _bridge.Actuators, client.MessageVersion, client.Compat));
                break;

            case "StopAllDevices":
                _bridge.ReleaseAll();
                LastCommand = "StopAllDevices";
                responses.Add(ButtplugProtocol.Ok(id));
                break;

            case "StopDeviceCmd":
                if (!CheckDeviceIndex(body, id, responses))
                    return;

                _bridge.ReleaseAll();
                LastCommand = "StopDeviceCmd";
                responses.Add(ButtplugProtocol.Ok(id));
                break;

            case "LinearCmd":
                if (!CheckDeviceIndex(body, id, responses))
                    return;

                HandleLinear(body, id, responses);
                break;

            case "ScalarCmd":
                if (!CheckDeviceIndex(body, id, responses))
                    return;

                HandleScalar(body, id, responses);
                break;

            // ---- v1 / v2 客户端的旧消息名 ----
            case "VibrateCmd":
                if (!CheckDeviceIndex(body, id, responses))
                    return;

                HandleVibrate(body, id, responses);
                break;

            case "SingleMotorVibrateCmd":
                if (!CheckDeviceIndex(body, id, responses))
                    return;

                HandleSingleMotorVibrate(body, id, responses);
                break;

            case "FleshlightLaunchFW12Cmd":
                if (!CheckDeviceIndex(body, id, responses))
                    return;

                HandleLaunch(body, id, responses);
                break;

            case "RotateCmd":
                if (!CheckDeviceIndex(body, id, responses))
                    return;

                HandleRotate(body, id, responses);
                break;

            case "RequestLog":
                // v3 的日志订阅：正确回复就是 Ok（之后服务端可以发 Log 消息）
                responses.Add(ButtplugProtocol.Ok(id));
                break;

            default:
                // 关键：**不能回 Error**。很多客户端库（游戏用的那种）收到 Error 会直接抛异常，
                // 表现就是"一连上就闪退"。回 Ok 表示收到了但没做处理，兼容性最好。
                Logger.Warn("Buttplug 服务器：收到未处理的消息 {0}（已按 Ok 回复以免客户端报错）", type);
                responses.Add(ButtplugProtocol.Ok(id));
                break;
        }
    }

    private static bool CheckDeviceIndex(JObject body, uint id, List<JObject> responses)
    {
        var deviceIndex = body.Value<int?>("DeviceIndex") ?? 0;
        if (deviceIndex == 0)
            return true;

        responses.Add(ButtplugProtocol.Error(id, ButtplugProtocol.ErrorDevice, $"未知的设备索引 {deviceIndex}"));
        return false;
    }

    private void HandleLinear(JObject body, uint id, List<JObject> responses)
    {
        var vectors = body["Vectors"] as JArray ?? [];
        foreach (var vector in vectors.OfType<JObject>())
        {
            var index = (int)(vector.Value<uint?>("Index") ?? 0);
            var position = vector.Value<double?>("Position") ?? double.NaN;
            var durationMilliseconds = vector.Value<uint?>("Duration") ?? 0;

            if (!double.IsFinite(position))
                continue;

            if (_bridge.ApplyLinear(index, position, durationMilliseconds / 1000d))
            {
                LastCommand = $"LinearCmd #{index} → {position:0.###}（{durationMilliseconds}ms）";
                Interlocked.Exchange(ref _lastCommandTick, Environment.TickCount64);
            }
        }

        responses.Add(ButtplugProtocol.Ok(id));
    }

    private void HandleScalar(JObject body, uint id, List<JObject> responses)
    {
        var scalars = body["Scalars"] as JArray ?? [];
        foreach (var scalar in scalars.OfType<JObject>())
        {
            var index = (int)(scalar.Value<uint?>("Index") ?? 0);
            var value = scalar.Value<double?>("Scalar") ?? double.NaN;

            if (!double.IsFinite(value))
                continue;

            if (_bridge.ApplyScalar(index, value))
            {
                LastCommand = $"ScalarCmd #{index} → {value:0.###}";
                Interlocked.Exchange(ref _lastCommandTick, Environment.TickCount64);
            }
        }

        responses.Add(ButtplugProtocol.Ok(id));
    }

    private void HandleRotate(JObject body, uint id, List<JObject> responses)
    {
        var rotations = body["Rotations"] as JArray ?? [];
        foreach (var rotation in rotations.OfType<JObject>())
        {
            var index = (int)(rotation.Value<uint?>("Index") ?? 0);
            var speed = rotation.Value<double?>("Speed") ?? double.NaN;
            var clockwise = rotation.Value<bool?>("Clockwise") ?? true;

            if (!double.IsFinite(speed))
                continue;

            if (_bridge.ApplyRotate(index, speed, clockwise))
            {
                LastCommand = $"RotateCmd #{index} → {speed:0.###}（{(clockwise ? "顺时针" : "逆时针")}）";
                Interlocked.Exchange(ref _lastCommandTick, Environment.TickCount64);
            }
        }

        responses.Add(ButtplugProtocol.Ok(id));
    }

    private void HandleVibrate(JObject body, uint id, List<JObject> responses)    {
        var speeds = body["Speeds"] as JArray ?? [];
        foreach (var speed in speeds.OfType<JObject>())
        {
            var index = (int)(speed.Value<uint?>("Index") ?? 0);
            var value = speed.Value<double?>("Speed") ?? double.NaN;

            if (!double.IsFinite(value))
                continue;

            if (_bridge.ApplyScalar(index, value))
            {
                LastCommand = $"VibrateCmd #{index} → {value:0.###}";
                Interlocked.Exchange(ref _lastCommandTick, Environment.TickCount64);
            }
        }

        responses.Add(ButtplugProtocol.Ok(id));
    }

    private void HandleSingleMotorVibrate(JObject body, uint id, List<JObject> responses)
    {
        var value = body.Value<double?>("Speed") ?? double.NaN;
        if (double.IsFinite(value))
        {
            foreach (var actuator in _bridge.Actuators.Where(a => a.Kind == ButtplugActuatorKind.Scalar))
                _bridge.ApplyScalar(actuator.Index, value);

            LastCommand = $"SingleMotorVibrateCmd → {value:0.###}";
            Interlocked.Exchange(ref _lastCommandTick, Environment.TickCount64);
        }

        responses.Add(ButtplugProtocol.Ok(id));
    }

    /// <summary>v1/v2 的 Fleshlight Launch 指令：Position 是 0~99，Speed 是 0~99（越大越快）。</summary>
    private void HandleLaunch(JObject body, uint id, List<JObject> responses)
    {
        var position = body.Value<double?>("Position") ?? double.NaN;
        var speed = body.Value<double?>("Speed") ?? 50;

        if (double.IsFinite(position))
        {
            var duration = Math.Clamp(1200 - 12 * speed, 100, 1500) / 1000d;
            if (_bridge.ApplyLinear(0, Math.Clamp(position / 99d, 0, 1), duration))
            {
                LastCommand = $"FleshlightLaunchFW12Cmd → {position:0}/{speed:0}";
                Interlocked.Exchange(ref _lastCommandTick, Environment.TickCount64);
            }
        }

        responses.Add(ButtplugProtocol.Ok(id));
    }

    private void OnIdleTimer(object state)
    {
        if (!IsRunning || !_bridge.HasTakenOverAxes)
            return;

        var idle = Environment.TickCount64 - Interlocked.Read(ref _lastCommandTick);
        if (idle < Math.Max(1, _options.IdleRestoreSeconds) * 1000L)
            return;

        Logger.Info("Buttplug 服务器：{0} 秒没有收到指令，把控制权交还给脚本", _options.IdleRestoreSeconds);
        _bridge.ReleaseAll();
        RaiseStatus();
    }

    private void RaiseStatus() => StatusChanged?.Invoke();

    public void Dispose() => Stop();

    private sealed class ClientConnection
    {
        public string Remote { get; init; }
        public WebSocket Socket { get; set; }
        public int MessageVersion { get; set; } = ButtplugProtocol.MessageVersion;
        public string Name { get; set; }
        public bool Compat { get; set; }
        public bool LoggedFirstCommand { get; set; }
    }
}
