using MultiFunPlayer.Input;
using System.ComponentModel;

namespace MultiFunPlayer.Shortcut;

[DisplayName("切换启用")]
internal sealed class ToggleEnableShortcut(IShortcutActionRunner actionRunner, IToggleInputGestureDescriptor gesture)
    : AbstractShortcut<IToggleInputGesture, IToggleInputGestureData>(actionRunner, gesture)
{
    protected override void Update(IToggleInputGesture gesture)
    {
        if (gesture.State)
            Invoke(ToggleInputGestureData.FromGesture(gesture));
    }
}