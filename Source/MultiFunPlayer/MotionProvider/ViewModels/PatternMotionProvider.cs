using MultiFunPlayer.Common;
using MultiFunPlayer.Property;
using MultiFunPlayer.Shortcut;
using Newtonsoft.Json;
using Stylet;
using System.ComponentModel;
using System.Reflection;

namespace MultiFunPlayer.MotionProvider.ViewModels;

public enum PatternType
{
    [Description("三角波")]
    Triangle,
    [Description("正弦波")]
    Sine,
    [Description("双跳")]
    DoubleBounce,
    [Description("尖跳")]
    SharpBounce,
    [Description("锯齿波")]
    Saw,
    [Description("方波")]
    Square
}

[DisplayName("Pattern")]
[JsonObject(MemberSerialization = MemberSerialization.OptIn)]
internal sealed class PatternMotionProvider(DeviceAxis target, IEventAggregator eventAggregator) : AbstractMotionProvider(target, eventAggregator)
{
    private double _time;

    [JsonProperty] public PatternType Pattern { get; set; } = PatternType.Triangle;

    public override void Update(double deltaTime)
    {
        Value = MathUtils.Map(Calculate(), 0, 1, Minimum, Maximum);
        _time += Speed * deltaTime;
    }

    private double Calculate()
    {
        var t = MathUtils.Clamp01(_time % 4 / 4);
        switch (Pattern)
        {
            case PatternType.Triangle: return Math.Abs(Math.Abs(t * 2 - 1.5) - 1);
            case PatternType.Sine: return -Math.Sin(t * Math.PI * 2) / 2 + 0.5;
            case PatternType.DoubleBounce:
                {
                    var x = t * Math.PI * 2 - Math.PI / 4;
                    return -(Math.Pow(Math.Sin(x), 5) + Math.Pow(Math.Cos(x), 5)) / 2 + 0.5;
                }
            case PatternType.SharpBounce:
                {
                    var x = (t + 0.41957) * Math.PI / 2;
                    var s = Math.Sin(x) * Math.Sin(x);
                    var c = Math.Cos(x) * Math.Cos(x);
                    return Math.Sqrt(Math.Max(c - s, s - c));
                }
            case PatternType.Saw: return t;
            case PatternType.Square: return t < 0.5 ? 1 : 0;
            default: return 0;
        }
    }

    public static void RegisterActions(IShortcutManager s, Func<DeviceAxis, PatternMotionProvider> getInstance)
    {
        void UpdateProperty(DeviceAxis axis, Action<PatternMotionProvider> callback)
        {
            var motionProvider = getInstance(axis);
            if (motionProvider != null)
                callback(motionProvider);
        }

        AbstractMotionProvider.RegisterActions(s, getInstance);
        var name = typeof(PatternMotionProvider).GetCustomAttribute<DisplayNameAttribute>(inherit: false).DisplayName;

        #region PatternMotionProvider::Pattern
        s.RegisterAction<DeviceAxis, PatternType>($"MotionProvider::{name}::Pattern::Set",
            s => s.WithLabel("目标轴").WithItemsSource(DeviceAxis.All),
            s => s.WithLabel("图案").WithItemsSource(Enum.GetValues<PatternType>()),
            (axis, pattern) => UpdateProperty(axis, p => p.Pattern = pattern));
        #endregion
    }

    public static void RegisterProperties(IPropertyManager p, Func<DeviceAxis, PatternMotionProvider> getInstance)
    {
        TOut GetProperty<TOut>(DeviceAxis axis, Func<PatternMotionProvider, TOut> callback)
        {
            var motionProvider = getInstance(axis);
            if (motionProvider != null)
                callback(motionProvider);

            return default;
        }

        AbstractMotionProvider.RegisterProperties(p, getInstance);
        var name = typeof(PatternMotionProvider).GetCustomAttribute<DisplayNameAttribute>(inherit: false).DisplayName;

        p.RegisterProperty<DeviceAxis, PatternType>($"MotionProvider::{name}::Pattern", axis => GetProperty(axis, p => p.Pattern));
    }
}
