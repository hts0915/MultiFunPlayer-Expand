using MultiFunPlayer.Input;
using System.ComponentModel;

namespace MultiFunPlayer.Shortcut;

[DisplayName("按键松开")]
internal sealed class ButtonReleaseShortcut(IShortcutActionRunner actionRunner, IButtonInputGestureDescriptor gesture)
    : AbstractShortcut<IButtonInputGesture, IEmptyInputGestureData>(actionRunner, gesture)
{
    private bool _lastPressed;

    public bool HandleRepeating { get; set; } = false;

    protected override void Update(IButtonInputGesture gesture)
    {
        var wasReleased = _lastPressed && !gesture.State;
        _lastPressed = gesture.State;
        if (gesture.State)
            return;

        if (HandleRepeating || wasReleased)
            Invoke(EmptyInputGestureData.Default);
    }
}