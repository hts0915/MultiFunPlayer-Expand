using MaterialDesignThemes.Wpf;
using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget;
using MultiFunPlayer.OutputTarget.ViewModels;
using MultiFunPlayer.Property;
using MultiFunPlayer.Shortcut;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;
using System.Net;
using SerialPortInfo = MultiFunPlayer.OutputTarget.ViewModels.SerialOutputTarget.SerialPortInfo;

namespace MultiFunPlayer.UI.Controls.ViewModels;

internal sealed class OutputTargetViewModel : Conductor<IOutputTarget>.Collection.OneActive, IHandle<SettingsMessage>, IDisposable
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private const int StartupConnectDelayMilliseconds = 1200;

    private readonly IShortcutManager _shortcutManager;
    private readonly IPropertyManager _propertyManager;
    private readonly IOutputTargetFactory _outputTargetFactory;
    private readonly StartupConnectionSettingsViewModel _startupConnection;
    private readonly ISnackbarMessageQueue _snackbarMessageQueue;
    private Task _task;
    private Task _startupConnectTask;
    private CancellationTokenSource _cancellationSource;
    private Dictionary<IOutputTarget, SemaphoreSlim> _semaphores;
    private SemaphoreSlim _scanIntervalSemaphore;

    public List<Type> AvailableOutputTargetTypes { get; }

    public bool ContentVisible { get; set; }
    public int ScanDelay { get; set; } = 2500;
    public int ScanInterval { get; set; } = 5000;

    public OutputTargetViewModel(IShortcutManager shortcutManager, IPropertyManager propertyManager, IEventAggregator eventAggregator, IOutputTargetFactory outputTargetFactory,
                                 StartupConnectionSettingsViewModel startupConnection, ISnackbarMessageQueue snackbarMessageQueue)
    {
        _shortcutManager = shortcutManager;
        _propertyManager = propertyManager;
        _outputTargetFactory = outputTargetFactory;
        _startupConnection = startupConnection;
        _snackbarMessageQueue = snackbarMessageQueue;
        eventAggregator.Subscribe(this);

        _semaphores = [];
        _cancellationSource = new CancellationTokenSource();
        _scanIntervalSemaphore = new SemaphoreSlim(0);

        AvailableOutputTargetTypes = ReflectionUtils.FindImplementations<IOutputTarget>().ToList();
    }

    public void AddItem(Type type)
    {
        var usedIndices = Items.Where(x => x.GetType() == type)
                               .Select(x => x.InstanceIndex)
                               .ToList();

        var index = 0;
        for (; ; index++)
            if (!usedIndices.Contains(index))
                break;

        Logger.Trace("Adding new output [Index: {0}, Type: {1}]", index, type);
        var instance = _outputTargetFactory.CreateOutputTarget(type, index);
        if (instance == null)
            return;

        AddItem(instance);
    }

    private void AddItem(IOutputTarget target)
    {
        Items.Add(target);
        ActivateItem(target);
        _semaphores.Add(target, new SemaphoreSlim(1, 1));

        Logger.Debug("Added new output \"{0}\"", target.Identifier);
        RegisterActions(_shortcutManager, target);
        RegisterProperties(_propertyManager, target);
    }

    public async void RemoveItem(IOutputTarget target)
    {
        Logger.Debug("Removing output \"{0}\"", target.Identifier);

        CloseItem(target);

        var semaphore = _semaphores[target];
        _semaphores.Remove(target);

        var token = _cancellationSource.Token;
        await semaphore.WaitAsync(token);

        await target.WaitForIdle(token);
        if (target.Status == ConnectionStatus.Connected)
        {
            await target.DisconnectAsync();
            await target.WaitForDisconnect(token);
        }

        UnregisterActions(_shortcutManager, target);
        UnregisterProperties(_propertyManager, target);

        semaphore.Release();
        semaphore.Dispose();
        target.Dispose();
    }

    protected override void OnViewLoaded()
    {
        base.OnViewLoaded();
        _task ??= Task.Run(() => ScanAsync(_cancellationSource.Token));
        _startupConnectTask ??= Task.Run(() => StartupConnectAsync(_cancellationSource.Token));
    }

    public void Handle(SettingsMessage message)
    {
        if (message.Action == SettingsAction.Saving)
        {
            if (!message.Settings.EnsureContainsObjects("OutputTarget")
             || !message.Settings.TryGetObject(out var settings, "OutputTarget"))
                return;

            settings[nameof(ContentVisible)] = ContentVisible;
            settings[nameof(ScanDelay)] = ScanDelay;
            settings[nameof(ScanInterval)] = ScanInterval;
            settings[nameof(ActiveItem)] = ActiveItem?.Identifier;
            settings[nameof(Items)] = JArray.FromObject(Items);
        }
        else if (message.Action == SettingsAction.Loading)
        {
            if (!message.Settings.TryGetObject(out var settings, "OutputTarget"))
                return;

            if (settings.TryGetValue<bool>(nameof(ContentVisible), out var contentVisible))
                ContentVisible = contentVisible;
            if (settings.TryGetValue<int>(nameof(ScanDelay), out var scanDelay))
                ScanDelay = scanDelay;
            if (settings.TryGetValue<int>(nameof(ScanInterval), out var scanInterval))
                ScanInterval = scanInterval;

            if (settings.TryGetValue<List<IOutputTarget>>(nameof(Items), out var items))
            {
                foreach (var item in items)
                {
                    var isDuplicate = Items.Any(x => x.GetType() == item.GetType() && x.InstanceIndex == item.InstanceIndex);
                    if (isDuplicate)
                    {
                        Logger.Warn("Index \"{0}\" is already used for type \"{1}\"", item.InstanceIndex, item.GetType());
                        continue;
                    }

                    AddItem(item);
                }
            }

            if (settings.TryGetValue<string>(nameof(ActiveItem), out var selectedItem))
                ChangeActiveItem(Items.FirstOrDefault(x => string.Equals(x.Identifier, selectedItem, StringComparison.Ordinal)) ?? Items.FirstOrDefault(), closePrevious: false);
        }
    }

    public async Task ToggleConnectAsync(IOutputTarget target)
    {
        var token = _cancellationSource.Token;
        if (target == null)
            return;

        await _semaphores[target].WaitAsync(token);
        if (target.Status == ConnectionStatus.Connected)
            await DisconnectAsync(target, token);
        else if (target.Status == ConnectionStatus.Disconnected)
            await ConnectAsync(target, ConnectionType.Manual, token);

        _semaphores[target].Release();
    }

    private async Task ConnectAsync(IOutputTarget target, ConnectionType connectionType, CancellationToken token)
    {
        _scanIntervalSemaphore.Release();
        await target.ConnectAsync(connectionType);
        await target.WaitForIdle(token);
    }

    private async Task DisconnectAsync(IOutputTarget target, CancellationToken token)
    {
        _scanIntervalSemaphore.Release();
        await target.DisconnectAsync();
        await target.WaitForDisconnect(token);
    }

    private async Task ScanAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(ScanDelay, token);
            while (!token.IsCancellationRequested)
            {
                foreach (var target in Items.ToList())
                {
                    if (!target.AutoConnectEnabled)
                        continue;

                    await _semaphores[target].WaitAsync(token);
                    if (target.Status != ConnectionStatus.Connected)
                        await ConnectAsync(target, ConnectionType.AutoConnect, token);

                    _semaphores[target].Release();
                }

                while (await _scanIntervalSemaphore.WaitAsync(ScanInterval, token));
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// 启动时只自动连接数据线。连不上时不弹窗，而是把另外两条路准备好（只填参数、不自动连接）：
    /// 蓝牙单独一个串口目标并选中设备的蓝牙端口，WiFi 一个 UDP/TCP 目标并填好地址，
    /// 用户在「输出目标」面板点一下连接按钮就能用。
    /// </summary>
    private async Task StartupConnectAsync(CancellationToken token)
    {
        try
        {
            if (!_startupConnection.AutoConnectOnStartup)
                return;

            // 等主窗口和串口枚举就绪，避免和启动动画、首次端口扫描抢
            await Task.Delay(StartupConnectDelayMilliseconds, token);

            var usbTarget = Items.OfType<SerialOutputTarget>().FirstOrDefault();
            if (usbTarget == null)
                return;

            // 已经连上了（用户手动连的，或自动扫描抢先连的）就不打扰
            if (usbTarget.Status != ConnectionStatus.Disconnected)
                return;

            var reason = await TryConnectUsbAsync(usbTarget, token);
            if (reason == null)
                return;

            await PrepareManualTargetsAsync(usbTarget, reason, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Logger.Error(e, "Startup connection failed");
        }
    }

    /// <returns>连接成功返回 null，否则返回失败原因。</returns>
    private async Task<string> TryConnectUsbAsync(SerialOutputTarget target, CancellationToken token, bool forceRefresh = false)
    {
        await EnsurePortsRefreshedAsync(target, token, forceRefresh);

        var port = SerialPortUtils.FindUsbDevicePort(target.SerialPorts, _startupConnection.UsbSerialMatch);
        if (port == null)
        {
            Logger.Info("Startup connection: no data cable port found [Match: {0}]", _startupConnection.UsbSerialMatch);
            return "未检测到数据线的设备端口";
        }

        // 不管最后连没连上，都先把端口选好，这样面板里显示的始终是数据线端口
        SelectSerialPort(target, port);

        var availability = SerialPortUtils.Probe(port.PortName);
        if (availability is SerialPortAvailability.Busy or SerialPortAvailability.Missing)
        {
            Logger.Info("Startup connection: data cable port {0} is {1}", port.PortName, availability);
            return availability == SerialPortAvailability.Busy
                ? $"数据线端口 {port.PortName} 被其他程序占用"
                : $"数据线端口 {port.PortName} 当前不可用";
        }

        if (!await TryConnectAsync(target, token, ConnectionType.AutoConnect))
        {
            Logger.Info("Startup connection: failed to open data cable port {0}", port.PortName);
            return $"数据线端口 {port.PortName} 连接失败";
        }

        Logger.Info("Startup connection: connected to {0} via data cable", port.PortName);
        Notify($"已通过数据线连接 {port.PortName}");
        return null;
    }

    /// <summary>
    /// 数据线没连上时，把另外两条路提前准备好（只填参数，不自动连接），方便用户手动点连接。
    /// </summary>
    private async Task PrepareManualTargetsAsync(SerialOutputTarget usbTarget, string reason, CancellationToken token)
    {
        await EnsurePortsRefreshedAsync(usbTarget, token, forceRefresh: false);
        Logger.Info("Startup connection: preparing manual targets [Ports: {0}, Targets: {1}]", usbTarget.SerialPorts.Count, DescribeTargets());

        var prepared = new List<string>();

        var bluetoothPort = SerialPortUtils.FindBluetoothPort(usbTarget.SerialPorts, _startupConnection.BluetoothDeviceMatch);
        if (bluetoothPort != null)
        {
            var target = GetOrAddBluetoothTarget();
            if (target != null)
            {
                SelectSerialPort(target, bluetoothPort);
                SetAutoConnect(target, false);
                prepared.Add($"蓝牙 {target.Identifier}（{bluetoothPort.PortName}）");
                Logger.Info("Startup connection: bluetooth target {0} prepared with {1}", target.Identifier, bluetoothPort.PortName);
            }
            else
            {
                // 建不出第二个串口目标时退一步：直接把蓝牙端口选进现有串口目标，至少能手动连
                SelectSerialPort(usbTarget, bluetoothPort);
                prepared.Add($"蓝牙端口 {bluetoothPort.PortName}（已选进 {usbTarget.Identifier}）");
                Logger.Warn("Startup connection: falling back to selecting bluetooth port on {0}", usbTarget.Identifier);
            }
        }
        else
        {
            Logger.Warn("Startup connection: no bluetooth serial port found [Match: {0}]", _startupConnection.BluetoothDeviceMatch);
        }

        var wifiDescription = PrepareWifiTarget();
        if (wifiDescription != null)
            prepared.Add(wifiDescription);

        Logger.Info("Startup connection: prepared count = {0}, targets now: {1}", prepared.Count, DescribeTargets());

        if (prepared.Count == 0)
        {
            Logger.Warn("Startup connection: nothing prepared for manual connection [Reason: {0}]", reason);
            NotifyAlways($"{reason}；也没找到可用的蓝牙或 WiFi 目标，请在「输出目标」面板手动配置");
            return;
        }

        var message = $"{reason}；已准备好 {string.Join("、", prepared)}，在「输出目标」面板点连接即可";
        Logger.Info("Startup connection: {0}", message);
        NotifyAlways(message);
    }

    private string DescribeTargets()
        => string.Join(" | ", Items.Select(x => x == null ? "<null>" : $"{x.GetType().Name}/{x.InstanceIndex}/{x.Status}"));

    /// <returns>准备好时返回给用户看的描述，否则 null。</returns>
    private string PrepareWifiTarget()
    {
        if (!NetUtils.TryParseEndpoint(_startupConnection.DefaultWifiEndpoint, out var endpoint))
        {
            Logger.Warn("Startup connection: invalid WiFi endpoint \"{0}\"", _startupConnection.DefaultWifiEndpoint);
            return null;
        }

        var target = GetOrAddWifiTarget(endpoint, _startupConnection.DefaultWifiProtocol, out var created);
        if (target == null)
            return null;

        SetAutoConnect(target, false);
        Logger.Info("Startup connection: WiFi target {0} prepared [Endpoint: {1}, New: {2}]", target.Identifier, _startupConnection.DefaultWifiEndpoint, created);

        var state = string.Empty;
        if (_startupConnection.WifiProbeEnabled)
        {
            string probeError;
            var online = _startupConnection.DefaultWifiProtocol == WifiProtocol.Tcp
                ? TcodeDeviceProbe.TryProbeTcp(endpoint, out _, out probeError)
                : TcodeDeviceProbe.TryProbeUdp(endpoint, out _, out probeError);

            state = online ? "，设备在线"
                  : !NetUtils.IsOnLocalSubnet(endpoint) ? $"，但设备和电脑不在同一网络（电脑：{NetUtils.DescribeLocalAddresses()}）"
                  : $"，设备暂时没响应（{probeError}）";

            Logger.Info("Startup connection: WiFi endpoint {0} probe = {1}", _startupConnection.DefaultWifiEndpoint, state);
        }

        return $"WiFi {target.Identifier}（{_startupConnection.DefaultWifiEndpoint}{state}）";
    }

    /// <summary>蓝牙用的串口目标：第一个串口目标归数据线，蓝牙用第二个，没有就新建一个（Serial/1）。</summary>
    private SerialOutputTarget GetOrAddBluetoothTarget()
    {
        SerialOutputTarget target = null;
        var error = default(Exception);

        // 必须用 OnUIThreadSync：OnUIThread 是 Post（异步投递），动作稍后才执行，
        // 这里读到的 target 会永远是 null —— 之前"目标创建失败"就是这么来的
        Execute.OnUIThreadSync(() =>
        {
            try
            {
                target = Items.OfType<SerialOutputTarget>().Skip(1).FirstOrDefault();
                if (target != null)
                    return;

                AddItem(typeof(SerialOutputTarget));
                target = Items.OfType<SerialOutputTarget>().LastOrDefault();
            }
            catch (Exception e)
            {
                error = e;
            }
        });

        if (error != null)
            Logger.Error(error, "Startup connection: creating bluetooth serial target threw");
        else if (target == null)
            Logger.Warn("Startup connection: bluetooth serial target not created [Targets: {0}]", DescribeTargets());

        return target;
    }

    private static void SetAutoConnect(IOutputTarget target, bool enabled)
        => Execute.OnUIThreadSync(() =>
        {
            if (target is AbstractOutputTarget abstractTarget)
                abstractTarget.AutoConnectEnabled = enabled;
        });

    private IOutputTarget GetOrAddWifiTarget(EndPoint endpoint, WifiProtocol protocol, out bool created)
    {
        var type = protocol == WifiProtocol.Tcp ? typeof(TcpOutputTarget) : typeof(UdpOutputTarget);

        IOutputTarget target = null;
        var wasCreated = false;
        var error = default(Exception);

        // 同上：这里必须同步执行，否则拿不到返回值
        Execute.OnUIThreadSync(() =>
        {
            try
            {
                target = Items.FirstOrDefault(x => x != null && x.GetType() == type);
                if (target == null)
                {
                    AddItem(type);
                    target = Items.FirstOrDefault(x => x != null && x.GetType() == type);
                    wasCreated = true;
                }

                switch (target)
                {
                    case TcpOutputTarget tcp: tcp.Endpoint = endpoint; break;
                    case UdpOutputTarget udp: udp.Endpoint = endpoint; break;
                }
            }
            catch (Exception e)
            {
                error = e;
            }
        });

        created = wasCreated;
        if (error != null)
            Logger.Error(error, "Startup connection: creating WiFi output target threw [Type: {0}, Targets: {1}]", type.Name, DescribeTargets());
        else if (target == null)
            Logger.Warn("Startup connection: WiFi output target not created [Type: {0}, Targets: {1}]", protocol, DescribeTargets());

        return target;
    }

    private static void SelectSerialPort(SerialOutputTarget target, SerialPortInfo port)
        => Execute.OnUIThreadSync(() => target.SelectSerialPort(port));

    private async Task<bool> TryConnectAsync(IOutputTarget target, CancellationToken token, ConnectionType connectionType)
    {
        var semaphore = _semaphores[target];
        await semaphore.WaitAsync(token);
        try
        {
            if (target.Status == ConnectionStatus.Connected)
                return true;

            if (target.Status != ConnectionStatus.Disconnected)
            {
                await target.DisconnectAsync();
                await target.WaitForDisconnect(token);
            }

            // 启动时静默自动连数据线用 AutoConnect（失败不打扰，由启动流程自己弹窗解释）；
            // 用户在弹窗里主动选的连接用 Manual：失败会写日志、会在右下角报错，方便排查
            await ConnectAsync(target, connectionType, token);
            return target.Status == ConnectionStatus.Connected;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            Logger.Warn(e, "Failed to connect \"{0}\"", target.Identifier);
            return false;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>串口列表由界面初始化时异步刷新，这里等它刷完；为空（或强制）时再主动刷一次。</summary>
    private static async Task EnsurePortsRefreshedAsync(SerialOutputTarget target, CancellationToken token, bool forceRefresh)
    {
        for (var i = 0; i < 30 && target.IsRefreshBusy; i++)
            await Task.Delay(100, token);

        if (!forceRefresh && target.SerialPorts.Count > 0)
            return;

        // 在 UI 线程上发起刷新：RefreshPorts 内部会设置 SelectedSerialPort，
        // 那是绑定到下拉框的属性，跟着界面的线程走最稳妥。
        // 用同步版本，否则 refreshTask 还没被赋值我们就读过去了。
        var refreshTask = default(Task);
        Execute.OnUIThreadSync(() => refreshTask = target.RefreshPorts());
        if (refreshTask != null)
            await refreshTask;

        for (var i = 0; i < 30 && target.IsRefreshBusy; i++)
            await Task.Delay(100, token);
    }

    private void Notify(string message)
    {
        if (!_startupConnection.ShowConnectNotification)
            return;

        Execute.OnUIThread(() => _snackbarMessageQueue.Enqueue(message));
    }

    /// <summary>无视通知开关，一定要告诉用户（数据线失败后准备了哪些手动连接目标，不说用户就不知道）。</summary>
    private void NotifyAlways(string message) => Execute.OnUIThread(() => _snackbarMessageQueue.Enqueue(message));

    private void RegisterActions(IShortcutManager s, IOutputTarget target)
    {
        var token = _cancellationSource.Token;
        target.RegisterActions(s);

        #region Connection
        s.RegisterAction($"{target.Identifier}::Connection::Toggle", async () => await ToggleConnectAsync(target));
        s.RegisterAction($"{target.Identifier}::Connection::Connect", async () =>
        {
            await _semaphores[target].WaitAsync(token);
            if (target.Status == ConnectionStatus.Disconnected)
                await ConnectAsync(target, ConnectionType.Manual, token);
            _semaphores[target].Release();
        });
        s.RegisterAction($"{target.Identifier}::Connection::Disconnect", async () =>
        {
            await _semaphores[target].WaitAsync(token);
            if (target.Status == ConnectionStatus.Connected)
                await DisconnectAsync(target, token);
            _semaphores[target].Release();
        });
        #endregion
    }

    private void UnregisterActions(IShortcutManager s, IOutputTarget target)
    {
        target.UnregisterActions(s);
        s.UnregisterAction($"{target.Identifier}::Connection::Toggle");
        s.UnregisterAction($"{target.Identifier}::Connection::Connect");
        s.UnregisterAction($"{target.Identifier}::Connection::Disconnect");
    }

    private void RegisterProperties(IPropertyManager p, IOutputTarget target) => target.RegisterProperties(p);
    private void UnregisterProperties(IPropertyManager p, IOutputTarget target) => target.UnregisterProperties(p);

    private void Dispose(bool disposing)
    {
        _cancellationSource?.Cancel();

        _task?.GetAwaiter().GetResult();

        if (_semaphores != null)
            foreach (var (_, semaphore) in _semaphores)
                semaphore.Dispose();

        _cancellationSource?.Dispose();
        _scanIntervalSemaphore?.Dispose();

        _semaphores = null;
        _task = null;
        _cancellationSource = null;
        _scanIntervalSemaphore = null;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
