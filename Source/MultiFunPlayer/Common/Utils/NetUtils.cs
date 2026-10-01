using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace MultiFunPlayer.Common;

public static partial class NetUtils
{
    private static readonly SocketsHttpHandler _handler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        MaxConnectionsPerServer = 5
    };

    public static HttpClient CreateHttpClient()
        => new(_handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

    public static EndPoint ParseEndpoint(string endpointString)
    {
        if (string.IsNullOrWhiteSpace(endpointString))
            return null;

        var match = EndpointRegex.Match(endpointString);
        if (!match.Success)
            return null;

        var ipOrHost = match.Groups["ipOrHost"].Value;
        var port = int.Parse(match.Groups["port"].Value);

        if (match.Groups["family"].Success)
            return new DnsEndPoint(ipOrHost, port);

        return Uri.CheckHostName(ipOrHost) switch
        {
            UriHostNameType.IPv4 or UriHostNameType.IPv6 when IPAddress.TryParse(ipOrHost, out var ipAddress) => new IPEndPoint(ipAddress, port),
            UriHostNameType.Dns => new DnsEndPoint(ipOrHost, port),
            _ => null
        };
    }

    public static bool TryParseEndpoint(string endpointString, out EndPoint endpoint)
    {
        endpoint = ParseEndpoint(endpointString);
        return endpoint != null;
    }

    /// <summary>把端点解析成 IP（主机名会走 DNS）。解析不出来返回 null。</summary>
    public static IPAddress ResolveAddress(EndPoint endpoint)
    {
        try
        {
            return endpoint switch
            {
                IPEndPoint ip => ip.Address,
                DnsEndPoint dns => Dns.GetHostAddresses(dns.Host)
                                      .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                                   ?? Dns.GetHostAddresses(dns.Host).FirstOrDefault(),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    public static IEnumerable<IPAddress> GetAllLocalAddresses()
        => NetworkInterface.GetAllNetworkInterfaces()
                           .Where(i => i.OperationalStatus == OperationalStatus.Up)
                           .Select(i => i.GetIPProperties())
                           .SelectMany(p => p.UnicastAddresses)
                           .Select(a => a.Address);

    public static bool IsLocalAddress(EndPoint endpoint)
    {
        var addresses = GetAllLocalAddresses().ToList();
        return endpoint.GetAddresses().Any(addresses.Contains);
    }

    /// <summary>
    /// 目标地址是否和本机任一网卡处于同一个 /24 网段。
    /// 设备走无线、电脑走有线时经常各在一个网段（例如设备 192.168.0.x、电脑 192.168.1.x），
    /// 这种情况怎么连都不通，必须在界面上明确提示，而不是只报"连接超时"。
    /// </summary>
    public static bool IsOnLocalSubnet(EndPoint endpoint)
    {
        if (endpoint is not IPEndPoint ipEndPoint || ipEndPoint.Address.AddressFamily != AddressFamily.InterNetwork)
            return true;

        var target = ipEndPoint.Address.GetAddressBytes();
        foreach (var local in GetAllLocalAddresses())
        {
            if (local.AddressFamily != AddressFamily.InterNetwork)
                continue;

            var address = local.GetAddressBytes();
            if (address[0] == target[0] && address[1] == target[1] && address[2] == target[2])
                return true;
        }

        return false;
    }

    /// <summary>本机所有 IPv4 地址，用于提示"设备和你不在一个网络"。</summary>
    public static string DescribeLocalAddresses()
        => string.Join("、", GetAllLocalAddresses().Where(a => a.AddressFamily == AddressFamily.InterNetwork));

    public static async ValueTask<bool> IsLocalAddressAsync(EndPoint endpoint)
    {
        var addresses = GetAllLocalAddresses().ToList();
        return (await endpoint.GetAddressesAsync()).Any(addresses.Contains);
    }

    [GeneratedRegex(@"^(?:(?<family>InterNetwork|InterNetworkV6|Unspecified)\/)?(?<ipOrHost>.+):(?<port>\d+)$")]
    private static partial Regex EndpointRegex { get; }
}
