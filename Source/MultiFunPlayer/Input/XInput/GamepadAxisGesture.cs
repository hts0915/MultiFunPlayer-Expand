using System.ComponentModel;

namespace MultiFunPlayer.Input.XInput;

internal enum GamepadAxis
{
    [Description("左扳机")]
    LeftTrigger,
    [Description("右扳机")]
    RightTrigger,
    [Description("左摇杆 X")]
    LeftThumbX,
    [Description("左摇杆 Y")]
    LeftThumbY,
    [Description("右摇杆 X")]
    RightThumbX,
    [Description("右摇杆 Y")]
    RightThumbY
}

internal sealed record GamepadAxisGestureDescriptor(uint UserIndex, GamepadAxis Axis) : IAxisInputGestureDescriptor
{
    public override string ToString() => $"[Gamepad Axis: {UserIndex}/{Axis}]";
}

internal sealed class GamepadAxisGesture(GamepadAxisGestureDescriptor descriptor, double value, double delta, double deltaTime) : AbstractAxisInputGesture(descriptor, value, delta, deltaTime)
{
    public uint UserIndex => descriptor.UserIndex;
    public GamepadAxis Axis => descriptor.Axis;

    public override string ToString() => $"[Gamepad Axis: {UserIndex}/{Axis}, Value: {Value}, Delta: {Delta}]";

    public static GamepadAxisGesture Create(uint userIndex, GamepadAxis axis, double value, double delta, double deltaTime) => new(new(userIndex, axis), value, delta, deltaTime);
}
