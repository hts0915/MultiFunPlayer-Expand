using Newtonsoft.Json.Linq;

namespace MultiFunPlayer.Settings.Migrations;

/// <summary>
/// 把设备轴的中文显示名写进已存在的配置。
/// <br/>
/// <c>FriendlyName</c> 只用于界面显示（全仓没有任何代码比较它），但它是**持久化设置**，
/// 所以光改 <c>DeviceSettings.DefaultDevices</c> 的默认值对老配置不生效，必须迁移一次。
/// <br/>
/// 只匹配原始英文名，用户自己改过的名字不会被覆盖。
/// </summary>
internal sealed class Migration0047 : AbstractSettingsMigration
{
    private static readonly Dictionary<string, string> FriendlyNames = new()
    {
        ["Up/Down"] = "上下",
        ["Forward/Backward"] = "前后",
        ["Left/Right"] = "左右",
        ["Twist"] = "扭转",
        ["Roll"] = "翻滚",
        ["Pitch"] = "俯仰",
        ["Vibrate"] = "振动",
        ["Pump"] = "抽送",
        ["Suction"] = "吸吮",
        ["Valve"] = "阀门",
        ["Lube"] = "润滑",
    };

    protected override void InternalMigrate(JObject settings)
    {
        foreach (var (english, chinese) in FriendlyNames)
            EditPropertiesByPath(settings, $"$.Devices[*].Axes[?(@.FriendlyName == '{english}')].FriendlyName", _ => chinese);
    }
}
