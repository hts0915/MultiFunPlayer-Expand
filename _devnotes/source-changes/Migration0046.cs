using Newtonsoft.Json.Linq;

namespace MultiFunPlayer.Settings.Migrations;

/// <summary>
/// 把串口输出目标的 DTR / RTS 恢复为打开。
/// <br/>
/// 曾经尝试关闭它们以避免设备在连接瞬间复位扭动（v1.32.1 的 Migration0045 已删除），
/// 但实测 CH340 这类 USB 串口在 DTR/RTS 关闭时 <c>SerialPort.Open()</c> 会直接在
/// <c>InitializeDCB</c> 阶段抛 <c>IOException: 连到系统上的设备没有发挥作用</c>，端口根本打不开。
/// 所以必须保持打开；连接瞬间的复位动作只能从设备固件侧解决。
/// </summary>
internal sealed class Migration0046 : AbstractSettingsMigration
{
    protected override void InternalMigrate(JObject settings)
    {
        EditPropertiesByPath(settings, "$.OutputTarget.Items[*].DtrEnable", _ => true);
        EditPropertiesByPath(settings, "$.OutputTarget.Items[*].RtsEnable", _ => true);
    }
}
