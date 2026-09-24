using MultiFunPlayer.Common;
using MultiFunPlayer.Settings;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace MultiFunPlayer.UI.Controls.ViewModels;

internal enum ErrorDisplayType
{
    [Description("无")]
    None,
    [Description("对话框")]
    Dialog,
    [Description("底部提示条")]
    Snackbar
}

internal sealed class GeneralSettingsViewModel : Screen, IHandle<SettingsMessage>, IHandle<WindowCreatedMessage>
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly INewtonsoftJsonLoggerManager _newtonsoftLoggerManager;
    private readonly IStyletLoggerManager _styletLoggerManager;

    public IReadOnlyCollection<LogLevel> LogLevels { get; }

    public LogLevel LogLevel { get; set; } = LogLevel.Info;
    public bool EnableUILogging { get; set; } = false;
    public bool EnableJsonLogging { get; set; } = false;
    public bool AllowWindowResize { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = false;
    public ErrorDisplayType ErrorDisplayType { get; set; } = ErrorDisplayType.Snackbar;
    public Orientation AppOrientation { get; set; } = Orientation.Vertical;
    public bool RememberWindowLocation { get; set; } = false;

    public GeneralSettingsViewModel(INewtonsoftJsonLoggerManager newtonsoftLoggerManager, IStyletLoggerManager styletLoggerManager, IEventAggregator eventAggregator)
    {
        DisplayName = "通用";
        eventAggregator.Subscribe(this);

        _newtonsoftLoggerManager = newtonsoftLoggerManager;
        _styletLoggerManager = styletLoggerManager;
        LogLevels = [.. LogLevel.AllLevels];
    }

    public void OnAlwaysOnTopChanged()
    {
        var window = Application.Current.MainWindow;
        if (window == null)
            return;

        window.Topmost = AlwaysOnTop;
    }

    public void OnEnableJsonLoggingChanged() => _newtonsoftLoggerManager.IsEnabled = EnableJsonLogging;
    public void OnEnableUILoggingChanged() => _styletLoggerManager.IsEnabled = EnableUILogging;

    public void OnAllowWindowResizeChanged()
    {
        var window = Application.Current.MainWindow;
        if (window == null)
            return;

        if (AllowWindowResize)
        {
            // 带握把拉伸：Window.xaml 中 CanResizeWithGrip 对应 ResizeBorderThickness="4,4,18,18"
            // 并显示右下角 18x18 的握把，比四面各 4px 的边缘好抓得多。
            window.ResizeMode = ResizeMode.CanResizeWithGrip;
            window.SizeToContent = SizeToContent.Manual;
        }
        else
        {
            window.ResizeMode = ResizeMode.CanMinimize;
            window.SizeToContent = SizeToContent.Height;
        }

        // 宽度上限是否锁死取决于本开关，必须跟着刷新
        OnAppOrientationChanged();
    }

    public void OnAppOrientationChanged()
    {
        var window = Application.Current.MainWindow;
        if (window == null)
            return;

        var defaultWidth = AppOrientation == Orientation.Horizontal ? 1200d : 600d;
        window.MinWidth = defaultWidth;

        if (AllowWindowResize)
        {
            // 允许自由拉伸时不能锁死宽度上限：原实现把 Width/MinWidth/MaxWidth 三者设成同一个值，
            // 宽度被钉死，表现就是只能上下拉伸、不能左右或斜角拉伸。
            // 这里必须用 ClearValue/SetCurrentValue 而不是直接赋值，直接赋值会破坏
            // RootView.xaml 中 Width="{Binding WindowWidth}" 的绑定。
            window.ClearValue(Window.MaxWidthProperty);
            if (window.ActualWidth < defaultWidth)
                window.SetCurrentValue(Window.WidthProperty, defaultWidth);
        }
        else
        {
            window.SetCurrentValue(Window.MaxWidthProperty, defaultWidth);
            window.SetCurrentValue(Window.WidthProperty, defaultWidth);
        }
    }

    public void OnLogLevelChanged()
    {
        if (LogLevel == null)
            return;

        Logger.Info("Changing log level to \"{0}\"", LogLevel.Name);

        LogManager.Configuration.FindRuleByName("application")?.SetLoggingLevels(LogLevel, LogLevel.Fatal);
        if (Debugger.IsAttached)
        {
            var debugLogLevel = LogLevel.FromOrdinal(Math.Min(LogLevel.Ordinal, 1));
            LogManager.Configuration.FindRuleByName("debug")?.SetLoggingLevels(debugLogLevel, LogLevel.Fatal);
        }

        LogManager.ReconfigExistingLoggers();
    }

    public void Handle(SettingsMessage message)
    {
        var settings = message.Settings;

        if (message.Action == SettingsAction.Saving)
        {
            settings[nameof(AlwaysOnTop)] = AlwaysOnTop;
            settings[nameof(ErrorDisplayType)] = JToken.FromObject(ErrorDisplayType);
            settings[nameof(LogLevel)] = JToken.FromObject(LogLevel ?? LogLevel.Info);
            settings[nameof(EnableUILogging)] = EnableUILogging;
            settings[nameof(EnableJsonLogging)] = EnableJsonLogging;
            settings[nameof(AllowWindowResize)] = AllowWindowResize;
            settings[nameof(AppOrientation)] = JToken.FromObject(AppOrientation);
            settings[nameof(RememberWindowLocation)] = RememberWindowLocation;
        }
        else if (message.Action == SettingsAction.Loading)
        {
            if (settings.TryGetValue<bool>(nameof(AlwaysOnTop), out var alwaysOnTop))
                AlwaysOnTop = alwaysOnTop;
            if (settings.TryGetValue<ErrorDisplayType>(nameof(ErrorDisplayType), out var errorDisplayType))
                ErrorDisplayType = errorDisplayType;
            if (settings.TryGetValue<LogLevel>(nameof(LogLevel), out var logLevel))
                LogLevel = logLevel;
            if (settings.TryGetValue<bool>(nameof(EnableUILogging), out var enableUILogging))
                EnableUILogging = enableUILogging;
            if (settings.TryGetValue<bool>(nameof(EnableJsonLogging), out var enableJsonLogging))
                EnableJsonLogging = enableJsonLogging;
            if (message.Settings.TryGetValue<bool>(nameof(AllowWindowResize), out var allowWindowResize))
                AllowWindowResize = allowWindowResize;
            if (settings.TryGetValue<Orientation>(nameof(AppOrientation), out var appOrientation))
                AppOrientation = appOrientation;
            if (settings.TryGetValue<bool>(nameof(RememberWindowLocation), out var rememberWindowLocation))
                RememberWindowLocation = rememberWindowLocation;
        }
    }

    public void Handle(WindowCreatedMessage message)
    {
        OnAlwaysOnTopChanged();
        OnAllowWindowResizeChanged();
        OnAppOrientationChanged();
    }
}
