using MultiFunPlayer.Input;
using System.ComponentModel;

namespace MultiFunPlayer.Shortcut;

[DisplayName("切换翻转")]
internal sealed class ToggleFlipShortcut(IShortcutActionRunner actionRunner, IToggleInputGestureDescriptor gesture)
    : AbstractShortcut<IToggleInputGesture, IToggleInputGestureData>(actionRunner, gesture)
{
    protected override void Update(IToggleInputGesture gesture) => Invoke(ToggleInputGestureData.FromGesture(gesture));
}