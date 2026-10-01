using MultiFunPlayer.Buttplug;
using MultiFunPlayer.Common;
using MultiFunPlayer.Settings;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;

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

    private readonly ButtplugServer _server;

    public bool Enabled { get; set; } = false;
    public int Port { get; set; } = ButtplugProtocol.DefaultPort;
    public string ServerName { get; set; } = "MultiFunPlayer";
    public string DeviceName { get; set; } = "OSR6";
    public string ExposedAxes { get; set; } = "L0";
    public bool AutoTakeover { get; set; } = true;
    public int IdleRestoreSeconds { get; set; } = 3;
    public bool ForceCompat { get; set; } = false;

    public bool IsRunning => _server.IsRunning;
    public int ClientCount => _server.ClientCount;
    public string ListenAddress => _server.ListenAddress;
    public string LastCommand => _server.LastCommand;
    public string LastError => _server.LastError;

    public string ActuatorSummary => _server.Actuators.Count == 0
        ? "（还没有暴露任何轴）"
        : string.Join("、", _server.Actuators.Select(a => $"{a.Axis.Name}（{(a.Kind == ButtplugActuatorKind.Linear ? "位置" : a.Kind == ButtplugActuatorKind.Rotate ? "旋转" : "振动")} #{a.Index}）"));

    public string SkippedWarning => _server.SkippedAxes.Count == 0
        ? string.Empty
        : $"⚠ 这些轴没能暴露：{string.Join("、", _server.SkippedAxes)} —— 它们没在「设置 → 设备」里启用，请先在那里勾上「启用」再点「重启」。";

    public bool HasSkippedAxes => _server.SkippedAxes.Count > 0;

    public string StatusText => _server.IsRunning
        ? $"运行中　{_server.ListenAddress}　设备名：{_server.DeviceName}　客户端：{ClientCount}"
        : string.IsNullOrEmpty(_server.LastError) ? "未运行" : $"未运行（上次启动失败：{_server.LastError}）";

    public ButtplugServerSettingsViewModel(ScriptViewModel script, IEventAggregator eventAggregator)
    {
        DisplayName = "Buttplug";
        _server = new ButtplugServer(new ButtplugDeviceBridge(script));
        _server.StatusChanged += () => Execute.OnUIThread(RefreshStatus);
        eventAggregator.Subscribe(this);
    }

    /// <summary>设置里的开关一动就立刻生效（启用即启动，关闭即停止）。</summary>
    public void OnEnabledChanged()
    {
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
        _server.Start(BuildOptions());
        RefreshStatus();
    }

    private void Stop()
    {
        _server.Stop();
        RefreshStatus();
    }

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
        ForceCompat);

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
            };
        }
        else if (message.Action == SettingsAction.Loading)
        {
            if (!settings.TryGetObject(out var server, "ButtplugServer"))
                return;

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
        }
    }

    protected override void OnDeactivate() => Logger.Debug("Buttplug 设置页隐藏（服务器保持运行）");
}
