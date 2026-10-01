using NLog;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace MultiFunPlayer.OutputTarget;

/// <summary>
/// 在局域网里找 TCode 设备：向本机所在网段的每台主机发一条查询指令（<c>D1</c>），
/// 谁的回应里有 <c>TCode</c> 就是设备。
/// <br/>
/// 用途：设备换了 IP（DHCP 重新分配）之后不用用户去路由器里查，
/// 程序自己找到新地址并填回设置。首选还是 <c>tcode.local</c>（mDNS，设备固件支持），
/// 扫描是 mDNS 被路由器屏蔽时的兜底。
/// </summary>
internal static class TcodeDeviceScanner
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public const string Command = "D1\n";
    public const string SuccessKeyword = "TCode";
    public const int DefaultPort = 8000;

    /// <summary>扫描本机所有 IPv4 网段，返回回应了 TCode 的设备地址。</summary>
    public static IReadOnlyList<IPEndPoint> Scan(int port = DefaultPort, int timeoutMilliseconds = 1500)
    {
        var devices = new List<IPEndPoint>();
        var targets = GetSubnetAddresses().ToList();
        if (targets.Count == 0)
        {
            Logger.Warn("TCode 扫描：没有找到可扫描的局域网网段");
            return devices;
        }

        Logger.Info("TCode 扫描开始 [目标主机数: {0}, 端口: {1}]", targets.Count, port);

        using var client = new UdpClient();
        try
        {
            client.Client.ReceiveTimeout = 200;
            client.Client.SendTimeout = 500;
            client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        }
        catch (Exception e)
        {
            Logger.Warn(e, "TCode 扫描：创建套接字失败");
            return devices;
        }

        var payload = Encoding.ASCII.GetBytes(Command);
        var sent = 0;
        foreach (var address in targets)
        {
            try
            {
                client.Send(payload, payload.Length, new IPEndPoint(address, port));
                sent++;
            }
            catch (Exception e)
            {
                Logger.Trace(e, "TCode 扫描：向 {0} 发送失败", address);
            }
        }

        Logger.Debug("TCode 扫描已发出 {0}/{1} 个探测包，等待回应…", sent, targets.Count);

        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        var unreachable = 0;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                var remote = default(IPEndPoint);
                var text = Encoding.ASCII.GetString(client.Receive(ref remote)).Trim();
                if (!text.Contains(SuccessKeyword, StringComparison.OrdinalIgnoreCase))
                    continue;

                // 去重：同一台设备可能回多条
                if (devices.Any(d => d.Address.Equals(remote.Address)))
                    continue;

                Logger.Info("TCode 扫描发现设备 [{0}:{1}] 回应：\"{2}\"", remote.Address, remote.Port, text);
                devices.Add(new IPEndPoint(remote.Address, port));
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
            {
                // 正常：这一轮没数据了，继续等剩余时间（多等几轮能收到慢的回应）
            }
            catch (SocketException e)
            {
                // 网段里绝大多数主机都不在，会回 ICMP 端口不可达，Windows 在未连接的 UDP 套接字上
                // 把它报成 ConnectionReset —— 这非常常见，绝不能因此退出循环，否则扫描一开始就结束了
                if (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.NetworkUnreachable
                                       or SocketError.HostUnreachable or SocketError.MessageSize)
                {
                    unreachable++;
                    continue;
                }

                Logger.Debug(e, "TCode 扫描：接收回应出错（{0}），停止等待", e.SocketErrorCode);
                break;
            }
            catch (Exception e)
            {
                Logger.Debug(e, "TCode 扫描：接收回应出错，停止等待");
                break;
            }
        }

        Logger.Info("TCode 扫描结束，找到 {0} 台设备（收到 {1} 个端口不可达回应）", devices.Count, unreachable);
        return devices;
    }

    /// <summary>本机所有已启用网卡的 IPv4 地址，展开成该网段内的全部主机地址（跳过自己、网络号与广播）。</summary>
    private static IEnumerable<IPAddress> GetSubnetAddresses()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
                continue;
            if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (IPAddress.IsLoopback(unicast.Address))
                    continue;

                var address = unicast.Address.GetAddressBytes();
                var prefixLength = unicast.PrefixLength;

                // 只扫得了 /30 ~ /24 这一档：再大就太多主机，再小就没意义
                if (prefixLength is < 24 or > 30)
                {
                    Logger.Debug("TCode 扫描：跳过网段 {0}/{1}（前缀长度不合适）", unicast.Address, prefixLength);
                    continue;
                }

                var hostBits = 32 - prefixLength;
                var hostCount = (1 << hostBits) - 2;
                var baseValue = (uint)(address[0] << 24 | address[1] << 16 | address[2] << 8 | address[3]);
                var networkValue = baseValue & (uint.MaxValue << hostBits);

                for (var i = 1; i <= hostCount; i++)
                {
                    var value = networkValue + (uint)i;
                    var bytes = new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
                    var candidate = new IPAddress(bytes);
                    if (!candidate.Equals(unicast.Address))
                        yield return candidate;
                }
            }
        }
    }
}
