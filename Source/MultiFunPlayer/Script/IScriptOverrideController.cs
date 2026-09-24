using MultiFunPlayer.Common;

namespace MultiFunPlayer.Script;

/// <summary>
/// 让外部（插件）用一组脚本独立驱动设备，**不占用视频播放器的时间轴**。
/// <br/>
/// 覆盖期间 <c>MediaPosition</c>、<c>MediaDuration</c> 以及各轴已加载的脚本都不会被改动，
/// 所以 Script 面板里的时间轴、进度、heatmap、脚本名全部保持视频那一套；
/// 停止覆盖后设备自动回到原来的脚本，无需任何恢复动作。
/// <br/>
/// 这是插件用的公开接口，由 <c>ScriptViewModel</c> 实现并经 IoC 暴露。
/// </summary>
public interface IScriptOverrideController
{
    /// <summary>是否正在被覆盖驱动。</summary>
    bool IsOverrideActive { get; }

    /// <summary>覆盖是否处于暂停状态（暂停时位置不前进，但轴值保持不变）。</summary>
    bool IsOverridePaused { get; }

    /// <summary>覆盖时间轴的当前位置（秒）。</summary>
    double OverridePosition { get; }

    /// <summary>覆盖时间轴的总时长（秒），取自传入脚本里最后一个关键帧的最大位置。</summary>
    double OverrideDuration { get; }

    /// <summary>
    /// 开始（或替换）覆盖。会做一次软启动同步避免设备突然跳变。
    /// </summary>
    /// <param name="scripts">每个轴要用哪个脚本；为 null 或不含任何有效关键帧的轴会被忽略。</param>
    /// <param name="startPosition">从覆盖时间轴的哪个位置开始（秒）。</param>
    void StartOverride(IReadOnlyDictionary<DeviceAxis, IScriptResource> scripts, double startPosition);

    /// <summary>停止覆盖，设备立即回到媒体源驱动的脚本（各轴脚本从未被改动过）。</summary>
    void StopOverride();

    /// <summary>跳转覆盖时间轴（不会触发软启动，等同于拖动进度条）。</summary>
    void SeekOverride(double position);

    /// <summary>暂停/继续覆盖时间轴。</summary>
    void SetOverridePaused(bool paused);
}
