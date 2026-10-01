using MultiFunPlayer.Common;
using MultiFunPlayer.UI.Controls.ViewModels;
using NLog;

namespace MultiFunPlayer.Buttplug;

/// <summary>
/// 服务器把指令落到哪里：生产环境是 <see cref="ButtplugDeviceBridge"/>（走 MFP 轴管线），
/// 抽成接口是为了能单独测协议层（用假实现记录收到的指令）。
/// </summary>
internal interface IButtplugAxisSink
{
    string DeviceName { get; }
    IReadOnlyList<ButtplugActuator> Actuators { get; }
    bool HasTakenOverAxes { get; }

    void Configure(ButtplugServerOptions options);
    bool ApplyLinear(int index, double position, double durationSeconds);
    bool ApplyRotate(int index, double speed, bool clockwise);
    bool ApplyScalar(int index, double value);
    void ReleaseAll();
}

/// <summary>
/// 把 Buttplug 指令落到 MultiFunPlayer 的轴管线上。
/// <br/>
/// 为什么不直接往串口发 TCode：走轴管线的话，轴的**限位、限速、软启动、归位**全都生效，
/// 而且串口 / 蓝牙 / WiFi 任何输出目标都能用 —— 这正是 Intiface 直接驱动 OSR 的痛点
/// （社区反馈"用 Intiface 控制 OSR 会用满行程，基本没法用"）。
/// </summary>
internal sealed class ButtplugDeviceBridge(ScriptViewModel script) : IButtplugAxisSink
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly ScriptViewModel _script = script;
    private readonly Dictionary<DeviceAxis, AxisBypassState> _takenOver = [];
    private readonly Dictionary<DeviceAxis, double> _rotateDirection = [];

    private bool _autoTakeover;

    public string DeviceName { get; private set; } = "OSR";
    public IReadOnlyList<ButtplugActuator> Actuators { get; private set; } = [];
    public bool HasTakenOverAxes => _takenOver.Count > 0;

    public void Configure(ButtplugServerOptions options)
    {
        DeviceName = string.IsNullOrWhiteSpace(options.DeviceName) ? "OSR" : options.DeviceName.Trim();
        _autoTakeover = options.AutoTakeover;

        var actuators = new List<ButtplugActuator>();
        var linearIndex = 0;
        var rotateIndex = 0;
        var scalarIndex = 0;

        foreach (var name in options.ExposedAxes)
        {
            if (!DeviceAxis.TryParse(name, out var axis))
            {
                Logger.Warn("Buttplug 服务器：跳过未知的轴 \"{0}\"", name);
                continue;
            }

            if (actuators.Any(a => a.Axis == axis))
                continue;

            // L* 位置（LinearCmd）、R* 旋转（RotateCmd）、V*/A* 强度（ScalarCmd 的 Vibrate）
            var kind = axis.Name.ToUpperInvariant() switch
            {
                var n when n.StartsWith('L') => ButtplugActuatorKind.Linear,
                var n when n.StartsWith('R') => ButtplugActuatorKind.Rotate,
                _ => ButtplugActuatorKind.Scalar,
            };

            actuators.Add(kind switch
            {
                ButtplugActuatorKind.Linear => new ButtplugActuator(axis, kind, linearIndex++),
                ButtplugActuatorKind.Rotate => new ButtplugActuator(axis, kind, rotateIndex++),
                _ => new ButtplugActuator(axis, kind, scalarIndex++),
            });
        }

        Actuators = actuators;
        Logger.Info("Buttplug 服务器暴露的轴：{0}",
            actuators.Count == 0 ? "（无）" : string.Join("、", actuators.Select(a => $"{a.Axis.Name}={a.Kind}#{a.Index}")));
    }

    /// <summary>位置指令：LinearCmd 的 Vectors。</summary>
    public bool ApplyLinear(int index, double position, double durationSeconds)
    {
        if (!TryGetActuator(ButtplugActuatorKind.Linear, index, out var actuator))
            return false;

        TakeOver(actuator.Axis);
        _script.SetAxisTransition(actuator.Axis, MathUtils.Clamp01(position), Math.Max(durationSeconds, 0));
        return true;
    }

    /// <summary>
    /// 旋转指令：Buttplug 的 RotateCmd 是"转速 + 方向"，而 TCode 的 R 轴是位置型，
    /// 所以折中成来回摆动：幅度和时长按转速折算，方向决定先往哪边。
    /// </summary>
    public bool ApplyRotate(int index, double speed, bool clockwise)
    {
        if (!TryGetActuator(ButtplugActuatorKind.Rotate, index, out var actuator))
            return false;

        TakeOver(actuator.Axis);

        _rotateDirection.TryGetValue(actuator.Axis, out var direction);
        direction = direction >= 0 ? -1 : 1;
        _rotateDirection[actuator.Axis] = direction;

        var speed01 = MathUtils.Clamp01(speed);
        var side = direction * (clockwise ? 1 : -1);
        var target = MathUtils.Clamp01(0.5 + 0.5 * speed01 * side);
        var duration = Math.Max(0.08, 0.6 - 0.5 * speed01);

        _script.SetAxisTransition(actuator.Axis, target, duration);
        return true;
    }

    /// <summary>强度指令：ScalarCmd 的 Vibrate 等。</summary>
    public bool ApplyScalar(int index, double value)
    {
        if (!TryGetActuator(ButtplugActuatorKind.Scalar, index, out var actuator))
            return false;

        TakeOver(actuator.Axis);
        // 强度是瞬时值，给一个很短的过渡：既走同一条管线（受轴限位约束），又不会慢得看得见
        _script.SetAxisTransition(actuator.Axis, MathUtils.Clamp01(value), 0.1);
        return true;
    }

    private bool TryGetActuator(ButtplugActuatorKind kind, int index, out ButtplugActuator actuator)
    {
        actuator = Actuators.FirstOrDefault(a => a.Kind == kind && a.Index == index);
        if (actuator == null)
            Logger.Warn("Buttplug 服务器：没有 {0} 类型、索引 {1} 的执行器", kind, index);

        return actuator != null;
    }

    private void TakeOver(DeviceAxis axis)
    {
        if (!_autoTakeover || _takenOver.ContainsKey(axis))
            return;

        var settings = _script.AxisSettings[axis];
        _takenOver[axis] = new AxisBypassState(settings.BypassScript, settings.BypassMotionProvider);

        // 脚本和运动提供器都让开，保证外部指令是唯一来源（否则运动提供器会把值盖回去）
        settings.BypassScript = true;
        settings.BypassMotionProvider = true;

        Logger.Info("Buttplug 接管轴 {0}（暂时绕过脚本与运动提供器）", axis);
    }

    /// <summary>把控制权交还给脚本 / 运动提供器。</summary>
    public void ReleaseAll()
    {
        if (_takenOver.Count == 0)
            return;

        foreach (var (axis, original) in _takenOver)
        {
            var settings = _script.AxisSettings[axis];
            settings.BypassScript = original.BypassScript;
            settings.BypassMotionProvider = original.BypassMotionProvider;

            // 清掉外部过渡，让脚本 / 运动提供器立刻接手
            var state = _script.AxisStates[axis];
            using (state.BeginUpdateScope())
                state.ExternalTransition.Reset();
        }

        Logger.Info("Buttplug 已交还控制权：{0}", string.Join("、", _takenOver.Keys.Select(a => a.Name)));
        _takenOver.Clear();
    }

    private readonly record struct AxisBypassState(bool BypassScript, bool BypassMotionProvider);
}
