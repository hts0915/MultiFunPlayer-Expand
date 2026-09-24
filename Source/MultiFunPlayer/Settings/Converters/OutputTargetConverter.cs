using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using MultiFunPlayer.OutputTarget;
using MultiFunPlayer.Common;

namespace MultiFunPlayer.Settings.Converters;

[GlobalJsonConverter]
internal sealed class OutputTargetConverter(IOutputTargetFactory outputTargetFactory) : JsonConverter<IOutputTarget>
{
    public override IOutputTarget ReadJson(JsonReader reader, Type objectType, IOutputTarget existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        var o = JToken.ReadFrom(reader) as JObject;

        var type = o.GetTypeProperty()
            ?? throw new JsonReaderException($"找不到输出目标类型 \"{o["$type"]}\"");

        var index = o["$index"].ToObject<int>();
        o.Remove("$type");
        o.Remove("$index");

        var instance = outputTargetFactory.CreateOutputTarget(type, index)
            ?? throw new JsonReaderException($"无法创建类型 \"{type}\" 的实例（索引 \"{index}\"）");

        instance.HandleSettings(o, SettingsAction.Loading);
        return instance;
    }

    public override void WriteJson(JsonWriter writer, IOutputTarget value, JsonSerializer serializer)
    {
        var o = new JObject() { ["$index"] = value.InstanceIndex, };
        o.AddTypeProperty(value.GetType());

        value.HandleSettings(o, SettingsAction.Saving);
        serializer.Serialize(writer, o);
    }
}
