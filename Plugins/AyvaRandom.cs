// ============================================================================
//  AyvaRandom —— MultiFunPlayer 插件：实时随机行程（Ayva Stroker Lite 的 Free Play 搬运版）
// ----------------------------------------------------------------------------
//  依赖：带「脚本覆盖内核」（IScriptOverrideController）的 MultiFunPlayer，
//        以及本版本新增的「实时值」模式（SetOverrideLiveValues）。
//
//  功能：
//    * 内置 42 条 TempestStroke 行程（与 Ayva Stroker Lite 内置库一致，数据在同目录的
//      AyvaRandom.strokes.json，由 tools/ayva-to-funscript 生成）
//    * 随机播放：从勾选的行程里随机挑选，每段随机时长、随机 bpm，段与段之间平滑过渡
//    * bpm 可「连续变化」或「只在换段时变」；可选默认慢速扭转（R0）
//    * 运行动作以 50Hz 实时生成并交给覆盖内核 ⇒ 轴的限位 / 限速 / 软启动 / 归位全部生效
//    * 快捷键：AyvaRandom::Toggle / AyvaRandom::Next / AyvaRandom::Stop / AyvaRandom::Save
//
//  安装：把本文件与 AyvaRandom.strokes.json 一起放到 MultiFunPlayer.exe 同目录的 Plugins\ 下。
//  配置：AyvaRandom.config.json（与本文件同目录，自动读写）。
// ============================================================================

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using MahApps.Metro.Controls;
using MaterialDesignThemes.Wpf;
using MultiFunPlayer.Common;
using MultiFunPlayer.Script;
using MultiFunPlayer.UI.Controls;
using MultiFunPlayer.UI.Controls.ViewModels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Stylet;
using StyletIoC;

[DisplayName("Ayva 随机")]
[JsonObject(MemberSerialization = MemberSerialization.OptIn)]
public class AyvaRandom : PluginBase
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>工作线程空转间隔（毫秒）—— 动作按 50Hz 生成，所以用 20ms。</summary>
    private const int MonitorIntervalMilliseconds = 20;

    private const double EnginePeriod = 0.02;

    private static readonly string[] ActionNames =
    [
        "AyvaRandom::Toggle",
        "AyvaRandom::Next",
        "AyvaRandom::Stop",
        "AyvaRandom::Home",
        "AyvaRandom::FreePlay",
        "AyvaRandom::Save",
    ];

    #region 设置（持久化到 AyvaRandom.config.json）

    /// <summary>参与随机的行程名（AllStrokes=false 时生效）。</summary>
    [JsonProperty] public List<string> EnabledStrokes { get; set; } = [];

    /// <summary>true = 全部行程都参与随机（默认）；false = 只参与 <see cref="EnabledStrokes"/> 里勾选的。</summary>
    [JsonProperty] public bool AllStrokes { get; set; } = true;

    /// <summary>每条行程的权重（1~5，越大越容易被随机选中）。</summary>
    [JsonProperty] public Dictionary<string, int> StrokeWeights { get; set; } = [];

    [JsonProperty] public double BpmMinimum { get; set; } = 20;
    [JsonProperty] public double BpmMaximum { get; set; } = 60;

    /// <summary>true = bpm 只在换段（过渡）时改变（站点默认）；false = 段内持续变化。</summary>
    [JsonProperty] public bool BpmChangeOnTransition { get; set; } = true;

    /// <summary>加速度范围（bpm/s），只在「持续变化」模式下生效。</summary>
    [JsonProperty] public double AccelerationMinimum { get; set; } = 0;
    [JsonProperty] public double AccelerationMaximum { get; set; } = 20;

    /// <summary>每段行程持续时间的范围（秒），对应站点的 Pattern Duration。</summary>
    [JsonProperty] public double StrokeSecondsMinimum { get; set; } = 5;
    [JsonProperty] public double StrokeSecondsMaximum { get; set; } = 10;

    /// <summary>段与段之间过渡时长的范围（秒），对应站点的 Transition Duration。</summary>
    [JsonProperty] public double TransitionSecondsMinimum { get; set; } = 2;
    [JsonProperty] public double TransitionSecondsMaximum { get; set; } = 5;

    /// <summary>行程本身没有 R0 时，是否补一个扭转。</summary>
    [JsonProperty] public bool DefaultTwist { get; set; } = false;

    /// <summary>扭转的范围（对应站点的 Twist Range）。</summary>
    [JsonProperty] public double TwistRangeFrom { get; set; } = 0;
    [JsonProperty] public double TwistRangeTo { get; set; } = 1;

    /// <summary>扭转的相位（对应站点的 Twist Phase，范围 -4~4）。</summary>
    [JsonProperty] public double TwistPhase { get; set; } = 0;

    /// <summary>扭转的偏心度（对应站点的 Twist Eccentricity，范围 -1~1）。</summary>
    [JsonProperty] public double TwistEccentricity { get; set; } = 0;

    /// <summary>播放随机行程时暂停视频播放器（避免音频与设备动作无关地乱跑）。</summary>
    [JsonProperty] public bool PausePlayerOnPlay { get; set; } = true;

    #endregion

    #region 行程库

    private readonly record struct StrokeAxisParameter(double From, double To, double Phase, double Eccentricity)
    {
        /// <summary>
        /// Ayva 官网公式（Eccentric Parametric Oscillatory Motion）：
        ///   value = midpoint − scale · cos(θ + phase·π/2 + ecc·sin(θ + phase·π/2))
        /// 其中 θ 由 bpm 推进：θ' = bpm · t · 2π/60。
        /// </summary>
        public double Evaluate(double angle)
        {
            var scale = 0.5 * (To - From);
            var midpoint = 0.5 * (To + From);
            var a = angle + (0.5 * Math.PI * Phase);
            return midpoint - (scale * Math.Cos(a + (Eccentricity * Math.Sin(a))));
        }
    }

    private static DeviceAxis Axis(string name) => DeviceAxis.Parse(name) ?? throw new InvalidOperationException($"未知轴：{name}");

    private static Dictionary<string, Dictionary<DeviceAxis, StrokeAxisParameter>> _strokeLibrary;
    private static readonly Dictionary<string, Dictionary<DeviceAxis, StrokeAxisParameter>> FallbackLibrary = new()
    {
        ["down-forward"] = new()
        {
            [Axis("L0")] = new(0, 1, 0, 0.5),
            [Axis("L1")] = new(0.8, 0.2, 1, 0.8),
            [Axis("R2")] = new(0.2, 0.8, 1, 0.8),
        },
        ["orbit-grind"] = new()
        {
            [Axis("L0")] = new(0.1, 0.9, 0, 0.5),
            [Axis("L1")] = new(0.6, 0.4, 1, 0.5),
            [Axis("L2")] = new(0.6, 0.4, 2, 0.5),
            [Axis("R1")] = new(0.6, 0.4, 2, 0.5),
        },
    };

    private static Dictionary<string, Dictionary<DeviceAxis, StrokeAxisParameter>> StrokeLibrary
    {
        get
        {
            if (_strokeLibrary != null)
                return _strokeLibrary;

            try
            {
                var json = File.ReadAllText(GetStrokesPath());
                var raw = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, double[]>>>(json);
                var library = new Dictionary<string, Dictionary<DeviceAxis, StrokeAxisParameter>>();

                foreach (var (name, axes) in raw ?? [])
                {
                    var parsed = new Dictionary<DeviceAxis, StrokeAxisParameter>();
                    foreach (var (axisName, values) in axes)
                    {
                        var axis = DeviceAxis.Parse(axisName);
                        if (axis == null || values is not { Length: >= 4 })
                            continue;

                        parsed[axis] = new StrokeAxisParameter(values[0], values[1], values[2], values[3]);
                    }

                    if (parsed.Count > 0)
                        library[name] = parsed;
                }

                if (library.Count > 0)
                {
                    Log.Info("已载入 {0} 条行程（{1}）", library.Count, GetStrokesPath());
                    return _strokeLibrary = library;
                }

                Log.Warn("行程库文件是空的：{0}", GetStrokesPath());
            }
            catch (Exception e)
            {
                Log.Warn(e, "载入行程库失败：{0}", GetStrokesPath());
            }

            Log.Warn("改用内置的 {0} 条简化行程（请检查 AyvaRandom.strokes.json 是否和插件放在一起）", FallbackLibrary.Count);
            return _strokeLibrary = FallbackLibrary;
        }
    }

    private static string GetStrokesPath()
    {
        var sourcePath = GetSourceFilePath();
        if (!string.IsNullOrWhiteSpace(sourcePath))
            return Path.ChangeExtension(Path.ChangeExtension(sourcePath, null), ".strokes.json");

        return Path.Combine(Directory.GetCurrentDirectory(), "Plugins", "AyvaRandom.strokes.json");
    }

    private static string GetSourceFilePath([System.Runtime.CompilerServices.CallerFilePath] string path = null) => path;

    /// <summary>42 条内置行程的中文名（界面里显示这个，内部仍用英文键）。</summary>
    private static readonly Dictionary<string, string> ChineseNames = new()
    {
        ["down-forward"] = "下前插",
        ["down-backward"] = "下后拉",
        ["back-thrust-down"] = "后顶下压",
        ["back-thrust-down-swirl"] = "后顶下压·旋",
        ["thrust-forward"] = "向前顶",
        ["thrust-forward-swirl"] = "向前顶·旋",
        ["lean-forward-thrust-down"] = "前倾下压",
        ["lean-forward-thrust-down-swirl"] = "前倾下压·旋",
        ["diagonal-down-back"] = "斜向下后",
        ["diagonal-down-forward"] = "斜向下前",
        ["orbit-tease"] = "环绕挑逗",
        ["left-right-tease"] = "左右挑逗",
        ["forward-back-tease"] = "前后挑逗",
        ["vortex-tease"] = "漩涡挑逗",
        ["swirl-tease"] = "旋涡挑逗",
        ["forward-back-grind"] = "前后研磨",
        ["orbit-grind"] = "环绕研磨",
        ["short-low-roll-forward"] = "短·低位翻滚·前",
        ["short-low-roll-backward"] = "短·低位翻滚·后",
        ["short-mid-roll-forward"] = "短·中位翻滚·前",
        ["short-mid-roll-backward"] = "短·中位翻滚·后",
        ["short-high-roll-forward"] = "短·高位翻滚·前",
        ["short-high-roll-backward"] = "短·高位翻滚·后",
        ["long-stroke-1"] = "长行程 1",
        ["long-stroke-2"] = "长行程 2",
        ["long-stroke-3"] = "长行程 3",
        ["long-stroke-4"] = "长行程 4",
        ["long-stroke-5"] = "长行程 5",
        ["grind-circular"] = "圆周研磨",
        ["grind-vortex"] = "漩涡研磨",
        ["grind-forward-back"] = "前后研磨·同步",
        ["grind-forward-back-phased"] = "前后研磨·错相",
        ["grind-forward-back-tilt"] = "前后研磨·倾斜",
        ["grind-forward-tilt"] = "前向研磨·倾斜",
        ["tease-orbit-right"] = "环绕挑逗·右",
        ["tease-orbit-left"] = "环绕挑逗·左",
        ["tease-left-right-rock"] = "左右摇摆挑逗",
        ["tease-down-back"] = "下后挑逗",
        ["tease-back-swirl-right"] = "后旋挑逗·右",
        ["tease-back-swirl-left"] = "后旋挑逗·左",
        ["tease-up-down-circle-right"] = "上下绕圈·右",
        ["tease-up-down-circle-left"] = "上下绕圈·左",
    };

    /// <summary>行程的中文显示名（没有登记就退回英文键）。</summary>
    private static string DisplayNameOf(string key)
        => ChineseNames.TryGetValue(key, out var chinese) ? chinese : key;

    #endregion

    #region 引擎状态

    private readonly BlockingCollection<Action> _commands = new();
    private readonly object _engineLock = new();

    /// <summary>脚本覆盖内核（由宿主注入）——实时值模式就靠它把动作交给设备。</summary>
    [Inject] internal IScriptOverrideController OverrideController { get; set; }

    private Dictionary<DeviceAxis, double> _values = [];
    private Dictionary<DeviceAxis, double> _transitionFrom;
    private Dictionary<DeviceAxis, StrokeAxisParameter> _stroke;
    private string _strokeName;
    private double _angle;
    private double _twistAngle;
    private double _bpm = 120;
    private double _targetBpm = 120;
    private double _acceleration = 20;

    /// <summary>本插件自己暂停/恢复播放器时，在这段时间内忽略播放状态变化（毫秒）。</summary>
    private const int PlayerChangeSuppressMilliseconds = 2500;

    /// <summary>continuous 模式下累积的时间，用来"每秒按加速度走一步"。</summary>
    private double _bpmStepAccumulator;

    private double BpmMinimumClamped => Math.Max(2, Math.Min(BpmMinimum, BpmMaximum));

    private double BpmMaximumClamped => Math.Max(BpmMinimumClamped, Math.Max(BpmMinimum, BpmMaximum));

    /// <summary>下一段强制播放的行程（点列表里的名字时用；用完就清空）。</summary>
    private string _nextStrokeOverride;

    /// <summary>
    /// 用户点名字选中的那一条：会**一直循环播放**它，直到用户主动打断
    /// （再点 ▶、点「下一段」、点「重新开始」、或点另一条名字）。
    /// </summary>
    private string _lockedStroke;

    /// <summary>本插件自己造成的播放状态变化，在这之前忽略（避免把自己暂停/恢复当成"用户开始播放视频"）。</summary>
    private long _suppressPlayerStartTicks;
    private double _segmentRemaining;
    private double _segmentTotal;
    private double _transitionRemaining;
    private double _transitionTotal;
    private bool _playing;
    private bool _pausedPlayer;
    private long _lastStepTicks;

    private bool IsPlaying { get { lock (_engineLock) return _playing; } }

    private string CurrentStrokeName { get { lock (_engineLock) return _strokeName; } }

    private double CurrentBpm { get { lock (_engineLock) return _bpm; } }

    private double SegmentRemaining { get { lock (_engineLock) return _segmentRemaining; } }

    #endregion

    #region 生命周期

    protected override void OnInitialize()
    {
        EnabledStrokes ??= [];

        RegisterAction("AyvaRandom::Toggle", () => Enqueue(TogglePlay));
        RegisterAction("AyvaRandom::Next", () => Enqueue(NextStroke));
        RegisterAction("AyvaRandom::Stop", () => Enqueue(() => StopCore(true)));
        RegisterAction("AyvaRandom::Home", () => Enqueue(HomeDevice));
        RegisterAction("AyvaRandom::FreePlay", () => Enqueue(FreePlay));
        RegisterAction("AyvaRandom::Save", SaveSettingsNow);

        CancellationToken.Register(UnregisterAllActions);

        StartThread(Worker);
        RefreshUi();

        if (OverrideController == null)
        {
            Log.Error("未注入 IScriptOverrideController —— 这个 MultiFunPlayer 版本没有脚本覆盖内核（实时值模式）");
            SetHint("⚠ 当前 MultiFunPlayer 没有「脚本覆盖内核（实时值）」，无法实时驱动设备。");
        }
        else
        {
            Log.Info("AyvaRandom 已加载（行程 {0} 条）", StrokeLibrary.Count);
        }
    }

    protected override void OnDispose()
    {
        try
        {
            StopCore(true);
        }
        catch (Exception e)
        {
            Log.Warn(e, "AyvaRandom 退出还原失败");
        }

        UnregisterAllActions();

        try { _commands.CompleteAdding(); } catch { }
    }

    private void UnregisterAllActions()
    {
        foreach (var name in ActionNames)
        {
            try
            {
                UnregisterAction(name);
            }
            catch (Exception e)
            {
                Log.Trace(e, "注销动作失败 [{0}]", name);
            }
        }
    }

    public override UIElement CreateView() => BuildView();

    public override void HandleSettings(JObject settings, SettingsAction action)
    {
        base.HandleSettings(settings, action);

        if (action == SettingsAction.Loading)
        {
            EnabledStrokes ??= [];
            RefreshUi();
        }
    }

    /// <summary>
    /// 和「脚本预设」一样的优先级逻辑：视频开始播放时，把控制权交回视频脚本（停掉随机播放）。
    /// </summary>
    protected override void HandleMessage(MediaPlayingChangedMessage message)
    {
        if (!message.IsPlaying)
            return;

        if (Environment.TickCount64 < Interlocked.Read(ref _suppressPlayerStartTicks))
        {
            Log.Debug("忽略这次播放状态变化（{0} 毫秒内视作本插件自己造成的）", PlayerChangeSuppressMilliseconds);
            return;
        }

        Enqueue(() =>
        {
            if (!IsPlaying)
                return;

            Log.Info("检测到视频开始播放，停止随机播放（控制权交回视频脚本）");
            SetHint("检测到视频开始播放，随机播放已停止（控制权已交回视频脚本）");
            StopCore(false);
        });
    }

    private void Enqueue(Action command)
    {
        try
        {
            if (!_commands.IsAddingCompleted)
                _commands.Add(command);
        }
        catch (Exception e)
        {
            Log.Warn(e, "AyvaRandom 命令入队失败");
        }
    }

    private void Worker(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_commands.TryTake(out var command, MonitorIntervalMilliseconds, token))
                {
                    command();
                }
                else
                {
                    StepEngine();
                    FlushSettingsIfDirty();
                    HideExpiredHint();
                    UpdateStatusText();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                Log.Error(e, "AyvaRandom 工作线程出错");
            }
        }
    }

    #endregion

    #region 引擎

    private void TogglePlay()
    {
        if (IsPlaying)
            StopCore(true);
        else
            StartCore();
    }

    private void StartCore()
    {
        var controller = OverrideController;
        if (controller == null)
        {
            SetHint("⚠ 当前 MultiFunPlayer 没有「脚本覆盖内核（实时值）」，无法实时驱动设备。");
            return;
        }

        // 没勾任何行程时别"默默播全部"，直接提示（原来空列表被当成全部，导致「全不选」看起来没反应）；
        // 但点名字直接播放时不算（那是明确的单条选择）
        if (Pool().Count == 0 && _nextStrokeOverride == null && _lockedStroke == null)
        {
            SetHint("⚠ 没有勾选任何行程 —— 先在上面勾几条（或点「全选」）再播放。");
            return;
        }

        // ▶ = 随机模式（解除单条循环；点名字时会在这之后再重新锁定）
        _lockedStroke = null;

        // 可选：暂停视频播放器（和预设插件一样，避免两边同时抢设备）
        _pausedPlayer = false;
        if (PausePlayerOnPlay)
        {
            try
            {
                if (ReadProperty<bool>("Media::PlayPause"))
                {
                    Interlocked.Exchange(ref _suppressPlayerStartTicks, Environment.TickCount64 + PlayerChangeSuppressMilliseconds);
                    PublishMessage(new MediaPlayPauseMessage(false));
                    _pausedPlayer = true;
                }
            }
            catch (Exception e)
            {
                Log.Trace(e, "读取播放器状态失败");
            }
        }

        lock (_engineLock)
        {
            _playing = true;
            _values.Clear();
            _transitionFrom = null;
            _transitionRemaining = 0;
            _transitionTotal = 0;
            _twistAngle = 0;
            _lastStepTicks = Stopwatch.GetTimestamp();
            _bpm = NextRandomBpm();
            _bpmStepAccumulator = 0;
            StartNextSegment(initial: true);
        }

        Log.Info("AyvaRandom 开始播放（行程池 {0} 条，bpm {1:0}~{2:0}）", Pool().Count, Math.Min(BpmMinimum, BpmMaximum), Math.Max(BpmMinimum, BpmMaximum));
        RefreshUi();
    }

    private void StopCore(bool resumePlayer)
    {
        var wasPlaying = IsPlaying;

        lock (_engineLock)
        {
            _playing = false;
            _stroke = null;
            _strokeName = null;
            _transitionFrom = null;
            _transitionRemaining = 0;
            _transitionTotal = 0;
            _segmentTotal = 0;
        }

        try
        {
            OverrideController?.SetOverrideLiveValues(null);
        }
        catch (Exception e)
        {
            Log.Warn(e, "交还控制权失败");
        }

        if (resumePlayer && _pausedPlayer)
        {
            try
            {
                Interlocked.Exchange(ref _suppressPlayerStartTicks, Environment.TickCount64 + PlayerChangeSuppressMilliseconds);
                PublishMessage(new MediaPlayPauseMessage(true));
            }
            catch (Exception e)
            {
                Log.Trace(e, "恢复播放器失败");
            }
        }

        _pausedPlayer = false;

        if (wasPlaying)
            Log.Info("AyvaRandom 已停止");

        RefreshUi();
    }

    /// <summary>
    /// 随机播放（网站的 Free Play）：在勾选的行程里随机播。
    /// 如果当前正锁着某一条（点名字选的），会先解除锁定、回到随机。
    /// </summary>
    private void FreePlay()
    {
        _lockedStroke = null;
        _nextStrokeOverride = null;

        if (!IsPlaying)
        {
            StartCore();
            return;
        }

        lock (_engineLock)
            StartNextSegment(initial: false);

        RefreshUi();
    }

    /// <summary>
    /// 归位（网站的 Home Device）：先停止脚本，再按各轴的「归位目标值」把设备平滑送回原位，
    /// 最后交还控制权（不再压着轴，让 AutoHome 之类的设置继续按用户配置工作）。
    /// </summary>
    private void HomeDevice()
    {
        StopCore(false);

        var controller = OverrideController;
        if (controller == null)
            return;

        var targets = new Dictionary<DeviceAxis, double>();
        var starts = new Dictionary<DeviceAxis, double>();

        foreach (var axis in DeviceAxis.All)
        {
            var target = 0.5;
            var current = 0.5;

            try
            {
                target = Math.Clamp(ReadProperty<DeviceAxis, double>("Axis::AutoHomeTargetValue", axis), 0, 1);
            }
            catch (Exception e)
            {
                Log.Trace(e, "读取归位目标值失败 [{0}]", axis);
            }

            try
            {
                current = Math.Clamp(ReadProperty<DeviceAxis, double>("Axis::Value", axis), 0, 1);
            }
            catch (Exception e)
            {
                Log.Trace(e, "读取当前值失败 [{0}]", axis);
            }

            targets[axis] = target;
            starts[axis] = current;
        }

        // 平滑送回（约 1 秒，50 步 × 20ms），避免设备突然跳变
        const int steps = 50;
        for (var step = 1; step <= steps; step++)
        {
            var progress = (double)step / steps;
            var values = new Dictionary<DeviceAxis, double>();

            foreach (var (axis, target) in targets)
                values[axis] = starts[axis] + ((target - starts[axis]) * progress);

            controller.SetOverrideLiveValues(values);
            Thread.Sleep(20);
        }

        controller.SetOverrideLiveValues(null);
        Log.Info("AyvaRandom 已归位");
        SetHint("已停止并归位");
    }

    private void NextStroke()
    {
        if (!IsPlaying)
            return;

        // 「下一段」= 解除单条循环，回到随机
        _lockedStroke = null;

        lock (_engineLock)
            StartNextSegment(initial: false);

        RefreshUi();
    }

    /// <summary>
    /// 点列表里的名字：立刻播放这一条（像网页那样）。
    /// 没在播放就以它作为第一段开始；正在播放则平滑切到它，之后继续随机。
    /// </summary>
    private void PlaySingleStroke(string name)
    {
        if (!StrokeLibrary.ContainsKey(name))
            return;

        if (!IsPlaying)
        {
            _nextStrokeOverride = name;
            StartCore();
            _nextStrokeOverride = null;
            _lockedStroke = name;   // 点选的那条会一直循环，直到主动打断
            RefreshUi();
            return;
        }

        lock (_engineLock)
        {
            if (_strokeName == name && _lockedStroke == name)
                return;   // 已经在单条循环这一条

            _nextStrokeOverride = name;
            StartNextSegment(initial: false);
            _nextStrokeOverride = null;
        }

        _lockedStroke = name;
        RefreshUi();
    }

    /// <summary>
    /// 设置一改就立刻作用到**正在播放的这一段**（原来只在换段时才读，所以要等好几秒才生效）：
    /// bpm 夹进新范围并重抽目标（持续变化模式下会平滑过去）、剩余时长/过渡时长也夹进新范围。
    /// </summary>
    private void ApplySettingsLive()
    {
        MarkSettingsDirty();

        lock (_engineLock)
        {
            if (!_playing || _stroke == null)
                return;

            var bpmMin = Math.Max(2, Math.Min(BpmMinimum, BpmMaximum));
            var bpmMax = Math.Max(bpmMin, Math.Max(BpmMinimum, BpmMaximum));

            // 只把当前 bpm 夹进新范围，**不重抽** ——
            // 否则每次动一下滑块都会换一次速度（用户明确不想要这个行为）
            _bpm = Math.Clamp(_bpm, bpmMin, bpmMax);

            var segmentMin = Math.Max(0.5, Math.Min(StrokeSecondsMinimum, StrokeSecondsMaximum));
            var segmentMax = Math.Max(segmentMin, Math.Max(StrokeSecondsMinimum, StrokeSecondsMaximum));
            _segmentRemaining = Math.Clamp(_segmentRemaining, segmentMin, segmentMax);
            _segmentTotal = Math.Max(_segmentTotal, _segmentRemaining);

            if (_transitionRemaining > 0)
            {
                var transitionMax = Math.Max(0, Math.Max(TransitionSecondsMinimum, TransitionSecondsMaximum));
                _transitionRemaining = Math.Min(_transitionRemaining, transitionMax);
                _transitionTotal = Math.Max(_transitionTotal, _transitionRemaining);
            }
        }

        UpdateStatusText();
    }

    /// <summary>可选的行程池（「全选」= 全部；否则用勾选的那些；可能为空）。</summary>
    private List<string> Pool()
    {
        var library = StrokeLibrary;
        if (AllStrokes)
            return [.. library.Keys];

        return [.. (EnabledStrokes ?? []).Where(library.ContainsKey)];
    }

    /// <summary>某条行程的权重（1~5，默认 3）。</summary>
    private int WeightOf(string name)
        => StrokeWeights != null && StrokeWeights.TryGetValue(name, out var weight) ? Math.Clamp(weight, 1, 5) : 3;

    /// <summary>按权重随机挑一条：权重越大越容易被选中。</summary>
    private string PickRandomStroke(List<string> pool)
    {
        var total = 0;
        foreach (var name in pool)
            total += WeightOf(name);

        if (total <= 0)
            return pool[Random.Shared.Next(pool.Count)];

        var roll = Random.Shared.Next(total);
        foreach (var name in pool)
        {
            roll -= WeightOf(name);
            if (roll < 0)
                return name;
        }

        return pool[^1];
    }

    private double NextRandomBpm()
    {
        var min = Math.Min(BpmMinimum, BpmMaximum);
        var max = Math.Max(BpmMinimum, BpmMaximum);

        // 下限保护：bpm 太小时设备几乎不动（范围被拉到 0 就会看起来"没反应"）
        min = Math.Max(min, 2);
        max = Math.Max(max, min);

        return min + (Random.Shared.NextDouble() * (max - min));
    }

    private double NextRandomSegmentSeconds()
    {
        var min = Math.Min(StrokeSecondsMinimum, StrokeSecondsMaximum);
        var max = Math.Max(StrokeSecondsMinimum, StrokeSecondsMaximum);
        return Math.Max(0.5, min + (Random.Shared.NextDouble() * (max - min)));
    }

    private double NextRandomTransitionSeconds()
    {
        var min = Math.Min(TransitionSecondsMinimum, TransitionSecondsMaximum);
        var max = Math.Max(TransitionSecondsMinimum, TransitionSecondsMaximum);
        return Math.Max(0, min + (Random.Shared.NextDouble() * (max - min)));
    }

    /// <summary>本次换段用的加速度（bpm/s），只在「持续变化」模式下用得上。</summary>
    private double NextRandomAcceleration()
    {
        var min = Math.Min(AccelerationMinimum, AccelerationMaximum);
        var max = Math.Max(AccelerationMinimum, AccelerationMaximum);
        return Math.Max(0, min + (Random.Shared.NextDouble() * (max - min)));
    }

    /// <summary>开始下一段：随机挑一个行程，记下过渡起点（当前值），随机时长与 bpm。</summary>
    private void StartNextSegment(bool initial)
    {
        var pool = Pool();
        var forced = _nextStrokeOverride ?? _lockedStroke;
        if (pool.Count == 0 && forced == null)
        {
            _playing = false;
            return;
        }

        var name = forced ?? PickRandomStroke(pool);
        if (!StrokeLibrary.TryGetValue(name, out var axes))
            return;

        // 过渡起点 = 当前的实时值（没有就取各行程的中心）
        _transitionFrom = _values.Count > 0 ? new Dictionary<DeviceAxis, double>(_values) : null;
        _transitionTotal = NextRandomTransitionSeconds();
        _transitionRemaining = _transitionTotal;

        _stroke = axes;
        _strokeName = name;
        if (initial)
            _angle = 0;

        // transition 模式（或刚开始）才抽新的 bpm；continuous 模式让它一直游走下去
        if (initial || BpmChangeOnTransition)
            _bpm = NextRandomBpm();
        _bpmStepAccumulator = 0;
        _segmentRemaining = NextRandomSegmentSeconds();
        _segmentTotal = _segmentRemaining;

        if (!initial)
            Log.Debug("AyvaRandom 换段：{0}（bpm {1:0}，本段 {2:0.0}s，过渡 {3:0.0}s）", name, _bpm, _segmentRemaining, _transitionTotal);
    }

    /// <summary>按 50Hz 推进动作，并把各轴实时值交给覆盖内核。</summary>
    private void StepEngine()
    {
        if (!IsPlaying)
            return;

        var now = Stopwatch.GetTimestamp();
        var delta = (now - _lastStepTicks) / (double)Stopwatch.Frequency;
        _lastStepTicks = now;

        if (delta <= 0)
            return;

        // 掉帧（比如系统卡顿）时限制单步推进量，避免动作跳变
        delta = Math.Min(delta, EnginePeriod * 5);

        Dictionary<DeviceAxis, double> snapshot;

        lock (_engineLock)
        {
            if (!_playing || _stroke == null)
                return;

            // bpm：
            //   transition 模式 —— bpm 每段抽一次，只在换段（过渡）时改变（站点默认）
            //   continuous 模式 —— 与站点源码一致：**每秒按「加速度」随机走一步、方向随机**，
            //                     步长在加速度范围内随机取（不是朝某个目标移动），并夹在 bpm 范围内
            if (!BpmChangeOnTransition)
            {
                _bpmStepAccumulator += delta;
                while (_bpmStepAccumulator >= 1.0)
                {
                    _bpmStepAccumulator -= 1.0;

                    var step = NextRandomAcceleration();   // 本站点：Math.random() 取一个加速度值
                    if (step <= 0)
                        continue;

                    var direction = Random.Shared.NextDouble() < 0.5 ? -1 : 1;
                    _bpm = Math.Clamp(_bpm + (direction * step), BpmMinimumClamped, BpmMaximumClamped);
                }
            }

            // θ 连续推进：一个「行程」= π 弧度（向上或向下），公式本身以 2π 为周期，
            // 所以 θ 一直累加就是平滑的往复运动（回绕到 [0,2π) 不改变取值）。
            var previousStrokeIndex = (long)Math.Floor(_angle / Math.PI);
            _angle += _bpm * 2 * Math.PI / 60 * delta;
            var crossedStrokeBoundary = (long)Math.Floor(_angle / Math.PI) != previousStrokeIndex;

            while (_angle >= 2 * Math.PI)
            {
                _angle -= 2 * Math.PI;
                crossedStrokeBoundary = true;   // 回绕本身也是一次行程边界
            }

            _segmentRemaining -= delta;
            if (_transitionRemaining > 0)
                _transitionRemaining = Math.Max(0, _transitionRemaining - delta);

            var next = new Dictionary<DeviceAxis, double>();
            foreach (var (axis, parameter) in _stroke)
            {
                var value = parameter.Evaluate(_angle);

                if (_transitionRemaining > 0 && _transitionFrom != null && _transitionFrom.TryGetValue(axis, out var from))
                {
                    var progress = 1 - (_transitionRemaining / Math.Max(0.001, _transitionTotal));
                    var eased = 0.5 - (0.5 * Math.Cos(Math.PI * Math.Clamp(progress, 0, 1)));
                    value = from + ((value - from) * eased);
                }

                next[axis] = Math.Clamp(value, 0, 1);
            }

            // 可选：补一个扭转（行程本身"没用扭转"时），范围/相位/偏心度都按站点的 Twist 参数来。
            // 注意：内置库里每条行程都写了 R0，但大多是"静止的"（from == to），
            // 那种情况也算没用扭转 ⇒ 必须补上，否则「默认扭转」看起来不生效。
            if (DefaultTwist)
            {
                var twistAxis = DeviceAxis.Parse("R0");
                var hasMovingTwist = twistAxis != null
                                  && _stroke.TryGetValue(twistAxis, out var existing)
                                  && Math.Abs(existing.From - existing.To) > 1e-6;

                if (twistAxis != null && !hasMovingTwist)
                {
                    var twist = new StrokeAxisParameter(TwistRangeFrom, TwistRangeTo, TwistPhase, TwistEccentricity);
                    next[twistAxis] = Math.Clamp(twist.Evaluate(_angle), 0, 1);
                }
            }

            _values = next;
            snapshot = next;

            if (_segmentRemaining <= 0 && crossedStrokeBoundary)
                StartNextSegment(initial: false);
        }

        try
        {
            OverrideController?.SetOverrideLiveValues(snapshot);
        }
        catch (Exception e)
        {
            Log.Warn(e, "AyvaRandom 写入实时值失败");
        }
    }

    #endregion

    #region 配置持久化

    private bool _settingsDirty;
    private long _lastSaveTicks;

    private void MarkSettingsDirty() => _settingsDirty = true;

    private void FlushSettingsIfDirty()
    {
        if (!_settingsDirty)
            return;

        var now = Stopwatch.GetTimestamp();
        if (_lastSaveTicks != 0 && now - _lastSaveTicks < Stopwatch.Frequency * 5)
            return;

        _settingsDirty = false;
        SaveSettings();
    }

    private void SaveSettingsNow()
    {
        _settingsDirty = false;
        SaveSettings();
    }

    private void SaveSettings()
    {
        _lastSaveTicks = Stopwatch.GetTimestamp();

        try
        {
            File.WriteAllText(GetSettingsPath(), JsonConvert.SerializeObject(this, Formatting.Indented));
        }
        catch (Exception e)
        {
            Log.Warn(e, "保存 AyvaRandom 配置失败");
            SetHint($"保存失败：{e.Message}");
        }
    }

    private string GetSettingsPath()
    {
        var sourcePath = GetSourceFilePath();
        if (!string.IsNullOrWhiteSpace(sourcePath))
            return Path.ChangeExtension(Path.ChangeExtension(sourcePath, null), ".config.json");

        return Path.Combine(Directory.GetCurrentDirectory(), "Plugins", "AyvaRandom.config.json");
    }

    #endregion

    #region 界面

    private TextBlock _statusText;
    private TextBlock _hintText;
    private PluginViewBase _view;
    private Viewport3D _simulator;
    private Model3DGroup _simulatorModel;
    private Transform3DGroup _swayTransform;
    private Transform3DGroup _surgeTransform;
    private Transform3DGroup _strokeTransform;
    private Transform3DGroup _twistTransform;
    private Transform3DGroup _rollTransform;
    private Transform3DGroup _pitchTransform;
    private double _simulatorYaw = 32;
    private double _simulatorPitch = 16;
    private KeyframesHeatmap _timeline;
    private TextBlock _timelineTimeText;
    private IReadOnlyDictionary<DeviceAxis, KeyframeCollection> _segmentPreview;
    private long _lastTimelineUpdateTicks;
    private Button _playButton;
    private StackPanel _strokePoolPanel;
    private CheckBox _pausePlayerBox;
    private CheckBox _defaultTwistBox;
    private bool _loadingUi;
    private long _hintExpiresTicks;

    /// <summary>界面控件的回填动作（刷新时统一调用，省得为每个滑块留字段）。</summary>
    private readonly List<Action> _refreshControls = [];

    /// <summary>行程名按钮（用于高亮"正在播放"的那一条）。</summary>
    private readonly Dictionary<string, Button> _strokeButtons = [];
    private string _highlightedStroke;

    private UIElement BuildView()
    {
        Log.Info("AyvaRandom 面板已创建（插件标签页应出现在「脚本预设」旁边）");

        _statusText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 14,
            Margin = new Thickness(20, 0, 0, 0),
            Text = "空闲"
        };
        ApplyBodyForeground(_statusText);

        var root = new StackPanel { Margin = new Thickness(12, 8, 12, 12) };
        root.Children.Add(BuildTransport());
        root.Children.Add(BuildStrokePool());
        root.Children.Add(BuildRanges());

        _hintText = new TextBlock
        {
            FontSize = 13,
            Opacity = 0.9,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        ApplyBodyForeground(_hintText);
        root.Children.Add(_hintText);

        _ = StrokeLibrary;   // 提前载入，日志里能看到条数

        // 插件的根视图必须是 PluginViewBase —— 宿主的插件编译器会检查这一条
        // （不是的话会抛「插件视图必须继承 PluginViewBase」，整个插件编译失败、
        //   界面里就会出现"标签在、内容空"）。
        _view = new PluginViewBase
        {
            Content = root,
            ToolBarContent = _statusText
        };

        RefreshUi();
        return _view;
    }

    private UIElement BuildTransport()
    {
        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };

        // 三个按钮与网站一致：归位（停止并回到原位）/ 随机播放 / 停止
        var homeButton = MakeTextButton("归位", (_, _) => Enqueue(HomeDevice));
        homeButton.ToolTip = "停止脚本，并把设备平滑送回各轴的「归位目标值」（和网站的 Home Device 一样）";
        DockPanel.SetDock(homeButton, Dock.Left);
        row.Children.Add(homeButton);

        var freePlayButton = MakeTextButton("随机播放", (_, _) => Enqueue(FreePlay));
        freePlayButton.ToolTip = "在勾选的行程里随机播放（和网站的 Free Play 一样）";
        DockPanel.SetDock(freePlayButton, Dock.Left);
        row.Children.Add(freePlayButton);

        var stopButton = MakeTextButton("停止", (_, _) => Enqueue(() => StopCore(true)));
        stopButton.ToolTip = "停止脚本（设备停在当前位置；和网站的 Stop 一样）";
        DockPanel.SetDock(stopButton, Dock.Left);
        row.Children.Add(stopButton);

        row.Children.Add(_statusText);
        return row;
    }

    /// <summary>
    /// 实时 3D 机器模型（按用户要求替换掉原来的波形轴）：
    /// 用 WPF 的 Viewport3D 搭一个同风格的 OSR 模型 —— 底座不动，
    /// 上方托架按 L0/L1/L2 平移、按 R0/R1/R2 旋转，位置直接来自引擎的实时值。
    /// </summary>
    private UIElement BuildSimulator()
    {
        _simulatorModel = new Model3DGroup();

        var bodyMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x5B, 0x74, 0xA8)));
        var bodyDarkMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x41, 0x55, 0x7E)));
        var jointMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x33, 0x38, 0x40)));
        var armMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x22, 0x25, 0x2B)));
        var receiverMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x17, 0x19, 0x1D)));
        var steelMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB0)));

        // 变换链：底座 → 左右(L2) → 前后(L1) → 上下(L0) → 扭转(R0) → 侧倾(R1) → 俯仰(R2)
        _swayTransform = new Transform3DGroup();
        _surgeTransform = new Transform3DGroup();
        _strokeTransform = new Transform3DGroup();
        _twistTransform = new Transform3DGroup();
        _rollTransform = new Transform3DGroup();
        _pitchTransform = new Transform3DGroup();

        _swayTransform.Children.Add(new TranslateTransform3D());
        _surgeTransform.Children.Add(new TranslateTransform3D());
        _strokeTransform.Children.Add(new TranslateTransform3D());
        _twistTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0)));
        _rollTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0)));
        _pitchTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0)));

        // ---------- 只保留接收器圆柱体（按用户要求删掉底座与连杆） ----------
        var carriage = new Model3DGroup();

        carriage.Children.Add(MakeTaperedCylinder(24, 30, 86, 32, receiverMaterial, new TranslateTransform3D(0, 0, 0)));
        carriage.Children.Add(MakeCylinder(33, 10, 32, receiverMaterial, new TranslateTransform3D(0, 46, 0)));
        carriage.Children.Add(MakeCylinder(30, 8, 32, steelMaterial, new TranslateTransform3D(0, -45, 0)));

        carriage.Transform = _swayTransform;
        _simulatorModel.Children.Add(carriage);

        _swayTransform.Children.Add(_surgeTransform);
        _surgeTransform.Children.Add(_strokeTransform);
        _strokeTransform.Children.Add(_twistTransform);
        _twistTransform.Children.Add(_rollTransform);
        _rollTransform.Children.Add(_pitchTransform);

        _simulatorModel.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -1.4, -1)));
        _simulatorModel.Children.Add(new DirectionalLight(Color.FromRgb(0x77, 0x77, 0x77), new Vector3D(1, -0.5, 1)));
        _simulatorModel.Children.Add(new AmbientLight(Color.FromRgb(0x33, 0x33, 0x33)));

        _simulator = new Viewport3D
        {
            Height = 200,
            ClipToBounds = true,
            Children = { new ModelVisual3D { Content = _simulatorModel } }
        };

        // 按住拖动可以旋转视角
        var dragging = false;
        var last = new Point();
        _simulator.MouseLeftButtonDown += (_, e) =>
        {
            dragging = true;
            last = e.GetPosition(_simulator);
            _simulator.CaptureMouse();
        };
        _simulator.MouseLeftButtonUp += (_, _) =>
        {
            dragging = false;
            _simulator.ReleaseMouseCapture();
        };
        _simulator.MouseMove += (_, e) =>
        {
            if (!dragging)
                return;

            var now = e.GetPosition(_simulator);
            _simulatorYaw += (now.X - last.X) * 0.6;
            _simulatorPitch = Math.Clamp(_simulatorPitch + ((now.Y - last.Y) * 0.4), -80, 80);
            last = now;
            UpdateSimulatorCamera();
        };

        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
        var hint = new TextBlock
        {
            Text = "实时模拟（按住拖动可旋转视角）",
            FontSize = 12,
            Opacity = 0.8,
            Margin = new Thickness(0, 0, 0, 2)
        };
        ApplyBodyForeground(hint);
        panel.Children.Add(hint);
        panel.Children.Add(_simulator);

        UpdateSimulatorCamera();
        return panel;
    }

    private void UpdateSimulatorCamera()
    {
        if (_simulator == null)
            return;

        var yaw = _simulatorYaw * Math.PI / 180;
        var pitch = _simulatorPitch * Math.PI / 180;
        var distance = 340.0;
        var target = new Point3D(0, 0, 0);

        var eye = new Point3D(
            target.X + (distance * Math.Cos(pitch) * Math.Sin(yaw)),
            target.Y + (distance * Math.Sin(pitch)),
            target.Z + (distance * Math.Cos(pitch) * Math.Cos(yaw)));

        _simulator.Camera = new PerspectiveCamera(
            eye,
            new Vector3D(target.X - eye.X, target.Y - eye.Y, target.Z - eye.Z),
            new Vector3D(0, 1, 0),
            45);
    }

    /// <summary>用引擎的实时值驱动模型（调用频率由调用方限流）。</summary>
    private void UpdateSimulator()
    {
        if (_simulator == null)
            return;

        double l0 = 0.5, l1 = 0.5, l2 = 0.5, r0 = 0.5, r1 = 0.5, r2 = 0.5;
        lock (_engineLock)
        {
            if (_values.Count > 0)
            {
                double Read(string name)
                {
                    var axis = DeviceAxis.Parse(name);
                    return axis != null && _values.TryGetValue(axis, out var value) ? value : 0.5;
                }

                l0 = Read("L0");
                l1 = Read("L1");
                l2 = Read("L2");
                r0 = Read("R0");
                r1 = Read("R1");
                r2 = Read("R2");
            }
        }

        Execute.OnUIThread(() =>
        {
            if (_strokeTransform?.Children[0] is TranslateTransform3D stroke)
                stroke.OffsetY = (l0 - 0.5) * 120;

            if (_surgeTransform?.Children[0] is TranslateTransform3D surge)
                surge.OffsetZ = (l1 - 0.5) * 90;

            if (_swayTransform?.Children[0] is TranslateTransform3D sway)
                sway.OffsetX = (l2 - 0.5) * 90;

            if (_twistTransform?.Children[0] is RotateTransform3D twist && twist.Rotation is AxisAngleRotation3D twistAxis)
                twistAxis.Angle = (r0 - 0.5) * 360;

            if (_rollTransform?.Children[0] is RotateTransform3D roll && roll.Rotation is AxisAngleRotation3D rollAxis)
                rollAxis.Angle = (r1 - 0.5) * 90;

            if (_pitchTransform?.Children[0] is RotateTransform3D pitch && pitch.Rotation is AxisAngleRotation3D pitchAxis)
                pitchAxis.Angle = (r2 - 0.5) * 90;
        });
    }

    /// <summary>造一个长方体。</summary>
    private static GeometryModel3D MakeBox(double width, double height, double depth, Material material, Transform3D transform = null)
    {
        var x = width / 2;
        var y = height / 2;
        var z = depth / 2;

        var mesh = new MeshGeometry3D();
        Point3D[] corners =
        [
            new(-x, -y, -z), new(x, -y, -z), new(x, y, -z), new(-x, y, -z),
            new(-x, -y, z), new(x, -y, z), new(x, y, z), new(-x, y, z),
        ];

        foreach (var corner in corners)
            mesh.Positions.Add(corner);

        int[] faces =
        [
            0, 2, 1, 0, 3, 2,
            4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4,
            3, 7, 6, 3, 6, 2,
            0, 4, 7, 0, 7, 3,
            1, 2, 6, 1, 6, 5,
        ];

        foreach (var index in faces)
            mesh.TriangleIndices.Add(index);

        return new GeometryModel3D(mesh, material) { Transform = transform };
    }

    /// <summary>造一个上下半径不同的锥形圆柱（沿 Y 轴）。</summary>
    private static GeometryModel3D MakeTaperedCylinder(double bottomRadius, double topRadius, double height, int segments, Material material, Transform3D transform = null)
    {
        var mesh = new MeshGeometry3D();
        var half = height / 2;

        for (var i = 0; i < segments; i++)
        {
            var angle = 2 * Math.PI * i / segments;
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            mesh.Positions.Add(new Point3D(bottomRadius * cos, -half, bottomRadius * sin));
            mesh.Positions.Add(new Point3D(topRadius * cos, half, topRadius * sin));
        }

        for (var i = 0; i < segments; i++)
        {
            var next = (i + 1) % segments;
            var a = i * 2;
            var b = (i * 2) + 1;
            var c = next * 2;
            var d = (next * 2) + 1;

            mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(d); mesh.TriangleIndices.Add(b);
            mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(c); mesh.TriangleIndices.Add(d);

            var bottom = mesh.Positions.Count;
            mesh.Positions.Add(new Point3D(0, -half, 0));
            mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(bottom); mesh.TriangleIndices.Add(c);

            var top = mesh.Positions.Count;
            mesh.Positions.Add(new Point3D(0, half, 0));
            mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(d); mesh.TriangleIndices.Add(top);
        }

        return new GeometryModel3D(mesh, material) { Transform = transform };
    }

    /// <summary>造一个沿 Y 轴的圆柱。</summary>
    private static GeometryModel3D MakeCylinder(double radius, double height, int segments, Material material, Transform3D transform = null)
    {
        var mesh = new MeshGeometry3D();
        var half = height / 2;

        for (var i = 0; i < segments; i++)
        {
            var angle = 2 * Math.PI * i / segments;
            mesh.Positions.Add(new Point3D(radius * Math.Cos(angle), -half, radius * Math.Sin(angle)));
            mesh.Positions.Add(new Point3D(radius * Math.Cos(angle), half, radius * Math.Sin(angle)));
        }

        for (var i = 0; i < segments; i++)
        {
            var next = (i + 1) % segments;
            var a = i * 2;
            var b = (i * 2) + 1;
            var c = next * 2;
            var d = (next * 2) + 1;

            mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(d); mesh.TriangleIndices.Add(b);
            mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(c); mesh.TriangleIndices.Add(d);

            var bottom = mesh.Positions.Count;
            mesh.Positions.Add(new Point3D(0, -half, 0));
            mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(bottom); mesh.TriangleIndices.Add(c);

            var top = mesh.Positions.Count;
            mesh.Positions.Add(new Point3D(0, half, 0));
            mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(d); mesh.TriangleIndices.Add(top);
        }

        return new GeometryModel3D(mesh, material) { Transform = transform };
    }

    /// <summary>
    /// 本段行程的波形时间轴：和「脚本预设」面板一样用原生的 KeyframesHeatmap，
    /// 显示当前这一段随机行程的形状，进度条随时间前进（因为是实时随机的，不能拖动）。
    /// </summary>
    private UIElement BuildTimeline()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };

        var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };

        var label = new TextBlock { Text = "本段波形：", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        ApplyBodyForeground(label);
        DockPanel.SetDock(label, Dock.Left);
        head.Children.Add(label);

        _timelineTimeText = new TextBlock
        {
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Text = "-- : --"
        };
        ApplyBodyForeground(_timelineTimeText);
        head.Children.Add(_timelineTimeText);

        _timeline = new KeyframesHeatmap
        {
            Height = 36,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            BucketCount = 333,
            CombineHeat = true,
            ShowRange = true,
            EnablePreview = true,
            InvertY = false,
            Duration = double.NaN,
            Position = double.NaN,
            Settings = BuildHeatmapSettings(),
            ToolTip = "这一段的波形（实时随机生成，不能拖动）"
        };

        panel.Children.Add(head);
        panel.Children.Add(_timeline);
        return panel;
    }

    /// <summary>波形控件需要各轴的设置（插值类型）才能画得和脚本区一致。</summary>
    private Dictionary<DeviceAxis, AxisSettings> BuildHeatmapSettings()
    {
        var result = new Dictionary<DeviceAxis, AxisSettings>();

        foreach (var axis in DeviceAxis.All)
        {
            var settings = new AxisSettings(axis);
            try
            {
                settings.InterpolationType = ReadProperty<DeviceAxis, InterpolationType>("Axis::InterpolationType", axis);
            }
            catch (Exception e)
            {
                Log.Trace(e, "读取插值类型失败 [{0}]", axis);
            }

            result[axis] = settings;
        }

        return result;
    }

    /// <summary>给一段行程生成波形预览（各轴按 20ms 采样，用的就是播放时那条公式）。</summary>
    private void BuildSegmentPreview(Dictionary<DeviceAxis, StrokeAxisParameter> stroke, double seconds, double bpm)
    {
        var preview = new Dictionary<DeviceAxis, KeyframeCollection>();
        if (stroke == null || seconds <= 0)
        {
            _segmentPreview = preview;
            return;
        }

        const double step = 0.02;
        var count = Math.Max(2, (int)Math.Ceiling(seconds / step));
        var rate = Math.Max(1, bpm) * 2 * Math.PI / 60;

        foreach (var (axis, parameter) in stroke)
        {
            if (Math.Abs(parameter.From - parameter.To) < 1e-9)
                continue;   // 全程不动的轴不画

            var keyframes = new KeyframeCollection(count + 1);
            var angle = 0.0;
            for (var i = 0; i <= count; i++)
            {
                keyframes.Add(i * step, Math.Clamp(parameter.Evaluate(angle), 0, 1));
                angle += rate * step;
                while (angle >= 2 * Math.PI)
                    angle -= 2 * Math.PI;
            }

            preview[axis] = keyframes;
        }

        _segmentPreview = preview;
    }

    /// <summary>刷新波形与进度（限流到 20Hz，避免刷界面太频繁）。</summary>
    private void RefreshTimeline(bool force = false)
    {
        if (_timeline == null)
            return;

        var now = Stopwatch.GetTimestamp();
        if (!force && now - _lastTimelineUpdateTicks < Stopwatch.Frequency / 20)
            return;

        _lastTimelineUpdateTicks = now;

        double position, total;
        lock (_engineLock)
        {
            total = _segmentTotal;
            position = Math.Max(0, _segmentTotal - _segmentRemaining);
        }

        var preview = _segmentPreview;

        Execute.OnUIThread(() =>
        {
            if (_timeline == null)
                return;

            _timeline.Keyframes = preview;
            _timeline.Duration = total > 0 ? total : double.NaN;
            _timeline.Position = total > 0 ? position : double.NaN;

            if (_timelineTimeText != null)
                _timelineTimeText.Text = total > 0 ? $"{position:0.0} / {total:0.0} 秒" : "-- : --";
        });
    }

    private UIElement BuildStrokePool()
    {
        _strokePoolPanel = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };

        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };
        var label = new TextBlock { Text = "参与随机的行程：", VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
        ApplyBodyForeground(label);
        DockPanel.SetDock(label, Dock.Left);
        head.Children.Add(label);

        var allButton = MakeTextButton("全选", (_, _) => SetAllStrokes(true));
        DockPanel.SetDock(allButton, Dock.Left);
        head.Children.Add(allButton);

        var noneButton = MakeTextButton("全不选", (_, _) => SetAllStrokes(false));
        DockPanel.SetDock(noneButton, Dock.Left);
        head.Children.Add(noneButton);

        var weightHint = new TextBlock
        {
            Text = "（右侧滑块 = 权重，越大越常被选中）",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Opacity = 0.8
        };
        ApplyBodyForeground(weightHint);
        DockPanel.SetDock(weightHint, Dock.Left);
        head.Children.Add(weightHint);

        var panel = new StackPanel();
        panel.Children.Add(head);
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _strokePoolPanel
        });

        return panel;
    }

    private UIElement BuildRanges()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        // 改变 BPM：过渡时改变（站点默认）/ 持续变化
        var modeRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        var modeLabel = new TextBlock { Text = "改变 BPM：", Width = 92, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        ApplyBodyForeground(modeLabel);
        DockPanel.SetDock(modeLabel, Dock.Left);
        modeRow.Children.Add(modeLabel);

        var modeBox = new ComboBox { Width = 140, VerticalAlignment = VerticalAlignment.Center };
        modeBox.Items.Add("过渡时改变");
        modeBox.Items.Add("持续变化");
        modeBox.SelectionChanged += (_, _) =>
        {
            if (_loadingUi)
                return;

            BpmChangeOnTransition = modeBox.SelectedIndex == 0;
            ApplySettingsLive();
            RefreshUi();
        };
        DockPanel.SetDock(modeBox, Dock.Left);
        modeRow.Children.Add(modeBox);
        _refreshControls.Add(() => modeBox.SelectedIndex = BpmChangeOnTransition ? 0 : 1);
        panel.Children.Add(modeRow);

        // BPM 范围（站点 0~200，默认 20~60）
        panel.Children.Add(BuildSliderRangeRow("BPM 范围：", 0, 200, 1, "0",
            () => BpmMinimum, value => BpmMinimum = value,
            () => BpmMaximum, value => BpmMaximum = value));

        // 加速度（bpm/s，仅「持续变化」时可用）
        panel.Children.Add(BuildSliderRangeRow("加速度：", 0, 200, 1, "0",
            () => AccelerationMinimum, value => AccelerationMinimum = value,
            () => AccelerationMaximum, value => AccelerationMaximum = value,
            () => !BpmChangeOnTransition));

        // 模式（每段）持续时间（站点 0~30 秒，默认 5~10）
        panel.Children.Add(BuildSliderRangeRow("每段时长：", 0, 30, 0.1, "0.0",
            () => StrokeSecondsMinimum, value => StrokeSecondsMinimum = value,
            () => StrokeSecondsMaximum, value => StrokeSecondsMaximum = value,
            unit: " 秒"));

        // 过渡持续时间（站点 0~30 秒，默认 2~5）
        panel.Children.Add(BuildSliderRangeRow("过渡时长：", 0, 30, 0.1, "0.0",
            () => TransitionSecondsMinimum, value => TransitionSecondsMinimum = value,
            () => TransitionSecondsMaximum, value => TransitionSecondsMaximum = value,
            unit: " 秒"));

        var switches = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };

        _defaultTwistBox = new CheckBox { Content = "默认扭转", FontSize = 14, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        _defaultTwistBox.Checked += (_, _) => { if (!_loadingUi) { DefaultTwist = true; ApplySettingsLive(); RefreshUi(); } };
        _defaultTwistBox.Unchecked += (_, _) => { if (!_loadingUi) { DefaultTwist = false; ApplySettingsLive(); RefreshUi(); } };
        ApplyBodyForeground(_defaultTwistBox);
        switches.Children.Add(_defaultTwistBox);

        _pausePlayerBox = new CheckBox { Content = "播放时暂停视频", FontSize = 14, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        _pausePlayerBox.Checked += (_, _) => { if (!_loadingUi) { PausePlayerOnPlay = true; MarkSettingsDirty(); } };
        _pausePlayerBox.Unchecked += (_, _) => { if (!_loadingUi) { PausePlayerOnPlay = false; MarkSettingsDirty(); } };
        ApplyBodyForeground(_pausePlayerBox);
        switches.Children.Add(_pausePlayerBox);

        panel.Children.Add(switches);

        // 扭转范围 / 相位 / 偏心度（对应站点的 Twist Range / Phase / Eccentricity）
        panel.Children.Add(BuildSliderRangeRow("扭转范围：", 0, 1, 0.05, "0.00",
            () => TwistRangeFrom, value => TwistRangeFrom = value,
            () => TwistRangeTo, value => TwistRangeTo = value,
            () => DefaultTwist));

        panel.Children.Add(BuildSliderRow("扭曲阶段：", -4, 4, 0.1, "0.00",
            () => TwistPhase, value => TwistPhase = value, () => DefaultTwist));

        panel.Children.Add(BuildSliderRow("扭曲偏心：", -1, 1, 0.05, "0.00",
            () => TwistEccentricity, value => TwistEccentricity = value, () => DefaultTwist));

        return panel;
    }

    /// <summary>
    /// 一条范围条：直接用 MFP 自带的 MahApps RangeSlider 样式
    /// （MaterialDesignCompactRangeSlider，和界面其它滑块完全同色），
    /// 它本身就是"一条轨道 + 两个把手"，并且保证 下限 ≤ 上限。
    /// </summary>
    private UIElement BuildRangeSlider(double minimum, double maximum, double step,
        Func<double> getMinimum, Action<double> setMinimum,
        Func<double> getMaximum, Action<double> setMaximum,
        Func<bool> isEnabled = null, Action onChanged = null)
    {
        var slider = new MahApps.Metro.Controls.RangeSlider
        {
            Minimum = minimum,
            Maximum = maximum,
            MinRange = Math.Max(0, step),
            SmallChange = step,
            LargeChange = Math.Max(step, (maximum - minimum) / 20),
            TickFrequency = step,
            IsSnapToTickEnabled = step > 0,
            LowerValue = getMinimum(),
            UpperValue = getMaximum(),
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 2)
        };
        slider.SetResourceReference(FrameworkElement.StyleProperty, "MaterialDesignCompactRangeSlider");

        slider.LowerValueChanged += (_, _) =>
        {
            if (_loadingUi)
                return;

            setMinimum(slider.LowerValue);
            ApplySettingsLive();
            onChanged?.Invoke();
        };

        slider.UpperValueChanged += (_, _) =>
        {
            if (_loadingUi)
                return;

            setMaximum(slider.UpperValue);
            ApplySettingsLive();
            onChanged?.Invoke();
        };

        _refreshControls.Add(() =>
        {
            _loadingUi = true;
            try
            {
                slider.LowerValue = getMinimum();
                slider.UpperValue = getMaximum();
            }
            finally
            {
                _loadingUi = false;
            }

            slider.IsEnabled = isEnabled?.Invoke() ?? true;
        });

        return slider;
    }

    /// <summary>
    /// 一行「范围滑块」：标签 + 上方当前值 + 一条轨道两个把手。
    /// 刷新界面时通过 _refreshControls 回填，不需要为每个控件各留一个字段。
    /// </summary>
    private UIElement BuildSliderRangeRow(string label, double minimum, double maximum, double step, string format,
        Func<double> getMinimum, Action<double> setMinimum,
        Func<double> getMaximum, Action<double> setMaximum,
        Func<bool> isEnabled = null, string unit = "")
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };

        var head = new DockPanel { LastChildFill = true };
        var labelText = new TextBlock { Text = label, Width = 92, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        ApplyBodyForeground(labelText);
        DockPanel.SetDock(labelText, Dock.Left);
        head.Children.Add(labelText);

        var valueText = new TextBlock
        {
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.9
        };
        ApplyBodyForeground(valueText);
        head.Children.Add(valueText);
        panel.Children.Add(head);

        void UpdateValueText()
            => valueText.Text = $"{getMinimum().ToString(format)}{unit} ~ {getMaximum().ToString(format)}{unit}";

        UpdateValueText();

        panel.Children.Add(BuildRangeSlider(minimum, maximum, step, getMinimum, setMinimum, getMaximum, setMaximum, isEnabled, UpdateValueText));

        _refreshControls.Add(UpdateValueText);

        return panel;
    }

    /// <summary>一行「单值滑块」。</summary>
    private UIElement BuildSliderRow(string label, double minimum, double maximum, double step, string format,
        Func<double> getValue, Action<double> setValue, Func<bool> isEnabled = null, string unit = "")
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };

        var head = new DockPanel { LastChildFill = true };
        var labelText = new TextBlock { Text = label, Width = 92, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        ApplyBodyForeground(labelText);
        DockPanel.SetDock(labelText, Dock.Left);
        head.Children.Add(labelText);

        var valueText = new TextBlock
        {
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.9
        };
        ApplyBodyForeground(valueText);
        head.Children.Add(valueText);
        panel.Children.Add(head);

        var slider = MakeSlider(minimum, maximum, step);
        slider.ValueChanged += (_, e) =>
        {
            if (_loadingUi)
                return;

            setValue(e.NewValue);
            valueText.Text = $"{getValue().ToString(format)}{unit}";
            ApplySettingsLive();
        };
        panel.Children.Add(slider);

        _refreshControls.Add(() =>
        {
            slider.Value = getValue();

            var enabled = isEnabled?.Invoke() ?? true;
            slider.IsEnabled = enabled;
            valueText.Opacity = enabled ? 0.9 : 0.4;

            valueText.Text = $"{getValue().ToString(format)}{unit}";
        });

        return panel;
    }

    private static Slider MakeSlider(double minimum, double maximum, double step)
        => new()
        {
            Minimum = minimum,
            Maximum = maximum,
            SmallChange = step,
            LargeChange = Math.Max(step, (maximum - minimum) / 20),
            TickFrequency = step,
            IsSnapToTickEnabled = step > 0,
            VerticalAlignment = VerticalAlignment.Center
        };

    private void SetAllStrokes(bool enabled)
    {
        AllStrokes = enabled;
        EnabledStrokes = enabled ? [.. StrokeLibrary.Keys] : [];
        MarkSettingsDirty();
        RefreshUi();
    }

    private void RefreshUi()
    {
        Execute.OnUIThread(() =>
        {
            if (_statusText == null)
                return;

            _loadingUi = true;
            try
            {
                if (_strokePoolPanel != null)
                {
                    _strokePoolPanel.Children.Clear();
                    _strokeButtons.Clear();
                    var enabled = EnabledStrokes ?? [];
                    foreach (var name in StrokeLibrary.Keys.OrderBy(x => x, StringComparer.Ordinal))
                    {
                        var captured = name;
                        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 3) };

                        // 右：权重滑块（1~5，越大越常被选中）
                        var weight = new Slider
                        {
                            Minimum = 1,
                            Maximum = 5,
                            TickFrequency = 1,
                            IsSnapToTickEnabled = true,
                            Width = 96,
                            Margin = new Thickness(8, 0, 0, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Value = WeightOf(name),
                            ToolTip = $"{DisplayNameOf(name)}（{name}）的权重：{WeightOf(name)}/5"
                        };
                        weight.ValueChanged += (_, e) =>
                        {
                            if (_loadingUi)
                                return;

                            StrokeWeights ??= [];
                            StrokeWeights[captured] = (int)Math.Round(e.NewValue);
                            weight.ToolTip = $"{DisplayNameOf(captured)}（{captured}）的权重：{StrokeWeights[captured]}/5";
                            MarkSettingsDirty();
                        };
                        DockPanel.SetDock(weight, Dock.Right);
                        row.Children.Add(weight);

                        // 左：勾选框（只决定"是否参与随机"）
                        var box = new CheckBox
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            IsChecked = AllStrokes || enabled.Contains(name),
                            ToolTip = "勾上 = 参与随机挑选"
                        };
                        box.Checked += (_, _) =>
                        {
                            if (_loadingUi)
                                return;

                            EnabledStrokes ??= [];
                            if (AllStrokes)
                            {
                                // 从"全部参与"切成"逐个挑选"：先铺开全部，再确保这一条在里面
                                EnabledStrokes = [.. StrokeLibrary.Keys];
                                AllStrokes = false;
                            }

                            if (!EnabledStrokes.Contains(captured))
                                EnabledStrokes.Add(captured);

                            MarkSettingsDirty();
                        };
                        box.Unchecked += (_, _) =>
                        {
                            if (_loadingUi)
                                return;

                            if (AllStrokes)
                            {
                                // 从"全部参与"里排掉这一条
                                EnabledStrokes = [.. StrokeLibrary.Keys.Where(x => x != captured)];
                                AllStrokes = false;
                            }
                            else
                            {
                                EnabledStrokes?.Remove(captured);
                            }

                            MarkSettingsDirty();
                        };
                        ApplyBodyForeground(box);
                        DockPanel.SetDock(box, Dock.Left);
                        row.Children.Add(box);

                        // 名字本身可以点：点一下立刻播放这一条（像网页里那样）
                        var nameButton = new Button
                        {
                            Content = MakeButtonText(DisplayNameOf(name)),
                            ToolTip = $"{name}\n点一下立即播放这一条（会一直循环，直到你主动打断）",
                            FontSize = 13,
                            HorizontalAlignment = HorizontalAlignment.Left,
                            HorizontalContentAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(6, 0, 0, 0),
                            Padding = new Thickness(8, 1, 8, 1),
                            BorderThickness = new Thickness(0),
                            Background = Brushes.Transparent
                        };
                        nameButton.Click += (_, _) => Enqueue(() => PlaySingleStroke(captured));
                        DockPanel.SetDock(nameButton, Dock.Left);
                        row.Children.Add(nameButton);
                        _strokeButtons[captured] = nameButton;

                        _strokePoolPanel.Children.Add(row);
                    }
                }

                if (_pausePlayerBox != null) _pausePlayerBox.IsChecked = PausePlayerOnPlay;
                if (_defaultTwistBox != null) _defaultTwistBox.IsChecked = DefaultTwist;

                // 所有滑块/下拉框通过注册的回填动作刷新（先更新开关，再刷新依赖它们的控件）
                foreach (var refresh in _refreshControls)
                {
                    try
                    {
                        refresh();
                    }
                    catch (Exception e)
                    {
                        Log.Trace(e, "刷新控件失败");
                    }
                }

                if (_playButton?.Content is PackIcon icon)
                    icon.Kind = IsPlaying ? PackIconKind.Stop : PackIconKind.Play;

                if (_playButton != null)
                {
                    if (_playButton.Content is PackIcon playIcon)
                        playIcon.Foreground = IsPlaying ? Brushes.OrangeRed : Brushes.Green;

                    _playButton.ToolTip = IsPlaying ? "停止随机播放" : "开始随机播放";
                }

                UpdateStatusTextCore();
            }
            finally
            {
                _loadingUi = false;
            }
        });
    }

    private void UpdateStatusText()
    {
        if (_statusText == null)
            return;

        Execute.OnUIThread(UpdateStatusTextCore);
        UpdateStrokeHighlight();
    }

    /// <summary>把"正在播放"的那条行程在列表里标出来（▶ 前缀）。</summary>
    private void UpdateStrokeHighlight()
    {
        var current = IsPlaying ? CurrentStrokeName : null;
        if (current == _highlightedStroke)
            return;

        _highlightedStroke = current;

        Execute.OnUIThread(() =>
        {
            foreach (var (name, button) in _strokeButtons)
            {
                if (button.Content is TextBlock text)
                    text.Text = (name == current ? "▶ " : string.Empty) + DisplayNameOf(name);
            }
        });
    }

    private void UpdateStatusTextCore()
    {
        if (_statusText == null)
            return;

        if (!IsPlaying)
        {
            _statusText.Text = $"空闲（行程池 {Pool().Count} / {StrokeLibrary.Count} 条）";
            return;
        }

        var transition = _transitionRemaining > 0 ? $"　过渡中 {_transitionRemaining:0.0}s" : string.Empty;

        if (!string.IsNullOrEmpty(_lockedStroke))
        {
            _statusText.Text = $"🔁 单条循环：{DisplayNameOf(_lockedStroke)}　bpm {CurrentBpm:0}　本段剩余 {SegmentRemaining:0.0}s{transition}";
            return;
        }

        _statusText.Text = $"▶ {DisplayNameOf(CurrentStrokeName)}　bpm {CurrentBpm:0}　本段剩余 {SegmentRemaining:0.0}s{transition}";
    }

    private void SetHint(string message, double autoHideSeconds = 4)
    {
        Execute.OnUIThread(() =>
        {
            if (_hintText == null)
                return;

            _hintText.Text = message;
            _hintText.Visibility = Visibility.Visible;
            _hintExpiresTicks = autoHideSeconds > 0
                ? Environment.TickCount64 + (long)(autoHideSeconds * 1000)
                : 0;
        });
    }

    private void HideExpiredHint()
    {
        var expires = Interlocked.Read(ref _hintExpiresTicks);
        if (expires == 0 || Environment.TickCount64 < expires)
            return;

        Interlocked.Exchange(ref _hintExpiresTicks, 0);
        Execute.OnUIThread(() =>
        {
            if (_hintText != null)
                _hintText.Visibility = Visibility.Collapsed;
        });
    }

    private void ApplyBodyForeground(FrameworkElement element)
    {
        try
        {
            element.SetResourceReference(Control.ForegroundProperty, "MaterialDesignBody");
        }
        catch (Exception e)
        {
            Log.Trace(e, "套用主题前景色失败");
        }
    }

    private static Button MakeIconButton(PackIconKind kind, string tooltip, Brush foreground, RoutedEventHandler onClick)
    {
        var icon = new PackIcon
        {
            Kind = kind,
            Width = 18,
            Height = 18,
            Foreground = foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var button = new Button
        {
            Content = icon,
            Width = 36,
            Height = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tooltip
        };

        button.Click += onClick;
        return button;
    }

    /// <summary>按钮里的文字统一用常规字重（MaterialDesign 的按钮样式默认半粗体，得设在内容上才有效）。</summary>
    private static TextBlock MakeButtonText(string text)
        => new() { Text = text, FontWeight = FontWeights.Normal };

    private static Button MakeTextButton(string content, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = MakeButtonText(content),
            Height = 26,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        button.Click += onClick;
        return button;
    }

    #endregion
}
