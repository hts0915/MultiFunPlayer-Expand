using Newtonsoft.Json;
using Stylet;

namespace MultiFunPlayer.Common;

[JsonObject(MemberSerialization = MemberSerialization.OptIn)]
internal sealed class DeviceSettings : PropertyChangedBase
{
    [JsonProperty] public string Name { get; set; } = null;
    [JsonProperty] public bool IsDefault { get; set; } = false;
    [JsonProperty] public int OutputPrecision { get; set; } = 3;
    [JsonProperty] public ObservableConcurrentCollection<DeviceAxisSettings> Axes { get; set; } = [];

    public DeviceSettings Clone(string name) => new()
    {
        Name = name,
        IsDefault = false,
        OutputPrecision = OutputPrecision,
        Axes = new(Axes.Select(a => a.Clone()))
    };

    public static readonly IReadOnlyList<DeviceSettings> DefaultDevices =
    [
        new()
        {
            Name = "TCode-0.2",
            OutputPrecision = 3,
            IsDefault = true,
            Axes =
            [
                new() { Name = "L0", FriendlyName = "上下", FunscriptNames = ["*", "stroke", "L0", "up"], Enabled = true, DefaultValue = 0.5, },
                new() { Name = "L1", FriendlyName = "前后", FunscriptNames = ["surge", "L1", "forward"], Enabled = true, DefaultValue = 0.5, },
                new() { Name = "L2", FriendlyName = "左右", FunscriptNames = ["sway", "L2", "left"], Enabled = true, DefaultValue = 0.5, },
                new() { Name = "R0", FriendlyName = "扭转", FunscriptNames = ["twist", "R0", "yaw"], Enabled = true, DefaultValue = 0.5, },
                new() { Name = "R1", FriendlyName = "翻滚", FunscriptNames = ["roll", "R1"], Enabled = true, DefaultValue = 0.5, },
                new() { Name = "R2", FriendlyName = "俯仰", FunscriptNames = ["pitch", "R2"], Enabled = true, DefaultValue = 0.5, },
                new() { Name = "V0", FriendlyName = "振动", FunscriptNames = ["vib", "V0"], Enabled = false, DefaultValue = 0, },
                new() { Name = "V1", FriendlyName = "抽送", FunscriptNames = ["pump", "lube", "V1"], Enabled = false, DefaultValue = 0, },
                new() { Name = "L3", FriendlyName = "吸吮", FunscriptNames = ["suck", "valve", "L3"], Enabled = false, DefaultValue = 0, }
            ]
        },
        new()
        {
            Name = "TCode-0.3",
            OutputPrecision = 4,
            IsDefault = true,
            Axes =
            [
                new() { Name = "L0", FriendlyName = "上下", FunscriptNames = ["*", "stroke", "L0", "up"], Enabled = true, DefaultValue = 0.5, },
                new() { Name = "L1", FriendlyName = "前后", FunscriptNames = ["surge", "L1", "forward"], Enabled = true, DefaultValue = 0.5, },
                new() { Name = "L2", FriendlyName = "左右", FunscriptNames = ["sway", "L2", "left"], Enabled = true, DefaultValue = 0.5, },
                new() { Name = "R0", FriendlyName = "扭转", FunscriptNames = ["twist", "R0", "yaw"], Enabled = true, DefaultValue = 0.5, },
                new() { Name = "R1", FriendlyName = "翻滚", FunscriptNames = ["roll", "R1"], Enabled = true, DefaultValue = 0.5, },
                new() { Name = "R2", FriendlyName = "俯仰", FunscriptNames = ["pitch", "R2"], Enabled = true, DefaultValue = 0.5, },
                new() { Name = "V0", FriendlyName = "振动", FunscriptNames = ["vib", "V0"], Enabled = false, DefaultValue = 0, },
                new() { Name = "V1", FriendlyName = "抽送", FunscriptNames = ["pump", "V1"], Enabled = false, DefaultValue = 0, },
                new() { Name = "A0", FriendlyName = "阀门", FunscriptNames = ["valve", "A0"], Enabled = false, DefaultValue = 0, },
                new() { Name = "A1", FriendlyName = "吸吮", FunscriptNames = ["suck", "A1"], Enabled = false, DefaultValue = 0, },
                new() { Name = "A2", FriendlyName = "润滑", FunscriptNames = ["lube", "A2"], Enabled = false, DefaultValue = 0, }
            ]
        }
    ];
}

[JsonObject(MemberSerialization = MemberSerialization.OptIn)]
internal sealed class DeviceAxisSettings : PropertyChangedBase
{
    [JsonProperty] public string Name { get; set; } = null;
    [JsonProperty] public string FriendlyName { get; set; } = null;
    [JsonProperty] public ObservableConcurrentCollection<string> FunscriptNames { get; set; } = [];
    [JsonProperty] public double DefaultValue { get; set; } = 0;
    [JsonProperty] public bool Enabled { get; set; } = false;

    public DeviceAxisSettings Clone() => new()
    {
        Name = Name,
        FriendlyName = FriendlyName,
        FunscriptNames = new(FunscriptNames),
        DefaultValue = DefaultValue,
        Enabled = Enabled
    };
}
