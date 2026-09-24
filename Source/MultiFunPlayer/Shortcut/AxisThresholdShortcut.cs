using MultiFunPlayer.Input;
using System.ComponentModel;
using System.Diagnostics;

namespace MultiFunPlayer.Shortcut;

[DisplayName("轴阈值")]
internal sealed class AxisThresholdShortcut(IShortcutActionRunner actionRunner, IAxisInputGestureDescriptor gesture)
    : AbstractShortcut<IAxisInputGesture, IEmptyInputGestureData>(actionRunner, gesture)
{
    public double Threshold { get; set; } = 0.5;
    public AxisThresholdTriggerMode TriggerMode { get; set; } = AxisThresholdTriggerMode.Rising;

    protected override void Update(IAxisInputGesture gesture)
    {
        var isRising = gesture.Delta > 0 && gesture.Value >= Threshold && gesture.Value - gesture.Delta < Threshold;
        var isFalling = gesture.Delta < 0 && gesture.Value <= Threshold && gesture.Value - gesture.Delta > Threshold;
        var didTrigger = (isRising, isFalling, TriggerMode) switch
        {
            (true, false, AxisThresholdTriggerMode.Rising) => true,
            (false, true, AxisThresholdTriggerMode.Falling) => true,
            (true, true, _) => throw new UnreachableException(),
            (_, _, AxisThresholdTriggerMode.Both) => true,
            _ => false,
        };

        if (!didTrigger)
            return;

        Invoke(EmptyInputGestureData.Default);
    }
}

internal enum AxisThresholdTriggerMode
{
    [Description("上升沿")]
    Rising,
    [Description("下降沿")]
    Falling,
    [Description("双向")]
    Both
}