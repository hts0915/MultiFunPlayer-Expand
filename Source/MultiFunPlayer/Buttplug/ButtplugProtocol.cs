using MultiFunPlayer.Common;
using Newtonsoft.Json.Linq;

namespace MultiFunPlayer.Buttplug;

/// <summary>
/// Buttplug 协议的常量与消息构造。
/// <br/>
/// 消息格式：JSON 数组，每项一个键值对，键名就是消息类型，例如 <c>[{"RequestServerInfo":{...}}]</c>。
/// 规范允许一帧里放多条，但真正的 Intiface 是**一条一帧**发送的；有些软件只读一帧里的第一条，
/// 所以这里也按一条一帧发（见 <see cref="ButtplugServer"/>）。
/// <br/>
/// 同时兼容 v1/v2 客户端：它们用 <c>VibrateCmd</c> / <c>SingleMotorVibrateCmd</c> 之类的旧消息名，
/// 设备能力描述也不一样（<c>VibrateCmd.FeatureCount</c> 而不是 <c>ScalarCmd</c> 数组）。
/// </summary>
internal static class ButtplugProtocol
{
    public const int MessageVersion = 3;
    public const int MajorVersion = 3;
    public const int MinorVersion = 0;
    public const int DefaultPort = 12345;

    /// <summary>WebSocket 握手的魔术字符串。</summary>
    public const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    // ErrorCode（取自 Buttplug 规范）
    public const int ErrorUnknown = 1;
    public const int ErrorInit = 2;
    public const int ErrorPing = 3;
    public const int ErrorMessage = 4;
    public const int ErrorDevice = 5;

    public static JObject Ok(uint id) => Message("Ok", new JObject { ["Id"] = id });
    public static JObject Error(uint id, int code, string message) => Message("Error", new JObject
    {
        ["Id"] = id,
        ["ErrorCode"] = code,
        ["ErrorMessage"] = message,
    });

    public static JObject ServerInfo(uint id, string serverName, int messageVersion) => Message("ServerInfo", new JObject
    {
        ["Id"] = id,
        ["ServerName"] = serverName,
        ["MajorVersion"] = MajorVersion,
        ["MinorVersion"] = MinorVersion,
        ["MessageVersion"] = messageVersion,
        ["MaxPingTime"] = 0,
    });

    /// <summary>扫描结束（Intiface 在设备枚举完后会发这条；少发它有些客户端会一直等）。</summary>
    public static JObject ScanningFinished() => Message("ScanningFinished", new JObject { ["Id"] = 0 });

    /// <summary>DeviceAdded / DeviceList 里描述一台设备的内容。</summary>
    public static JObject DeviceInfo(string deviceName, IReadOnlyCollection<ButtplugActuator> actuators, int messageVersion)
    {
        var linearCount = actuators.Count(a => a.Kind == ButtplugActuatorKind.Linear);
        var scalarCount = actuators.Count(a => a.Kind == ButtplugActuatorKind.Scalar);

        var messages = new JObject();
        if (linearCount > 0)
        {
            messages["LinearCmd"] = new JObject
            {
                ["StepCount"] = new JArray(Enumerable.Repeat(100, linearCount)),
            };
        }

        if (scalarCount > 0)
        {
            if (messageVersion >= 3)
            {
                messages["ScalarCmd"] = new JArray(Enumerable.Repeat<object>(
                    new JObject { ["StepCount"] = 100, ["ActuatorType"] = "Vibrate" }, scalarCount));
            }
            else
            {
                // v1/v2 没有 ScalarCmd，用 VibrateCmd + FeatureCount
                messages["VibrateCmd"] = new JObject { ["FeatureCount"] = scalarCount };
                messages["SingleMotorVibrateCmd"] = new JObject();
            }
        }

        messages["StopDeviceCmd"] = new JObject();

        return new JObject
        {
            ["Id"] = 0,
            ["DeviceName"] = deviceName,
            ["DeviceIndex"] = 0,
            ["DeviceMessages"] = messages,
        };
    }

    public static JObject DeviceAdded(string deviceName, IReadOnlyCollection<ButtplugActuator> actuators, int messageVersion)
        => Message("DeviceAdded", DeviceInfo(deviceName, actuators, messageVersion));

    public static JObject DeviceRemoved(int deviceIndex) => Message("DeviceRemoved", new JObject
    {
        ["Id"] = 0,
        ["DeviceIndex"] = deviceIndex,
    });

    public static JObject DeviceList(uint id, string deviceName, IReadOnlyCollection<ButtplugActuator> actuators, int messageVersion)
    {
        var device = DeviceInfo(deviceName, actuators, messageVersion);
        device.Remove("Id");
        return Message("DeviceList", new JObject
        {
            ["Id"] = id,
            ["Devices"] = new JArray(device),
        });
    }

    private static JObject Message(string type, JObject body) => new() { [type] = body };
}

/// <summary>执行器的两类：位置型（LinearCmd）与强度型（振动）。</summary>
internal enum ButtplugActuatorKind
{
    Linear,
    Scalar,
}

/// <summary>暴露给 Buttplug 客户端的一个执行器，对应设备的一个轴。</summary>
internal sealed record ButtplugActuator(DeviceAxis Axis, ButtplugActuatorKind Kind, int Index);

/// <summary>服务器的一份配置快照（从设置界面取）。</summary>
internal sealed record ButtplugServerOptions(
    int Port,
    string ServerName,
    string DeviceName,
    IReadOnlyList<string> ExposedAxes,
    bool AutoTakeover,
    int IdleRestoreSeconds);
