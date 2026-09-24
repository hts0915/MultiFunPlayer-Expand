using MultiFunPlayer.Input;
using System.ComponentModel;

namespace MultiFunPlayer.Shortcut;

[DisplayName("按键单击")]
internal sealed class ButtonClickShortcut(IShortcutActionRunner actionRunner, IButtonInputGestureDescriptor gesture)
    : AbstractShortcut<IButtonInputGesture, IEmptyInputGestureData>(actionRunner, gesture)
{
    private int _stateCounter;

    public int ClickCount { get; set; } = 1;
    public int MaximumClickInterval { get; set; } = 200;

    protected override void Update(IButtonInputGesture gesture)
    {
        if (gesture.State && _stateCounter % 2 == 0)
        {
            _stateCounter++;
            CancelDelay();
        }
        else if (!gesture.State && _stateCounter % 2 == 1)
        {
            _stateCounter++;

            Delay(MaximumClickInterval, () => {
                if (_stateCounter == 2 * ClickCount)
                    Invoke(EmptyInputGestureData.Default);

                _stateCounter = 0;
            });
        }
    }
}