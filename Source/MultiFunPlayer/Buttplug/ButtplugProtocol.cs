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
/// 关于能力描述（DeviceMessages）有两种形态：
/// <list type="bullet">
/// <item>标准：<c>LinearCmd</c> 是对象 <c>{StepCount:[...]}</c>，<c>RotateCmd</c>/<c>ScalarCmd</c> 是执行器数组
/// —— 遵守 Buttplug v3 规范的库（VAM / buttplug-rs / buttplug-js）用这个。</item>
/// <item>兼容：<c>LinearCmd</c> 也变成执行器数组，并带上 <c>ActuatorType</c> / <c>FeatureDescriptor</c>，
/// 设备层再加 <c>DeviceDisplayName</c> / <c>DeviceMessageTimingGap</c>
/// —— 实测 Beat Banger 的自写客户端（bbfh-client）只认这一种，用标准形态会直接崩，
/// 因为它会无脑遍历 LinearCmd / RotateCmd / ScalarCmd 三个键。</item>
/// </list>
/// 两种形态都保证这三个键存在（哪怕是空数组），避免客户端遍历到 null。
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

    /// <summary>只会说"兼容形态"的自写客户端（按名字模糊匹配，不区分大小写）。</summary>
    private static readonly string[] CompatClientNames = ["bbfh", "beat banger"];

    public static bool IsCompatClient(string clientName)
        => !string.IsNullOrEmpty(clientName)
        && CompatClientNames.Any(name => clientName.Contains(name, StringComparison.OrdinalIgnoreCase));

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
    public static JObject DeviceInfo(string deviceName, IReadOnlyCollection<ButtplugActuator> actuators, int messageVersion, bool compat)
    {
        var linear = actuators.Where(a => a.Kind == ButtplugActuatorKind.Linear).OrderBy(a => a.Index).ToList();
        var rotate = actuators.Where(a => a.Kind == ButtplugActuatorKind.Rotate).OrderBy(a => a.Index).ToList();
        var scalar = actuators.Where(a => a.Kind == ButtplugActuatorKind.Scalar).OrderBy(a => a.Index).ToList();

        var messages = new JObject();

        // LinearCmd：标准形态是对象，兼容形态是执行器数组
        if (compat)
        {
            messages["LinearCmd"] = new JArray(linear.Select(a => Feature(a, "Position")));
        }
        else if (linear.Count > 0)
        {
            messages["LinearCmd"] = new JObject
            {
                ["StepCount"] = new JArray(Enumerable.Repeat(100, linear.Count)),
            };
        }

        // 这两个在规范里本来就是执行器数组；兼容形态只是多带 ActuatorType / FeatureDescriptor
        messages["RotateCmd"] = new JArray(rotate.Select(a => Feature(a, "Rotate")));
        messages["ScalarCmd"] = new JArray(scalar.Select(a => Feature(a, "Vibrate")));

        if (messageVersion < 3 && scalar.Count > 0)
        {
            messages["VibrateCmd"] = new JObject { ["FeatureCount"] = scalar.Count };
            messages["SingleMotorVibrateCmd"] = new JObject();
        }

        messages["StopDeviceCmd"] = new JObject();

        var device = new JObject
        {
            ["Id"] = 0,
            ["DeviceName"] = deviceName,
            ["DeviceIndex"] = 0,
            ["DeviceMessages"] = messages,
        };

        if (compat)
        {
            // Beat Banger 的自写客户端会直接读这两个字段
            device["DeviceDisplayName"] = $"{deviceName}/SR6 (TCode v3)";
            device["DeviceMessageTimingGap"] = 0;
        }

        return device;

        static JObject Feature(ButtplugActuator actuator, string actuatorType) => new()
        {
            ["ActuatorType"] = actuatorType,
            ["FeatureDescriptor"] = $"{actuator.Axis.FriendlyName} ({actuator.Axis.Name})",
            ["StepCount"] = 100,
        };
    }

    public static JObject DeviceAdded(string deviceName, IReadOnlyCollection<ButtplugActuator> actuators, int messageVersion, bool compat)
        => Message("DeviceAdded", DeviceInfo(deviceName, actuators, messageVersion, compat));

    public static JObject DeviceRemoved(int deviceIndex) => Message("DeviceRemoved", new JObject
    {
        ["Id"] = 0,
        ["DeviceIndex"] = deviceIndex,
    });

    public static JObject DeviceList(uint id, string deviceName, IReadOnlyCollection<ButtplugActuator> actuators, int messageVersion, bool compat)
    {
        var device = DeviceInfo(deviceName, actuators, messageVersion, compat);
        device.Remove("Id");
        return Message("DeviceList", new JObject
        {
            ["Id"] = id,
            ["Devices"] = new JArray(device),
        });
    }

    private static JObject Message(string type, JObject body) => new() { [type] = body };
}

/// <summary>执行器的三类：位置（LinearCmd）、旋转（RotateCmd）、强度（ScalarCmd）。</summary>
internal enum ButtplugActuatorKind
{
    Linear,
    Rotate,
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
    int IdleRestoreSeconds,
    bool ForceCompat);
