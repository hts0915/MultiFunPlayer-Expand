using NLog;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiFunPlayer.OutputTarget;

internal sealed record SerialPortOptions(
    string PortName,
    int BaudRate,
    Parity Parity,
    StopBits StopBits,
    int DataBits,
    Handshake Handshake,
    bool DtrEnable,
    bool RtsEnable,
    int ReadTimeout,
    int WriteTimeout,
    int ReadBufferSize,
    int WriteBufferSize);

/// <summary>串口传输的抽象，方便在 Win32 直控与系统串口库之间切换。</summary>
internal interface ISerialTransport : IDisposable
{
    bool IsOpen { get; }
    int BytesToRead { get; }
    string ReadExisting();
    void Write(string text);

    /// <summary>日志里区分实际走的是哪条路径。</summary>
    string Description { get; }
}

internal static class SerialTextEncoding
{
    /// <summary>
    /// 设备回应的文字是 UTF-8（会带中文状态，例如「AP模式」「Station模式」），
    /// 用 ASCII 解码会全变成问号。发送的命令本身是纯 ASCII，两种编码等价。
    /// </summary>
    public static Encoding Device { get; } = new UTF8Encoding(false);
}

internal static class SerialTransport
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// 优先用 Win32 直控打开串口（全程不碰 DTR/RTS），失败时才退回系统串口库。
    /// <br/>
    /// 为什么需要这样：.NET 的 <c>SerialPort</c> 在 <c>Handshake.None</c> 下会先把 DTR/RTS
    /// 拉高（InitializeDCB 里写 ENABLE），然后才按 DtrEnable/RtsEnable 用 EscapeCommFunction 改回去，
    /// 于是**每次打开串口都会产生一次 DTR/RTS 跳变**。OSR 设备固件会跟着 DTR 的变化复位/归位，
    /// 表现出来就是"连接和断开设备的瞬间乱扭"。自己控制 DCB 可以把这两个信号一直钉在关闭状态，
    /// 连接与断开都不产生任何跳变。
    /// </summary>
    public static ISerialTransport Open(SerialPortOptions options)
    {
        try
        {
            var transport = new Win32SerialTransport(options);
            transport.Open();
            Logger.Info("{0} 已用 Win32 直控打开 [DTR: {1}, RTS: {2}]", options.PortName, options.DtrEnable, options.RtsEnable);
            return transport;
        }
        catch (Exception e)
        {
            Logger.Warn(e, "Win32 直控打开 {0} 失败，退回系统串口库（此时连接瞬间可能出现 DTR/RTS 跳变）", options.PortName);

            var fallback = new ManagedSerialTransport(options);
            fallback.Open();
            return fallback;
        }
    }
}

/// <summary>
/// 直接用 Win32 控制串口：自己 CreateFile + SetCommState + SetCommTimeouts + ReadFile/WriteFile。
/// 关键点是**不调用 EscapeCommFunction**，所以 DTR/RTS 完全按 DCB 里的设置（默认关闭）保持不动。
/// </summary>
internal sealed class Win32SerialTransport(SerialPortOptions options) : ISerialTransport
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint PURGE_RXCLEAR = 0x0008;
    private const uint PURGE_TXCLEAR = 0x0004;
    private const uint SETDTR = 5;
    private const uint SETRTS = 3;
    private const uint DTR_CONTROL_DISABLE = 0;
    private const uint DTR_CONTROL_ENABLE = 1;
    private const uint RTS_CONTROL_DISABLE = 0;
    private const uint RTS_CONTROL_ENABLE = 1;
    private const uint RTS_CONTROL_HANDSHAKE = 2;
    private const uint MAXDWORD = 0xFFFFFFFF;

    private static readonly IntPtr InvalidHandle = new(-1);

    private IntPtr _handle = InvalidHandle;

    public string Description => "Win32";
    public bool IsOpen => _handle != InvalidHandle;

    public int BytesToRead
    {
        get
        {
            if (!IsOpen)
                return 0;

            var status = new ComStat();
            if (!ClearCommError(_handle, out _, ref status))
                return 0;

            return (int)status.cbInQue;
        }
    }

    public void Open()
    {
        if (IsOpen)
            return;

        var handle = CreateFile($@"\\.\{options.PortName}", GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == InvalidHandle)
            throw new IOException($"无法打开串口 {options.PortName}：{LastError()}");

        try
        {
            SetupComm(handle, (uint)options.ReadBufferSize, (uint)options.WriteBufferSize);

            var dcb = BuildDcb(handle);
            if (!SetCommState(handle, ref dcb))
                throw new IOException($"设置串口参数失败（{options.PortName}）：{LastError()}");

            var timeouts = new CommTimeouts()
            {
                // 读：有数据就立刻返回，没数据也立刻返回（非阻塞轮询）
                ReadIntervalTimeout = MAXDWORD,
                ReadTotalTimeoutMultiplier = MAXDWORD,
                ReadTotalTimeoutConstant = 0,
                WriteTotalTimeoutMultiplier = 0,
                WriteTotalTimeoutConstant = (uint)Math.Max(0, options.WriteTimeout),
            };

            if (!SetCommTimeouts(handle, ref timeouts))
                throw new IOException($"设置串口超时失败（{options.PortName}）：{LastError()}");

            PurgeComm(handle, PURGE_RXCLEAR | PURGE_TXCLEAR);

            _handle = handle;
            handle = InvalidHandle;

            // 只有用户显式打开开关时才去动 DTR/RTS；默认一次都不碰，避免设备复位
            if (options.DtrEnable && !EscapeCommFunction(_handle, SETDTR))
                Logger.Warn("设置 {0} 的 DTR 失败：{1}", options.PortName, LastError());
            if (options.RtsEnable && !EscapeCommFunction(_handle, SETRTS))
                Logger.Warn("设置 {0} 的 RTS 失败：{1}", options.PortName, LastError());
        }
        finally
        {
            if (handle != InvalidHandle)
                CloseHandle(handle);
        }
    }

    private Dcb BuildDcb(IntPtr handle)
    {
        var dcb = new Dcb() { DCBlength = (uint)Marshal.SizeOf<Dcb>() };
        if (!GetCommState(handle, ref dcb))
            throw new IOException($"读取串口参数失败（{options.PortName}）：{LastError()}");

        dcb.BaudRate = (uint)options.BaudRate;
        dcb.ByteSize = (byte)options.DataBits;
        dcb.Parity = (byte)options.Parity;
        dcb.StopBits = options.StopBits switch
        {
            StopBits.One => 0,
            StopBits.OnePointFive => 1,
            StopBits.Two => 2,
            _ => 0,
        };

        dcb.XonChar = 17;
        dcb.XoffChar = 19;
        dcb.ErrorChar = 0;
        dcb.EofChar = 0;
        dcb.EvtChar = 0;
        if (dcb.XonLim == 0) dcb.XonLim = 2048;
        if (dcb.XoffLim == 0) dcb.XoffLim = 512;

        // DCB.Flags 是位域：fBinary(0) fParity(1) fOutxCtsFlow(2) fOutxDsrFlow(3)
        // fDtrControl(4-5) fDsrSensitivity(6) fTXContinueOnXoff(7) fOutX(8) fInX(9)
        // fErrorChar(10) fNull(11) fRtsControl(12-13) fAbortOnError(14)
        var dtrControl = options.DtrEnable ? DTR_CONTROL_ENABLE : DTR_CONTROL_DISABLE;
        var rtsControl = options.RtsEnable ? RTS_CONTROL_ENABLE : RTS_CONTROL_DISABLE;

        var flags = 1u;                             // fBinary
        if (options.Parity != Parity.None)
            flags |= 1u << 1;                       // fParity

        switch (options.Handshake)
        {
            case Handshake.XOnXOff:
                flags |= (1u << 8) | (1u << 9);     // fOutX | fInX
                break;
            case Handshake.RequestToSend:
                flags |= 1u << 2;                   // fOutxCtsFlow
                rtsControl = RTS_CONTROL_HANDSHAKE;
                break;
            case Handshake.RequestToSendXOnXOff:
                flags |= 1u << 2;                   // fOutxCtsFlow
                flags |= (1u << 8) | (1u << 9);     // fOutX | fInX
                rtsControl = RTS_CONTROL_HANDSHAKE;
                break;
        }

        flags |= (dtrControl & 0x3u) << 4;
        flags |= (rtsControl & 0x3u) << 12;
        dcb.Flags = flags;

        return dcb;
    }

    public string ReadExisting()
    {
        var available = BytesToRead;
        if (!IsOpen || available <= 0)
            return string.Empty;

        var buffer = new byte[available];
        if (!ReadFile(_handle, buffer, (uint)available, out var read, IntPtr.Zero) || read == 0)
            return string.Empty;

        return SerialTextEncoding.Device.GetString(buffer, 0, (int)read);
    }

    public void Write(string text)
    {
        if (!IsOpen || string.IsNullOrEmpty(text))
            return;

        var buffer = Encoding.ASCII.GetBytes(text);
        while (buffer.Length > 0)
        {
            if (!WriteFile(_handle, buffer, (uint)buffer.Length, out var written, IntPtr.Zero))
                throw new IOException($"串口写入失败（{options.PortName}）：{LastError()}");

            if (written == 0 || written >= buffer.Length)
                break;

            buffer = buffer[(int)written..];
        }
    }

    public void Dispose()
    {
        if (_handle == InvalidHandle)
            return;

        CloseHandle(_handle);
        _handle = InvalidHandle;
    }

    private static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public uint DCBlength;
        public uint BaudRate;
        public uint Flags;
        public ushort wReserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EvtChar;
        public ushort wReserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ComStat
    {
        public uint Flags;
        public uint cbInQue;
        public uint cbOutQue;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
                                            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCommState(IntPtr handle, ref Dcb dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCommState(IntPtr handle, ref Dcb dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCommTimeouts(IntPtr handle, ref CommTimeouts timeouts);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupComm(IntPtr handle, uint inQueue, uint outQueue);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PurgeComm(IntPtr handle, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClearCommError(IntPtr handle, out uint errors, ref ComStat status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(IntPtr handle, byte[] buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(IntPtr handle, byte[] buffer, uint bytesToWrite, out uint bytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EscapeCommFunction(IntPtr handle, uint function);
}

/// <summary>系统串口库的实现，仅在 Win32 直控失败时作为兜底。</summary>
internal sealed class ManagedSerialTransport(SerialPortOptions options) : ISerialTransport
{
    private readonly SerialPort _port = new()
    {
        PortName = options.PortName,
        BaudRate = options.BaudRate,
        Parity = options.Parity,
        StopBits = options.StopBits,
        DataBits = options.DataBits,
        Handshake = options.Handshake,
        DtrEnable = options.DtrEnable,
        RtsEnable = options.RtsEnable,
        ReadTimeout = options.ReadTimeout,
        WriteTimeout = options.WriteTimeout,
        ReadBufferSize = options.ReadBufferSize,
        WriteBufferSize = options.WriteBufferSize,
        Encoding = SerialTextEncoding.Device,
    };

    public string Description => "系统串口库";
    public bool IsOpen => _port.IsOpen;
    public int BytesToRead => _port.IsOpen ? _port.BytesToRead : 0;

    public void Open() => _port.Open();
    public string ReadExisting() => _port.IsOpen ? _port.ReadExisting() : string.Empty;
    public void Write(string text)
    {
        if (_port.IsOpen)
            _port.Write(text);
    }

    public void Dispose()
    {
        try { _port.Dispose(); }
        catch (Exception e) { Logger.Warn(e, "关闭串口 {0} 时出错", options.PortName); }
    }

    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
}
