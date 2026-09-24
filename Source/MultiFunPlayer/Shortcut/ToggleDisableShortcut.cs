using MultiFunPlayer.Input;
using System.ComponentModel;

namespace MultiFunPlayer.Shortcut;

[DisplayName("切换禁用")]
internal sealed class ToggleDisableShortcut(IShortcutActionRunner actionRunner, IToggleInputGestureDescriptor gesture)
    : AbstractShortcut<IToggleInputGesture, IToggleInputGestureData>(actionRunner, gesture)
{
    protected override void Update(IToggleInputGesture gesture)
    {
        if (!gesture.State)
            Invoke(ToggleInputGestureData.FromGesture(gesture));
    }
}