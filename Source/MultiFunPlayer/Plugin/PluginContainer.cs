using MultiFunPlayer.Common;
using MultiFunPlayer.Settings;
using NLog;
using Stylet;
using System.IO;
using System.Windows;

namespace MultiFunPlayer.Plugin;

internal enum PluginState
{
    Idle,
    Compiling,
    Running,
    Stopping,
    Stopped,
    Faulted
}

internal sealed class PluginContainer : PropertyChangedBase, IChildDelegate, IScreenState, IDisposable
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private PluginCompilationResult _compilationResult;

    public FileInfo File { get; }
    public Exception Exception { get; private set; }
    public PluginState State { get; private set; }

    public UIElement View => _compilationResult?.PluginInstance?.View;
    public PluginBase ViewModel => _compilationResult?.PluginInstance;

    /// <summary>
    /// 标签页标题：插件还没编译完（或编译失败）时退回用文件名，保证标签页总有名字 ——
    /// 否则会出现"插件加载了、却在界面上找不到"（标签没标题）的情况。
    /// </summary>
    public string DisplayName => ViewModel?.DisplayName ?? Path.GetFileNameWithoutExtension(File.Name);

    /// <summary>插件标签页排序权重（插件还没编译出来时排到最后）。</summary>
    public int Order => ViewModel?.Order ?? int.MaxValue;

    public bool CanCompile => State is not PluginState.Compiling;

    public PluginContainer(FileInfo pluginFile)
    {
        File = pluginFile;
        QueueCompile();
    }

    public void QueueCompile()
    {
        if (!File.Exists || State == PluginState.Compiling)
            return;

        State = PluginState.Compiling;
        PluginCompiler.QueueCompile(File, result =>
        {
            if (_compilationResult != null)
                Dispose();

            _compilationResult = result;
            if (_compilationResult.Success)
            {
                _compilationResult.PluginInstance.InternalInitialize(this);

                State = PluginState.Running;
                Exception = null;
            }
            else
            {
                State = PluginState.Faulted;
                Exception = _compilationResult.Exception;
            }

            HandleSettings(SettingsAction.Loading);
            NotifyOfPropertyChange(nameof(View));
            NotifyOfPropertyChange(nameof(DisplayName));
        });
    }

    private void HandleSettings(SettingsAction action)
    {
        if (_compilationResult?.Success != true)
            return;

        var settingsFileName = $"{Path.GetFileNameWithoutExtension(File.Name)}.config.json";
        var settingsPath = Path.Join(File.DirectoryName, settingsFileName);
        var settings = SettingsHelper.ReadOrEmpty(settingsPath);

        try
        {
            _compilationResult.PluginInstance.HandleSettings(settings, action);
        }
        catch (Exception e)
        {
            Logger.Warn(e, "Plugin settings failed with exception [Action: {0}]", action);
        }

        if (action == SettingsAction.Saving && settings.HasValues)
            SettingsHelper.Write(settings, settingsPath);
    }

    private void Dispose(bool disposing)
    {
        if (State is PluginState.Stopping or PluginState.Stopped)
            return;

        State = PluginState.Stopping;

        HandleSettings(SettingsAction.Saving);

        Close();
        _compilationResult?.Dispose();
        _compilationResult = null;

        NotifyOfPropertyChange(nameof(View));
        NotifyOfPropertyChange(nameof(DisplayName));

        State = PluginState.Stopped;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    #region IScreenState
    public ScreenState ScreenState => _compilationResult?.PluginInstance?.ScreenState ?? ScreenState.Deactivated;
    public bool IsActive => _compilationResult?.PluginInstance?.IsActive ?? false;

    public void Activate() => (_compilationResult?.PluginInstance as IScreenState)?.Activate();
    public void Deactivate() => (_compilationResult?.PluginInstance as IScreenState)?.Deactivate();
    public void Close() => (_compilationResult?.PluginInstance as IScreenState)?.Close();

    event EventHandler<ScreenStateChangedEventArgs> IScreenState.StateChanged
    {
        add => throw new NotImplementedException();
        remove => throw new NotImplementedException();
    }

    event EventHandler<ActivationEventArgs> IScreenState.Activated
    {
        add => throw new NotImplementedException();
        remove => throw new NotImplementedException();
    }

    event EventHandler<DeactivationEventArgs> IScreenState.Deactivated
    {
        add => throw new NotImplementedException();
        remove => throw new NotImplementedException();
    }

    event EventHandler<CloseEventArgs> IScreenState.Closed
    {
        add => throw new NotImplementedException();
        remove => throw new NotImplementedException();
    }
    #endregion

    #region IChildDelegate
    void IChildDelegate.CloseItem(object item, bool? dialogResult) => Dispose();
    #endregion
}
