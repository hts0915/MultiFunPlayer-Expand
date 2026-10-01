using MultiFunPlayer.Common;
using Newtonsoft.Json.Linq;

namespace MultiFunPlayer.Buttplug;

/// <summary>
/// Buttplug Protocol v3 的常量与消息构造。
/// <br/>
/// 消息格式是「JSON 数组，每项一个键值对」：<c>[{"RequestServerInfo":{...}}]</c>，
/// 键名就是消息类型。这里只实现做成「Intiface 兼容的服务器」所必需的部分。
/// </summary>
internal static class ButtplugProtocol
{
    public const int MessageVersion = 3;
    public const int MajorVersion = 3;
    public const int MinorVersion = 0;
    public const int DefaultPort = 12345;

    /// <summary>Buttplug 的 WebSocket 握手魔术字符串。</summary>
    public const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    // ErrorCode（取自 Buttplug 规范的 Error 消息）
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

    public static JObject ServerInfo(uint id, string serverName) => Message("ServerInfo", new JObject
    {
        ["Id"] = id,
        ["ServerName"] = serverName,
        ["MajorVersion"] = MajorVersion,
        ["MinorVersion"] = MinorVersion,
        ["MessageVersion"] = MessageVersion,
        ["MaxPingTime"] = 0,
    });

    /// <summary>DeviceAdded / DeviceList 里描述一台设备的内容。</summary>
    public static JObject DeviceInfo(string deviceName, IReadOnlyCollection<ButtplugActuator> actuators)
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
            messages["ScalarCmd"] = new JArray(Enumerable.Repeat<object>(
                new JObject { ["StepCount"] = 100, ["ActuatorType"] = "Vibrate" }, scalarCount));
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

    public static JObject DeviceAdded(string deviceName, IReadOnlyCollection<ButtplugActuator> actuators)
        => Message("DeviceAdded", DeviceInfo(deviceName, actuators));

    public static JObject DeviceRemoved(int deviceIndex) => Message("DeviceRemoved", new JObject
    {
        ["Id"] = 0,
        ["DeviceIndex"] = deviceIndex,
    });

    public static JObject DeviceList(uint id, string deviceName, IReadOnlyCollection<ButtplugActuator> actuators)
    {
        var device = DeviceInfo(deviceName, actuators);
        device.Remove("Id");
        return Message("DeviceList", new JObject
        {
            ["Id"] = id,
            ["Devices"] = new JArray(device),
        });
    }

    private static JObject Message(string type, JObject body) => new() { [type] = body };

    public static string Serialize(params JObject[] messages) => new JArray(messages).ToString(Newtonsoft.Json.Formatting.None);
}

/// <summary>执行器的两类：位置型（LinearCmd）与强度型（ScalarCmd 的 Vibrate）。</summary>
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
