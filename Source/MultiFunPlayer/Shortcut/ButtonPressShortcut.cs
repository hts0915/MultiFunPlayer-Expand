using MultiFunPlayer.Input;
using System.ComponentModel;

namespace MultiFunPlayer.Shortcut;

[DisplayName("按键按下")]
internal sealed class ButtonPressShortcut(IShortcutActionRunner actionRunner, IButtonInputGestureDescriptor gesture)
    : AbstractShortcut<IButtonInputGesture, IEmptyInputGestureData>(actionRunner, gesture)
{
    private bool _lastPressed;

    public bool HandleRepeating { get; set; } = false;

    protected override void Update(IButtonInputGesture gesture)
    {
        var wasPressed = !_lastPressed && gesture.State;
        _lastPressed = gesture.State;
        if (!gesture.State)
            return;

        if (HandleRepeating || wasPressed)
            Invoke(EmptyInputGestureData.Default);
    }
}