using MultiFunPlayer.Input;
using System.ComponentModel;

namespace MultiFunPlayer.Shortcut;

[DisplayName("按键长按")]
internal sealed class ButtonHoldShortcut(IShortcutActionRunner actionRunner, IButtonInputGestureDescriptor gesture)
    : AbstractShortcut<IButtonInputGesture, IEmptyInputGestureData>(actionRunner, gesture)
{
    public int MinimumHoldDuration { get; set; } = 1000;
    public int MaximumHoldDuration { get; set; } = -1;
    public ButtonHoldInvokeType InvokeType { get; set; } = ButtonHoldInvokeType.OnRelease;

    private int _pressTime;

    protected override void Update(IButtonInputGesture gesture)
    {
        if (gesture.State && _pressTime == 0)
        {
            _pressTime = Environment.TickCount;

            if (InvokeType == ButtonHoldInvokeType.WhileHolding)
                Delay(MinimumHoldDuration, () => Invoke(EmptyInputGestureData.Default));
        }
        else if (!gesture.State && _pressTime > 0)
        {
            var duration = Environment.TickCount - _pressTime;
            _pressTime = 0;

            if (InvokeType == ButtonHoldInvokeType.WhileHolding)
            {
                CancelDelay();
            }
            else if (InvokeType == ButtonHoldInvokeType.OnRelease)
            {
                if (duration < MinimumHoldDuration)
                    return;
                if (MaximumHoldDuration > MinimumHoldDuration && duration > MaximumHoldDuration)
                    return;

                Invoke(EmptyInputGestureData.Default);
            }
        }
    }
}

internal enum ButtonHoldInvokeType
{
    [Description("松开时")]
    OnRelease,
    [Description("按住期间")]
    WhileHolding
}