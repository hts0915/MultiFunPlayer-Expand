using MultiFunPlayer.UI.Controls.ViewModels;
using NLog;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace MultiFunPlayer.Buttplug;

/// <summary>
/// 一个「Intiface 兼容」的 Buttplug Protocol v3 WebSocket 服务器：把 OSR 设备当成一台 Buttplug 设备暴露出去，
/// 让只支持 Buttplug 的软件（例如 Virt-A-Mate）通过本程序驱动设备。
/// <br/>
/// 实现方式：<see cref="TcpListener"/>（只监听 127.0.0.1）+ 自己完成 WebSocket 握手 +
/// <see cref="WebSocket.CreateFromStream"/> 处理帧。这样不依赖 HttpListener，
/// 因此**不需要管理员权限、也不受 URL ACL 限制**，并且任何 Host 头（localhost / 127.0.0.1）都能连。
/// </summary>
internal sealed class ButtplugServer : IDisposable
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly IButtplugAxisSink _bridge;
    private readonly List<Task> _clients = [];

    private TcpListener _listener;
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

    public void Start(ButtplugServerOptions options)
    {
        if (IsRunning)
            return;

        _options = options;
        _bridge.Configure(options);

        try
        {
            _cancellationSource = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, options.Port);
            _listener.Start();

            _lastCommandTick = Environment.TickCount64;
            _idleTimer = new Timer(OnIdleTimer, null, 1000, 1000);

            IsRunning = true;
            LastError = null;

            _clients.Add(Task.Run(() => AcceptLoopAsync(_cancellationSource.Token)));
            Logger.Info("Buttplug 服务器已启动 [{0}] [设备名: {1}, 暴露轴: {2}]",
                ListenAddress, _bridge.DeviceName, string.Join("、", _bridge.Actuators.Select(a => a.Axis.Name)));
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Logger.Error(e, "Buttplug 服务器启动失败 [端口: {0}]", options.Port);
            Stop();
        }

        RaiseStatus();
    }

    public void Stop()
    {
        if (!IsRunning && _listener == null)
            return;

        IsRunning = false;

        try { _cancellationSource?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }

        _idleTimer?.Dispose();
        _idleTimer = null;
        _listener = null;

        _bridge.ReleaseAll();

        _cancellationSource?.Dispose();
        _cancellationSource = null;

        Logger.Info("Buttplug 服务器已停止");
        RaiseStatus();
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token);
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
        WebSocket socket = null;

        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();

            if (!await TryHandshakeAsync(stream, token))
            {
                Logger.Warn("Buttplug 服务器：来自 {0} 的连接不是合法的 WebSocket 握手", remote);
                return;
            }

            socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
            Interlocked.Increment(ref _clientCount);
            Logger.Info("Buttplug 客户端已连接 [{0}]，当前 {1} 个", remote, ClientCount);
            RaiseStatus();

            var buffer = new byte[16 * 1024];
            var pending = new StringBuilder();

            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage)
                    continue;

                var payload = pending.ToString();
                pending.Clear();

                var response = Dispatch(payload);
                if (!string.IsNullOrEmpty(response))
                    await socket.SendAsync(Encoding.UTF8.GetBytes(response), WebSocketMessageType.Text, true, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception e)
        {
            Logger.Warn(e, "Buttplug 客户端处理出错 [{0}]", remote);
        }
        finally
        {
            try { if (socket != null) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch { }

            socket?.Dispose();
            client.Dispose();

            Interlocked.Decrement(ref _clientCount);
            Logger.Info("Buttplug 客户端已断开 [{0}]，当前 {1} 个", remote, ClientCount);

            // 最后一个客户端走了就把控制权交还给脚本
            if (ClientCount <= 0)
                _bridge.ReleaseAll();

            RaiseStatus();
        }
    }

    /// <summary>完成 WebSocket 握手（只做这一个 HTTP 交互，之后全是 WebSocket 帧）。</summary>
    private static async Task<bool> TryHandshakeAsync(NetworkStream stream, CancellationToken token)
    {
        var request = new StringBuilder();
        var buffer = new byte[1024];
        while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read <= 0)
                return false;

            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (request.Length > 16 * 1024)
                return false;
        }

        var key = default(string);
        foreach (var line in request.ToString().Split("\r\n"))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;

            if (line[..separator].Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
            {
                key = line[(separator + 1)..].Trim();
                break;
            }
        }

        if (string.IsNullOrEmpty(key))
            return false;

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + ButtplugProtocol.WebSocketGuid)));
        var response = "HTTP/1.1 101 Switching Protocols\r\n"
                     + "Connection: Upgrade\r\n"
                     + "Upgrade: websocket\r\n"
                     + $"Sec-WebSocket-Accept: {accept}\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token);
        await stream.FlushAsync(token);
        return true;
    }

    private string Dispatch(string payload)
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
            return ButtplugProtocol.Serialize(ButtplugProtocol.Error(0, ButtplugProtocol.ErrorMessage, "消息不是合法的 JSON 数组"));
        }

        foreach (var item in messages.OfType<JObject>())
        {
            var property = item.Properties().FirstOrDefault();
            if (property == null)
                continue;

            var type = property.Name;
            var body = property.Value as JObject ?? [];
            var id = body.Value<uint?>("Id") ?? 0;

            try
            {
                DispatchMessage(type, body, id, responses);
            }
            catch (Exception e)
            {
                Logger.Warn(e, "Buttplug 服务器：处理 {0} 时出错", type);
                responses.Add(ButtplugProtocol.Error(id, ButtplugProtocol.ErrorUnknown, e.Message));
            }
        }

        if (responses.Count == 0)
            return null;

        RaiseStatus();
        return ButtplugProtocol.Serialize([.. responses]);
    }

    private void DispatchMessage(string type, JObject body, uint id, List<JObject> responses)
    {
        switch (type)
        {
            case "RequestServerInfo":
                responses.Add(ButtplugProtocol.ServerInfo(id, _options.ServerName));
                break;

            case "Ping":
                responses.Add(ButtplugProtocol.Ok(id));
                break;

            case "StartScanning":
                responses.Add(ButtplugProtocol.Ok(id));
                responses.Add(ButtplugProtocol.DeviceAdded(_bridge.DeviceName, _bridge.Actuators));
                break;

            case "StopScanning":
                responses.Add(ButtplugProtocol.Ok(id));
                break;

            case "RequestDeviceList":
                responses.Add(ButtplugProtocol.DeviceList(id, _bridge.DeviceName, _bridge.Actuators));
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

            case "RotateCmd":
                // 没有旋转型执行器（TCode 的 R 轴是位置型，按 LinearCmd 暴露），确认即可
                responses.Add(ButtplugProtocol.Ok(id));
                break;

            default:
                Logger.Debug("Buttplug 服务器：收到未处理的消息 {0}", type);
                responses.Add(ButtplugProtocol.Error(id, ButtplugProtocol.ErrorMessage, $"不支持的消息：{type}"));
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
}
