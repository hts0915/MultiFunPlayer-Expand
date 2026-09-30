using MultiFunPlayer.OutputTarget.ViewModels;
using NLog;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SerialPortInfo = MultiFunPlayer.OutputTarget.ViewModels.SerialOutputTarget.SerialPortInfo;

namespace MultiFunPlayer.OutputTarget;

/// <summary>串口的可用状态。</summary>
internal enum SerialPortAvailability
{
    /// <summary>端口存在，且当前没有被其他程序占用。</summary>
    Free,
    /// <summary>端口存在，但已经被其他程序打开（例如管理软件、播放器）。</summary>
    Busy,
    /// <summary>端口当前不存在（设备没插、没上电或驱动没起来）。</summary>
    Missing,
    /// <summary>探测失败，原因未知。</summary>
    Unknown,
}

/// <summary>
/// 启动时判断"数据线连的设备端口能不能用"所需的工具。
/// 探测只用 CreateFile 独占打开再立刻关闭，不碰 DCB/DTR/RTS，所以不会让设备抖一下。
/// </summary>
internal static class SerialPortUtils
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;

    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_ACCESS_DENIED = 5;

    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly Regex BluetoothMacRegex = new(@"DEV_(?<mac>[0-9A-F]{12})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
                                            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>探测端口：能不能独占打开。独占打开失败即代表有别的程序占着。</summary>
    public static SerialPortAvailability Probe(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
            return SerialPortAvailability.Missing;

        var handle = IntPtr.Zero;
        try
        {
            handle = CreateFile($@"\\.\{portName}", GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle != InvalidHandleValue)
                return SerialPortAvailability.Free;

            var error = Marshal.GetLastWin32Error();
            var availability = error switch
            {
                ERROR_ACCESS_DENIED => SerialPortAvailability.Busy,
                ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND => SerialPortAvailability.Missing,
                _ => SerialPortAvailability.Unknown,
            };

            Logger.Debug("Probe \"{0}\" -> {1} [Win32Error: {2}]", portName, availability, error);
            return availability;
        }
        catch (Exception e)
        {
            Logger.Warn(e, "Failed to probe serial port \"{0}\"", portName);
            return SerialPortAvailability.Unknown;
        }
        finally
        {
            if (handle != IntPtr.Zero && handle != InvalidHandleValue)
                CloseHandle(handle);
        }
    }

    /// <summary>
    /// 找数据线连接设备的串口。默认按 CH340 的 VID/PID（VID_1A86&amp;PID_7523）匹配，
    /// 匹配串可以用分号分隔多个片段；都不中时再退回按芯片型号名匹配。
    /// </summary>
    public static SerialPortInfo FindUsbDevicePort(IEnumerable<SerialPortInfo> ports, string deviceIdMatch)
    {
        if (ports == null)
            return null;

        if (!string.IsNullOrWhiteSpace(deviceIdMatch))
        {
            foreach (var match in deviceIdMatch.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var port = ports.FirstOrDefault(p => p.DeviceID != null && p.DeviceID.Contains(match, StringComparison.OrdinalIgnoreCase));
                if (port != null)
                    return port;
            }
        }

        return ports.FirstOrDefault(p => (p.Name != null && p.Name.Contains("CH340", StringComparison.OrdinalIgnoreCase))
                                      || (p.Description != null && p.Description.Contains("CH340", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// 找设备的蓝牙串口：先按名字找到已配对的蓝牙设备拿到它的 MAC，
    /// 再找 DeviceID 里带这个 MAC 的 SPP 串口。
    /// 蓝牙设备 DeviceID 形如 BTHENUM\DEV_5CF15014D593\...，对应串口形如 BTHENUM\{00001101-...}_LOCALMFG&amp;0002\...&amp;5CF15014D593_...
    /// </summary>
    public static SerialPortInfo FindBluetoothPort(IEnumerable<SerialPortInfo> ports, string deviceNameMatch)
    {
        if (ports == null || string.IsNullOrWhiteSpace(deviceNameMatch))
            return null;

        var mac = FindBluetoothDeviceMac(deviceNameMatch);
        if (mac == null)
        {
            Logger.Info("No paired bluetooth device matching \"{0}\"", deviceNameMatch);
            return null;
        }

        var port = ports.FirstOrDefault(p => p.DeviceID != null
                                          && p.DeviceID.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase)
                                          && p.DeviceID.Contains(mac, StringComparison.OrdinalIgnoreCase));
        Logger.Info("Bluetooth device \"{0}\" [MAC: {1}] -> port {2}", deviceNameMatch, mac, port?.PortName ?? "<none>");
        return port;
    }

    /// <summary>判断某个串口是不是蓝牙串口（用于下拉列表只展示蓝牙口）。</summary>
    public static bool IsBluetoothPort(SerialPortInfo port)
        => port?.DeviceID != null && port.DeviceID.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase);

    private static string FindBluetoothDeviceMac(string deviceNameMatch)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var searcher = new ManagementObjectSearcher(new SelectQuery("Win32_PnPEntity"));
            foreach (var o in searcher.Get())
            {
                using var entity = (ManagementObject)o;

                var deviceId = entity.GetPropertyValue("DeviceID") as string;
                if (string.IsNullOrEmpty(deviceId) || !deviceId.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = entity.GetPropertyValue("Name") as string;
                if (string.IsNullOrEmpty(name) || !name.Contains(deviceNameMatch, StringComparison.OrdinalIgnoreCase))
                    continue;

                var match = BluetoothMacRegex.Match(deviceId);
                if (match.Success)
                {
                    Logger.Debug("Matched bluetooth device \"{0}\" in {1:F0}ms", name, stopwatch.Elapsed.TotalMilliseconds);
                    return match.Groups["mac"].Value;
                }
            }
        }
        catch (Exception e)
        {
            Logger.Warn(e, "Failed to look up bluetooth device \"{0}\"", deviceNameMatch);
        }

        Logger.Debug("Bluetooth device lookup took {0:F0}ms", stopwatch.Elapsed.TotalMilliseconds);
        return null;
    }
}
