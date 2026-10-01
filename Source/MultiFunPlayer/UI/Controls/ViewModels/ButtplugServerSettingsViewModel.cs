using MultiFunPlayer.Buttplug;
using MultiFunPlayer.Common;
using MultiFunPlayer.Settings;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;
using StyletIoC;

namespace MultiFunPlayer.UI.Controls.ViewModels;

/// <summary>
/// 「Buttplug 服务器」设置页。
/// <br/>
/// 把设备当成一台 Buttplug 设备暴露给只支持 Buttplug 的软件（例如 Virt-A-Mate），
/// 外部指令走本程序的轴管线，因此轴的限位、限速、归位全都生效，串口 / 蓝牙 / WiFi 也都能用。
/// </summary>
internal sealed class ButtplugServerSettingsViewModel : Screen, IHandle<SettingsMessage>
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly IContainer _container;
    private ButtplugServer _server;
    private bool _loading;
    private bool _dirty;

    /// <summary>
    /// 服务器延迟到真正启动时才创建：这样本页的构造不依赖 <see cref="ScriptViewModel"/>，
    /// 被「输出目标」面板引用时不会影响那边的初始化顺序。
    /// </summary>
    private ButtplugServer Server => _server ??= new ButtplugServer(new ButtplugDeviceBridge(_container.Get<ScriptViewModel>()));

    public bool Enabled { get; set; } = false;
    public int Port { get; set; } = ButtplugProtocol.DefaultPort;
    public string ServerName { get; set; } = "MultiFunPlayer";
    public string DeviceName { get; set; } = "OSR6";
    public string ExposedAxes { get; set; } = "L0";
    public bool AutoTakeover { get; set; } = true;
    public int IdleRestoreSeconds { get; set; } = 3;
    public bool ForceCompat { get; set; } = false;
    public bool RotateAsLinear { get; set; } = false;

    public bool IsRunning => _server?.IsRunning ?? false;
    public int ClientCount => _server?.ClientCount ?? 0;
    public string ListenAddress => _server?.ListenAddress ?? $"ws://127.0.0.1:{Port}";
    public string LastCommand => _server?.LastCommand;
    public string LastError => _server?.LastError;

    public string ActuatorSummary
    {
        get
        {
            if (_server == null || _server.Actuators.Count == 0)
                return "（还没有暴露任何轴）";

            // 按类型分组、显式写出「索引=轴名」：索引是该类型里的第几个（从 0 开始、按你写的顺序），
            // 和轴号不一定相同（比如只填 R2 时它就是旋转 #0）
            var groups = _server.Actuators
                .GroupBy(a => a.Kind)
                .OrderBy(g => g.Key)
                .Select(g => $"{KindName(g.Key)}：{string.Join("、", g.OrderBy(a => a.Index).Select(a => $"#{a.Index}={a.Axis.Name}"))}");

            return string.Join("　", groups);
        }
    }

    private static string KindName(ButtplugActuatorKind kind) => kind switch
    {
        ButtplugActuatorKind.Linear => "位置",
        ButtplugActuatorKind.Rotate => "旋转",
        _ => "振动",
    };

    public string SkippedWarning => _server == null || _server.SkippedAxes.Count == 0
        ? string.Empty
        : $"⚠ 这些轴没能暴露：{string.Join("、", _server.SkippedAxes)} —— 它们没在「设置 → 设备」里启用，请先在那里勾上「启用」再点「重启」。";

    public bool HasSkippedAxes => _server != null && _server.SkippedAxes.Count > 0;

    public string StatusText
    {
        get
        {
            if (_server == null)
                return "未启动";
            if (_server.IsRunning)
                return $"运行中　{_server.ListenAddress}　设备名：{_server.DeviceName}　客户端：{ClientCount}{(_dirty ? "　⚠ 设置已改，点「重启」生效" : string.Empty)}";
            return string.IsNullOrEmpty(_server.LastError) ? "未运行" : $"未运行（上次启动失败：{_server.LastError}）";
        }
    }

    // 端口 / 设备名 / 暴露的轴等改动不会立即生效，标记一下让状态栏提示去点「重启」
    public void OnPortChanged() => MarkDirty();
    public void OnServerNameChanged() => MarkDirty();
    public void OnDeviceNameChanged() => MarkDirty();
    public void OnExposedAxesChanged() => MarkDirty();
    public void OnAutoTakeoverChanged() => MarkDirty();
    public void OnIdleRestoreSecondsChanged() => MarkDirty();
    public void OnForceCompatChanged() => MarkDirty();
    public void OnRotateAsLinearChanged() => MarkDirty();

    private void MarkDirty()
    {
        if (_loading || !IsRunning || _dirty)
            return;

        _dirty = true;
        NotifyOfPropertyChange(nameof(StatusText));
    }

    public ButtplugServerSettingsViewModel(IContainer container, IEventAggregator eventAggregator)
    {
        DisplayName = "Buttplug";
        _container = container;
        eventAggregator.Subscribe(this);
    }

    /// <summary>设置里的开关一动就立刻生效（启用即启动，关闭即停止）。</summary>
    public void OnEnabledChanged()
    {
        // 加载设置期间绝不能启动：Loading 是按字段逐个赋值的，而 Enabled 排在 ExposedAxes / Port
        // 等字段前面 —— 那时候启动只会用到默认值（暴露的轴只剩默认的 L0），
        // 表现就是"设了六个轴，重启后又只剩 L0"。加载完再统一启动。
        if (_loading)
            return;

        if (Enabled)
            Start();
        else
            Stop();
    }

    public void OnStart() => Start();
    public void OnStop() => Stop();

    public void OnRestart()
    {
        Stop();
        Start();
    }

    private void Start()
    {
        var server = Server;
        server.StatusChanged -= HandleServerStatusChanged;
        server.StatusChanged += HandleServerStatusChanged;
        server.Start(BuildOptions());
        _dirty = false;
        RefreshStatus();
    }

    private void Stop()
    {
        _server?.Stop();
        _dirty = false;
        RefreshStatus();
    }

    // 名字别叫 OnXxxChanged：Fody 会当成属性变更回调而报警告
    private void HandleServerStatusChanged() => Execute.OnUIThread(RefreshStatus);

    private ButtplugServerOptions BuildOptions() => new(
        Math.Clamp(Port, 1, 65535),
        string.IsNullOrWhiteSpace(ServerName) ? "MultiFunPlayer" : ServerName.Trim(),
        string.IsNullOrWhiteSpace(DeviceName) ? "OSR6" : DeviceName.Trim(),
        (ExposedAxes ?? string.Empty)
            .Split([',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList(),
        AutoTakeover,
        Math.Clamp(IdleRestoreSeconds, 1, 60),
        ForceCompat,
        RotateAsLinear);

    private void RefreshStatus()
    {
        NotifyOfPropertyChange(nameof(IsRunning));
        NotifyOfPropertyChange(nameof(ClientCount));
        NotifyOfPropertyChange(nameof(ListenAddress));
        NotifyOfPropertyChange(nameof(LastCommand));
        NotifyOfPropertyChange(nameof(LastError));
        NotifyOfPropertyChange(nameof(ActuatorSummary));
        NotifyOfPropertyChange(nameof(SkippedWarning));
        NotifyOfPropertyChange(nameof(HasSkippedAxes));
        NotifyOfPropertyChange(nameof(StatusText));
    }

    public void Handle(SettingsMessage message)
    {
        var settings = message.Settings;

        // 存成配置里的一个嵌套小节（和 Script / OutputTarget 一样），
        // 避免 Enabled / Port 这类通用键名以后跟别的设置撞名
        if (message.Action == SettingsAction.Saving)
        {
            settings["ButtplugServer"] = new JObject
            {
                [nameof(Enabled)] = Enabled,
                [nameof(Port)] = Port,
                [nameof(ServerName)] = ServerName,
                [nameof(DeviceName)] = DeviceName,
                [nameof(ExposedAxes)] = ExposedAxes,
                [nameof(AutoTakeover)] = AutoTakeover,
                [nameof(IdleRestoreSeconds)] = IdleRestoreSeconds,
                [nameof(ForceCompat)] = ForceCompat,
                [nameof(RotateAsLinear)] = RotateAsLinear,
            };
        }
        else if (message.Action == SettingsAction.Loading)
        {
            _loading = true;
            try
            {
                if (settings.TryGetObject(out var server, "ButtplugServer"))
                {
                    if (server.TryGetValue<bool>(nameof(Enabled), out var enabled))
                        Enabled = enabled;
                    if (server.TryGetValue<int>(nameof(Port), out var port))
                        Port = port;
                    if (server.TryGetValue<string>(nameof(ServerName), out var serverName))
                        ServerName = serverName;
                    if (server.TryGetValue<string>(nameof(DeviceName), out var deviceName))
                        DeviceName = deviceName;
                    if (server.TryGetValue<string>(nameof(ExposedAxes), out var exposedAxes))
                        ExposedAxes = exposedAxes;
                    if (server.TryGetValue<bool>(nameof(AutoTakeover), out var autoTakeover))
                        AutoTakeover = autoTakeover;
                    if (server.TryGetValue<int>(nameof(IdleRestoreSeconds), out var idleRestoreSeconds))
                        IdleRestoreSeconds = idleRestoreSeconds;
                    if (server.TryGetValue<bool>(nameof(ForceCompat), out var forceCompat))
                        ForceCompat = forceCompat;
                    if (server.TryGetValue<bool>(nameof(RotateAsLinear), out var rotateAsLinear))
                        RotateAsLinear = rotateAsLinear;
                }
            }
            finally
            {
                _loading = false;
            }

            // 所有字段都读完了才启动，否则会用到默认值（暴露的轴默认只有 L0）
            if (Enabled)
                Start();
        }
    }

    protected override void OnDeactivate() => Logger.Debug("Buttplug 设置页隐藏（服务器保持运行）");
}
