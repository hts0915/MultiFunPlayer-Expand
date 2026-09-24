// ============================================================================
//  ScriptPreset —— MultiFunPlayer 插件：一键播放预设 funscript（多轴）
// ----------------------------------------------------------------------------
//  依赖：需要带「脚本覆盖内核」的 MultiFunPlayer（IScriptOverrideController）。
//        该内核让一组脚本拥有自己独立的时间轴，完全不动视频的位置、时长和已加载脚本，
//        所以 Script 面板里显示的一直是视频那一套，两张时间轴互不干扰。
//
//  功能：
//    * 把「一组 funscript 文件（可对应不同轴）」保存为一个命名预设
//    * 一键播放预设：设备改由预设脚本驱动，视频的脚本/时间轴完全不受影响
//    * 预设面板里有独立的波形时间轴和播放/暂停按钮
//    * 记住每个预设播放到哪里，下次（包括重启后）从上次的位置接着播
//    * 与视频互斥：播放预设时暂停视频；视频开始播放时自动停下预设
//
//  安装：把本文件放到 MultiFunPlayer.exe 同目录的 Plugins\ 下
//  配置：ScriptPreset.config.json，与本文件同目录（自动读写）。
//
//  快捷键动作名：
//        ScriptPreset::Preset::Play::ByName     参数：预设名
//        ScriptPreset::Preset::Play::ByIndex    参数：预设序号（从 0 开始）
//        ScriptPreset::Preset::Toggle::ByName   参数：预设名（正在播这个就停，否则播）
//        ScriptPreset::Stop
//        ScriptPreset::Save                     立即保存配置
// ============================================================================

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using MaterialDesignThemes.Wpf;
using MultiFunPlayer.MediaSource.MediaResource;
using MultiFunPlayer.UI.Controls;
using MultiFunPlayer.UI.Controls.ViewModels;
using StyletIoC;

[DisplayName("脚本预设")]
[JsonObject(MemberSerialization = MemberSerialization.OptIn)]
public class ScriptPreset : PluginBase
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>工作线程的空转间隔。</summary>
    private const int MonitorIntervalMilliseconds = 50;

    private const int PauseAttempts = 3;

    /// <summary>距离脚本末尾小于这个秒数时，续播位置视为「已播完」并从 0 开始。</summary>
    private const double ResumeEndThreshold = 1.0;

    /// <summary>
    /// 本插件注册的全部动作名。
    /// MultiFunPlayer 的 PluginBase.InternalDispose 会在 foreach 遍历 _registeredActions 的同时
    /// 调 UnregisterAction 从同一个 list 里删元素，导致每次退出都抛 InvalidOperationException。
    /// 我们在 CancellationToken 的取消回调里先把它们注销掉（Cancel() 早于那一行执行），从而绕开这个 bug。
    /// </summary>
    private static readonly string[] ActionNames =
    [
        "ScriptPreset::Preset::Play::ByName",
        "ScriptPreset::Preset::Play::ByIndex",
        "ScriptPreset::Preset::Toggle::ByName",
        "ScriptPreset::Stop",
        "ScriptPreset::Save",
    ];

    #region 可持久化配置

    /// <summary>播放预设时是否暂停视频（与视频互斥）。</summary>
    [JsonProperty] public bool PausePlayerOnPlay { get; set; } = true;

    /// <summary>
    /// 是否自动修复「首次连接播放器拿不到视频路径」的问题。
    /// 这是 MultiFunPlayer 的 PotPlayer 媒体源 bug（向播放器要文件名时没有超时，读循环会卡死），
    /// 断开重连一次即可恢复，插件在检测到连接后迟迟拿不到路径时自动做这件事。
    /// </summary>
    [JsonProperty] public bool AutoReconnectPlayer { get; set; } = true;

    /// <summary>所有预设。</summary>
    [JsonProperty] public List<PresetDefinition> Presets { get; set; } = [];

    #endregion

    #region 注入

    /// <summary>宿主提供的脚本覆盖内核。属性必须是 NonPublic，宿主才会注入。</summary>
    [Inject] internal IScriptOverrideController OverrideController { get; set; }

    #endregion

    #region 运行时状态

    private readonly BlockingCollection<Action> _commands = new();
    private readonly ObservableCollection<string> _presetNames = [];

    private PluginViewBase _view;
    private StackPanel _presetListPanel;
    private TextBlock _statusText;
    private TextBlock _hintText;

    /// <summary>当前行显示并操作的预设。在展开列表里选中后自动折叠；正在播放的预设也会同步到这里。</summary>
    private volatile PresetDefinition _selectedPreset;

    /// <summary>预设选择列表（Popup）。展开时只浮在界面上，面板本身始终只占一行。</summary>
    private Popup _presetListPopup;

    // 预设自己的传输区
    private KeyframesHeatmap _transportTimeline;
    private TextBlock _transportTimeText;
    private long _lastTransportUpdateTicks;
    private double _timelineDuration;

    private volatile PresetDefinition _activePreset;
    private volatile int _activePresetIndex = -1;
    private double _displayPosition;

    // 视频侧状态（只用来决定停止预设后要不要恢复视频播放）
    private bool _savedIsPlaying;
    private bool _pausedPlayer;

    private volatile bool _settingsDirty;
    private long _lastSaveTicks;
    private long _hintHideTicks;
    private string _lastPresetFolder;

    // 媒体源连接跟踪（用于自动修复首次连接拿不到路径的问题）
    private string _trackedSourceName;
    private long _trackedSourceConnectedTicks;
    private bool _trackedSourceReconnected;
    private long _lastConnectionCheckTicks;

    #endregion

    #region 生命周期

    protected override void OnInitialize()
    {
        Presets ??= [];

        RegisterAction<string>("ScriptPreset::Preset::Play::ByName",
            s => s.WithLabel("预设").WithItemsSource(_presetNames),
            name => Enqueue(() => PlayByName(name)));

        RegisterAction<int>("ScriptPreset::Preset::Play::ByIndex",
            s => s.WithLabel("序号").AsNumericUpDown(minimum: 0),
            index => Enqueue(() => PlayByIndex(index)));

        RegisterAction<string>("ScriptPreset::Preset::Toggle::ByName",
            s => s.WithLabel("预设").WithItemsSource(_presetNames),
            name => Enqueue(() => ToggleByName(name)));

        RegisterAction("ScriptPreset::Stop", () => Enqueue(() => StopCore(true)));
        RegisterAction("ScriptPreset::Save", SaveSettingsNow);

        // 见 ActionNames 的注释：这里是绕开宿主退出崩溃的关键
        CancellationToken.Register(UnregisterAllActions);

        StartThread(Worker);

        RefreshUi();

        if (OverrideController == null)
        {
            Log.Error("未注入 IScriptOverrideController —— 这个 MultiFunPlayer 版本没有脚本覆盖内核");
            SetHint("⚠ 当前 MultiFunPlayer 没有「脚本覆盖内核」，预设无法播放。请使用带该内核的版本。");
        }
        else
        {
            Log.Info("ScriptPreset 已加载");
        }
    }

    protected override void OnDispose()
    {
        try
        {
            if (_activePreset != null)
                StopCore(true);
        }
        catch (Exception e)
        {
            Log.Warn(e, "ScriptPreset 退出还原失败");
        }

        UnregisterAllActions();

        try { _commands.CompleteAdding(); } catch { }
    }

    /// <summary>把自己注册的动作全部注销。可重复调用。</summary>
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

        // 配置文件在 OnInitialize 之后才加载，所以载入完必须刷新一次界面
        if (action == SettingsAction.Loading)
        {
            foreach (var preset in Presets ?? [])
                preset.Entries ??= [];

            RefreshUi();
        }
    }

    #endregion

    #region 与视频播放器的互斥

    // 预设和视频各自有独立的时间轴，两者互斥：
    //   * 播放预设时 → 暂停视频（见 PlayCore）
    //   * 视频开始播放时 → 停下预设，设备交还给视频脚本（这里）

    protected override void HandleMessage(MediaPlayPauseMessage message)
    {
        if (_activePreset == null || !message.ShouldBePlaying)
            return;

        Log.Info("检测到视频开始播放，停下预设（预设进度已记住）");
        Enqueue(() =>
        {
            _pausedPlayer = false;
            _savedIsPlaying = true;
            StopCore(false);
        });
    }

    #endregion

    #region 后台工作线程

    private void Worker(CancellationToken token)
    {
        try
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
                        MonitorPreset();
                        TrackMediaSourceConnection();
                        FlushSettingsIfDirty();
                        HideExpiredHint();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    Log.Error(e, "ScriptPreset 执行出错");
                    SetHint($"出错：{e.Message}");
                }
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "ScriptPreset 工作线程退出");
        }
    }

    private void Enqueue(Action action)
    {
        try
        {
            if (!_commands.IsAddingCompleted)
                _commands.Add(action);
        }
        catch (Exception e)
        {
            Log.Warn(e, "ScriptPreset 命令入队失败");
        }
    }

    /// <summary>从内核读取预设时间轴进度、记录续播位置、处理循环/播完。</summary>
    private void MonitorPreset()
    {
        var preset = _activePreset;
        var controller = OverrideController;
        if (preset == null || controller == null)
            return;

        var position = controller.OverridePosition;
        var duration = controller.OverrideDuration;

        _displayPosition = position;
        _timelineDuration = duration;

        // 记录续播位置（节流保存由 FlushSettingsIfDirty 负责）
        preset.ResumePosition = position;
        MarkSettingsDirty();

        UpdateTransportPosition();

        if (duration <= 0 || position < duration - 0.001)
            return;

        if (preset.Loop)
        {
            Log.Debug("预设「{0}」循环回到开头", preset.Name);
            controller.SeekOverride(0);
            _displayPosition = 0;
        }
        else
        {
            Log.Info("预设「{0}」播放结束，自动停止", preset.Name);
            preset.ResumePosition = 0;
            StopCore(true);
        }
    }

    /// <summary>
    /// 跟踪媒体源连接。若连接后迟迟拿不到视频路径，说明踩到了宿主的「首次连接读循环卡死」bug，
    /// 自动断开重连一次即可恢复（这也是手动断开重连能修好的原因）。
    /// </summary>
    private void TrackMediaSourceConnection()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - _lastConnectionCheckTicks < Stopwatch.Frequency / 2)
            return;

        _lastConnectionCheckTicks = now;

        var name = FindConnectedMediaSource();
        if (name == null)
        {
            _trackedSourceName = null;
            _trackedSourceConnectedTicks = 0;
            _trackedSourceReconnected = false;
            return;
        }

        if (name != _trackedSourceName)
        {
            _trackedSourceName = name;
            _trackedSourceConnectedTicks = now + (long)(Stopwatch.Frequency * 3);
            _trackedSourceReconnected = false;
            return;
        }

        if (_trackedSourceReconnected || _trackedSourceConnectedTicks == 0 || now < _trackedSourceConnectedTicks)
            return;

        _trackedSourceReconnected = true;

        if (!AutoReconnectPlayer || _activePreset != null)
            return;

        if (ReadProp<MediaResourceInfo>("Media::Resource") != null)
            return;

        Log.Info("「{0}」已连接 3 秒但还没拿到视频路径，自动重连一次（宿主首次连接会卡住读循环）", name);
        ReconnectMediaSource(name);
    }

    private string FindConnectedMediaSource()
    {
        try
        {
            foreach (var propertyName in AvailableProperties)
            {
                if (!propertyName.EndsWith("::Connection::Status", StringComparison.Ordinal))
                    continue;

                var sourceName = propertyName[..^"::Connection::Status".Length];
                try
                {
                    if (ReadProperty<ConnectionStatus>(propertyName) == ConnectionStatus.Connected)
                        return sourceName;
                }
                catch (Exception e)
                {
                    Log.Trace(e, "读取媒体源状态失败 [{0}]", propertyName);
                }
            }
        }
        catch (Exception e)
        {
            Log.Trace(e, "枚举可用属性失败");
        }

        return null;
    }

    private void ReconnectMediaSource(string name)
    {
        try
        {
            InvokeAction($"{name}::Connection::Disconnect");
            WaitUntil(() => ReadProp<ConnectionStatus>($"{name}::Connection::Status") != ConnectionStatus.Connected, 3000);
            Thread.Sleep(200);

            InvokeAction($"{name}::Connection::Connect");

            SetHint($"已自动重连「{name}」以恢复脚本自动匹配", autoHideSeconds: 6);
            Log.Info("已自动重连「{0}」", name);
        }
        catch (Exception e)
        {
            Log.Warn(e, "自动重连「{0}」失败", name);
        }
        finally
        {
            _trackedSourceConnectedTicks = 0;
        }
    }

    #endregion

    #region 核心：播放 / 停止 / 暂停

    private void PlayByName(string name)
    {
        var index = FindPresetIndex(name);
        if (index < 0)
        {
            SetHint($"找不到预设「{name}」。");
            return;
        }

        PlayByIndex(index);
    }

    private void PlayByIndex(int index)
    {
        var presets = Presets;
        if (presets == null || index < 0 || index >= presets.Count)
        {
            SetHint($"预设序号 {index} 不存在。");
            return;
        }

        PlayCore(presets[index], index);
    }

    private void ToggleByName(string name)
    {
        var index = FindPresetIndex(name);
        if (index < 0)
        {
            SetHint($"找不到预设「{name}」。");
            return;
        }

        if (_activePreset != null && _activePresetIndex == index)
            StopCore(true);
        else
            PlayCore(Presets[index], index);
    }

    private void PlayCore(PresetDefinition preset, int index)
    {
        if (preset == null)
            return;

        _selectedPreset = preset;   // 快捷键触发时也让这一行切到该预设

        var controller = OverrideController;
        if (controller == null)
        {
            SetHint("⚠ 当前 MultiFunPlayer 没有「脚本覆盖内核」，无法播放预设。");
            return;
        }

        // 切换预设时先停下上一个
        if (_activePreset != null)
            StopCore(true);

        // ---- 1) 等播放器状态稳定后读取，用于停止预设时决定要不要恢复视频播放 ----
        Thread.Sleep(250);
        _savedIsPlaying = ReadProp<bool>("Media::PlayPause");
        _pausedPlayer = false;

        // ---- 2) 与视频互斥：暂停视频 ----
        if (PausePlayerOnPlay)
        {
            for (var attempt = 1; attempt <= PauseAttempts; attempt++)
            {
                PublishMessage(new MediaPlayPauseMessage(false));
                if (WaitUntil(() => !ReadProp<bool>("Media::PlayPause"), 800))
                    break;

                Log.Warn("第 {0}/{1} 次暂停播放器未生效", attempt, PauseAttempts);
            }

            _pausedPlayer = _savedIsPlaying;
            Log.Info("视频状态 [暂停前: {0}, 停止时会恢复播放: {1}]", _savedIsPlaying, _pausedPlayer);
        }

        // ---- 3) 读取预设里的脚本文件 ----
        var scripts = new Dictionary<DeviceAxis, IScriptResource>();
        var missing = new List<string>();

        foreach (var entry in preset.Entries ?? [])
        {
            var axis = DeviceAxis.Parse(entry.Axis);
            if (axis == null)
            {
                missing.Add($"{entry.Axis}(未知轴)");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.ScriptPath) || !File.Exists(entry.ScriptPath))
            {
                missing.Add($"{entry.Axis}(文件不存在)");
                continue;
            }

            var result = FunscriptReader.Default.FromPath(entry.ScriptPath);
            if (result?.IsSuccess != true)
            {
                missing.Add($"{entry.Axis}(读取失败)");
                continue;
            }

            if (result.IsMultiAxis)
            {
                foreach (var (multiAxis, resource) in result.Resources)
                    scripts[multiAxis] = resource;
            }
            else
            {
                scripts[axis] = result.Resource;
            }
        }

        if (scripts.Count == 0)
        {
            SetHint($"预设「{preset.Name}」里没有可用的脚本文件{(missing.Count > 0 ? $"：{string.Join("、", missing)}" : "")}");
            StopCore(true);
            return;
        }

        // ---- 4) 决定从哪开始：接着上次的位置播 ----
        var duration = 0d;
        foreach (var resource in scripts.Values)
            duration = Math.Max(duration, GetScriptDuration(resource));

        var startPosition = preset.ResumePosition;
        if (!double.IsFinite(startPosition) || startPosition < 0 || startPosition >= duration - ResumeEndThreshold)
            startPosition = 0;

        // ---- 5) 交给内核：独立时间轴接管，MediaPosition / 各轴脚本都不受影响 ----
        controller.StartOverride(scripts, startPosition);

        _activePreset = preset;
        _activePresetIndex = index;
        _displayPosition = startPosition;
        _timelineDuration = controller.OverrideDuration;

        // 把预设脚本的关键帧交给波形时间轴（原生 KeyframesHeatmap，和脚本区长得一样）
        var keyframes = scripts
            .Where(pair => pair.Value?.Keyframes is { Count: > 1 })
            .ToDictionary(pair => pair.Key, pair => pair.Value.Keyframes);

        Execute.OnUIThread(() =>
        {
            if (_transportTimeline == null)
                return;

            _transportTimeline.Keyframes = keyframes;
            _transportTimeline.Duration = controller.OverrideDuration;
            _transportTimeline.Position = startPosition;
        });

        Log.Info("开始播放预设「{0}」[脚本数: {1}, 时长: {2:F2}s, 循环: {3}, 起点: {4:F2}s]",
            preset.Name, scripts.Count, controller.OverrideDuration, preset.Loop, startPosition);

        if (missing.Count > 0)
            SetHint($"已跳过：{string.Join("、", missing)}");

        RefreshUi();
        UpdateTransportPosition(force: true);
    }

    /// <param name="resumePlayer">是否按之前记录的状态恢复视频播放。</param>
    private void StopCore(bool resumePlayer)
    {
        var preset = _activePreset;
        if (preset == null)
            return;

        var controller = OverrideController;

        // 记下这次播到哪，下次接着播
        if (controller is { IsOverrideActive: true })
            preset.ResumePosition = controller.OverridePosition;

        controller?.StopOverride();   // 设备立刻回到视频脚本（各轴脚本从未被改动过）

        _activePreset = null;
        _activePresetIndex = -1;

        // 恢复视频播放状态
        if (resumePlayer && _pausedPlayer)
        {
            PublishMessage(new MediaPlayPauseMessage(true));
            _pausedPlayer = false;
        }

        _savedIsPlaying = false;

        Log.Info("已停止预设「{0}」[续播位置: {1:F2}s]", preset.Name, preset.ResumePosition);
        SaveSettingsNow();

        RefreshUi();
        UpdateTransportPosition(force: true);
    }

    /// <summary>把预设时间轴跳到指定位置。</summary>
    private void SeekPresetTo(double position)
    {
        var controller = OverrideController;
        if (_activePreset == null || controller == null)
            return;

        controller.SeekOverride(position);
        _displayPosition = controller.OverridePosition;
        UpdateTransportPosition(force: true);
    }

    #endregion

    #region 预设的创建 / 删除

    /// <summary>打开资源管理器挑选 funscript 文件（可多选），按文件名自动匹配轴。</summary>
    private void CreatePresetFromFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择预设要使用的 funscript（可多选，文件名决定对应的轴）",
            Multiselect = true,
            CheckFileExists = true,
            Filter = "Funscript (*.funscript)|*.funscript|所有文件 (*.*)|*.*"
        };

        // 默认打开当前已加载脚本所在的目录（通常就是视频所在目录），省得每次手动找
        var folder = GetCurrentScriptsFolder() ?? _lastPresetFolder;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            dialog.InitialDirectory = folder;

        if (dialog.ShowDialog() != true)
            return;

        _lastPresetFolder = Path.GetDirectoryName(dialog.FileNames[0]);

        var byAxis = new Dictionary<string, PresetEntry>();
        var unmatched = new List<string>();

        foreach (var file in dialog.FileNames)
        {
            var axes = DeviceAxisUtils.FindAxesMatchingName(Path.GetFileName(file)).ToList();
            if (axes.Count == 0)
            {
                unmatched.Add(Path.GetFileName(file));
                continue;
            }

            byAxis[axes[0].Name] = new PresetEntry(axes[0].Name, Path.GetFullPath(file));
        }

        if (byAxis.Count == 0)
        {
            SetHint($"没有文件能匹配到已启用的轴：{string.Join("、", unmatched)}");
            return;
        }

        var entries = byAxis.Values.OrderBy(e => e.Axis, StringComparer.Ordinal).ToList();
        var preset = new PresetDefinition
        {
            Name = MakeUniqueName(Path.GetFileNameWithoutExtension(entries[0].ScriptPath)),
            Loop = true,
            Entries = entries
        };

        Execute.OnUIThread(() =>
        {
            Presets.Add(preset);
            _selectedPreset = preset;   // 新预设排在最后，直接让这一行切到它
            RefreshUi();
        });

        SaveSettingsNow();

        if (unmatched.Count > 0)
            SetHint($"✔ 已保存新预设「{preset.Name}」；已跳过无法匹配的文件：{string.Join("、", unmatched)}", autoHideSeconds: 6);
        else
            SetHint($"✔ 已保存新预设「{preset.Name}」（{entries.Count} 个脚本）", autoHideSeconds: 4);

        Log.Info("已创建预设「{0}」[{1}]", preset.Name, string.Join(", ", entries.Select(e => e.ToString())));
    }

    private void DeletePreset(PresetDefinition preset)
    {
        if (preset == null)
            return;

        if (_activePreset == preset)
            Enqueue(() => StopCore(true));

        Execute.OnUIThread(() =>
        {
            Presets.Remove(preset);
            RefreshUi();
        });

        SaveSettingsNow();
        SetHint("✔ 已删除预设", autoHideSeconds: 3);
    }

    /// <summary>把某个预设的续播位置清零（下次从头播）。</summary>
    private void ResetPresetPosition(PresetDefinition preset)
    {
        if (preset == null)
            return;

        preset.ResumePosition = 0;

        if (ReferenceEquals(_activePreset, preset))
            Enqueue(() => SeekPresetTo(0));

        // 时间轴立刻回到开头（原来只有下次开始播放时才刷新）
        if (ReferenceEquals(_activePreset, preset) || ReferenceEquals(_selectedPreset, preset))
        {
            _displayPosition = 0;
            UpdateTransportPosition(force: true);
        }

        SaveSettingsNow();
        Execute.OnUIThread(RefreshUi);
        SetHint($"✔ 已将「{preset.Name}」重置到开头", autoHideSeconds: 3);
    }

    private string MakeUniqueName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            name = "预设";

        name = name.Trim();
        if (Presets.All(p => p.Name != name))
            return name;

        for (var i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (Presets.All(p => p.Name != candidate))
                return candidate;
        }
    }

    private int FindPresetIndex(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Presets == null)
            return -1;

        for (var i = 0; i < Presets.Count; i++)
            if (string.Equals(Presets[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;

        return -1;
    }

    #endregion

    #region 与宿主交互的小工具

    private IScriptResource ReadScript(DeviceAxis axis)
    {
        try
        {
            return ReadProperty<DeviceAxis, IScriptResource>("Axis::Script", axis);
        }
        catch (Exception e)
        {
            Log.Trace(e, "读取轴脚本失败 [{0}]", axis);
            return null;
        }
    }

    /// <summary>当前第一个已加载脚本所在的目录，用作文件对话框的初始目录。</summary>
    private string GetCurrentScriptsFolder()
    {
        foreach (var axis in DeviceAxis.All)
        {
            var path = TryGetScriptPath(ReadScript(axis));
            if (path != null)
            {
                try { return Path.GetDirectoryName(path); }
                catch { }
            }
        }

        return null;
    }

    private static string TryGetScriptPath(IScriptResource script)
    {
        if (script == null)
            return null;
        if (string.IsNullOrWhiteSpace(script.Name) || string.IsNullOrWhiteSpace(script.Source))
            return null;

        try
        {
            // 文件型脚本：Name 是文件名，Source 是所在目录；网络仓库加载的脚本会在这里被过滤掉
            var path = Path.IsPathRooted(script.Name) ? script.Name : Path.Combine(script.Source, script.Name);
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private T ReadProp<T>(string propertyName)
    {
        try
        {
            return ReadProperty<T>(propertyName);
        }
        catch (Exception e)
        {
            Log.Trace(e, "读取属性失败 [{0}]", propertyName);
            return default;
        }
    }

    private static double GetScriptDuration(IScriptResource resource)
    {
        var keyframes = resource?.Keyframes;
        if (keyframes == null || keyframes.Count == 0)
            return 0;

        try
        {
            return keyframes[^1].Position;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>轮询等待条件成立，返回是否成立。用于等待播放器状态变化。</summary>
    private static bool WaitUntil(Func<bool> condition, int timeoutMilliseconds)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (condition())
                return true;

            if (stopwatch.ElapsedMilliseconds >= timeoutMilliseconds)
                return false;

            Thread.Sleep(25);
        }
    }

    private static string FormatPosition(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            return "0:00";

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    #endregion

    #region 配置持久化

    // 宿主在插件卸载/退出时会保存一次配置，但它不保证一定被触发（取决于 IoC 容器是否被 Dispose），
    // 所以这里自己再存一份，避免改完预设名退出后丢失。写入内容与宿主的格式完全一致。

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
            Log.Warn(e, "保存 ScriptPreset 配置失败");
            SetHint($"保存失败：{e.Message}");
        }
    }

    private string GetSettingsPath()
    {
        try
        {
            // PluginCompiler 编译时把语法树 path 设成了插件 .cs 的真实绝对路径，
            // 所以 CallerFilePath 拿到的就是本文件位置——配置文件和它同目录同名。
            var sourcePath = GetSourceFilePath();
            if (!string.IsNullOrWhiteSpace(sourcePath))
                return Path.ChangeExtension(Path.ChangeExtension(sourcePath, null), ".config.json");
        }
        catch (Exception e)
        {
            Log.Trace(e, "无法从 CallerFilePath 推断配置路径");
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "Plugins", "ScriptPreset.config.json");
    }

    private static string GetSourceFilePath([System.Runtime.CompilerServices.CallerFilePath] string path = null) => path;

    #endregion

    #region 界面（纯代码构建，不依赖 XAML）

    private UIElement BuildView()
    {
        // 展开条上放状态（折叠时也能看到在播什么），左边距对齐标签页标题「脚本预设」
        _statusText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 14,
            Margin = new Thickness(20, 0, 0, 0),
            Text = "空闲"
        };
        ApplyBodyForeground(_statusText);

        _presetListPanel = new StackPanel();

        _hintText = new TextBlock
        {
            FontSize = 14,
            Opacity = 0.85,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        ApplyBodyForeground(_hintText);

        var root = new StackPanel();
        root.Children.Add(_presetListPanel);
        root.Children.Add(_hintText);
        root.Children.Add(BuildTransport());   // 选择脚本文件 + 时间 + 波形时间轴

        _view = new PluginViewBase
        {
            Content = root,
            ToolBarContent = _statusText
        };

        return _view;
    }

    /// <summary>预设自己的传输区：一行是「选择脚本文件」+ 时间，下面一行是占满整宽的原生波形时间轴。</summary>
    private UIElement BuildTransport()
    {
        var selectButton = MakeTextButton("选择脚本文件…", (_, _) => CreatePresetFromFiles());

        _transportTimeText = new TextBlock
        {
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Text = "-- : --"
        };
        ApplyBodyForeground(_transportTimeText);

        var topRow = new DockPanel
        {
            Margin = new Thickness(0, 10, 0, 4),
            LastChildFill = true
        };

        DockPanel.SetDock(selectButton, Dock.Left);
        topRow.Children.Add(selectButton);
        topRow.Children.Add(_transportTimeText);   // 填充剩余宽度，右对齐

        // 直接用 MultiFunPlayer 原生的 KeyframesHeatmap，外观和脚本区那条时间轴完全一致
        _transportTimeline = new KeyframesHeatmap
        {
            Height = 36,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = Cursors.Hand,
            BucketCount = 333,
            CombineHeat = true,
            ShowRange = true,
            EnablePreview = true,
            InvertY = false,
            Duration = double.NaN,
            Position = double.NaN,
            Settings = BuildHeatmapSettings(),
            ToolTip = "预设的时间轴：点或拖动可跳转"
        };

        _transportTimeline.SeekRequest += (_, e) => Enqueue(() => SeekPresetTo(e.Position.TotalSeconds));

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(topRow);
        panel.Children.Add(_transportTimeline);

        return panel;
    }

    /// <summary>套用宿主资源里的样式；键不存在时静默回退到默认样式。</summary>
    private static void ApplyStyle(FrameworkElement element, string styleKey)
        => element.SetResourceReference(FrameworkElement.StyleProperty, styleKey);

    /// <summary>用主题的正文画刷，保证文字在任何主题下都有足够对比度。</summary>
    private static void ApplyBodyForeground(TextBlock text)
        => text.SetResourceReference(TextBlock.ForegroundProperty, "MaterialDesignBody");

    private static void ApplyBodyForeground(Control control)
        => control.SetResourceReference(Control.ForegroundProperty, "MaterialDesignBody");

    /// <summary>
    /// heatmap 的悬浮提示需要每轴的插值类型来画预览曲线，这里按宿主当前设置构造一份。
    /// </summary>
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

    private static Button MakeTextButton(string content, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = content,
            FontSize = 14,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 6, 4)
        };

        ApplyStyle(button, "MaterialDesignPaperButton");
        ApplyBodyForeground(button);

        button.Click += onClick;
        return button;
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

        ApplyStyle(button, "MaterialDesignPaperButton");

        button.Click += onClick;
        return button;
    }

    private void RefreshUi()
    {
        Execute.OnUIThread(() =>
        {
            if (_presetListPanel == null || _statusText == null)
                return;

            var presets = Presets ?? [];
            var active = _activePreset;
            var paused = OverrideController?.IsOverridePaused ?? false;

            _presetListPanel.Children.Clear();
            if (presets.Count == 0)
            {
                var emptyText = new TextBlock
                {
                    Text = "还没有预设。点下面的「选择脚本文件…」挑一组 funscript（可多选，文件名决定对应的轴）。",
                    FontSize = 14,
                    Opacity = 0.85,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4)
                };
                ApplyBodyForeground(emptyText);
                _presetListPanel.Children.Add(emptyText);
            }
            else
            {
                var ordered = presets.ToList();

                // 始终只有一行：优先当前选中的预设，其次正在播放的，最后第一个。
                var header = _selectedPreset != null && ordered.Any(p => ReferenceEquals(p, _selectedPreset))
                    ? _selectedPreset
                    : active != null && ordered.Any(p => ReferenceEquals(p, active))
                        ? active
                        : ordered[0];

                _presetListPanel.Children.Add(BuildPresetRow(header, withToggle: true));
            }

            _statusText.Text = active == null
                ? $"空闲（共 {presets.Count} 个预设）"
                : $"{(paused ? "⏸ 已暂停" : "▶ 正在播放")}「{active.Name}」";

            if (_transportTimeline != null)
                _transportTimeline.Opacity = active == null ? 0.35 : 1.0;

            if (_view != null)
            {
                _view.StatusContent = active?.Name;
                _view.StatusForeground = active == null
                    ? SystemColors.ControlTextBrush
                    : Brushes.OrangeRed;
            }

            RefreshPresetNames();
        });
    }

    /// <summary>刷新传输条的时间显示与播放头（播放中 20Hz）。</summary>
    private void UpdateTransportPosition(bool force = false)
    {
        if (_transportTimeline == null)
            return;

        var now = Stopwatch.GetTimestamp();
        if (!force && now - _lastTransportUpdateTicks < Stopwatch.Frequency / 20)
            return;

        _lastTransportUpdateTicks = now;

        var position = _displayPosition;
        var duration = _timelineDuration;

        Execute.OnUIThread(() =>
        {
            if (_transportTimeline == null)
                return;

            _transportTimeline.Position = position;

            if (_transportTimeText != null)
                _transportTimeText.Text = duration > 0
                    ? $"{FormatPosition(position)} / {FormatPosition(duration)}"
                    : "-- : --";
        });
    }

    private UIElement BuildPresetRow(PresetDefinition preset, bool withToggle = false)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };

        // 右侧：删除
        var deleteButton = MakeTextButton("删除", (_, _) => DeletePreset(preset));
        deleteButton.Margin = new Thickness(0);
        DockPanel.SetDock(deleteButton, Dock.Right);
        row.Children.Add(deleteButton);

        // 右侧：循环
        var loop = new CheckBox
        {
            Content = "循环",
            FontSize = 14,
            IsChecked = preset.Loop,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };

        loop.Checked += (_, _) => { preset.Loop = true; MarkSettingsDirty(); };
        loop.Unchecked += (_, _) => { preset.Loop = false; MarkSettingsDirty(); };
        ApplyBodyForeground(loop);
        DockPanel.SetDock(loop, Dock.Right);
        row.Children.Add(loop);

        // 左侧：播放/停止合一按钮
        var isActive = ReferenceEquals(_activePreset, preset);
        var toggle = MakeIconButton(
            isActive ? PackIconKind.Stop : PackIconKind.Play,
            isActive ? "停止" : "播放",
            isActive ? Brushes.OrangeRed : Brushes.Green,
            (_, _) =>
            {
                if (ReferenceEquals(_activePreset, preset))
                    Enqueue(() => StopCore(true));
                else
                    Enqueue(() => PlayCore(preset, Presets.IndexOf(preset)));
            });
        DockPanel.SetDock(toggle, Dock.Left);
        row.Children.Add(toggle);

        // 左侧：重置到开头
        var rewind = MakeIconButton(PackIconKind.Restart, "从开头播放（清除续播位置）",
            Brushes.SteelBlue, (_, _) => ResetPresetPosition(preset));
        DockPanel.SetDock(rewind, Dock.Left);
        row.Children.Add(rewind);

        // 左侧：预设名
        var nameBox = new TextBox
        {
            Text = preset.Name,
            FontSize = 14,
            Width = 160,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        ApplyStyle(nameBox, "MaterialDesignFloatingHintTextBox");
        HintAssist.SetHint(nameBox, "预设名");
        ApplyBodyForeground(nameBox);

        nameBox.TextChanged += (_, _) =>
        {
            preset.Name = nameBox.Text;
            MarkSettingsDirty();
            RefreshPresetNames();
        };
        DockPanel.SetDock(nameBox, Dock.Left);
        row.Children.Add(nameBox);

        // 折叠/展开：与宿主其它面板完全一致的 ChevronDown 旋转按钮
        // （MaterialDesignToolBarRotatingToggleButton，勾选时旋转 180°）。
        // 展开内容放在 Popup 里，所以无论展开与否这个面板都只占一行；选中一项后自动关闭。
        if (withToggle && (Presets?.Count ?? 0) > 1)
        {
            var list = new StackPanel();

            foreach (var candidate in (Presets ?? []).ToList())
            {
                var isCurrent = ReferenceEquals(candidate, preset);
                var item = new Button
                {
                    Content = new TextBlock
                    {
                        Text = (isCurrent ? "● " : "　 ") + candidate.Name + $"　{(candidate.Entries?.Count ?? 0)} 个脚本",
                        FontSize = 14,
                        FontWeight = isCurrent ? FontWeights.Bold : FontWeights.Normal,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(12, 5, 12, 5),
                    Margin = new Thickness(0)
                };

                ApplyStyle(item, "MaterialDesignFlatButton");
                ApplyBodyForeground(item);

                var target = candidate;
                item.Click += (_, _) =>
                {
                    _selectedPreset = target;
                    if (_presetListPopup != null)
                        _presetListPopup.IsOpen = false;
                    RefreshUi();
                    SetHint($"已选择预设「{target.Name}」，点 ▶ 播放", autoHideSeconds: 3);
                };

                list.Children.Add(item);
            }

            var popupContent = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(0, 4, 0, 4),
                Margin = new Thickness(4),
                MinWidth = 200,
                Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.35 },
                Child = list
            };
            popupContent.SetResourceReference(Border.BackgroundProperty, "MaterialDesignPaper");
            popupContent.SetResourceReference(Border.BorderBrushProperty, "MaterialDesignDivider");

            var popup = new Popup
            {
                Placement = PlacementMode.Bottom,
                StaysOpen = false,          // 点外面即关闭
                AllowsTransparency = true,
                Child = popupContent
            };

            var chevron = new PackIcon
            {
                Kind = PackIconKind.ChevronDown,
                Width = 24,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            ApplyBodyForeground(chevron);

            var fold = new ToggleButton
            {
                Content = chevron,
                Height = 26,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "展开"
            };
            ApplyStyle(fold, "MaterialDesignToolBarRotatingToggleButton");

            popup.PlacementTarget = fold;
            _presetListPopup = popup;

            // Popup 设了 StaysOpen=false，点箭头本身也会先被当成「点在外面」而关闭，
            // 紧接着 ToggleButton 又把 IsChecked 从 false 翻回 true 重新打开 —— 表现就是「展开后收不回去」。
            // 用 closingByToggle 区分这次关闭是箭头点的（交给 Unchecked 收尾）还是点外部触发的（复位箭头）。
            var closingByToggle = false;

            fold.PreviewMouseLeftButtonDown += (_, _) => closingByToggle = true;

            // 提示文字与宿主其它折叠控件（Expander）保持一致
            fold.Checked += (_, _) => { closingByToggle = false; _presetListPopup.IsOpen = true; fold.ToolTip = "折叠"; };
            fold.Unchecked += (_, _) => { closingByToggle = false; _presetListPopup.IsOpen = false; fold.ToolTip = "展开"; };
            popup.Closed += (_, _) =>
            {
                fold.ToolTip = "展开";

                if (!closingByToggle)
                {
                    fold.IsChecked = false;   // 点外部关闭 → 箭头转回向下
                    return;
                }

                // 箭头自己点的：延到本次点击处理完之后再复位，否则会被 ToggleButton 翻回 true 又打开
                fold.Dispatcher.BeginInvoke(new Action(() =>
                {
                    closingByToggle = false;
                    fold.IsChecked = false;
                }), DispatcherPriority.Background);
            };

            DockPanel.SetDock(fold, Dock.Left);
            row.Children.Add(fold);
        }

        // 中间（填充）：脚本摘要 + 续播位置
        var resume = preset.ResumePosition >= 0.5 ? FormatPosition(preset.ResumePosition) : null;
        var summary = string.Join("\n", (preset.Entries ?? []).Select(e => $"{e.Axis}  ←  {e.ScriptPath}"));

        var label = new TextBlock
        {
            Text = $"{(preset.Entries?.Count ?? 0)} 个脚本" + (resume != null ? $" · 续播 {resume}" : ""),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = string.IsNullOrEmpty(summary) ? null : summary
        };
        ApplyBodyForeground(label);

        row.Children.Add(label);
        return row;
    }

    private void RefreshPresetNames()
    {
        var names = (Presets ?? []).Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

        _presetNames.Clear();
        foreach (var name in names)
            _presetNames.Add(name);
    }

    private void SetHint(string message, double autoHideSeconds = 0)
    {
        Execute.OnUIThread(() =>
        {
            if (_hintText == null)
                return;

            _hintText.Text = message ?? string.Empty;
            _hintText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
            _hintHideTicks = autoHideSeconds > 0
                ? Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * autoHideSeconds)
                : 0;
        });
    }

    private void HideExpiredHint()
    {
        var hideAt = _hintHideTicks;
        if (hideAt == 0 || Stopwatch.GetTimestamp() < hideAt)
            return;

        _hintHideTicks = 0;
        SetHint(null);
    }

    #endregion
}

/// <summary>一个预设 = 名字 + 若干「轴 ← funscript 文件」+ 是否循环 + 续播位置。</summary>
public class PresetDefinition
{
    public string Name { get; set; }
    public bool Loop { get; set; } = true;
    public List<PresetEntry> Entries { get; set; } = [];

    /// <summary>上次播到的位置（秒）。下次播放这个预设时从这里接着播；播完或被重置则为 0。</summary>
    public double ResumePosition { get; set; }

    public override string ToString() => Name;
}

/// <summary>预设里的一条：某个轴用哪个 funscript 文件。</summary>
public class PresetEntry
{
    public PresetEntry() { }

    public PresetEntry(string axis, string scriptPath)
    {
        Axis = axis;
        ScriptPath = scriptPath;
    }

    public string Axis { get; set; }
    public string ScriptPath { get; set; }

    public override string ToString() => $"{Axis} ← {Path.GetFileName(ScriptPath)}";
}
