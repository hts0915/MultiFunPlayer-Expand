using MahApps.Metro.Controls;
using MaterialDesignThemes.Wpf;
using MultiFunPlayer.UI.Controls.ViewModels;
using MultiFunPlayer.UI.Dialogs.ViewModels;
using Stylet;
using StyletIoC;
using System.Reflection;
using System.Windows;

namespace MultiFunPlayer.UI;

internal static class DialogHelper
{
    private static SettingsViewModel Settings { get; set; }
    private static IViewManager ViewManager { get; set; }
    private static ISnackbarMessageQueue SnackbarMessageQueue { get; set; }
    private static HashSet<WeakReference<DialogHost>> LoadedInstances { get; }

    static DialogHelper()
    {
        LoadedInstances = (HashSet<WeakReference<DialogHost>>)typeof(DialogHost).GetField(nameof(LoadedInstances), BindingFlags.Static | BindingFlags.NonPublic)
                                                                                .GetValue(null);
    }

    public static void Initialize(IContainer container)
    {
        Settings = container.Get<SettingsViewModel>();
        ViewManager = container.Get<IViewManager>();
        SnackbarMessageQueue = container.Get<ISnackbarMessageQueue>();
    }

    public static async Task ShowErrorAsync(Exception exception, string message, string dialogIdentifier)
    {
        var displayType = Settings?.General?.ErrorDisplayType ?? ErrorDisplayType.None;
        if (displayType == ErrorDisplayType.None)
            return;

        var dialogModel = new ErrorMessageDialog(exception, message);
        if (displayType == ErrorDisplayType.Dialog)
        {
            await ShowAsync(dialogModel, dialogIdentifier);
        }
        else if (displayType == ErrorDisplayType.Snackbar)
        {
            await Execute.OnUIThreadAsync(() =>
                SnackbarMessageQueue.Enqueue(message, "查看",
                    async m => await ShowAsync(m, dialogIdentifier), dialogModel,
                    true, true, TimeSpan.FromSeconds(5)));
        }
    }

    public static async Task ShowAsync(Func<object> modelFactory, string dialogIdentifier)
        => await ExecuteOnUIThreadAsync(async () => _ = await ShowAsync<object>(modelFactory(), dialogIdentifier));

    public static async Task<TResult> ShowAsync<TResult>(Func<object> modelFactory, string dialogIdentifier)
        => await ExecuteOnUIThreadAsync(async () => await ShowAsync<TResult>(modelFactory(), dialogIdentifier));

    public static async Task ShowAsync(object model, string dialogIdentifier) => _ = await ShowAsync<object>(model, dialogIdentifier);
    public static async Task<TResult> ShowAsync<TResult>(object model, string dialogIdentifier)
    {
        var result = default(TResult);
        await ExecuteOnUIThreadAsync(async () =>
        {
            if (GetInstance(d => IsIdentifierEqual(d, dialogIdentifier) && IsModelEqual(d, model))?.CurrentSession is DialogSession { IsEnded: false })
                return;

            Close(GetInstance(d => IsIdentifierEqual(d, dialogIdentifier) && !IsModelEqual(d, model)));
            Close(GetInstance(d => !IsIdentifierEqual(d, dialogIdentifier) && IsModelEqual(d, model)));

            (model as IScreenState)?.Activate();
            var view = ViewManager.CreateAndBindViewForModelIfNecessary(model);
            result = (TResult)await DialogHost.Show(view, dialogIdentifier);
            (model as IScreenState)?.Deactivate();
        });

        return result;
    }

    public static void CloseByModel(object model, object parameter = null)
        => Close(() => GetInstance(d => IsModelEqual(d, model)), parameter);
    public static void CloseByModel(object model, string dialogIdentifier, object parameter = null)
        => Close(() => GetInstance(d => IsModelEqual(d, model) && IsIdentifierEqual(d, dialogIdentifier)), parameter);
    public static void Close(string dialogIdentifier, object parameter = null)
        => Close(() => GetInstance(d => IsIdentifierEqual(d, dialogIdentifier)), parameter);
    private static void Close(Func<DialogHost> dialogFactory, object parameter = null)
        => Execute.OnUIThreadSync(() => Close(dialogFactory(), parameter));

    private static void Close(DialogHost dialogInstance, object parameter = null)
    {
        if (dialogInstance?.CurrentSession is not DialogSession { IsEnded: false } session)
            return;

        Execute.OnUIThreadSync(() => CloseChildrenAndSelf(dialogInstance, BuildParentChildrenMap()));

        void Close(DialogSession session)
        {
            session.Close(parameter);
            (GetSessionModel(session) as IScreenState)?.Deactivate();
        }

        void CloseChildrenAndSelf(DialogHost dialogInstance, Dictionary<DialogHost, List<DialogHost>> map)
        {
            if (map.TryGetValue(dialogInstance, out var children))
                foreach (var child in children)
                    CloseChildrenAndSelf(child, map);

            if (dialogInstance?.CurrentSession is DialogSession { IsEnded: false } session)
                Close(session);
        }

        Dictionary<DialogHost, List<DialogHost>> BuildParentChildrenMap()
        {
            var result = new Dictionary<DialogHost, List<DialogHost>>();
            foreach (var item in LoadedInstances.ToList())
            {
                if (item.TryGetTarget(out var dialogInstance))
                {
                    var parentDialog = dialogInstance.TryFindParent<DialogHost>();
                    if (parentDialog == null)
                        continue;

                    if (result.TryGetValue(parentDialog, out var children))
                        children.Add(dialogInstance);
                    else
                        result.Add(parentDialog, [dialogInstance]);
                }
                else
                {
                    LoadedInstances.Remove(item);
                }
            }

            return result;
        }
    }

    private static bool IsIdentifierEqual(DialogHost dialogInstance, string dialogIdentifier)
    {
        if (dialogInstance == null)
            return false;

        var identifier = dialogInstance.Identifier;
        return Equals(dialogIdentifier, identifier);
    }

    private static bool IsModelEqual(DialogHost dialogInstance, object model)
    {
        if (dialogInstance == null)
            return false;

        var session = dialogInstance.CurrentSession;
        if (session?.IsEnded != false)
            return false;

        var sessionModel = GetSessionModel(session);
        return sessionModel?.Equals(model) == true;
    }

    private static DialogHost GetInstance(Func<DialogHost, bool> selector)
    {
        var list = new List<DialogHost>();
        foreach (var item in LoadedInstances.ToList())
        {
            if (item.TryGetTarget(out var dialogInstance))
            {
                if (selector(dialogInstance))
                    list.Add(dialogInstance);
            }
            else
            {
                LoadedInstances.Remove(item);
            }
        }

        return list.SingleOrDefault();
    }

    private static object GetSessionModel(DialogSession session) => (session?.Content as FrameworkElement)?.DataContext;

    private static Task ExecuteOnUIThreadAsync(Func<Task> taskFactory)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            var tcs = new TaskCompletionSource();
            dispatcher.Invoke(() => taskFactory().ContinueWith(t => tcs.SetFromTask(t), TaskScheduler.FromCurrentSynchronizationContext()));
            return tcs.Task;
        }
        else
        {
            return taskFactory();
        }
    }

    private static Task<TResult> ExecuteOnUIThreadAsync<TResult>(Func<Task<TResult>> taskFactory)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            var tcs = new TaskCompletionSource<TResult>();
            dispatcher.Invoke(() => taskFactory().ContinueWith(t => tcs.SetFromTask(t), TaskScheduler.FromCurrentSynchronizationContext()));
            return tcs.Task;
        }
        else
        {
            return taskFactory();
        }
    }
}
