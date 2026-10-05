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
using System.Windows.Media;
using MahApps.Metro.Controls;
using MaterialDesignThemes.Wpf;
using MultiFunPlayer.Common;
using MultiFunPlayer.Script;
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
        "AyvaRandom::Save",
    ];

    #region 设置（持久化到 AyvaRandom.config.json）

    /// <summary>参与随机的行程名；为空表示全部参与。</summary>
    [JsonProperty] public List<string> EnabledStrokes { get; set; } = [];

    [JsonProperty] public double BpmMinimum { get; set; } = 90;
    [JsonProperty] public double BpmMaximum { get; set; } = 150;

    /// <summary>每段行程持续时间的范围（秒）。</summary>
    [JsonProperty] public double StrokeSecondsMinimum { get; set; } = 8;
    [JsonProperty] public double StrokeSecondsMaximum { get; set; } = 20;

    /// <summary>段与段之间的过渡时长（秒）。</summary>
    [JsonProperty] public double TransitionSeconds { get; set; } = 2.0;

    /// <summary>true = bpm 在段内连续变化；false = 只在换段时变。</summary>
    [JsonProperty] public bool ContinuousBpm { get; set; } = true;

    /// <summary>行程本身没有 R0 时，是否补一个慢速扭转。</summary>
    [JsonProperty] public bool DefaultTwist { get; set; } = false;

    [JsonProperty] public double DefaultTwistBpm { get; set; } = 8;

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
    private double _segmentRemaining;
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

        // 可选：暂停视频播放器（和预设插件一样，避免两边同时抢设备）
        _pausedPlayer = false;
        if (PausePlayerOnPlay)
        {
            try
            {
                if (ReadProperty<bool>("Media::PlayPause"))
                {
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
            _targetBpm = NextRandomBpm();
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

    private void NextStroke()
    {
        if (!IsPlaying)
            return;

        lock (_engineLock)
            StartNextSegment(initial: false);
    }

    /// <summary>可选的行程池（勾选的行程；一个都没勾就全部参与）。</summary>
    private List<string> Pool()
    {
        var library = StrokeLibrary;
        if (EnabledStrokes is { Count: > 0 })
        {
            var pool = EnabledStrokes.Where(library.ContainsKey).ToList();
            if (pool.Count > 0)
                return pool;
        }

        return [.. library.Keys];
    }

    private double NextRandomBpm()
    {
        var min = Math.Min(BpmMinimum, BpmMaximum);
        var max = Math.Max(BpmMinimum, BpmMaximum);
        return min + (Random.Shared.NextDouble() * (max - min));
    }

    private double NextRandomSegmentSeconds()
    {
        var min = Math.Min(StrokeSecondsMinimum, StrokeSecondsMaximum);
        var max = Math.Max(StrokeSecondsMinimum, StrokeSecondsMaximum);
        return Math.Max(1.0, min + (Random.Shared.NextDouble() * (max - min)));
    }

    /// <summary>开始下一段：随机挑一个行程，记下过渡起点（当前值），随机时长与 bpm。</summary>
    private void StartNextSegment(bool initial)
    {
        var pool = Pool();
        if (pool.Count == 0)
        {
            _playing = false;
            return;
        }

        var name = pool[Random.Shared.Next(pool.Count)];
        if (!StrokeLibrary.TryGetValue(name, out var axes))
            return;

        // 过渡起点 = 当前的实时值（没有就取各行程的中心）
        _transitionFrom = _values.Count > 0 ? new Dictionary<DeviceAxis, double>(_values) : null;
        _transitionTotal = Math.Max(0, TransitionSeconds);
        _transitionRemaining = _transitionTotal;

        _stroke = axes;
        _strokeName = name;
        if (initial)
            _angle = 0;
        _bpm = initial || !ContinuousBpm ? NextRandomBpm() : _bpm;
        _targetBpm = NextRandomBpm();
        _segmentRemaining = NextRandomSegmentSeconds();

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

            // bpm：连续变化慢慢靠向目标；否则整段保持不变
            if (ContinuousBpm)
                _bpm += (_targetBpm - _bpm) * Math.Min(1, delta / 0.6);

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

            // 可选：补一个慢速扭转（行程本身没有 R0 时）
            if (DefaultTwist)
            {
                var twistAxis = DeviceAxis.Parse("R0");
                if (twistAxis != null && !next.ContainsKey(twistAxis) && !_stroke.ContainsKey(twistAxis))
                {
                    _twistAngle += Math.Max(0.1, DefaultTwistBpm) * 2 * Math.PI / 60 * delta;
                    next[twistAxis] = 0.5 - (0.5 * Math.Cos(_twistAngle));
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
    private Button _playButton;
    private WrapPanel _strokePoolPanel;
    private NumericUpDown _bpmMinBox;
    private NumericUpDown _bpmMaxBox;
    private NumericUpDown _segmentMinBox;
    private NumericUpDown _segmentMaxBox;
    private NumericUpDown _transitionBox;
    private CheckBox _continuousBpmBox;
    private CheckBox _pausePlayerBox;
    private CheckBox _defaultTwistBox;
    private bool _loadingUi;
    private long _hintExpiresTicks;

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
        RefreshUi();
        return root;
    }

    private UIElement BuildTransport()
    {
        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };

        _playButton = MakeIconButton(PackIconKind.Play, "开始随机播放", Brushes.Green, (_, _) => Enqueue(TogglePlay));
        DockPanel.SetDock(_playButton, Dock.Left);
        row.Children.Add(_playButton);

        var nextButton = MakeTextButton("下一段", (_, _) => Enqueue(NextStroke));
        DockPanel.SetDock(nextButton, Dock.Left);
        row.Children.Add(nextButton);

        var restartButton = MakeTextButton("重新开始", (_, _) => Enqueue(() => { StopCore(false); StartCore(); }));
        DockPanel.SetDock(restartButton, Dock.Left);
        row.Children.Add(restartButton);

        row.Children.Add(_statusText);
        return row;
    }

    private UIElement BuildStrokePool()
    {
        _strokePoolPanel = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };

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

        var panel = new StackPanel();
        panel.Children.Add(head);
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 150,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _strokePoolPanel
        });

        return panel;
    }

    private UIElement BuildRanges()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        panel.Children.Add(BuildRangeRow("bpm 范围：", out _bpmMinBox, out _bpmMaxBox, 30, 300, stringFormat: "{0:F0}", suffix: null));
        panel.Children.Add(BuildRangeRow("每段时长：", out _segmentMinBox, out _segmentMaxBox, 2, 600, stringFormat: "{0:F0}", suffix: " 秒"));
        panel.Children.Add(BuildRangeRow("过渡时长：", out _transitionBox, out _, 0, 30, stringFormat: "{0:F1}", suffix: " 秒", single: true));

        var switches = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };

        _continuousBpmBox = new CheckBox { Content = "bpm 连续变化", FontSize = 14, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        _continuousBpmBox.Checked += (_, _) => { if (!_loadingUi) { ContinuousBpm = true; MarkSettingsDirty(); } };
        _continuousBpmBox.Unchecked += (_, _) => { if (!_loadingUi) { ContinuousBpm = false; MarkSettingsDirty(); } };
        ApplyBodyForeground(_continuousBpmBox);
        switches.Children.Add(_continuousBpmBox);

        _pausePlayerBox = new CheckBox { Content = "播放时暂停视频", FontSize = 14, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        _pausePlayerBox.Checked += (_, _) => { if (!_loadingUi) { PausePlayerOnPlay = true; MarkSettingsDirty(); } };
        _pausePlayerBox.Unchecked += (_, _) => { if (!_loadingUi) { PausePlayerOnPlay = false; MarkSettingsDirty(); } };
        ApplyBodyForeground(_pausePlayerBox);
        switches.Children.Add(_pausePlayerBox);

        _defaultTwistBox = new CheckBox { Content = "补默认扭转（慢速 R0）", FontSize = 14, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        _defaultTwistBox.Checked += (_, _) => { if (!_loadingUi) { DefaultTwist = true; MarkSettingsDirty(); } };
        _defaultTwistBox.Unchecked += (_, _) => { if (!_loadingUi) { DefaultTwist = false; MarkSettingsDirty(); } };
        ApplyBodyForeground(_defaultTwistBox);
        switches.Children.Add(_defaultTwistBox);

        panel.Children.Add(switches);
        return panel;
    }

    private UIElement BuildRangeRow(string label, out NumericUpDown first, out NumericUpDown second, double minimum, double maximum, string stringFormat, string suffix, bool single = false)
    {
        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };

        var text = new TextBlock { Text = label, Width = 84, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
        ApplyBodyForeground(text);
        DockPanel.SetDock(text, Dock.Left);
        row.Children.Add(text);

        first = new NumericUpDown
        {
            Width = 84,
            Minimum = minimum,
            Maximum = maximum,
            Interval = single ? 0.5 : 5,
            StringFormat = stringFormat,
            TrackMouseWheelWhenMouseOver = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        first.ValueChanged += (_, _) => { if (!_loadingUi) OnRangeChanged(); };
        DockPanel.SetDock(first, Dock.Left);
        row.Children.Add(first);

        second = null;
        if (!single)
        {
            var tilde = new TextBlock { Text = "~", Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
            ApplyBodyForeground(tilde);
            DockPanel.SetDock(tilde, Dock.Left);
            row.Children.Add(tilde);

            second = new NumericUpDown
            {
                Width = 84,
                Minimum = minimum,
                Maximum = maximum,
                Interval = 5,
                StringFormat = stringFormat,
                TrackMouseWheelWhenMouseOver = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            second.ValueChanged += (_, _) => { if (!_loadingUi) OnRangeChanged(); };
            DockPanel.SetDock(second, Dock.Left);
            row.Children.Add(second);
        }

        if (!string.IsNullOrEmpty(suffix))
        {
            var suffixText = new TextBlock { Text = suffix, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
            ApplyBodyForeground(suffixText);
            DockPanel.SetDock(suffixText, Dock.Left);
            row.Children.Add(suffixText);
        }

        return row;
    }

    private void OnRangeChanged()
    {
        BpmMinimum = _bpmMinBox?.Value ?? BpmMinimum;
        BpmMaximum = _bpmMaxBox?.Value ?? BpmMaximum;
        StrokeSecondsMinimum = _segmentMinBox?.Value ?? StrokeSecondsMinimum;
        StrokeSecondsMaximum = _segmentMaxBox?.Value ?? StrokeSecondsMaximum;
        if (_transitionBox?.Value is { } transition)
            TransitionSeconds = transition;

        MarkSettingsDirty();
    }

    private void SetAllStrokes(bool enabled)
    {
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
                    var enabled = EnabledStrokes ?? [];
                    foreach (var name in StrokeLibrary.Keys.OrderBy(x => x, StringComparer.Ordinal))
                    {
                        var box = new CheckBox
                        {
                            Content = name,
                            FontSize = 13,
                            Margin = new Thickness(0, 0, 12, 4),
                            VerticalAlignment = VerticalAlignment.Center,
                            IsChecked = enabled.Count == 0 || enabled.Contains(name)
                        };
                        var captured = name;
                        box.Checked += (_, _) =>
                        {
                            if (_loadingUi)
                                return;

                            EnabledStrokes ??= [];
                            if (!EnabledStrokes.Contains(captured))
                                EnabledStrokes.Add(captured);

                            MarkSettingsDirty();
                        };
                        box.Unchecked += (_, _) =>
                        {
                            if (_loadingUi)
                                return;

                            EnabledStrokes ??= [];
                            if (EnabledStrokes.Count == 0)
                                return;   // 从"全部"取消某一个：先把全部铺开再移除

                            EnabledStrokes.Remove(captured);
                            MarkSettingsDirty();
                        };
                        ApplyBodyForeground(box);
                        _strokePoolPanel.Children.Add(box);
                    }
                }

                if (_bpmMinBox != null) _bpmMinBox.Value = BpmMinimum;
                if (_bpmMaxBox != null) _bpmMaxBox.Value = BpmMaximum;
                if (_segmentMinBox != null) _segmentMinBox.Value = StrokeSecondsMinimum;
                if (_segmentMaxBox != null) _segmentMaxBox.Value = StrokeSecondsMaximum;
                if (_transitionBox != null) _transitionBox.Value = TransitionSeconds;
                if (_continuousBpmBox != null) _continuousBpmBox.IsChecked = ContinuousBpm;
                if (_pausePlayerBox != null) _pausePlayerBox.IsChecked = PausePlayerOnPlay;
                if (_defaultTwistBox != null) _defaultTwistBox.IsChecked = DefaultTwist;

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
        _statusText.Text = $"▶ {CurrentStrokeName}　bpm {CurrentBpm:0}　本段剩余 {SegmentRemaining:0.0}s{transition}";
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

    private static Button MakeTextButton(string content, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = content,
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
