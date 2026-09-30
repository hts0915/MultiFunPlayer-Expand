using Microsoft.Win32;
using MultiFunPlayer.Common;
using MultiFunPlayer.Input;
using MultiFunPlayer.Input.TCode;
using MultiFunPlayer.Property;
using MultiFunPlayer.Shortcut;
using MultiFunPlayer.UI;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;

namespace MultiFunPlayer.OutputTarget.ViewModels;

[DisplayName("Serial")]
internal sealed class SerialOutputTarget(int instanceIndex, IEventAggregator eventAggregator, IDeviceAxisValueProvider valueProvider, IInputProcessorFactory inputProcessorFactory)
    : ThreadAbstractOutputTarget(instanceIndex, eventAggregator, valueProvider)
{
    private CancellationTokenSource _refreshCancellationSource = new();

    /// <summary>
    /// 刚连接后这段时间内，下发给设备的过渡时间（TCode 的 I 参数）不会被压到极小值。
    /// 首帧的 elapsed 接近 0，会发出 I0，设备会以最大速度猛地弹到目标位置；这里给一个下限做缓冲。
    /// </summary>
    private const double ConnectRampDurationMilliseconds = 1000;
    private const double ConnectRampMinimumIntervalMilliseconds = 250;

    /// <summary>刚打开串口后先安静这么久再发第一条指令，避免把指令打进设备可能的重启过程。</summary>
    private const double FirstWriteDelayMilliseconds = 200;

    public override ConnectionStatus Status { get; protected set; }
    public bool IsConnected => Status == ConnectionStatus.Connected;
    public bool IsDisconnected => Status == ConnectionStatus.Disconnected;
    public bool IsConnectBusy => Status is ConnectionStatus.Connecting or ConnectionStatus.Disconnecting;
    public bool CanToggleConnect => !IsConnectBusy && !IsRefreshBusy && SelectedSerialPortDeviceId != null;

    public DeviceAxisUpdateType UpdateType { get; set; } = DeviceAxisUpdateType.FixedUpdate;
    public bool CanChangeUpdateType => !IsConnectBusy && !IsConnected;

    public ObservableConcurrentCollection<SerialPortInfo> SerialPorts { get; set; } = [];
    public SerialPortInfo SelectedSerialPort { get; set; }
    public string SelectedSerialPortDeviceId { get; set; }

    public int BaudRate { get; set; } = 115200;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
    public int DataBits { get; set; } = 8;
    public Handshake Handshake { get; set; } = Handshake.None;

    // 默认关闭。DTR/RTS 只要被拉高再放下，OSR 设备固件就会跟着复位归位，
    // 表现就是"连接和断开的瞬间设备乱扭"。配合 SerialTransport 里的 Win32 直控，
    // 默认全程不碰这两个信号；确实需要时可以在高级设置里手动打开。
    public bool DtrEnable { get; set; } = false;
    public bool RtsEnable { get; set; } = false;
    public int ReadTimeout { get; set; } = 250;
    public int WriteTimeout { get; set; } = 250;
    public int WriteBufferSize { get; set; } = 2048;
    public int ReadBufferSize { get; set; } = 4096;

    public IReadOnlyCollection<int> AvailableBaudRates { get; } = [50, 75, 110, 134, 150, 200, 300, 600, 1200, 1800, 2400, 4800, 9600, 19200, 28800, 38400, 57600, 76800, 115200, 230400, 460800, 576000, 921600];

    protected override IUpdateContext RegisterUpdateContext(DeviceAxisUpdateType updateType) => updateType switch
    {
        DeviceAxisUpdateType.FixedUpdate => new TCodeThreadFixedUpdateContext(),
        DeviceAxisUpdateType.PolledUpdate => new ThreadPolledUpdateContext(),
        _ => null,
    };

    protected override void OnInitialActivate()
    {
        base.OnInitialActivate();
        if (Status == ConnectionStatus.Disconnected)
            _ = RefreshPorts();
    }

    public bool CanChangePort => !IsRefreshBusy && !IsConnectBusy && !IsConnected;
    public bool IsRefreshBusy { get; set; }
    public bool CanRefreshPorts => !IsRefreshBusy && !IsConnectBusy && !IsConnected;

    private bool _isRefreshingFlag;
    public async Task RefreshPorts()
    {
        if (Interlocked.CompareExchange(ref _isRefreshingFlag, true, false))
            return;

        try
        {
            var token = _refreshCancellationSource.Token;
            token.ThrowIfCancellationRequested();

            IsRefreshBusy = true;
            await DoRefreshPorts(token);
        }
        catch (Exception e)
        {
            Logger.Warn(e, $"{Identifier} port refresh failed with exception");
        }
        finally
        {
            Interlocked.Exchange(ref _isRefreshingFlag, false);
            IsRefreshBusy = false;
        }

        async Task DoRefreshPorts(CancellationToken token)
        {
            await Task.Delay(250, token);

            var serialPorts = new List<SerialPortInfo>();
            var scope = new ManagementScope("\\\\.\\ROOT\\cimv2");
            var observer = new ManagementOperationObserver();
            using var searcher = new ManagementObjectSearcher(scope, new SelectQuery("Win32_PnPEntity"));

            observer.ObjectReady += (_, e) =>
            {
                var portInfo = SerialPortInfo.FromManagementObject(e.NewObject as ManagementObject);
                if (portInfo == null)
                    return;

                serialPorts.Add(portInfo);
            };

            var taskCompletion = new TaskCompletionSource();
            observer.Completed += (_, _) => taskCompletion.TrySetResult();

            searcher.Get(observer);
            await using (token.Register(() => taskCompletion.TrySetCanceled()))
                await taskCompletion.Task.WaitAsync(token);

            var lastSelectedDeviceId = SelectedSerialPortDeviceId;
            SerialPorts.RemoveRange(SerialPorts.Except(serialPorts).ToList());
            SerialPorts.AddRange(serialPorts.Except(SerialPorts).ToList());

            SelectSerialPortByDeviceId(lastSelectedDeviceId);

            await Task.Delay(250, token);
        }
    }

    private void SelectSerialPortByDeviceId(string deviceId)
    {
        SelectedSerialPort = SerialPorts.FirstOrDefault(p => string.Equals(p.DeviceID, deviceId, StringComparison.Ordinal));
        if (SelectedSerialPort == null)
            SelectedSerialPortDeviceId = deviceId;
    }

    /// <summary>启动自动连接时由外部直接选中端口（例如探测到数据线端口，或用户在弹窗里选了蓝牙端口）。</summary>
    public void SelectSerialPort(SerialPortInfo info)
    {
        SelectedSerialPort = info;
        SelectedSerialPortDeviceId = info?.DeviceID;
    }

    public void OnSelectedSerialPortChanged() => SelectedSerialPortDeviceId = SelectedSerialPort?.DeviceID;

    protected override async ValueTask<bool> OnConnectingAsync(ConnectionType connectionType)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        if (connectionType != ConnectionType.AutoConnect)
            Logger.Info("Connecting to {0} at \"{1}\" [Type: {2}]", Identifier, SelectedSerialPortDeviceId, connectionType);

        if (SelectedSerialPortDeviceId == null)
            return false;

        // 先用注册表按 DeviceID 直接解析端口名（微秒级），
        // 避开连接时那次很慢的 WMI 全量枚举（Win32_PnPEntity 遍历整机，可能要好几秒）
        if (SelectedSerialPort == null)
        {
            var portName = TryGetPortNameFromDeviceId(SelectedSerialPortDeviceId);
            if (portName != null)
            {
                SelectedSerialPort = SerialPortInfo.FromPortName(SelectedSerialPortDeviceId, portName);

                Logger.Info("{0} resolved \"{1}\" from registry in {2:F0}ms", Identifier, portName, stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        if (SelectedSerialPort == null)
        {
            Logger.Info("{0} could not resolve port from registry, falling back to WMI refresh", Identifier);
            await RefreshPorts();
            Logger.Info("{0} WMI port refresh took {1:F0}ms", Identifier, stopwatch.Elapsed.TotalMilliseconds);
        }

        Logger.Info("{0} connect preparation took {1:F0}ms", Identifier, stopwatch.Elapsed.TotalMilliseconds);
        return SelectedSerialPort != null;
    }

    /// <summary>按设备 DeviceID 从注册表直接读端口名（COMx），避免 WMI 全量枚举。</summary>
    private string TryGetPortNameFromDeviceId(string deviceId)
    {
        try
        {
            return Registry.GetValue($@"HKEY_LOCAL_MACHINE\System\CurrentControlSet\Enum\{deviceId}\Device Parameters", "PortName", null) as string;
        }
        catch (Exception e)
        {
            Logger.Trace(e, "Failed to read port name for \"{0}\" from registry", deviceId);
            return null;
        }
    }

    protected override void Run(ConnectionType connectionType, CancellationToken token)
    {
        var transport = default(ISerialTransport);
        var openStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var firstWriteLogged = false;

        try
        {
            transport = SerialTransport.Open(new SerialPortOptions(
                SelectedSerialPort.PortName, BaudRate, Parity, StopBits, DataBits, Handshake,
                DtrEnable, RtsEnable, ReadTimeout, WriteTimeout, ReadBufferSize, WriteBufferSize));

            Status = ConnectionStatus.Connected;

            Logger.Info("{0} serial port \"{1}\" opened in {2:F0}ms [Transport: {3}, DTR: {4}, RTS: {5}]",
                Identifier, SelectedSerialPort.PortName, openStopwatch.Elapsed.TotalMilliseconds, transport.Description, DtrEnable, RtsEnable);
        }
        catch (Exception e)
        {
            try { transport?.Dispose(); }
            catch { }

            if (connectionType != ConnectionType.AutoConnect)
            {
                Logger.Error(e, "Error when connecting to {0} at \"{1}\"", Name, SelectedSerialPortDeviceId);
                _ = DialogHelper.ShowErrorAsync(e, $"连接 {Name} 时出错", "RootDialog");
            }

            return;
        }

        try
        {
            EventAggregator.Publish(new SyncRequestMessage());

            using var tcodeInputProcessor = inputProcessorFactory.GetInputProcessor<TCodeInputProcessor>();
            if (UpdateType == DeviceAxisUpdateType.FixedUpdate)
            {
                var currentValues = DeviceAxis.All.ToDictionary(a => a, _ => double.NaN);
                var lastSentValues = DeviceAxis.All.ToDictionary(a => a, _ => double.NaN);
                FixedUpdate<TCodeThreadFixedUpdateContext>(() => !token.IsCancellationRequested && transport.IsOpen, (context, elapsed) =>
                {
                    Logger.Trace("Begin FixedUpdate [Elapsed: {0}]", elapsed);
                    GetValues(currentValues);

                    if (transport.IsOpen && transport.BytesToRead > 0)
                        ReadExisting();

                    // 刚打开的头一小段时间先不发指令，避免指令落进设备的重启/自检过程
                    if (openStopwatch.Elapsed.TotalMilliseconds < FirstWriteDelayMilliseconds)
                        return;

                    var values = context.SendDirtyValuesOnly ? currentValues.Where(x => DeviceAxis.IsValueDirty(x.Value, lastSentValues[x.Key])) : currentValues;
                    values = values.Where(x => AxisSettings[x.Key].Enabled);

                    var intervalMilliseconds = elapsed * 1000;

                    // 刚连上的一小段时间里给一个过渡时间下限，避免设备以最大速度猛地弹到位
                    if (openStopwatch.Elapsed.TotalMilliseconds < ConnectRampDurationMilliseconds)
                        intervalMilliseconds = Math.Max(intervalMilliseconds, ConnectRampMinimumIntervalMilliseconds);

                    var commands = context.OffloadElapsedTime ? DeviceAxis.ToString(values) : DeviceAxis.ToString(values, intervalMilliseconds);
                    if (transport.IsOpen && !string.IsNullOrWhiteSpace(commands))
                    {
                        Logger.Trace("Sending \"{0}\" to \"{1}\"", commands.Trim(), SelectedSerialPortDeviceId);

                        if (!firstWriteLogged)
                        {
                            firstWriteLogged = true;
                            Logger.Info("{0} first TCode sent {1:F0}ms after serial port open [Interval: {2:F0}ms]",
                                Identifier, openStopwatch.Elapsed.TotalMilliseconds, intervalMilliseconds);
                        }

                        transport.Write(commands);
                        lastSentValues.Merge(values);
                    }
                });
            }
            else if (UpdateType == DeviceAxisUpdateType.PolledUpdate)
            {
                PolledUpdate(DeviceAxis.All, () => !token.IsCancellationRequested && transport.IsOpen, (_, axis, axisEvent, elapsed) =>
                {
                    Logger.Trace("Begin PolledUpdate [Axis: {0}, Event: {1}, Elapsed: {2}]", axis, axisEvent, elapsed);

                    // 原来靠 SerialPort.DataReceived 事件边收边读，改成在循环里轮询
                    // （Win32 直控没有这个事件，对 TCode 设备来说读的时机差别可以忽略）
                    if (transport.IsOpen && transport.BytesToRead > 0)
                        ReadExisting();

                    if (openStopwatch.Elapsed.TotalMilliseconds < FirstWriteDelayMilliseconds)
                        return;

                    var settings = AxisSettings[axis];
                    if (!settings.Enabled)
                        return;
                    if (!double.IsFinite(axisEvent.TargetValue))
                        return;

                    var value = MathUtils.Lerp(settings.Minimum, settings.Maximum, axisEvent.TargetValue);
                    var duration = axisEvent.Duration;

                    var command = DeviceAxis.ToString(axis, value, duration * 1000);
                    if (transport.IsOpen && !string.IsNullOrWhiteSpace(command))
                    {
                        Logger.Trace("Sending \"{0}\" to \"{1}\"", command, SelectedSerialPortDeviceId);

                        transport.Write($"{command}\n");
                    }
                }, token);
            }

            void ReadExisting()
            {
                var receivedString = transport.ReadExisting();
                Logger.Debug("Received \"{0}\" from \"{1}\"", receivedString, SelectedSerialPortDeviceId);
                tcodeInputProcessor.Parse(receivedString);
            }
        }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
            Logger.Error(e, $"{Identifier} failed with exception");
            _ = DialogHelper.ShowErrorAsync(e, $"{Identifier} 发生异常", "RootDialog");
        }
        catch (Exception e) { Logger.Error(e, $"{Identifier} failed with exception"); }

        try { transport?.Dispose(); }
        catch { }
    }

    public override void HandleSettings(JObject settings, SettingsAction action)
    {
        base.HandleSettings(settings, action);

        if (action == SettingsAction.Saving)
        {
            settings[nameof(UpdateType)] = JToken.FromObject(UpdateType);
            settings[nameof(SelectedSerialPort)] = SelectedSerialPortDeviceId;
            settings[nameof(BaudRate)] = BaudRate;
            settings[nameof(Parity)] = JToken.FromObject(Parity);
            settings[nameof(StopBits)] = JToken.FromObject(StopBits);
            settings[nameof(DataBits)] = DataBits;
            settings[nameof(Handshake)] = JToken.FromObject(Handshake);
            settings[nameof(DtrEnable)] = DtrEnable;
            settings[nameof(RtsEnable)] = RtsEnable;
            settings[nameof(ReadTimeout)] = ReadTimeout;
            settings[nameof(WriteTimeout)] = WriteTimeout;
            settings[nameof(WriteBufferSize)] = WriteBufferSize;
            settings[nameof(ReadBufferSize)] = ReadBufferSize;
        }
        else if (action == SettingsAction.Loading)
        {
            if (settings.TryGetValue<DeviceAxisUpdateType>(nameof(UpdateType), out var updateType))
                UpdateType = updateType;
            if (settings.TryGetValue<string>(nameof(SelectedSerialPort), out var selectedSerialPort))
                SelectSerialPortByDeviceId(selectedSerialPort);

            if (settings.TryGetValue<int>(nameof(BaudRate), out var baudRate)) BaudRate = baudRate;
            if (settings.TryGetValue<Parity>(nameof(Parity), out var parity)) Parity = parity;
            if (settings.TryGetValue<StopBits>(nameof(StopBits), out var stopBits)) StopBits = stopBits;
            if (settings.TryGetValue<int>(nameof(DataBits), out var dataBits)) DataBits = dataBits;
            if (settings.TryGetValue<Handshake>(nameof(Handshake), out var handshake)) Handshake = handshake;
            if (settings.TryGetValue<bool>(nameof(DtrEnable), out var dtrEnable)) DtrEnable = dtrEnable;
            if (settings.TryGetValue<bool>(nameof(RtsEnable), out var rtsEnable)) RtsEnable = rtsEnable;
            if (settings.TryGetValue<int>(nameof(ReadTimeout), out var readTimeout)) ReadTimeout = readTimeout;
            if (settings.TryGetValue<int>(nameof(WriteTimeout), out var writeTimeout)) WriteTimeout = writeTimeout;
            if (settings.TryGetValue<int>(nameof(WriteBufferSize), out var writeBufferSize)) WriteBufferSize = writeBufferSize;
            if (settings.TryGetValue<int>(nameof(ReadBufferSize), out var readBufferSize)) ReadBufferSize = readBufferSize;
        }
    }

    public override void RegisterActions(IShortcutManager s)
    {
        base.RegisterActions(s);

        #region SerialPort
        s.RegisterAction<string>($"{Identifier}::SerialPort::Set", s => s.WithLabel("设备 ID"), SelectSerialPortByDeviceId);
        #endregion
    }

    public override void UnregisterActions(IShortcutManager s)
    {
        base.UnregisterActions(s);
        s.UnregisterAction($"{Identifier}::SerialPort::Set");
    }

    public override void RegisterProperties(IPropertyManager p)
    {
        base.RegisterProperties(p);
        p.RegisterProperty($"{Identifier}::SerialPort", () => SelectedSerialPort?.PortName);
    }

    public override void UnregisterProperties(IPropertyManager p)
    {
        base.UnregisterProperties(p);
        p.UnregisterProperty($"{Identifier}::SerialPort");
    }

    protected override void Dispose(bool disposing)
    {
        _refreshCancellationSource?.Cancel();
        _refreshCancellationSource?.Dispose();
        _refreshCancellationSource = null;

        base.Dispose(disposing);
    }

    public sealed class SerialPortInfo
    {
        private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

        private SerialPortInfo() { }

        /// <summary>不经过 WMI，直接用已知的 DeviceID + 端口名构造（用于连接时走注册表快速路径）。</summary>
        public static SerialPortInfo FromPortName(string deviceId, string portName) => new()
        {
            DeviceID = deviceId,
            Name = portName,
            PortName = portName
        };

        public string Caption { get; init; }
        public string ClassGuid { get; init; }
        public string Description { get; init; }
        public string DeviceID { get; init; }
        public string Manufacturer { get; init; }
        public string Name { get; init; }
        public string PNPClass { get; init; }
        public string PNPDeviceID { get; init; }
        public string PortName { get; init; }

        public static SerialPortInfo FromManagementObject(ManagementObject o)
        {
            T GetPropertyValueOrDefault<T>(string propertyName, T defaultValue = default)
            {
                try { return (T)o.GetPropertyValue(propertyName); }
                catch { return defaultValue; }
            }

            try
            {
                var name = o.GetPropertyValue(nameof(Name)) as string;
                if (string.IsNullOrEmpty(name) || !Regex.IsMatch(name, @"\(COM\d+\)"))
                    return null;

                var deviceId = o.GetPropertyValue(nameof(DeviceID)) as string;
                var portName = Registry.GetValue($@"HKEY_LOCAL_MACHINE\System\CurrentControlSet\Enum\{deviceId}\Device Parameters", "PortName", "").ToString();

                return new SerialPortInfo()
                {
                    Caption = GetPropertyValueOrDefault<string>(nameof(Caption)),
                    ClassGuid = GetPropertyValueOrDefault<string>(nameof(ClassGuid)),
                    Description = GetPropertyValueOrDefault<string>(nameof(Description)),
                    Manufacturer = GetPropertyValueOrDefault<string>(nameof(Manufacturer)),
                    PNPClass = GetPropertyValueOrDefault<string>(nameof(PNPClass)),
                    PNPDeviceID = GetPropertyValueOrDefault<string>(nameof(PNPDeviceID)),
                    Name = name,
                    DeviceID = deviceId,
                    PortName = portName
                };
            }
            catch (Exception e)
            {
                Logger.Warn(e, "Failed to create SerialPortInfo [{0}]", o?.ToString());
            }

            return null;
        }

        public override bool Equals(object o) => o is SerialPortInfo other && string.Equals(DeviceID, other.DeviceID, StringComparison.Ordinal);
        public override int GetHashCode() => HashCode.Combine(DeviceID);
    }
}
