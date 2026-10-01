using MultiFunPlayer.Common;
using NLog;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MultiFunPlayer.OutputTarget;

/// <summary>
/// 用 TCode 的查询指令判断设备到底在不在。
/// <br/>
/// <c>D1</c> 是"查询设备信息"而不是动作指令，发它不会让设备动，用它探测是安全的；
/// 设备会回一串带 <c>TCode</c> 的信息。
/// <br/>
/// 为什么必须探测：UDP 是无连接的，"连接"永远不会失败，地址填错、设备没开机、
/// 设备根本没配网都会显示"已连接"，但设备一动不动 —— 光看界面分不清是哪一种。
/// </summary>
internal static class TcodeDeviceProbe
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public const string Command = "D1\n";
    public const string SuccessKeyword = "TCode";

    private const int TimeoutMilliseconds = 1500;
    private const int UdpAttempts = 3;

    public static bool TryProbeUdp(EndPoint endpoint, out string response, out string error)
    {
        response = null;
        error = null;

        using var client = new UdpClient();
        try
        {
            client.Client.SendTimeout = TimeoutMilliseconds;
            client.Client.ReceiveTimeout = TimeoutMilliseconds;
            client.Connect(endpoint);
        }
        catch (Exception e)
        {
            error = DescribeFailure(e, endpoint);
            return false;
        }

        var payload = Encoding.ASCII.GetBytes(Command);
        for (var attempt = 1; attempt <= UdpAttempts; attempt++)
        {
            try
            {
                client.Send(payload, payload.Length);

                var remote = default(IPEndPoint);
                var text = Encoding.ASCII.GetString(client.Receive(ref remote)).Trim();
                if (string.IsNullOrEmpty(text))
                    continue;

                response = text;
                if (text.Contains(SuccessKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Info("UDP 探测成功 [Endpoint: {0}, Attempt: {1}, Response: \"{2}\"]", endpoint.ToUriString(), attempt, text);
                    return true;
                }

                Logger.Debug("UDP 探测收到非预期回应 [Endpoint: {0}, Response: \"{1}\"]", endpoint.ToUriString(), text);
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock) { }
            catch (Exception e)
            {
                error = DescribeFailure(e, endpoint);
                return false;
            }
        }

        error = $"发了 {UdpAttempts} 次 {Command.Trim()} 都没有收到任何回应";
        return false;
    }

    public static bool TryProbeTcp(EndPoint endpoint, out string response, out string error)
    {
        response = null;
        error = null;

        if (!TryResolveAddress(endpoint, out var address, out error))
            return false;

        var port = endpoint switch
        {
            IPEndPoint ip => ip.Port,
            DnsEndPoint dns => dns.Port,
            _ => 0,
        };

        using var client = new TcpClient() { NoDelay = true };
        try
        {
            if (!client.ConnectAsync(address, port).Wait(TimeoutMilliseconds))            {
                error = $"连接 {endpoint.ToUriString()} 超时（{TimeoutMilliseconds} 毫秒）";
                return false;
            }
        }
        catch (Exception e)
        {
            error = DescribeFailure(e, endpoint);
            return false;
        }

        try
        {
            var stream = client.GetStream();
            stream.ReadTimeout = TimeoutMilliseconds;
            stream.WriteTimeout = TimeoutMilliseconds;

            var payload = Encoding.ASCII.GetBytes(Command);
            stream.Write(payload, 0, payload.Length);

            var buffer = new byte[256];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                error = "设备把连接关掉了，没有返回数据";
                return false;
            }

            var text = Encoding.ASCII.GetString(buffer, 0, read).Trim();
            response = text;
            if (text.Contains(SuccessKeyword, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info("TCP 探测成功 [Endpoint: {0}, Response: \"{1}\"]", endpoint.ToUriString(), text);
                return true;
            }

            error = $"设备有回应，但内容里没有 TCode 标识：\"{text}\"";
            return false;
        }
        catch (Exception e)
        {
            error = DescribeFailure(e, endpoint);
            return false;
        }
    }

    private static bool TryResolveAddress(EndPoint endpoint, out IPAddress address, out string error)
    {
        address = null;
        error = null;

        if (endpoint is IPEndPoint ipEndPoint)
        {
            address = ipEndPoint.Address;
            return true;
        }

        if (endpoint is DnsEndPoint dnsEndPoint)
        {
            try
            {
                address = Dns.GetHostAddresses(dnsEndPoint.Host)
                            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                         ?? Dns.GetHostAddresses(dnsEndPoint.Host).FirstOrDefault();
            }
            catch (Exception e)
            {
                error = $"无法解析主机名 \"{dnsEndPoint.Host}\"（{e.Message}）";
                return false;
            }

            if (address == null)
            {
                error = $"无法解析主机名 \"{dnsEndPoint.Host}\"";
                return false;
            }

            return true;
        }

        error = $"不支持的目标地址类型 {endpoint.GetType().Name}";
        return false;
    }

    private static string DescribeFailure(Exception exception, EndPoint endpoint)
    {
        if (exception is AggregateException aggregate && aggregate.InnerException != null)
            exception = aggregate.InnerException;

        return exception switch
        {
            SocketException socket when socket.SocketErrorCode == SocketError.HostNotFound
                => $"无法解析主机名（{endpoint.ToUriString()}）",
            SocketException socket when socket.SocketErrorCode == SocketError.ConnectionRefused
                => $"对方拒绝连接（{endpoint.ToUriString()}），设备可能没在监听这个端口",
            SocketException socket when socket.SocketErrorCode == SocketError.NetworkUnreachable
                => $"网络不可达（{endpoint.ToUriString()}），电脑和设备可能不在同一个网络",
            SocketException socket when socket.SocketErrorCode == SocketError.TimedOut
                => $"连接 {endpoint.ToUriString()} 超时",
            // UDP 收到 ICMP 端口不可达时 Windows 报这个：地址上有主机，但没人在那个端口上听
            SocketException socket when socket.SocketErrorCode == SocketError.ConnectionReset
                => $"这个地址上没有设备在监听（{endpoint.ToUriString()}），设备的 IP 可能已经变了；建议改用 tcode.local",
            _ => exception.Message,
        };
    }
}
