using Newtonsoft.Json.Linq;

namespace MultiFunPlayer.Settings.Migrations;

/// <summary>
/// 把串口输出目标的 DTR / RTS 关掉。
/// <br/>
/// 原因：.NET 自带的 <c>SerialPort</c> 在 <c>Handshake.None</c> 下会先把 DTR/RTS 拉高
/// （InitializeDCB 里写 ENABLE），然后才按 <c>DtrEnable</c>/<c>RtsEnable</c> 用 EscapeCommFunction 改回去，
/// 于是**每次打开/关闭串口都会产生一次电平跳变**。OSR 设备固件会跟着 DTR 变化复位归位，
/// 表现出来就是"连接和断开的瞬间设备乱扭"。
/// <br/>
/// 现在串口改由 Win32 直控（见 <c>OutputTarget/SerialTransport.cs</c>），只要把这两个信号
/// 一直钉在关闭状态就不存在任何跳变。旧配置里存的是 <c>true</c>，所以必须迁移一次；
/// 确实需要 DTR/RTS 的人可以在串口「高级设置」里重新打开。
/// </summary>
internal sealed class Migration0048 : AbstractSettingsMigration
{
    protected override void InternalMigrate(JObject settings)
    {
        EditPropertiesByPath(settings, "$.OutputTarget.Items[*].DtrEnable", _ => false);
        EditPropertiesByPath(settings, "$.OutputTarget.Items[*].RtsEnable", _ => false);
    }
}
