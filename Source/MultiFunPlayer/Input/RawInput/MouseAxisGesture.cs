using System.ComponentModel;

namespace MultiFunPlayer.Input.RawInput;

internal enum MouseAxis
{
    [Description("X 轴")]
    X,
    [Description("Y 轴")]
    Y,
    [Description("滚轮")]
    MouseWheel,
    [Description("水平滚轮")]
    MouseHorizontalWheel
}

internal sealed record MouseAxisGestureDescriptor(MouseAxis Axis) : IAxisInputGestureDescriptor
{
    public override string ToString() => $"[Mouse Axis: {Axis}]";
}

internal sealed class MouseAxisGesture(MouseAxisGestureDescriptor descriptor, double value, double delta, double deltaTime) : AbstractAxisInputGesture(descriptor, value, delta, deltaTime)
{
    public MouseAxis Axis => descriptor.Axis;

    public override string ToString() => $"[Mouse Axis: {Axis}, Value: {Value}, Delta: {Delta}]";

    public static MouseAxisGesture Create(MouseAxis axis, double value, double delta, double deltaTime) => new(new(axis), value, delta, deltaTime);
}
