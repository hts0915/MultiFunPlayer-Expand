using Stylet;
using StyletIoC;

namespace MultiFunPlayer.UI.Controls.ViewModels;

internal sealed class SettingsViewModel : Conductor<IScreen>.Collection.OneActive, IInjectionAware
{
    [Inject] public GeneralSettingsViewModel General { get; set; }
    [Inject] public StartupConnectionSettingsViewModel StartupConnection { get; set; }
    [Inject] public DeviceSettingsViewModel Device { get; set; }
    [Inject] public ThemeSettingsViewModel Theme { get; set; }
    [Inject] public InputSettingsViewModel Input { get; set; }
    [Inject] public ShortcutSettingsViewModel Shortcut { get; set; }
    [Inject] public ButtplugServerSettingsViewModel ButtplugServer { get; set; }

    public void ParametersInjected()
    {
        Items.Add(General);
        Items.Add(StartupConnection);
        Items.Add(Device);
        Items.Add(Theme);
        Items.Add(Input);
        Items.Add(Shortcut);
        Items.Add(ButtplugServer);
    }
}
