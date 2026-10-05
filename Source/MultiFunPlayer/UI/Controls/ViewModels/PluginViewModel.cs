using MultiFunPlayer.Common;
using MultiFunPlayer.Plugin;
using PropertyChanged;
using Stylet;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;

namespace MultiFunPlayer.UI.Controls.ViewModels;

internal sealed class PluginViewModel : Conductor<PluginContainer>.Collection.OneActive, IHandle<SettingsMessage>
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly IReadOnlyObservableConcurrentCollection<PluginContainer> _source;

    // 插件面板标题栏的展开/收起状态。媒体源、输出目标那些同类属性都会存进配置，
    // 这里原来漏了 —— 所以插件（例如预设脚本）每次启动都是收起状态。
    public bool ContentVisible { get; set; } = true;

    public PluginViewModel(IPluginManager pluginManager, IEventAggregator eventAggregator)
    {
        _source = pluginManager.Containers;
        _source.CollectionChanged += OnSourceCollectionChanged;

        eventAggregator.Subscribe(this);

        OnSourceCollectionChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void Handle(SettingsMessage message)
    {
        if (message.Action == SettingsAction.Saving)
        {
            message.Settings["PluginContentVisible"] = ContentVisible;
        }
        else if (message.Action == SettingsAction.Loading)
        {
            if (message.Settings.TryGetValue<bool>("PluginContentVisible", out var contentVisible))
                ContentVisible = contentVisible;
        }
    }

    [SuppressPropertyChangedWarnings]
    private void OnSourceCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems?.Count > 1 || e.NewItems?.Count > 1)
            throw new NotSupportedException();

        var oldItem = e.OldItems?.Cast<PluginContainer>().FirstOrDefault();
        var newItem = e.NewItems?.Cast<PluginContainer>().FirstOrDefault();
        var newIndex = MapIndex(e.NewStartingIndex);

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                AddItem(newItem, newIndex);
                break;
            case NotifyCollectionChangedAction.Remove:
                RemoveItem(oldItem);
                break;
            case NotifyCollectionChangedAction.Replace:
                RemoveItem(oldItem);
                AddItem(newItem, newIndex == -1 ? MapIndex(_source.IndexOf(newItem)) : newIndex);
                break;
            case NotifyCollectionChangedAction.Move:
                RemoveItem(oldItem);
                AddItem(newItem, newIndex);
                break;
            case NotifyCollectionChangedAction.Reset:
                foreach(var item in Items)
                    RemoveItem(item);
                if (Items.Count != 0)
                    throw new UnreachableException();

                Logger.Info("插件标签页：源 {0} 个（{1}）", _source.Count,
                    string.Join("、", _source.Select(x => $"{x.DisplayName}@{(x.View == null ? "无视图" : "有视图")}")));

                foreach(var item in _source)
                    AddItem(item, -1);

                Logger.Info("插件标签页：显示 {0} 个（{1}）", Items.Count,
                    string.Join("、", Items.Select(x => x.DisplayName)));
                break;
        }
    }

    private void AddItem(PluginContainer item, int index)
    {
        item.PropertyChanged -= OnContainerPropertyChanged;
        item.PropertyChanged += OnContainerPropertyChanged;

        // 这里**不能**要求 View 已经就绪：插件的 View 是"标签页被激活"时才创建的，
        // 没视图就不给标签 ⇒ 没标签就不能激活 ⇒ 永远没视图（插件加载成功却在界面上看不到）。
        if (!Items.Contains(item))
            AddItemUnchecked(item, index);
    }

    private void AddItemUnchecked(PluginContainer item, int index)
    {
        if (index == -1)
            Items.Add(item);
        else
            Items.Insert(index++, item);

        if (ActiveItem == null)
            ActivateItem(item);
    }

    private void RemoveItem(PluginContainer item)
    {
        item.PropertyChanged -= OnContainerPropertyChanged;
        if (Items.Contains(item))
            RemoveItemUnchecked(item);
    }

    private void RemoveItemUnchecked(PluginContainer item)
    {
        CloseItem(item);
    }

    private int MapIndex(int index)
    {
        if (index == -1)
            return -1;

        while (--index >= 0)
        {
            var ourIndex = Items.IndexOf(_source[index]);
            if (ourIndex >= 0)
                return ourIndex + 1;
        }

        return 0;
    }

    [SuppressPropertyChangedWarnings]
    private void OnContainerPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PluginContainer.View))
        {
            var container = (PluginContainer)sender;
            Logger.Info("插件视图变化：{0}（视图：{1}）", container.DisplayName, container.View == null ? "空" : "已有");

            // 只做"补加"：视图变空只代表插件被停用/重编译，这时把标签页删掉会连锁触发
            // CloseItem → Dispose，把整个插件容器销毁掉（原来就是这样把标签页全弄没的）。
            if (container.View != null && !Items.Contains(container))
                AddItemUnchecked(container, MapIndex(_source.IndexOf(container)));
        }
        else if (e.PropertyName == nameof(PluginContainer.DisplayName))
        {
            OnPropertyChanged(nameof(Items));
        }
    }
}
