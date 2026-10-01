using MultiFunPlayer.MediaSource.MediaResource;
using MultiFunPlayer.Script;
using Newtonsoft.Json.Linq;

namespace MultiFunPlayer.Common;

public enum SettingsAction
{
    Saving,
    Loading
}

internal sealed record SettingsMessage(JObject Settings, SettingsAction Action);
internal sealed record WindowCreatedMessage();

public sealed record MediaSpeedChangedMessage(double Speed);
public sealed record MediaPositionChangedMessage(TimeSpan Position, bool ForceSeek = false);
public sealed record MediaPlayingChangedMessage(bool IsPlaying);
public sealed record MediaPathChangedMessage(string Path, bool ReloadScripts = true, object Context = null);
public sealed record MediaDurationChangedMessage(TimeSpan Duration);
public sealed record MediaResetMessage();

public sealed record PreScriptSearchMessage(MediaResourceInfo MediaResource);
/// <summary>请求扫描局域网找出 TCode 设备的新地址（「启动连接」页的按钮发出）。</summary>
public sealed record DetectWifiDeviceMessage();
/// <summary>扫描结果（输出目标执行完扫描后发回给设置页显示）。</summary>
public sealed record WifiDeviceDetectedMessage(string Status);public sealed record PostScriptSearchMessage(MediaResourceInfo MediaResource, Dictionary<DeviceAxis, IScriptResource> Scripts);
public sealed record ScriptChangedMessage(DeviceAxis Axis, IScriptResource Script);
public sealed record ChangeScriptMessage(IReadOnlyDictionary<DeviceAxis, IScriptResource> Scripts)
{
    public ChangeScriptMessage(DeviceAxis axis, IScriptResource script) : this(new Dictionary<DeviceAxis, IScriptResource>() { [axis] = script }.AsReadOnly()) { }
    public ChangeScriptMessage(IEnumerable<DeviceAxis> axes, IScriptResource scriptResource) : this(axes.ToDictionary(a => a, _ => scriptResource).AsReadOnly()) { }
}

public interface IMediaSourceControlMessage;
public sealed record MediaSeekMessage(TimeSpan Position) : IMediaSourceControlMessage;
public sealed record MediaPlayPauseMessage(bool ShouldBePlaying) : IMediaSourceControlMessage;
public sealed record MediaChangePathMessage(string Path) : IMediaSourceControlMessage;
public sealed record MediaChangeSpeedMessage(double Speed) : IMediaSourceControlMessage;

public sealed record SyncRequestMessage(IReadOnlyList<DeviceAxis> Axes = null)
{
    public SyncRequestMessage(params DeviceAxis[] axes) : this(axes?.AsEnumerable()) { }
    public SyncRequestMessage(IEnumerable<DeviceAxis> axes) : this(axes?.ToList().AsReadOnly()) { }
}

public sealed record ReloadScriptsRequestMessage(IReadOnlyList<DeviceAxis> Axes = null)
{
    public ReloadScriptsRequestMessage(params DeviceAxis[] axes) : this(axes?.AsEnumerable()) { }
    public ReloadScriptsRequestMessage(IEnumerable<DeviceAxis> axes) : this(axes?.ToList().AsReadOnly()) { }
}
