using System.ComponentModel;

namespace MultiFunPlayer.Common;

internal enum DeviceAxisUpdateType
{
    [Description("固定频率更新")]
    FixedUpdate,
    [Description("按需轮询更新")]
    PolledUpdate
}

internal interface IDeviceAxisValueProvider
{
    public double GetValue(DeviceAxis axis);

    public void BeginEventPolling(object context);
    public void EndEventPolling(object context);

    public (DeviceAxis, DeviceAxisValueEvent) WaitForEventAny(IReadOnlyList<DeviceAxis> axes, object context, CancellationToken cancellationToken);
    public ValueTask<(DeviceAxis, DeviceAxisValueEvent)> WaitForEventAnyAsync(IReadOnlyList<DeviceAxis> axes, object context, CancellationToken cancellationToken);
    public (bool, DeviceAxisValueEvent) WaitForEvent(DeviceAxis axis, object context, CancellationToken cancellationToken);
    public ValueTask<(bool, DeviceAxisValueEvent)> WaitForEventAsync(DeviceAxis axis, object context, CancellationToken cancellationToken);
}

internal record DeviceAxisValueEvent(double TargetValue, double Duration);

internal sealed record class DeviceAxisScriptEvent(Keyframe From, Keyframe To)
    : DeviceAxisValueEvent(To?.Value ?? double.NaN, To?.Position - From?.Position ?? double.NaN);

internal sealed record class DeviceAxisAutoHomeEvent(double TargetValue, double Duration)
    : DeviceAxisValueEvent(TargetValue, Duration);

internal sealed record class DeviceAxisSpeedLimitedScriptEvent(DeviceAxisScriptEvent Event, double SpeedLimitUnitsPerSecond)
    : DeviceAxisValueEvent(GetTargetValue(Event, SpeedLimitUnitsPerSecond), Event.Duration)
{
    private static double GetTargetValue(DeviceAxisScriptEvent e, double speedLimitUnitsPerSecond)
    {
        var from = e.From?.Value ?? double.NaN;
        var to = e.To?.Value ?? double.NaN;

        var step = to - from;
        if (!double.IsFinite(step))
            return e.TargetValue;

        var speed = step / e.Duration;
        if (Math.Abs(speed) < speedLimitUnitsPerSecond)
            return e.TargetValue;

        return MathUtils.Clamp01(from + speedLimitUnitsPerSecond * e.Duration * Math.Sign(speed));
    }
}
