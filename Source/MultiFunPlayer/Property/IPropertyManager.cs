using MultiFunPlayer.Common;
using NLog;

namespace MultiFunPlayer.Property;

internal interface IPropertyManager
{
    IReadOnlyObservableConcurrentCollection<string> AvailableProperties { get; }

    void RegisterProperty<TOut>(string propertyName, Func<TOut> getter);
    void RegisterProperty<T0, TOut>(string propertyName, Func<T0, TOut> getter);
    void RegisterProperty<T0, T1, TOut>(string propertyName, Func<T0, T1, TOut> getter);

    void UnregisterProperty(string propertyName);
    TOut GetValue<TOut>(string propertyName, params object[] arguments);
    TOut GetValue<TOut>(string propertyName);
    TOut GetValue<T0, TOut>(string propertyName, T0 arg0);
    TOut GetValue<T0, T1, TOut>(string propertyName, T0 arg0, T1 arg1);
}

internal sealed class PropertyManager : IPropertyManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ObservableConcurrentCollection<string> _availableProperties;
    private readonly ObservableConcurrentDictionary<string, IPropertyDelegate> _properties;

    public IReadOnlyObservableConcurrentCollection<string> AvailableProperties => _availableProperties;

    public PropertyManager()
    {
        _availableProperties = [];
        _properties = [];
    }

    private void RegisterProperty(string propertyName, IPropertyDelegate propertyDelegate)
    {
        var added = _properties.TryAdd(propertyName, propertyDelegate);
        if (!added)
            throw new ArgumentException($"已添加具有相同键的项。键: {propertyName}");

        _availableProperties.Add(propertyName);

        Logger.Trace("Registered \"{0}\" property", propertyName);
    }

    public void RegisterProperty<TOut>(string propertyName, Func<TOut> getter) => RegisterProperty(propertyName, new PropertyDelegate<TOut>(getter));
    public void RegisterProperty<T0, TOut>(string propertyName, Func<T0, TOut> getter) => RegisterProperty(propertyName, new PropertyDelegate<T0, TOut>(getter));
    public void RegisterProperty<T0, T1, TOut>(string propertyName, Func<T0, T1, TOut> getter) => RegisterProperty(propertyName, new PropertyDelegate<T0, T1, TOut>(getter));

    public void UnregisterProperty(string propertyName)
    {
        _availableProperties.Remove(propertyName);
        _properties.Remove(propertyName);

        Logger.Debug("Unregistered \"{0}\" property", propertyName);
    }

    public TOut GetValue<TOut>(string propertyName, params object[] args)
    {
        var property = _properties[propertyName] as IPropertyDelegate<TOut>;
        return property.GetValue(args);
    }

    public TOut GetValue<TOut>(string propertyName)
    {
        var property = _properties[propertyName] as PropertyDelegate<TOut>;
        return property.GetValue();
    }

    public TOut GetValue<T0, TOut>(string propertyName, T0 arg0)
    {
        var property = _properties[propertyName] as PropertyDelegate<T0, TOut>;
        return property.GetValue(arg0);
    }

    public TOut GetValue<T0, T1, TOut>(string propertyName, T0 arg0, T1 arg1)
    {
        var property = _properties[propertyName] as PropertyDelegate<T0, T1, TOut>;
        return property.GetValue(arg0, arg1);
    }
}