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
    private static readonly string[] CompatClientNames = ["bbfh", "beat banger", "lustbound"];

    /// <summary>
    /// 要求"与 Intiface Central 完全一致"的老客户端。
    /// 这类客户端的反序列化对**多余字段/多余消息**零容忍（多一个 DeviceDisplayName 都会抛
    /// "Could not find member"），因此对它我们必须逐字照抄 Intiface 的实测输出：
    ///   · 设备对象只给 DeviceIndex / DeviceName / DeviceMessages
    ///   · FeatureDescriptor 给空串
    ///   · 对 StartScanning 只回 Ok（不发 DeviceAdded / ScanningFinished）
    /// </summary>
    private static readonly string[] IntifaceExactClientNames = ["lustbound"];

    public static bool IsCompatClient(string clientName)
        => !string.IsNullOrEmpty(clientName)
        && CompatClientNames.Any(name => clientName.Contains(name, StringComparison.OrdinalIgnoreCase));

    public static bool IsIntifaceExactClient(string clientName)
        => !string.IsNullOrEmpty(clientName)
        && IntifaceExactClientNames.Any(name => clientName.Contains(name, StringComparison.OrdinalIgnoreCase));

    public static JObject Ok(uint id) => Message("Ok", new JObject { ["Id"] = id });
    public static JObject Error(uint id, int code, string message) => Message("Error", new JObject
    {
        ["Id"] = id,
        ["ErrorCode"] = code,
        ["ErrorMessage"] = message,
    });

    /// <summary>
    /// ServerInfo 回复。
    /// ⚠️ 字段集**照抄 Intiface Central（参考实现）的实测输出**：
    ///     {"ServerInfo":{"Id":1,"MessageVersion":3,"MaxPingTime":0,"ServerName":"Intiface Server"}}
    /// 即：**不发** MajorVersion / MinorVersion。
    /// 之前"按 v3 文档写全"反而害人：老客户端（Lustbound / Companion Demo）会因为多出的
    /// MajorVersion 抛 "Could not find member 'MajorVersion'"，少发 MaxPingTime 又会抛
    /// "Required property 'MaxPingTime' not found"，两次都当场断开连接。
    /// 实测 Intiface 就是这个形态，而官方 JS 客户端也正常工作 ⇒ 以参考实现为准最安全。
    /// </summary>
    public static JObject ServerInfo(uint id, string serverName, int messageVersion, bool compat) => Message("ServerInfo", new JObject
    {
        ["Id"] = id,
        ["ServerName"] = serverName,
        ["MessageVersion"] = messageVersion,
        ["MaxPingTime"] = 0,
    });

    /// <summary>扫描结束（Intiface 在设备枚举完后会发这条；少发它有些客户端会一直等）。</summary>
    public static JObject ScanningFinished() => Message("ScanningFinished", new JObject { ["Id"] = 0 });

    /// <summary>DeviceAdded / DeviceList 里描述一台设备的内容。</summary>
    public static JObject DeviceInfo(string deviceName, IReadOnlyCollection<ButtplugActuator> actuators, int messageVersion, bool compat, bool intifaceExact = false)
    {
        var linear = actuators.Where(a => a.Kind == ButtplugActuatorKind.Linear).OrderBy(a => a.Index).ToList();
        var rotate = actuators.Where(a => a.Kind == ButtplugActuatorKind.Rotate).OrderBy(a => a.Index).ToList();
        var scalar = actuators.Where(a => a.Kind == ButtplugActuatorKind.Scalar).OrderBy(a => a.Index).ToList();

        var messages = new JObject();

        // ⚠️ LinearCmd / RotateCmd / ScalarCmd 在官方客户端里**都必须是"执行器数组"**：
        //    MessageAttributes.update() 会对每一项执行 `forEach((x, i) => x.Index = i)`
        //    （见 buttplug 包里的 dist/main/src/core/Messages.js）
        // 之前按 v3 文档把 LinearCmd 写成 {"StepCount":[...]}，会让官方客户端抛
        //    TypeError: _c.forEach is not a function
        // 客户端随后丢弃这条 DeviceList 并一直等 ⇒ 网页表现为"连上了却一直转圈"。
        // ⚠️ DeviceMessages 的形态**随协议版本而变**，混用会让客户端"看不到设备"：
        //    v3：LinearCmd / RotateCmd / ScalarCmd 都是**执行器数组**
        //        [{ "ActuatorType":.., "FeatureDescriptor":.., "StepCount":100 }, ..]
        //    v1 / v2（实测 Intiface 的原样输出）：
        //        "LinearCmd":{ "FeatureCount":1, "StepCount":[100] }
        //        即 FeatureCount = 执行器个数、StepCount = 与之对应的数组；
        //        振动是 VibrateCmd { "FeatureCount": n }（+ SingleMotorVibrateCmd {}）。
        // 实测：Nudica 的新游戏（JOI Demo）自称 MessageVersion 2，
        //      我们给它 v3 的数组、或 {"StepCount":100} 都不认 ⇒ 它一条指令都不发 ⇒ 设备不动。
        if (messageVersion >= 3)
        {
            if (linear.Count > 0)
                messages["LinearCmd"] = new JArray(linear.Select(a => Feature(a, "Position")));

            if (rotate.Count > 0)
                messages["RotateCmd"] = new JArray(rotate.Select(a => Feature(a, "Rotate")));

            if (scalar.Count > 0)
                messages["ScalarCmd"] = new JArray(scalar.Select(a => Feature(a, "Vibrate")));
        }
        else
        {
            if (linear.Count > 0)
                messages["LinearCmd"] = new JObject
                {
                    ["FeatureCount"] = linear.Count,
                    ["StepCount"] = new JArray(linear.Select(_ => 100)),
                };

            if (rotate.Count > 0)
                messages["RotateCmd"] = new JObject
                {
                    ["FeatureCount"] = rotate.Count,
                    ["StepCount"] = new JArray(rotate.Select(_ => 100)),
                };

            if (scalar.Count > 0)
            {
                messages["VibrateCmd"] = new JObject { ["FeatureCount"] = scalar.Count };
                messages["SingleMotorVibrateCmd"] = new JObject();
            }
        }

        messages["StopDeviceCmd"] = new JObject();

        var device = new JObject
        {
            ["Id"] = 0,
            ["DeviceName"] = deviceName,
            ["DeviceIndex"] = 0,
            ["DeviceMessages"] = messages,
        };

        if (compat && !intifaceExact)
        {
            // Beat Banger / bbfh 的自写客户端会直接读这两个字段；
            // 但"要求与 Intiface 完全一致"的老库（Lustbound）多一个字段就会崩 ⇒ 对它不加。
            device["DeviceDisplayName"] = $"{deviceName}/SR6 (TCode v3)";
            device["DeviceMessageTimingGap"] = 0;
        }

        return device;

        JObject Feature(ButtplugActuator actuator, string actuatorType) => new()
        {
            ["ActuatorType"] = actuatorType,
            // Intiface 对单轴设备给的 FeatureDescriptor 是空串 ⇒ 逐字照抄
            ["FeatureDescriptor"] = intifaceExact ? string.Empty : DescribeFeature(actuator.Axis),
            ["StepCount"] = 100,
        };
    }

    /// <summary>
    /// 执行器描述文字。刻意用 OSR 社区/参考软件里的英文名（Beat Banger 能用的那份数据就是这些），
    /// 因为有些自写客户端会按描述文字来认轴。
    /// </summary>
    private static string DescribeFeature(DeviceAxis axis) => axis.Name.ToUpperInvariant() switch
    {
        "L0" => "Main Stroke (L0)",
        "L1" => "Surge (L1)",
        "L2" => "Sway (L2)",
        "R0" => "Twist (R0)",
        "R1" => "Roll (R1)",
        "R2" => "Pitch (R2)",
        "V0" => "Vibrate 0 (V0)",
        "V1" => "Vibrate 1 (V1)",
        _ => axis.Name,
    };

    public static JObject DeviceAdded(string deviceName, IReadOnlyCollection<ButtplugActuator> actuators, int messageVersion, bool compat, bool intifaceExact = false)
        => Message("DeviceAdded", DeviceInfo(deviceName, actuators, messageVersion, compat, intifaceExact));

    public static JObject DeviceRemoved(int deviceIndex) => Message("DeviceRemoved", new JObject
    {
        ["Id"] = 0,
        ["DeviceIndex"] = deviceIndex,
    });

    public static JObject DeviceList(uint id, string deviceName, IReadOnlyCollection<ButtplugActuator> actuators, int messageVersion, bool compat, bool intifaceExact = false)
    {
        var device = DeviceInfo(deviceName, actuators, messageVersion, compat, intifaceExact);
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
    bool ForceCompat,
    bool RotateAsLinear);
