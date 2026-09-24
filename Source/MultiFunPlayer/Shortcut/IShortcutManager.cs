using MultiFunPlayer.Common;
using MultiFunPlayer.Input;
using NLog;
using Stylet;
using System.Diagnostics;

namespace MultiFunPlayer.Shortcut;

internal interface IShortcutActionResolver
{
    IShortcutAction GetAction(string actionName);
    bool TryGetAction(string actionName, out IShortcutAction action);
}

internal interface IShortcutManager : IShortcutActionResolver, IDisposable
{
    bool HandleGestures { get; set; }
    IReadOnlyObservableConcurrentCollection<string> AvailableActions { get; }
    IReadOnlyObservableConcurrentCollection<IShortcut> Shortcuts { get; }

    IShortcutActionConfiguration BindAction(IShortcut shortcut, string actionName);
    IShortcutActionConfiguration BindAction(IShortcut shortcut, string actionName, IEnumerable<TypedValue> values);

    void UnbindAction(IShortcut shortcut, IShortcutActionConfiguration action);

    IShortcut AddShortcut(IShortcut shortcut);
    IShortcut AddShortcut<T>(IInputGestureDescriptor gesture) where T : IShortcut
        => AddShortcut((IShortcut)Activator.CreateInstance(typeof(T), [gesture]));

    bool RemoveShortcut(IShortcut shortcut);
    void ClearShortcuts();

    void RegisterAction(string actionName, Func<ValueTask> action);
    void RegisterAction<T0>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<T0, ValueTask> action);
    void RegisterAction<T0, T1>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Func<T0, T1, ValueTask> action);
    void RegisterAction<T0, T1, T2>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> settings2, Func<T0, T1, T2, ValueTask> action);
    void RegisterAction<T0, T1, T2, T3>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> settings2, Func<IShortcutSettingBuilder<T3>, IShortcutSettingBuilder<T3>> settings3, Func<T0, T1, T2, T3, ValueTask> action);
    void RegisterAction<T0, T1, T2, T3, T4>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> settings2, Func<IShortcutSettingBuilder<T3>, IShortcutSettingBuilder<T3>> settings3, Func<IShortcutSettingBuilder<T4>, IShortcutSettingBuilder<T4>> settings4, Func<T0, T1, T2, T3, T4, ValueTask> action);

    void RegisterAction<TD>(string actionName, Func<TD, ValueTask> action) where TD : IInputGestureData;
    void RegisterAction<TD, T0>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<TD, T0, ValueTask> action) where TD : IInputGestureData;
    void RegisterAction<TD, T0, T1>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Func<TD, T0, T1, ValueTask> action) where TD : IInputGestureData;
    void RegisterAction<TD, T0, T1, T2>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> settings2, Func<TD, T0, T1, T2, ValueTask> action) where TD : IInputGestureData;
    void RegisterAction<TD, T0, T1, T2, T3>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> settings2, Func<IShortcutSettingBuilder<T3>, IShortcutSettingBuilder<T3>> settings3, Func<TD, T0, T1, T2, T3, ValueTask> action) where TD : IInputGestureData;

    void RegisterAction(string actionName, Action action);
    void RegisterAction<T0>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Action<T0> action);
    void RegisterAction<T0, T1>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Action<T0, T1> action);
    void RegisterAction<T0, T1, T2>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> settings2, Action<T0, T1, T2> action);
    void RegisterAction<T0, T1, T2, T3>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> settings2, Func<IShortcutSettingBuilder<T3>, IShortcutSettingBuilder<T3>> settings3, Action<T0, T1, T2, T3> action);
    void RegisterAction<T0, T1, T2, T3, T4>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> settings2, Func<IShortcutSettingBuilder<T3>, IShortcutSettingBuilder<T3>> settings3, Func<IShortcutSettingBuilder<T4>, IShortcutSettingBuilder<T4>> settings4, Action<T0, T1, T2, T3, T4> action);

    void RegisterAction<TD>(string actionName, Action<TD> action) where TD : IInputGestureData;
    void RegisterAction<TD, T0>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Action<TD, T0> action) where TD : IInputGestureData;
    void RegisterAction<TD, T0, T1>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Action<TD, T0, T1> action) where TD : IInputGestureData;
    void RegisterAction<TD, T0, T1, T2>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> settings2, Action<TD, T0, T1, T2> action) where TD : IInputGestureData;
    void RegisterAction<TD, T0, T1, T2, T3>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> settings2, Func<IShortcutSettingBuilder<T3>, IShortcutSettingBuilder<T3>> settings3, Action<TD, T0, T1, T2, T3> action) where TD : IInputGestureData;

    void UnregisterAction(string actionName);

    bool ActionAcceptsGestureData(string actionName, Type gestureDataType);
    IShortcutActionConfiguration CreateShortcutActionConfigurationInstance(string actionName, IEnumerable<TypedValue> values);
}

internal sealed class ShortcutManager : IShortcutManager, IHandle<IInputGesture>
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ObservableConcurrentCollection<string> _availableActions;
    private readonly ObservableConcurrentDictionary<string, (IShortcutAction Action, IShortcutActionConfigurationBuilder Builder)> _actions;
    private readonly ObservableConcurrentCollection<IShortcut> _shortcuts;

    public bool HandleGestures { get; set; } = true;
    public IReadOnlyObservableConcurrentCollection<string> AvailableActions => _availableActions;
    public IReadOnlyObservableConcurrentCollection<IShortcut> Shortcuts => _shortcuts;

    public ShortcutManager(IEventAggregator eventAggregator)
    {
        eventAggregator.Subscribe(this, IInputProcessor.EventAggregatorChannelName);

        _availableActions = [];
        _actions = [];
        _shortcuts = [];
    }

    public IShortcutActionConfiguration BindAction(IShortcut shortcut, string actionName) => BindAction(shortcut, actionName, []);
    public IShortcutActionConfiguration BindAction(IShortcut shortcut, string actionName, IEnumerable<TypedValue> values)
    {
        Logger.Trace("Binding \"{0}\" action to \"{1}\" shortcut", actionName, shortcut.Gesture);

        var configuration = CreateShortcutActionConfigurationInstance(actionName, values);
        shortcut.Configurations.Add(configuration);
        return configuration;
    }

    public void UnbindAction(IShortcut shortcut, IShortcutActionConfiguration configuration)
    {
        Logger.Trace("Unbinding \"{0}\" action from \"{1}\" shortcut", configuration.Name, shortcut.Gesture);
        shortcut.Configurations.Remove(configuration);
    }

    public IShortcut AddShortcut(IShortcut shortcut)
    {
        _shortcuts.Add(shortcut);
        OnShortcutAdded(shortcut);
        return shortcut;
    }

    public bool RemoveShortcut(IShortcut shortcut)
        => _shortcuts.Remove(shortcut);
    public void ClearShortcuts()
        => _shortcuts.Clear();

    public IShortcutAction GetAction(string actionName)
        => TryGetAction(actionName, out var action) ? action : null;
    public bool TryGetAction(string actionName, out IShortcutAction action)
    {
        var result = _actions.TryGetValue(actionName, out var item);
        (action, _) = item;
        return result;
    }

    private void RegisterAction(string actionName, IShortcutAction action, params IEnumerable<IShortcutSettingBuilder> builders)
    {
        var builder = new ShortcutActionConfigurationBuilder(actionName, builders);
        var added = _actions.TryAdd(actionName, (action, builder));
        if (!added)
            throw new ArgumentException($"已添加具有相同键的项。键: {actionName}");

        _availableActions.Add(actionName);

        Logger.Trace("Registered \"{0}\" action", actionName);
        OnActionRegistered(actionName);
    }

    public void RegisterAction(string actionName, Func<ValueTask> action)
        => RegisterAction(actionName, new ShortcutAction(action));
    public void RegisterAction<T0>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> builder0, Func<T0, ValueTask> action)
        => RegisterAction(actionName, new ShortcutAction<T0>(action), builder0(new ShortcutSettingBuilder<T0>()));
    public void RegisterAction<T0, T1>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> builder0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> builder1, Func<T0, T1, ValueTask> action)
        => RegisterAction(actionName, new ShortcutAction<T0, T1>(action), builder0(new ShortcutSettingBuilder<T0>()), builder1(new ShortcutSettingBuilder<T1>()));
    public void RegisterAction<T0, T1, T2>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> builder0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> builder1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> builder2, Func<T0, T1, T2, ValueTask> action)
        => RegisterAction(actionName, new ShortcutAction<T0, T1, T2>(action), builder0(new ShortcutSettingBuilder<T0>()), builder1(new ShortcutSettingBuilder<T1>()), builder2(new ShortcutSettingBuilder<T2>()));
    public void RegisterAction<T0, T1, T2, T3>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> builder0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> builder1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> builder2, Func<IShortcutSettingBuilder<T3>, IShortcutSettingBuilder<T3>> builder3, Func<T0, T1, T2, T3, ValueTask> action)
        => RegisterAction(actionName, new ShortcutAction<T0, T1, T2, T3>(action), builder0(new ShortcutSettingBuilder<T0>()), builder1(new ShortcutSettingBuilder<T1>()), builder2(new ShortcutSettingBuilder<T2>()), builder3(new ShortcutSettingBuilder<T3>()));
    public void RegisterAction<T0, T1, T2, T3, T4>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> builder0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> builder1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> builder2, Func<IShortcutSettingBuilder<T3>, IShortcutSettingBuilder<T3>> builder3, Func<IShortcutSettingBuilder<T4>, IShortcutSettingBuilder<T4>> builder4, Func<T0, T1, T2, T3, T4, ValueTask> action)
        => RegisterAction(actionName, new ShortcutAction<T0, T1, T2, T3, T4>(action), builder0(new ShortcutSettingBuilder<T0>()), builder1(new ShortcutSettingBuilder<T1>()), builder2(new ShortcutSettingBuilder<T2>()), builder3(new ShortcutSettingBuilder<T3>()), builder4(new ShortcutSettingBuilder<T4>()));

    public void RegisterAction<TD>(string actionName, Func<TD, ValueTask> action) where TD : IInputGestureData
        => RegisterAction(actionName, new ShortcutAction<TD>(action));
    public void RegisterAction<TD, T0>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> builder0, Func<TD, T0, ValueTask> action) where TD : IInputGestureData
        => RegisterAction(actionName, new ShortcutAction<TD, T0>(action), builder0(new ShortcutSettingBuilder<T0>()));
    public void RegisterAction<TD, T0, T1>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> builder0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> builder1, Func<TD, T0, T1, ValueTask> action) where TD : IInputGestureData
        => RegisterAction(actionName, new ShortcutAction<TD, T0, T1>(action), builder0(new ShortcutSettingBuilder<T0>()), builder1(new ShortcutSettingBuilder<T1>()));
    public void RegisterAction<TD, T0, T1, T2>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> builder0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> builder1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> builder2, Func<TD, T0, T1, T2, ValueTask> action) where TD : IInputGestureData
        => RegisterAction(actionName, new ShortcutAction<TD, T0, T1, T2>(action), builder0(new ShortcutSettingBuilder<T0>()), builder1(new ShortcutSettingBuilder<T1>()), builder2(new ShortcutSettingBuilder<T2>()));
    public void RegisterAction<TD, T0, T1, T2, T3>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> builder0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> builder1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> builder2, Func<IShortcutSettingBuilder<T3>, IShortcutSettingBuilder<T3>> builder3, Func<TD, T0, T1, T2, T3, ValueTask> action) where TD : IInputGestureData
        => RegisterAction(actionName, new ShortcutAction<TD, T0, T1, T2, T3>(action), builder0(new ShortcutSettingBuilder<T0>()), builder1(new ShortcutSettingBuilder<T1>()), builder2(new ShortcutSettingBuilder<T2>()), builder3(new ShortcutSettingBuilder<T3>()));

    public void RegisterAction(string actionName, Action action)
    {
        RegisterAction(actionName, ActionAsValueTask);
        ValueTask ActionAsValueTask() { action(); return ValueTask.CompletedTask; }
    }

    public void RegisterAction<T0>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Action<T0> action)
    {
        RegisterAction(actionName, settings0, ActionAsValueTask);
        ValueTask ActionAsValueTask(T0 arg0) { action(arg0); return ValueTask.CompletedTask; }
    }

    public void RegisterAction<T0, T1>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Action<T0, T1> action)
    {
        RegisterAction(actionName, settings0, settings1, ActionAsValueTask);
        ValueTask ActionAsValueTask(T0 arg0, T1 arg1) { action(arg0, arg1); return ValueTask.CompletedTask; }
    }

    public void RegisterAction<T0, T1, T2>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> settings2, Action<T0, T1, T2> action)
    {
        RegisterAction(actionName, settings0, settings1, settings2, ActionAsValueTask);
        ValueTask ActionAsValueTask(T0 arg0, T1 arg1, T2 arg2) { action(arg0, arg1, arg2); return ValueTask.CompletedTask; }
    }

    public void RegisterAction<T0, T1, T2, T3>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> settings2, Func<IShortcutSettingBuilder<T3>, IShortcutSettingBuilder<T3>> settings3, Action<T0, T1, T2, T3> action)
    {
        RegisterAction(actionName, settings0, settings1, settings2, settings3, ActionAsValueTask);
        ValueTask ActionAsValueTask(T0 arg0, T1 arg1, T2 arg2, T3 arg3) { action(arg0, arg1, arg2, arg3); return ValueTask.CompletedTask; }
    }

    public void RegisterAction<T0, T1, T2, T3, T4>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> settings2, Func<IShortcutSettingBuilder<T3>, IShortcutSettingBuilder<T3>> settings3, Func<IShortcutSettingBuilder<T4>, IShortcutSettingBuilder<T4>> settings4, Action<T0, T1, T2, T3, T4> action)
    {
        RegisterAction(actionName, settings0, settings1, settings2, settings3, settings4, ActionAsValueTask);
        ValueTask ActionAsValueTask(T0 arg0, T1 arg1, T2 arg2, T3 arg3, T4 arg4) { action(arg0, arg1, arg2, arg3, arg4); return ValueTask.CompletedTask; }
    }

    public void RegisterAction<TD>(string actionName, Action<TD> action) where TD : IInputGestureData
    {
        RegisterAction<TD>(actionName, ActionAsValueTask);
        ValueTask ActionAsValueTask(TD argD) { action(argD); return ValueTask.CompletedTask; }
    }

    public void RegisterAction<TD, T0>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Action<TD, T0> action) where TD : IInputGestureData
    {
        RegisterAction<TD, T0>(actionName, settings0, ActionAsValueTask);
        ValueTask ActionAsValueTask(TD argD, T0 arg0) { action(argD, arg0); return ValueTask.CompletedTask; }
    }

    public void RegisterAction<TD, T0, T1>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Action<TD, T0, T1> action) where TD : IInputGestureData
    {
        RegisterAction<TD, T0, T1>(actionName, settings0, settings1, ActionAsValueTask);
        ValueTask ActionAsValueTask(TD argD, T0 arg0, T1 arg1) { action(argD, arg0, arg1); return ValueTask.CompletedTask; }
    }

    public void RegisterAction<TD, T0, T1, T2>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> settings2, Action<TD, T0, T1, T2> action) where TD : IInputGestureData
    {
        RegisterAction<TD, T0, T1, T2>(actionName, settings0, settings1, settings2, ActionAsValueTask);
        ValueTask ActionAsValueTask(TD argD, T0 arg0, T1 arg1, T2 arg2) { action(argD, arg0, arg1, arg2); return ValueTask.CompletedTask; }
    }

    public void RegisterAction<TD, T0, T1, T2, T3>(string actionName, Func<IShortcutSettingBuilder<T0>, IShortcutSettingBuilder<T0>> settings0, Func<IShortcutSettingBuilder<T1>, IShortcutSettingBuilder<T1>> settings1, Func<IShortcutSettingBuilder<T2>, IShortcutSettingBuilder<T2>> settings2, Func<IShortcutSettingBuilder<T3>, IShortcutSettingBuilder<T3>> settings3, Action<TD, T0, T1, T2, T3> action) where TD : IInputGestureData
    {
        RegisterAction<TD, T0, T1, T2, T3>(actionName, settings0, settings1, settings2, settings3, ActionAsValueTask);
        ValueTask ActionAsValueTask(TD argD, T0 arg0, T1 arg1, T2 arg2, T3 arg3) { action(argD, arg0, arg1, arg2, arg3); return ValueTask.CompletedTask; }
    }

    public void UnregisterAction(string actionName)
    {
        if (!_actions.ContainsKey(actionName))
            return;

        _availableActions.Remove(actionName);
        _actions.Remove(actionName);

        Logger.Debug("Unregistered \"{0}\" action", actionName);
        OnActionUnregistered(actionName);
    }

    private void OnShortcutAdded(IShortcut shortcut)
    {
        foreach (var configuration in shortcut.Configurations.Where(c => c.State == ShortcutActionConfigurationState.Valid && !_availableActions.Contains(c.Name)))
        {
            configuration.State = ShortcutActionConfigurationState.MissingAction;
            Logger.Debug("Invalidated configuration \"{0}\" due to missing action in \"{1}\" shortcut", configuration.Name, shortcut.Gesture);
        }
    }

    private void OnActionRegistered(string actionName)
    {
        var (_, builder) = _actions[actionName];
        foreach (var shortcut in _shortcuts)
        {
            for (var i = 0; i < shortcut.Configurations.Count; i++)
            {
                var configuration = shortcut.Configurations[i];
                if (actionName != configuration.Name)
                    continue;
                if (configuration.State == ShortcutActionConfigurationState.Valid)
                    throw new UnreachableException();

                if (configuration.State == ShortcutActionConfigurationState.Placeholder)
                {
                    Logger.Trace("Replacing placeholder configuration \"{0}\" in \"{1}\" shortcut", configuration.Name, shortcut.Gesture);
                    shortcut.Configurations[i] = builder.Build(configuration.Settings.Select(s => s.AsTypedValue()));
                }
                else
                {
                    configuration.State = ShortcutActionConfigurationState.Valid;
                }

                Logger.Debug("Validated configuration \"{0}\" in \"{1}\" shortcut", configuration.Name, shortcut.Gesture);
            }
        }
    }

    private void OnActionUnregistered(string actionName)
    {
        foreach (var shortcut in _shortcuts)
        {
            foreach (var configuration in shortcut.Configurations.Where(a => actionName == a.Name))
            {
                configuration.State = ShortcutActionConfigurationState.MissingAction;
                Logger.Debug("Invalidated configuration \"{0}\" due to missing action in \"{1}\" shortcut", configuration.Name, shortcut.Gesture);
            }
        }
    }

    public bool ActionAcceptsGestureData(string actionName, Type gestureDataType)
    {
        if (!TryGetAction(actionName, out var action))
            return false;

        return action.AcceptsGestureData(gestureDataType);
    }

    public IShortcutActionConfiguration CreateShortcutActionConfigurationInstance(string actionName, IEnumerable<TypedValue> values)
    {
        if (_actions.TryGetValue(actionName, out var item))
            return item.Builder.Build(values);
        else
            return new ShortcutActionConfiguration(actionName, values.Select(IShortcutSetting.FromTypedValue)) { State = ShortcutActionConfigurationState.Placeholder };
    }

    public void Handle(IInputGesture gesture)
    {
        if (!HandleGestures)
            return;

        foreach (var shortcut in _shortcuts)
            shortcut.Update(gesture);
    }

    private void Dispose(bool disposing)
    {
        foreach (var shortcut in _shortcuts)
            shortcut.Dispose();

        _shortcuts.Clear();
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}