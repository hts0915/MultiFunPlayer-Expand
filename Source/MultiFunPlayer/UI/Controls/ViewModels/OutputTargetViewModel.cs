using MaterialDesignThemes.Wpf;
using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget;
using MultiFunPlayer.OutputTarget.ViewModels;
using MultiFunPlayer.Property;
using MultiFunPlayer.Shortcut;
using MultiFunPlayer.UI.Dialogs.ViewModels;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;
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
    /// 启动时连接设备：先探测数据线端口，空闲就直接连；
    /// 被其他程序占用（或者没插线）时弹窗让用户选择蓝牙或 WiFi。
    /// </summary>
    private async Task StartupConnectAsync(CancellationToken token)
    {
        try
        {
            if (!_startupConnection.AutoConnectOnStartup)
                return;

            // 等主窗口和串口枚举就绪，避免和启动动画、首次端口扫描抢
            await Task.Delay(StartupConnectDelayMilliseconds, token);

            var serialTarget = Items.OfType<SerialOutputTarget>().FirstOrDefault();
            if (serialTarget == null)
                return;

            // 已经连上了（用户手动连的，或自动扫描抢先连的）就不打扰
            if (serialTarget.Status != ConnectionStatus.Disconnected)
                return;

            var reason = await TryConnectUsbAsync(serialTarget, token);
            if (reason == null)
                return;

            await ShowConnectionChoiceDialogAsync(serialTarget, reason, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Logger.Error(e, "Startup connection failed");
        }
    }

    /// <returns>连接成功返回 null，否则返回失败原因（用于弹窗展示）。</returns>
    private async Task<string> TryConnectUsbAsync(SerialOutputTarget target, CancellationToken token, bool forceRefresh = false)
    {
        await EnsurePortsRefreshedAsync(target, token, forceRefresh);

        var port = SerialPortUtils.FindUsbDevicePort(target.SerialPorts, _startupConnection.UsbSerialMatch);
        if (port == null)
        {
            Logger.Info("Startup connection: no data cable port found [Match: {0}]", _startupConnection.UsbSerialMatch);
            return "未检测到通过数据线连接的设备";
        }

        switch (SerialPortUtils.Probe(port.PortName))
        {
            case SerialPortAvailability.Busy:
                Logger.Info("Startup connection: data cable port {0} is busy", port.PortName);
                return $"数据线端口 {port.PortName} 正被其他程序占用";

            case SerialPortAvailability.Missing:
                Logger.Info("Startup connection: data cable port {0} does not exist", port.PortName);
                return $"数据线端口 {port.PortName} 当前不可用";

            // Unknown：探测结果不明确（例如某些蓝牙/USB 转串口驱动的独占语义不同），
            // 直接尝试连接，连不上再走弹窗，避免误判成"被占用"
        }

        SelectSerialPort(target, port);
        if (!await TryConnectAsync(target, token))
        {
            Logger.Info("Startup connection: failed to open data cable port {0}", port.PortName);
            return $"数据线端口 {port.PortName} 连接失败，可能刚被其他程序占用";
        }
        Logger.Info("Startup connection: connected to {0} via data cable", port.PortName);
        Notify($"已通过数据线连接 {port.PortName}");
        return null;
    }

    private async Task ShowConnectionChoiceDialogAsync(SerialOutputTarget serialTarget, string reason, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // 蓝牙设备名 → MAC → SPP 串口要查 WMI（约 1 秒），必须在后台线程算好再开弹窗，
            // 否则弹窗构造时会卡住界面。每轮重算一次，用户插拔设备后列表也能跟上。
            var ports = serialTarget.SerialPorts.ToList();
            var bluetoothPorts = ports.Where(SerialPortUtils.IsBluetoothPort).ToList();
            if (bluetoothPorts.Count == 0)
                bluetoothPorts = ports;

            var selectedPort = SerialPortUtils.FindBluetoothPort(ports, _startupConnection.BluetoothDeviceMatch) ?? bluetoothPorts.FirstOrDefault();

            var choice = await DialogHelper.ShowAsync<StartupConnectionChoice>(
                () => new StartupConnectionDialog(reason, bluetoothPorts, selectedPort, _startupConnection), "RootDialog");

            // 关掉弹窗（Esc / 右上角）等同于跳过
            if (choice == null || choice.Kind == StartupConnectionKind.Skip)
            {
                Logger.Info("Startup connection: user skipped");
                return;
            }

            if (choice.Kind == StartupConnectionKind.RetryUsb)
            {
                // 用户可能刚关掉占用端口的软件，或者刚把线插上，所以强制重新枚举端口
                var retryReason = await TryConnectUsbAsync(serialTarget, token, forceRefresh: true);
                if (retryReason == null)
                    return;

                reason = retryReason;
                continue;
            }

            if (choice.Kind == StartupConnectionKind.Bluetooth)
            {
                var port = serialTarget.SerialPorts.FirstOrDefault(p => string.Equals(p.DeviceID, choice.SerialPortDeviceId, StringComparison.Ordinal));
                if (port == null)
                {
                    reason = "选中的蓝牙端口已不可用，请重新选择";
                    continue;
                }

                if (serialTarget.Status != ConnectionStatus.Disconnected)
                    await DisconnectAsync(serialTarget, token);

                SelectSerialPort(serialTarget, port);
                if (await TryConnectAsync(serialTarget, token))
                {
                    Logger.Info("Startup connection: connected to {0} via bluetooth", port.PortName);
                    Notify($"已通过蓝牙连接 {port.PortName}");
                    return;
                }

                reason = $"蓝牙端口 {port.PortName} 连接失败";
                continue;
            }

            if (choice.Kind == StartupConnectionKind.Wifi)
            {
                if (await TryConnectWifiAsync(choice, token))
                {
                    Logger.Info("Startup connection: connected to {0} via {1}", choice.Endpoint?.ToUriString(), choice.Protocol);
                    Notify($"已通过 {FormatProtocol(choice.Protocol)} 连接 {choice.Endpoint?.ToUriString()}");
                    return;
                }

                reason = "WiFi 连接失败，请检查设备电源、地址和协议";
                continue;
            }
        }
    }

    private async Task<bool> TryConnectWifiAsync(StartupConnectionChoice choice, CancellationToken token)
    {
        if (choice.Endpoint == null)
            return false;

        var target = GetOrAddWifiTarget(choice);
        if (target == null)
            return false;

        // 同一台设备只保留一个连接，先把其他已连上的输出断开
        foreach (var other in Items.Where(x => !ReferenceEquals(x, target) && x.Status == ConnectionStatus.Connected).ToList())
            await DisconnectAsync(other, token);

        return await TryConnectAsync(target, token);
    }

    /// <summary>
    /// 取出（或新建）指定类型的输出目标。/ 选中串口都必须在 UI 线程上做：
    /// Items 是绑定到界面的集合，后台线程直接改会抛"不支持从其他线程修改集合"。
    /// </summary>
    private IOutputTarget GetOrAddWifiTarget(StartupConnectionChoice choice)
    {
        var type = choice.Protocol == WifiProtocol.Tcp ? typeof(TcpOutputTarget) : typeof(UdpOutputTarget);

        IOutputTarget target = null;
        Execute.OnUIThread(() =>
        {
            target = Items.FirstOrDefault(x => x.GetType() == type);
            if (target == null)
            {
                AddItem(type);
                target = Items.LastOrDefault(x => x.GetType() == type);
            }

            switch (target)
            {
                case TcpOutputTarget tcp: tcp.Endpoint = choice.Endpoint; break;
                case UdpOutputTarget udp: udp.Endpoint = choice.Endpoint; break;
            }
        });

        return target;
    }

    private static void SelectSerialPort(SerialOutputTarget target, SerialPortInfo port)
        => Execute.OnUIThread(() => target.SelectSerialPort(port));

    private async Task<bool> TryConnectAsync(IOutputTarget target, CancellationToken token)
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

            // 用 AutoConnect 类型：失败时不会弹内置错误框，由启动连接流程自己解释原因
            await ConnectAsync(target, ConnectionType.AutoConnect, token);
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
        // 那是绑定到下拉框的属性，跟着界面的线程走最稳妥
        var refreshTask = default(Task);
        Execute.OnUIThread(() => refreshTask = target.RefreshPorts());
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

    private static string FormatProtocol(WifiProtocol protocol) => protocol == WifiProtocol.Tcp ? "TCP" : "UDP";

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
